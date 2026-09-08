# Azure Compute Schedule .NET Samples

This repository contains .NET samples for Azure Compute Schedule operations such as create, start, deallocate, delete, hibernate, and Flex create.

The samples are intentionally small and focused. Each project folder represents a self-contained sample and may use its own configuration pattern depending on the scenario it demonstrates.

## Projects

| Project | What it demonstrates |
|---|---|
| `ExecuteCreate` | Standard VM create flow with network and disk setup |
| `ExecuteVDICreateFlex` | VDI Flex create API sample flow |
| `BulkCreateCustom` | 100-VM per-VM override request using the BulkActions SDK; per-size batch currently disabled |
| `ExecuteStart` | Start existing VMs |
| `ExecuteDeallocate` | Deallocate existing VMs |
| `ExecuteDelete` | Delete existing VMs |
| `ExecuteHibernate` | Hibernate existing VMs |
| `OperationFallback` | Retry and fallback scenarios for start, hibernate, and create operations |
| `AllScenarios` | Combined example flow using shared helpers |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An Azure subscription
- An existing Azure resource group
- Azure CLI installed and signed in with `az login`
- NuGet feeds configured through `src/NuGet.config`

`DefaultAzureCredential` is used throughout the samples, so `az login` is the simplest way to authenticate locally.

## Quick Start

### 1. Clone the repository

```bash
git clone https://github.com/Azure/azure-computeschedule-dotnet-samples.git
cd azure-computeschedule-dotnet-samples
```

### 2. Sign in to Azure

```bash
az login
```

### 3. Choose and configure a sample

Review the project folder for the sample you want to run and apply the configuration model documented there.

Depending on the sample, that may involve:

- setting environment variables or a local `.env` file
- updating `appsettings.json`
- updating sample values in `Program.cs`
- replacing sample VM names, resource IDs, subscription IDs, or resource group names

Sample-specific documentation should be treated as the source of truth for configuration.

## Build

From the repository root:

```bash
dotnet build src/azure-computeschedule-dotnet-samples.sln
```

From `src`:

```bash
dotnet build
```

## Run Samples

### From the repository root

Use the project file path for whichever sample you want to run:

```bash
dotnet run --project src/ExecuteCreate/ExecuteCreate.csproj
dotnet run --project src/ExecuteVDICreateFlex/ExecuteVDICreateFlex.csproj -- --api-demo --resource-count 5
dotnet run --project src/ExecuteVDICreateFlex/ExecuteVDICreateFlex.csproj -- --api-demo-json-string --resource-count 5
dotnet run --project src/ExecuteStart/ExecuteStart.csproj
dotnet run --project src/ExecuteDeallocate/ExecuteDeallocate.csproj
dotnet run --project src/ExecuteDelete/ExecuteDelete.csproj
dotnet run --project src/ExecuteHibernate/ExecuteHibernate.csproj
dotnet run --project src/OperationFallback/OperationFallback.csproj
dotnet run --project src/AllScenarios/AllScenarios.csproj
```

### From `src`

```bash
dotnet run --project ./ExecuteCreate/ExecuteCreate.csproj
dotnet run --project ./ExecuteVDICreateFlex/ExecuteVDICreateFlex.csproj -- --api-demo --resource-count 5
dotnet run --project ./ExecuteVDICreateFlex/ExecuteVDICreateFlex.csproj -- --api-demo-json-string --resource-count 5
dotnet run --project ./ExecuteStart/ExecuteStart.csproj
dotnet run --project ./ExecuteDeallocate/ExecuteDeallocate.csproj
dotnet run --project ./ExecuteDelete/ExecuteDelete.csproj
dotnet run --project ./ExecuteHibernate/ExecuteHibernate.csproj
dotnet run --project ./OperationFallback/OperationFallback.csproj
dotnet run --project ./AllScenarios/AllScenarios.csproj
```

### From an individual project directory

In any sample folder under `src/<ProjectName>`, you can usually run:

```bash
dotnet run
```

Some projects support additional command-line arguments. For example, `ExecuteVDICreateFlex` can be run with:

```bash
dotnet run -- --api-demo --resource-count 5
dotnet run -- --api-demo-json-string --resource-count 5
```

## Sample Notes

For detailed setup, configuration, and usage instructions, check the documentation and source files in the folder for the sample you want to run.

[`BulkCreateCustom`](./src/BulkCreateCustom/README.md) is a separate .NET 10 sample.
`dotnet run --project src\BulkCreateCustom\BulkCreateCustom.csproj -- --validate`
performs offline checks; normal execution reads the sample's local `config.json`
and creates 100 billable VMs (Batch A only). `--config <path>` optionally overrides the file.

## Project Structure

```text
src/
├── Common/
├── ExecuteCreate/
├── ExecuteVDICreateFlex/
├── ExecuteStart/
├── ExecuteDeallocate/
├── ExecuteDelete/
├── ExecuteHibernate/
├── OperationFallback/
├── AllScenarios/
├── NuGet.config
└── azure-computeschedule-dotnet-samples.sln
```

## Documentation

- [src/ExecuteVDICreateFlex/README.md](./src/ExecuteVDICreateFlex/README.md): setup and run instructions for the ExecuteVDICreateFlex sample
- [src/ExecuteVDICreateFlex/rest-api-documentation.md](./src/ExecuteVDICreateFlex/rest-api-documentation.md): ExecuteVDICreateFlex request and response reference
- [src/ExecuteDeallocate/docs/deallocate-preempts-start.md](./src/ExecuteDeallocate/docs/deallocate-preempts-start.md): behavior and API semantics when deallocate preempts a pending or in-progress start
- [src/OperationFallback/README.md](./src/OperationFallback/README.md): retry policy and `onFailureAction` fallback scenarios

## Shared Code

The legacy sample projects reference `src/Common`, which contains the shared helper layer used across the repository. `BulkCreateCustom` is isolated and uses its own pinned BulkActions SDK:

- `ComputescheduleOperations.cs`: common create, start, deallocate, delete, and hibernate operation flows
- `HelperMethods.cs`: resource helpers, request builders, VNet creation, data disk creation, and operation polling
- `ConsoleProgressRenderer.cs`: single-line progress updates for longer-running flows
- `SetHeaderPolicy.cs`: example of adding a custom ARM pipeline header policy