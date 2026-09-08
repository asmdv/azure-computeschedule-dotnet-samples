using System.Text.Json;
using Azure.Identity;
using Azure.ResourceManager;
using BulkCreateCustom;

if (args is ["--validate"])
    return await LocalValidation.RunAsync();

if (args.Length != 0 && args is not ["--config", _])
{
    Console.Error.WriteLine("Usage: no arguments (local config.json), --config <path>, or --validate (offline only). Normal execution creates 100 billable VMs (Batch A only).");
    return 2;
}

try
{
    var config = DemoConfig.Load(args.Length == 0 ? DemoConfig.DefaultPath : args[1]);
    var password = Environment.GetEnvironmentVariable("BULK_VM_ADMIN_PASSWORD")
        ?? throw new ArgumentException("Set BULK_VM_ADMIN_PASSWORD through a secure environment, not the configuration file.");
    var batches = BulkCreateRequestBuilder.Build(config, password);
    var logPath = Path.Combine(AppContext.BaseDirectory, $"bulk-create-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
    using var logFile = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    var log = new DemoLog(Console.Out, password, logFile);
    log.Write($"Log file: {logPath}");
    log.Write("Creating 100 VMs with per-VM overrides (Batch A only). Batch B is disabled. No resource cleanup is performed by this sample.");
    var options = new ArmClientOptions();
    options.Diagnostics.IsLoggingContentEnabled = false;
    options.Retry.MaxRetries = 0; // Do not replay create requests after ambiguous transport failures.
    var client = new SdkBulkCreateClient(new ArmClient(new DefaultAzureCredential(), config.SubscriptionId, options), config);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    return await BulkCreateDemo.RunAsync(client, batches, log, TimeSpan.FromMinutes(config.PollTimeoutMinutes),
        TimeSpan.FromSeconds(10), cancellation.Token);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (JsonException)
{
    Console.Error.WriteLine("Invalid configuration JSON; consult config.example.json. Values omitted for safety.");
    return 2;
}
catch (FileNotFoundException)
{
    Console.Error.WriteLine("Configuration file not found. Create src\\BulkCreateCustom\\config.json from config.example.json and rebuild, or supply --config <path>.");
    return 2;
}
catch (IOException)
{
    Console.Error.WriteLine("Configuration or log file I/O failed. No automatic resubmission; if submission started, Azure operations may still be running.");
    return 2;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("Configuration or log file access denied.");
    return 2;
}
