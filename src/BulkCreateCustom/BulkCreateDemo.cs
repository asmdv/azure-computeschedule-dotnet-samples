using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute.BulkActions;
using Azure.ResourceManager.Compute.BulkActions.Models;
using Azure.ResourceManager.Resources;

namespace BulkCreateCustom;

internal interface IBulkCreateClient
{
    string ResourceId(BatchRequest batch);
    Task<ISubmittedBatch> SubmitAsync(BatchRequest batch, CancellationToken cancellationToken);
}

internal interface ISubmittedBatch
{
    string OperationId { get; }
    int SubmissionStatus { get; }
    Task<BatchSnapshot> RefreshAsync(CancellationToken cancellationToken);
}

internal sealed record BatchSnapshot(bool OperationCompleted, bool OperationFailed, string ProvisioningState,
    int? FulfilledCapacity, IReadOnlyList<ComputeBulkOperationResult> Results,
    bool StatusAvailable = true, string? ErrorCode = null, string? Observation = null);

internal sealed class SdkBulkCreateClient(ArmClient client, DemoConfig config) : IBulkCreateClient
{
    public string ResourceId(BatchRequest batch) =>
        LocationBasedBulkCreateCustomResource.CreateResourceIdentifier(
            config.SubscriptionId, config.ResourceGroup, config.Region, batch.OperationName).ToString();

    public async Task<ISubmittedBatch> SubmitAsync(BatchRequest batch, CancellationToken cancellationToken)
    {
        var group = client.GetResourceGroupResource(
            ResourceGroupResource.CreateResourceIdentifier(config.SubscriptionId, config.ResourceGroup));
        var operation = await group.GetLocationBasedBulkCreateCustoms(config.Region).CreateOrUpdateAsync(
            WaitUntil.Started, batch.OperationName, batch.Data, cancellationToken);
        var resource = client.GetLocationBasedBulkCreateCustomResource(new ResourceIdentifier(ResourceId(batch)));
        return new SubmittedBatch(operation.Id, operation.GetRawResponse().Status, resource);
    }

    private sealed class SubmittedBatch(
        string operationId,
        int submissionStatus,
        LocationBasedBulkCreateCustomResource resource) : ISubmittedBatch
    {
        public string OperationId => operationId;
        public int SubmissionStatus => submissionStatus;

        public async Task<BatchSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            // Poll the custom endpoint's pageable VM results directly, not the async-operation store.
            var results = new List<ComputeBulkOperationResult>();
            string? statusError = null;
            try
            {
                await foreach (var item in resource.VirtualMachinesGetOperationStatusAsync(cancellationToken))
                    results.Add(item);
            }
            catch (RequestFailedException ex) when (IsNotYetVisible(ex))
            {
                statusError = ex.ErrorCode;
            }
            BulkCreateCustomProperties? data = null;
            try
            {
                data = (await resource.GetAsync(cancellationToken)).Value.Data.Properties;
            }
            catch (RequestFailedException ex) when (IsNotYetVisible(ex))
            {
                statusError ??= ex.ErrorCode;
            }
            var state = data?.ProvisioningState?.ToString() ?? "Unknown";
            var completed = state is "Succeeded" or "Failed" or "Canceled";
            var failed = state is "Failed" or "Canceled";
            return new BatchSnapshot(completed, failed, state, data?.PartialFulfillmentPolicy?.FulfilledCapacity, results,
                StatusAvailable: statusError is null,
                ErrorCode: results.Select(item => item.ErrorCode ?? item.Operation?.Error?.ErrorCode).FirstOrDefault(code => code is not null)
                    ?? statusError,
                Observation: statusError is null ? null
                    : "Operation resource or per-VM status is not yet visible (HTTP 404); retrying observation only within the polling deadline.");
        }

        private static bool IsNotYetVisible(RequestFailedException ex) =>
            ex.Status == 404 && ex.ErrorCode is "BulkActionNotFoundException" or "ResourceNotFound";
    }
}

internal sealed class DemoLog(TextWriter writer, string password, TextWriter? fileWriter = null)
{
    public void Write(string message)
    {
        // Raw request bodies and exception messages may contain credentials.
        var safe = message.Replace(password, "[REDACTED]", StringComparison.Ordinal);
        safe = new string(safe.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        lock (writer)
            WriteLine(safe);
    }

    public void WriteRequest(BatchRequest batch)
    {
        var json = JsonNode.Parse(ModelReaderWriter.Write(batch.Data, new ModelReaderWriterOptions("W")).ToString())
            ?? throw new InvalidDataException("SDK request serialization returned no JSON.");
        Redact(json);
        lock (writer)
        {
            WriteLine($"Batch {batch.Label} request JSON (secrets redacted):");
            WriteLine(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void WriteLine(string text)
    {
        writer.WriteLine(text);
        fileWriter?.WriteLine(text);
        fileWriter?.Flush();
    }

    private void Redact(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (key.ToLowerInvariant() is "adminpassword" or "password" or "secrets"
                    or "protectedsettings" or "protectedsettingsfromkeyvault" or "customdata" or "userdata")
                    obj[key] = "[REDACTED]";
                else if (value is not null)
                    Redact(value);
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                if (array[i] is { } value)
                    Redact(value);
        }
        else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Contains(password, StringComparison.Ordinal))
        {
            node.ReplaceWith(JsonValue.Create(text.Replace(password, "[REDACTED]", StringComparison.Ordinal)));
        }
    }
}

internal sealed record OutcomeCounts(int Succeeded, int Failed, int Cancelled, int Pending, int Unreported)
{
    public static OutcomeCounts From(BatchRequest batch, BatchSnapshot? snapshot)
    {
        var results = snapshot?.Results ?? [];
        var names = results.Select(r => r.ResourceId?.Name).ToArray();
        if (names.Any(n => n is null || !batch.ComputerNames.ContainsKey(n))
            || results.Any(r => !string.Equals(r.ResourceId.ToString(), batch.VmResourceIdPrefix + r.ResourceId.Name,
                StringComparison.OrdinalIgnoreCase))
            || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidDataException("Status contains duplicate, missing or unexpected resource IDs.");
        var succeeded = results.Count(r => r.Operation?.State == BulkActionOperationState.Succeeded
            && r.ErrorCode is null && r.Operation.Error is null);
        var failed = results.Count(r => r.Operation?.State == BulkActionOperationState.Failed
            || r.ErrorCode is not null || r.Operation?.Error is not null);
        var cancelled = results.Count(r => r.Operation?.State == BulkActionOperationState.Cancelled
            && r.ErrorCode is null && r.Operation.Error is null);
        return new(succeeded, failed, cancelled, results.Count - succeeded - failed - cancelled,
            batch.Data.Properties.Capacity - results.Count);
    }
}

internal static class BulkCreateDemo
{
    public static async Task<int> RunAsync(IBulkCreateClient client, BatchRequest[] batches,
        DemoLog log, TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        foreach (var batch in batches)
            log.WriteRequest(batch);

        // Start all selected batches before awaiting completion; one failure does not cancel another.
        var tasks = batches.Select(batch => RunBatchAsync(client, batch, log, timeout, pollInterval, cancellationToken)).ToArray();
        var outcomes = await Task.WhenAll(tasks);
        log.Write($"Overall: requested={batches.Sum(batch => batch.Data.Properties.Capacity)}; " +
            $"fully successful batches={outcomes.Count(x => x)}/{batches.Length}.");
        return outcomes.All(x => x) ? 0 : 1;
    }

    private static async Task<bool> RunBatchAsync(IBulkCreateClient client, BatchRequest batch,
        DemoLog log, TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var capacity = batch.Data.Properties.Capacity;
        log.Write($"Batch {batch.Label}: requested={capacity}; correlation={batch.CorrelationId}; resource={client.ResourceId(batch)}");
        foreach (var (size, disk) in batch.ExpectedDisks)
            log.Write($"Batch {batch.Label}: expected OS disk for allocated {size} = {disk} GiB (not observed).");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        BatchSnapshot? snapshot = null;
        var accepted = false;
        var success = false;
        try
        {
            var submitted = await client.SubmitAsync(batch, deadline.Token);
            accepted = true;
            log.Write($"Batch {batch.Label}: submission accepted; HTTP={submitted.SubmissionStatus}; operation={submitted.OperationId}. " +
                "This is the initial submission response, not VM completion.");
            while (true)
            {
                try
                {
                    snapshot = await submitted.RefreshAsync(deadline.Token);
                }
                catch (RequestFailedException ex) when (ex.Status is 408 or 429 or 500 or 502 or 503 or 504)
                {
                    log.Write($"Batch {batch.Label}: status observation temporarily unavailable; HTTP={ex.Status}; " +
                        $"code={ex.ErrorCode ?? "unspecified"}. Retrying observation only; no create resubmission.");
                    await Task.Delay(pollInterval, deadline.Token);
                    continue;
                }
                var counts = OutcomeCounts.From(batch, snapshot);
                log.Write($"Batch {batch.Label}: state={snapshot.ProvisioningState}; succeeded={counts.Succeeded}; failed={counts.Failed}; " +
                    $"cancelled={counts.Cancelled}; pending={counts.Pending}; unreported={counts.Unreported}; " +
                    $"fulfilledCapacity={snapshot.FulfilledCapacity?.ToString() ?? "not supplied"}");
                if (snapshot.Observation is not null)
                    log.Write($"Batch {batch.Label}: {snapshot.Observation}");
                if (snapshot.ErrorCode is not null)
                    log.Write($"Batch {batch.Label}: operation/status errorCode={snapshot.ErrorCode}");
                if (snapshot.OperationCompleted && (snapshot.OperationFailed || snapshot.ProvisioningState is "Failed" or "Canceled"))
                    break;
                if (snapshot.OperationCompleted && snapshot.StatusAvailable && counts.Pending == 0 && counts.Unreported == 0
                    && (snapshot.ProvisioningState == "Succeeded" || counts.Failed > 0 || counts.Cancelled > 0))
                {
                    success = snapshot.ProvisioningState == "Succeeded" && counts.Succeeded == capacity;
                    break;
                }
                await Task.Delay(pollInterval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            log.Write($"Batch {batch.Label}: {(cancellationToken.IsCancellationRequested ? "local cancellation" : "poll timeout")}; " +
                "Azure operations are NOT cancelled and may continue creating billable resources.");
        }
        catch (OperationCanceledException)
        {
            log.Write($"Batch {batch.Label}: transport request timed out; service acceptance may be unknown. No resubmission.");
        }
        catch (RequestFailedException ex)
        {
            log.Write($"Batch {batch.Label}: Azure request failed; HTTP={ex.Status}; code={ex.ErrorCode ?? "unspecified"}. " +
                "No automatic resubmission; inspect the operation resource for any accepted work.");
        }
        catch (AuthenticationFailedException)
        {
            log.Write($"Batch {batch.Label}: authentication failed; details omitted to protect credentials.");
        }
        catch (HttpRequestException)
        {
            log.Write($"Batch {batch.Label}: transport failure; service acceptance may be unknown.");
        }
        catch (InvalidDataException)
        {
            log.Write($"Batch {batch.Label}: invalid status response; cannot account for all requested VMs.");
            snapshot = null;
        }
        finally
        {
            var counts = OutcomeCounts.From(batch, snapshot);
            log.Write($"Batch {batch.Label} final: accepted={accepted}; requested={capacity}; succeeded={counts.Succeeded}; " +
                $"failed={counts.Failed}; cancelled={counts.Cancelled}; pending={counts.Pending}; unreported={counts.Unreported}; " +
                $"completeSuccess={success}. Unreported entries are NOT assumed failed, rejected, or successful.");
            foreach (var item in snapshot?.Results ?? [])
            {
                var name = item.ResourceId.Name;
                var size = item.VirtualMachineInfo?.VmSize;
                var expectedDisk = size is not null && batch.ExpectedDisks.TryGetValue(size, out var disk)
                    ? disk.ToString() : "unknown (allocated size not reported)";
                log.Write($"Batch {batch.Label}: resource={item.ResourceId}; state={item.Operation?.State?.ToString() ?? "Unknown"}; " +
                    $"errorCode={item.ErrorCode ?? item.Operation?.Error?.ErrorCode ?? "not supplied"}; computerName(expected)={batch.ComputerNames[name]}; " +
                    $"size={size ?? "not reported"}; zone={item.VirtualMachineInfo?.Zone ?? "not reported"}; " +
                    $"osDiskGiB(expected)={expectedDisk}; " +
                    $"errorDetails={item.ErrorDetails ?? item.Operation?.Error?.ErrorDetails ?? "not supplied"}");
            }
        }
        return success;
    }
}
