using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Compute.BulkActions;
using Azure.ResourceManager.Compute.BulkActions.Models;

namespace BulkCreateCustom;

internal static class LocalValidation
{
    // Synthetic fixture only. Never used with a real Azure client.
    private const string FixturePassword = "Offline-Only!73xQ";

    public static async Task<int> RunAsync()
    {
        Check(DemoConfig.DefaultPath == Path.Combine(AppContext.BaseDirectory, "config.json")
            && Path.IsPathFullyQualified(DemoConfig.DefaultPath), "default config is beside the executable, independent of the working directory");
        var config = new DemoConfig
        {
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceGroup = "offline-rg", Region = "eastus",
            SubnetId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/offline-rg/providers/Microsoft.Network/virtualNetworks/offline-vnet/subnets/default",
            Zones = ["1", "2", "3"], RunPrefix = "abcdefghijkl", AdminUsername = "bulkoperator",
            ImageVersion = "20348.0.0", ImageMinimumOSDiskGB = 127
        };
        var firstOnly = BulkCreateRequestBuilder.Build(config, FixturePassword);
        Check(firstOnly is [{ Label: "a" }] && firstOnly[0].Data.Properties.Capacity == 100,
            "normal run builds only Batch A with 100 VMs");
        await ScenarioAsync(firstOnly, "success", expectedExit: 0);
        await ScenarioAsync(firstOnly, "submission-failure", expectedExit: 1);
        var batches = BulkCreateRequestBuilder.Build(config, FixturePassword, includePerSizeBatch: true);
        var logPath = Path.Combine(Path.GetTempPath(), $"bulk-create-validation-{Guid.NewGuid():N}.log");
        try
        {
            using var console = new StringWriter();
            using (var file = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
            {
                var log = new DemoLog(console, FixturePassword, file);
                log.WriteRequest(batches[0]);
                log.Write($"Sensitive test text: {FixturePassword}");
                await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => log.Write($"Status {i}"))));
                using var reader = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                Check(reader.ReadToEnd() == console.ToString(), "file log matches console and flushes before close");
            }
            var persisted = File.ReadAllText(logPath);
            Check(persisted == console.ToString() && !persisted.Contains(FixturePassword)
                && persisted.Contains("[REDACTED]"), "redacted log persists after disposal");
        }
        finally
        {
            File.Delete(logPath);
        }
        foreach (var batch in batches)
        {
            using var output = new StringWriter();
            new DemoLog(output, FixturePassword).WriteRequest(batch);
            var printed = output.ToString();
            var actual = System.Text.Json.Nodes.JsonNode.Parse(printed[(printed.IndexOf('\n') + 1)..])!;
            var expected = System.Text.Json.Nodes.JsonNode.Parse(
                ModelReaderWriter.Write(batch.Data, new ModelReaderWriterOptions("W")).ToString())!;
            expected["properties"]!["computeProfile"]!["virtualMachineProfile"]!["osProfile"]!["adminPassword"] = "[REDACTED]";
            Check(System.Text.Json.Nodes.JsonNode.DeepEquals(actual, expected), "printed JSON matches the final wire body except secrets");
            Check(!printed.Contains(FixturePassword), "printed JSON redacts the password");
            Check(batch.Data.Properties.ComputeProfile.VirtualMachineProfile.OSProfile.AdminPassword == FixturePassword,
                "printing does not modify the submitted credentials");
        }
        const string escapedPassword = "Offline-\"\\Only!73xQ";
        var sensitiveBatch = BulkCreateRequestBuilder.Build(config, escapedPassword)[0];
        sensitiveBatch.Data.Properties.ComputeProfile.VirtualMachineProfile.OSProfile.CustomData = "private-bootstrap";
        sensitiveBatch.Data.Properties.OverridesProfile.Overrides[0].VirtualMachineProfile.OSProfile.AdminPassword = "different-override-password";
        sensitiveBatch.Data.Tags.Add("redaction-check", escapedPassword);
        using (var output = new StringWriter())
        {
            new DemoLog(output, escapedPassword).WriteRequest(sensitiveBatch);
            var printed = output.ToString();
            var json = System.Text.Json.Nodes.JsonNode.Parse(printed[(printed.IndexOf('\n') + 1)..])!;
            Check(json["tags"]!["redaction-check"]!.GetValue<string>() == "[REDACTED]"
                && json["properties"]!["computeProfile"]!["virtualMachineProfile"]!["osProfile"]!["adminPassword"]!.GetValue<string>() == "[REDACTED]",
                "JSON-escaped passwords and password copies are redacted");
            Check(!printed.Contains("private-bootstrap") && !printed.Contains("different-override-password"),
                "nested override and bootstrap secrets are redacted");
        }
        var allNames = batches.SelectMany(b => b.ComputerNames).ToArray();
        Check(allNames.Length == 150 && allNames.Select(p => p.Key).Distinct().Count() == 150
            && allNames.Select(p => p.Value).Distinct().Count() == 150, "150 unique resource and computer names");
        Check(allNames.All(p => p.Key.Length <= 64 && Regex.IsMatch(p.Value, "^[a-z][a-z0-9]{14}$")),
            "Windows name boundary and suffix preservation");
        Check(batches[0].CorrelationId != batches[1].CorrelationId
            && batches[0].OperationName != batches[1].OperationName, "independent operation identities");
        Check(batches.All(b => Guid.TryParseExact(b.OperationName, "D", out _)), "operation path names must be UUIDs");
        foreach (var (batch, index) in batches.Select((b, i) => (b, i)))
        {
            using var json = JsonDocument.Parse(ModelReaderWriter.Write(batch.Data, new ModelReaderWriterOptions("W")));
            Check(json.RootElement.GetProperty("zones").EnumerateArray().Select(z => z.GetString()).SequenceEqual(config.Zones),
                "top-level zones define allowed placement");
            var properties = json.RootElement.GetProperty("properties");
            Check(properties.GetProperty("capacity").GetInt32() == (index == 0 ? 100 : 50), "exact capacity");
            Check(properties.GetProperty("capacityType").GetString() == "VM", "VM rather than vCPU capacity");
            Check(properties.GetProperty("priorityProfile").GetProperty("allocationStrategy").GetString() == "Prioritized",
                "prioritized allocation");
            var zones = properties.GetProperty("zoneAllocationPolicy");
            Check(zones.GetProperty("distributionStrategy").GetString() == "BestEffortBalanced"
                && !zones.TryGetProperty("zonePreferences", out _), "balanced zone allocation without prioritized preferences");
            var retry = properties.GetProperty("executionParameters").GetProperty("retryPolicy");
            Check(retry.GetProperty("retryWindowInMinutes").GetInt32() == 5
                && !retry.TryGetProperty("retryCount", out _), "five-minute service retry without a count override");
            var baseProfile = properties.GetProperty("computeProfile").GetProperty("virtualMachineProfile");
            Check(!baseProfile.TryGetProperty("hardwareProfile", out _) && !baseProfile.TryGetProperty("zones", out _),
                "no fixed VM size or zone");
            Check(baseProfile.GetProperty("osProfile").GetProperty("adminPassword").GetString() == FixturePassword,
                "credential only in in-memory request");
            var overridesProfile = properties.GetProperty("overridesProfile");
            Check(!overridesProfile.TryGetProperty("virtualMachineNamePrefix", out _), "no prefix with explicit names");
            var entries = overridesProfile.GetProperty("overrides");
            Check(entries.GetArrayLength() == batch.Data.Properties.Capacity, "required identity count");
            foreach (var entry in entries.EnumerateArray())
            {
                var name = entry.GetProperty("virtualMachineName").GetString()!;
                var profile = entry.GetProperty("virtualMachineProfile");
                Check(profile.GetProperty("osProfile").GetProperty("computerName").GetString() == batch.ComputerNames[name],
                    "explicit per-VM identity");
                Check(!profile.TryGetProperty("storageProfile", out _) && !profile.TryGetProperty("hardwareProfile", out _),
                    "per-VM entries cannot mask disk or allocation");
            }
            var sizes = properties.GetProperty("vmSizesProfile");
            Check(sizes.GetArrayLength() == config.Sizes.Length, "size-keyed override count");
            foreach (var (size, rank) in sizes.EnumerateArray().Select((s, i) => (s, i)))
            {
                Check(size.GetProperty("rank").GetInt32() == rank, "explicit ranks");
                if (index == 0)
                    Check(!size.TryGetProperty("override", out _), "batch A has no per-size override");
                else
                {
                    var disk = size.GetProperty("override").GetProperty("virtualMachineProfile")
                        .GetProperty("storageProfile").GetProperty("osDisk");
                    Check(disk.GetProperty("diskSizeGB").GetInt32() == batch.ExpectedDisks[size.GetProperty("name").GetString()!],
                        "native size-keyed disk map");
                    Check(disk.GetProperty("createOption").GetString() == "FromImage", "required disk create option");
                }
            }
        }

        var invalidPasswordRejected = false;
        try { config.Validate("short"); }
        catch (ArgumentException) { invalidPasswordRejected = true; }
        Check(invalidPasswordRejected, "invalid credentials rejected before submission");
        var invalidConfigRejected = false;
        try { new DemoConfig().Validate(FixturePassword); }
        catch (ArgumentException) { invalidConfigRejected = true; }
        Check(invalidConfigRejected, "missing configuration rejected before submission");
        var tenSizes = new[] { "D4s", "D8s", "E4s", "E8s", "D4ds", "D8ds", "D4as", "D8as", "D4ads", "D8ads" }
            .Select((size, rank) => new SizeCandidate($"Standard_{size}_v5", rank, 256 + rank * 64)).ToArray();
        var configJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(config))!;
        configJson[nameof(DemoConfig.Sizes)] = JsonSerializer.SerializeToNode(tenSizes);
        var tenConfig = configJson.Deserialize<DemoConfig>()!;
        var tenBatches = BulkCreateRequestBuilder.Build(tenConfig, FixturePassword, includePerSizeBatch: true);
        Check(tenBatches.All(b => b.Data.Properties.VmSizesProfile.Count == 10), "ten eligible 4/8-core candidates supported");

        await ScenarioAsync(batches, "success", expectedExit: 0);
        await ScenarioAsync(batches, "submission-failure", expectedExit: 1);
        await ScenarioAsync(batches, "partial", expectedExit: 1);
        await ScenarioAsync(batches, "poll-failure", expectedExit: 1);
        await ScenarioAsync(batches, "timeout", expectedExit: 1);
        await ScenarioAsync(batches, "cancellation", expectedExit: 1);
        await ScenarioAsync(batches, "invalid-status", expectedExit: 1);
        await SdkTransportValidation.RunAsync(config, batches, FixturePassword);
        Console.WriteLine("Offline validation passed: serialized 100+50 requests, native size overrides, naming, " +
            "ranks/zones/retry, concurrent acceptance, sibling failure isolation, partial outcomes, timeout/cancellation and redaction. No Azure calls.");
        return 0;
    }

    private static async Task ScenarioAsync(BatchRequest[] batches, string scenario, int expectedExit)
    {
        using var output = new StringWriter();
        var log = new DemoLog(output, FixturePassword);
        log.Write($"Synthetic service text: {FixturePassword}");
        var fake = new FakeClient(scenario, output, batches);
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancellation")
            cancellation.Cancel();
        var exit = await BulkCreateDemo.RunAsync(fake, batches, log,
            scenario == "timeout" ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(1), cancellation.Token);
        Check(exit == expectedExit, $"{scenario} exit status");
        Check(fake.Submissions == batches.Length, $"{scenario}: only selected submissions started");
        var text = output.ToString();
        Check(batches.All(batch => text.Contains($"Batch {batch.Label} final:")), $"{scenario}: all selected outcomes reported");
        if (batches.Length == 1)
            Check(!text.Contains("Batch b") && text.Contains("Overall: requested=100;")
                && text.Contains(expectedExit == 0 ? "fully successful batches=1/1" : "fully successful batches=0/1"),
                "single-batch output excludes B and reports correct total");
        Check(!text.Contains(FixturePassword) && text.Contains("[REDACTED]"), "secret redaction");
        if (batches.Length == 2 && scenario is "submission-failure" or "poll-failure")
            Check(text.Contains("Batch b final: accepted=True; requested=50; succeeded=50"), "accepted sibling fully observed");
        if (scenario == "partial")
        {
            Check(text.Contains("succeeded=97; failed=1; cancelled=1; pending=0; unreported=1"), "partial accounting");
            Check(text.Contains("state=Failed; errorCode=InternalExecutionError;"),
                "nested VM-operation error code is included when top-level error is absent");
            Check(text.Contains("errorDetails=An internal execution error occurred. [REDACTED]"),
                "nested error details are printed with password redaction");
        }
        if (scenario is "timeout" or "cancellation")
            Check(text.Contains("Azure operations are NOT cancelled"), "local cancellation is not Azure cancellation");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"Offline validation failed: {label}");
    }

    private sealed class FakeClient(string scenario, StringWriter output, BatchRequest[] batches) : IBulkCreateClient
    {
        private readonly TaskCompletionSource bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int submissions;
        public int Submissions => submissions;
        public string ResourceId(BatchRequest batch) => $"/offline/operations/{batch.OperationName}";

        public async Task<ISubmittedBatch> SubmitAsync(BatchRequest batch, CancellationToken cancellationToken)
        {
            Check(batches.All(item => output.ToString().Contains($"Batch {item.Label} request JSON (secrets redacted):")),
                "selected request bodies printed before submission");
            if (Interlocked.Increment(ref submissions) == batches.Length)
                bothStarted.SetResult();
            // Serial submission deadlocks here until the test deadline and fails the success scenario.
            await bothStarted.Task.WaitAsync(cancellationToken);
            if (scenario == "submission-failure" && batch.Label == "a")
                throw new RequestFailedException(409, FixturePassword, "CapacityUnavailable", null);
            return new FakeBatch(batch, scenario);
        }
    }

    private sealed class FakeBatch(BatchRequest batch, string scenario) : ISubmittedBatch
    {
        public string OperationId => $"offline-{batch.Label}";
        public int SubmissionStatus => 201;
        public async Task<BatchSnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            if (scenario == "timeout")
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (scenario == "poll-failure" && batch.Label == "a")
                throw new RequestFailedException(503, FixturePassword);
            var results = batch.ComputerNames.Keys.Select(name =>
            {
                var id = new ResourceIdentifier($"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/offline-rg/providers/Microsoft.Compute/virtualMachines/{name}");
                return ArmComputeBulkActionsModelFactory.ComputeBulkOperationResult(resourceId: id,
                    operation: ArmComputeBulkActionsModelFactory.ComputeBulkOperationDetails(
                        operationId: Guid.NewGuid().ToString(), state: BulkActionOperationState.Succeeded, resourceContext: null),
                    virtualMachineInfo: ArmComputeBulkActionsModelFactory.VirtualMachineInfo(
                        vmSize: batch.ExpectedDisks.Keys.First(), zone: "1"));
            }).ToList();
            if (scenario == "partial" && batch.Label == "a")
            {
                results.RemoveAt(results.Count - 1);
                results[0] = ArmComputeBulkActionsModelFactory.ComputeBulkOperationResult(
                    resourceId: results[0].ResourceId, operation: ArmComputeBulkActionsModelFactory.ComputeBulkOperationDetails(
                        operationId: "failed", state: BulkActionOperationState.Failed, resourceContext: null,
                        error: ArmComputeBulkActionsModelFactory.ComputeBulkOperationError(
                            errorCode: "InternalExecutionError",
                            errorDetails: $"An internal execution error occurred. {FixturePassword}")), virtualMachineInfo: null);
                results[1] = ArmComputeBulkActionsModelFactory.ComputeBulkOperationResult(
                    resourceId: results[1].ResourceId, operation: ArmComputeBulkActionsModelFactory.ComputeBulkOperationDetails(
                        operationId: "cancelled", state: BulkActionOperationState.Cancelled, resourceContext: null), virtualMachineInfo: null);
            }
            if (scenario == "invalid-status")
                results.Add(results[0]);
            return new BatchSnapshot(true, scenario == "partial", scenario == "partial" ? "Failed" : "Succeeded",
                results.Count, results);
        }
    }
}
