using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;

namespace BulkCreateCustom;

internal static class SdkTransportValidation
{
    public static async Task RunAsync(DemoConfig config, BatchRequest[] batches, string password)
    {
        foreach (var scenario in new[] { "success", "rejected", "batch-failure", "vm-pending",
            "resource-not-visible", "status-not-visible", "status-incomplete",
            "status-never-visible", "status-transient-error", "second-page-not-visible", "resource-transient-error" })
        {
            using var handler = new OfflineHandler(batches, scenario);
            using var http = new HttpClient(handler);
            var options = new ArmClientOptions { Transport = new HttpClientTransport(http) };
            options.Retry.MaxRetries = 0;
            options.Diagnostics.IsLoggingContentEnabled = false;
            var client = new SdkBulkCreateClient(new ArmClient(new OfflineCredential(), config.SubscriptionId, options), config);
            using var output = new StringWriter();
            var result = await BulkCreateDemo.RunAsync(client, batches, new DemoLog(output, password),
                scenario == "status-never-visible" ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(1), CancellationToken.None);
            var fails = scenario is "rejected" or "batch-failure" or "status-never-visible";
            Require(result == (fails ? 1 : 0), $"SDK {scenario} outcome");
            Require(handler.PutCount == 2, "SDK concurrent PUT submissions");
            Require(output.ToString().Contains("Batch b: submission accepted; HTTP=201;"),
                "initial HTTP response status is logged from the SDK response");
            if (scenario == "rejected")
                Require(output.ToString().Contains("HTTP=409"), "rejected submission HTTP status remains visible");
            Require(handler.SecondPages >= (scenario is "rejected" or "status-never-visible" ? 1 : 2),
                "SDK status pagination");
            Require(!output.ToString().Contains(password), "SDK output redaction");
            Require(output.ToString().Contains("Batch b final: accepted=True; requested=50; succeeded=50"),
                "SDK observes accepted sibling");
            if (scenario == "batch-failure")
                Require(output.ToString().Contains("state=Failed; errorCode=InternalExecutionError;"),
                    "nested per-VM failure is reported");
            if (scenario is "resource-not-visible" or "status-not-visible" or "second-page-not-visible")
                Require(output.ToString().Contains("HTTP 404"), "temporary unavailability is reported");
            if (scenario == "status-never-visible")
                Require(output.ToString().Contains("poll timeout"), "persistent status 404 is bounded, not successful");
            if (scenario == "vm-pending")
                Require(output.ToString().Contains("pending=100") && output.ToString().Contains("succeeded=100"),
                    "per-VM pending results are polled until success");
        }
    }

    private static void Require(bool value, string label)
    {
        if (!value)
            throw new InvalidOperationException($"Offline transport validation failed: {label}");
    }

    private sealed class OfflineCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("offline-token-not-a-credential", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    // Every HTTP request terminates here. No base handler, sockets, Azure credentials or external network.
    private sealed class OfflineHandler(BatchRequest[] batches, string scenario) : HttpMessageHandler
    {
        private readonly TaskCompletionSource bothPuts = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int putCount;
        private int secondPages;
        private readonly ConcurrentDictionary<string, int> resourceReads = new();
        private readonly ConcurrentDictionary<string, int> statusReads = new();
        public int PutCount => putCount;
        public int SecondPages => secondPages;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            var batch = batches.SingleOrDefault(b => uri.AbsolutePath.Contains(b.OperationName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Unexpected offline request path.");
            var resourceId = uri.AbsolutePath.Split("/virtualMachinesGetOperationStatus")[0];
            var failed = scenario == "batch-failure" && batch.Label == "a";

            if (request.Method == HttpMethod.Put)
            {
                Require(Guid.TryParse(uri.Segments[^1], out _), "UUID operation path");
                Require(uri.Query.Contains("api-version=2026-08-06-preview"), "pinned custom API version");
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Require(payload.RootElement.GetProperty("properties").GetProperty("capacity").GetInt32()
                    == batch.Data.Properties.Capacity, "SDK wire capacity");
                Require(payload.RootElement.GetProperty("zones").GetArrayLength() == batch.Data.Zones.Count, "SDK wire zones");
                if (Interlocked.Increment(ref putCount) == 2)
                    bothPuts.SetResult();
                await bothPuts.Task.WaitAsync(cancellationToken);
                if (scenario == "rejected" && batch.Label == "a")
                    return Reply(HttpStatusCode.Conflict, new { error = new { code = "InsufficientCapacity", message = "Synthetic rejection" } });
                var response = ResourceReply(resourceId, "Creating");
                response.StatusCode = HttpStatusCode.Created;
                response.Headers.Add("Azure-AsyncOperation", $"https://management.azure.com/offline-async/{batch.OperationName}");
                response.Headers.Add("Retry-After", "0");
                return response;
            }
            Require(!uri.AbsolutePath.StartsWith("/offline-async/", StringComparison.Ordinal),
                "per-VM polling must never use the async-operation endpoint");
            if (request.Method == HttpMethod.Get)
            {
                Require(statusReads.GetValueOrDefault(batch.Label) > 0, "pageable per-VM status is queried before the resource");
                var read = resourceReads.AddOrUpdate(batch.Label, 1, (_, count) => count + 1);
                if (scenario == "resource-transient-error" && read == 1)
                    return Reply(HttpStatusCode.ServiceUnavailable, new { error = new { code = "ServiceUnavailable" } });
                if (scenario == "resource-not-visible" && read == 1)
                    return NotFound();
                return ResourceReply(resourceId, failed ? "Failed" : "Succeeded");
            }
            Require(request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/virtualMachinesGetOperationStatus"),
                "only create/get/status methods used");
            var pageTwo = uri.Query.Contains("page=2");
            if (!pageTwo)
            {
                var read = statusReads.AddOrUpdate(batch.Label, 1, (_, count) => count + 1);
                if (scenario == "status-transient-error" && read == 1)
                    return Reply(HttpStatusCode.ServiceUnavailable, new { error = new { code = "ServiceUnavailable" } });
                if ((scenario == "status-not-visible" && read == 1) || (scenario == "status-never-visible" && batch.Label == "a"))
                    return NotFound();
                if (scenario == "status-incomplete" && read == 1)
                    return Reply(HttpStatusCode.OK, new { results = Array.Empty<object>() });
            }
            if (pageTwo && scenario == "second-page-not-visible" && statusReads[batch.Label] == 1)
                return NotFound();
            if (pageTwo)
                Interlocked.Increment(ref secondPages);
            var half = batch.ComputerNames.Count / 2;
            var results = batch.ComputerNames.Keys.Skip(pageTwo ? half : 0).Take(half).Select((name, index) => new
            {
                resourceId = batch.VmResourceIdPrefix + name,
                operation = new
                {
                    operationId = $"offline-{name}",
                    state = failed && !pageTwo && index == 0 ? "Failed"
                        : scenario == "vm-pending" && statusReads[batch.Label] < 3 ? "Executing" : "Succeeded",
                    resourceOperationError = failed && !pageTwo && index == 0
                        ? new { errorCode = "InternalExecutionError", errorDetails = "Synthetic backend failure." } : null
                },
                virtualMachineInfo = new { vmSize = batch.ExpectedDisks.Keys.First(), zone = "1" }
            }).ToArray();
            return Reply(HttpStatusCode.OK, new
            {
                results,
                nextLink = pageTwo ? null : $"https://management.azure.com{uri.AbsolutePath}?page=2&api-version=2026-08-06-preview"
            });
        }

        private static HttpResponseMessage ResourceReply(string id, string state) => Reply(HttpStatusCode.OK, new
        {
            id, name = id.Split('/')[^1], type = "Microsoft.Compute/locations/bulkCreateCustom",
            properties = new { provisioningState = state }
        });

        private static HttpResponseMessage NotFound() => Reply(HttpStatusCode.NotFound, new
        {
            error = new { code = "BulkActionNotFoundException", message = "Synthetic operation not visible yet." }
        });

        private static HttpResponseMessage Reply(HttpStatusCode status, object payload) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }
}
