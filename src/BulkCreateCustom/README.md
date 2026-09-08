# BulkCreateCustom: Batch A only (100 VMs)

This standalone .NET 10 sample pins `Azure.ResourceManager.Compute.BulkActions`
**1.2.0-beta.2**. It does not reference `Common` or change the legacy samples.
Normal execution **creates 100 billable Windows VMs (Batch A only)**, their NICs,
and managed OS disks in an existing resource group/subnet. It performs no cleanup.
Only `--validate` is offline.

For a direct HTTP version of Batch A, open the [Bruno collection](bruno/README.md).
It uses the same request shape and does not send anything until you click Send.

Batch B is currently disabled: it is not built, printed or submitted during a
normal run. Its per-size example remains in the builder and offline checks for
later use. The comparison below describes both implemented examples, not two
requests sent by the current entry point.

## Request shape

| Batch | Capacity (`VM`, not vCPU) | Overrides |
|---|---:|---|
| A | 100 | Explicit unique VM resource names and Windows computer names |
| B (disabled) | 50 | Native `vmSizesProfile[].override.virtualMachineProfile.storageProfile.osDisk` settings, selected by allocated VM size |

**Batch B also has 50 identity-only per-VM entries.** The custom API requires
`overridesProfile.overrides.Count == capacity`; this is not 50 repeated disk
overrides. Both batches explicitly name all VMs, so `virtualMachineNamePrefix`
is omitted (the service rejects it when all entries are named).

`BulkCreateRequestBuilder.cs` shows the common profile followed by the per-size
and per-VM overlays. The precedence is **base < per-size < per-VM**. Neither the
base profile nor any override sets VM size, zone, or VM priority: allocation owns
these. Per-VM entries contain only name/computer-name fields, leaving credentials,
network, image and size-specific disk settings inherited from lower layers.

Both requests use regular priority, **Prioritized** VM-size allocation with
explicit contiguous ranks starting at zero, and **BestEffortBalanced** zones.
Configured zones are set on the request's top-level `zones` array. No
prioritized-only zone preferences are set. Balancing is best-effort, not an
equal-per-zone guarantee.

Default examples (confirm availability before a live run):

| Rank | Candidate | vCPUs | Batch B OS disk |
|---:|---|---:|---:|
| 0 | Standard_D4s_v5 | 4 | 256 GiB |
| 1 | Standard_D8s_v5 | 8 | 320 GiB |
| 2 | Standard_E4s_v5 | 4 | 384 GiB |
| 3 | Standard_E8s_v5 | 8 | 448 GiB |

The allocator need not select every candidate. An observed VM's size determines
which disk override should apply. Batch A uses `imageMinimumOSDiskGB` throughout.
The sample allows up to ten distinct 4/8-vCPU D/E `s`, `ds`, `as` or `ads` v5
size candidates and validates distinct disks above the verified image minimum.
For example, the four defaults plus D4ds, D8ds, D4as, D8as, D4ads and D8ads v5
form a ten-entry configuration; each must be eligible in your environment.
**Ten is a sample safety cap, not a verified service maximum.** Adding other
families requires reviewing image, architecture, generation and disk
compatibility and adjusting validation. No 10,000-VM capacity/concurrency
guarantee is made.

The service retry policy contains **only `retryWindowInMinutes: 5`**.
This uses a five-minute retry window; no optional retry count or failure action
is added. The SDK's optional integer does not itself validate service-side
limits. Regional service acceptance remains a live-run prerequisite.

## Configuration and prerequisites

Edit `src\BulkCreateCustom\config.json`, replacing every placeholder. This local
file is gitignored. On a fresh clone, create it from the example:

```powershell
Copy-Item src\BulkCreateCustom\config.example.json src\BulkCreateCustom\config.json
```

With no arguments, the sample reads `config.json` from its own executable folder
using `AppContext.BaseDirectory`, not from `$HOME` or the terminal's working
directory. Building/running copies the sample folder's `config.json` beside the
executable; publishing also copies it. Rebuild after editing configuration if
using `--no-build` or launching the compiled executable directly.
An explicit `--config <path>` still overrides the default.
Unknown JSON properties are rejected; there is deliberately no password field.
No `.env` file is automatically loaded.

Before a separately authorized live run, confirm an enabled subscription/region
for the custom endpoint, provider registration and RBAC for the operation,
VMs, disks and NICs, and subnet join permission. The VM resource group and subnet
must already exist in the selected region; this sample restricts the subnet to
the same subscription. Confirm the configured zones and every size are eligible
for that subscription, region and zone combination.

Reserve at least 100 free subnet IP addresses (in addition to Azure's reserved
addresses and existing usage), sufficient regional and VM-family quota for the
selected mix (up to 800 vCPUs for 100 eight-core VMs), disk/NIC quota, budget
and capacity. Quota does not guarantee capacity. Partial fulfillment is explicitly
disabled, but later individual provisioning failures can still leave resources.

The fixed image is `MicrosoftWindowsServer:WindowsServer:2022-datacenter-azure-edition`.
Set `imageVersion` to a real, verified version rather than `latest`, and set
`imageMinimumOSDiskGB` to its verified minimum (the example's 127 GiB is not an
image metadata lookup). All per-size disks must be larger than that base disk
and at most 4095 GiB. Confirm image availability and disk/VM compatibility.
Windows username/password policies are checked locally but final image/service
validation remains authoritative.

Authentication uses `DefaultAzureCredential`, e.g. an existing `az login`
session or a properly configured workload identity. The pinned SDK's transitive
`Azure.Core` 1.62.0 already supplies the `Azure.Identity` namespace/types; a
separate older Azure.Identity package introduces duplicate types and is not needed.
Supply the VM administrator password through the `BULK_VM_ADMIN_PASSWORD`
process environment from a secure source. Do not place passwords/client secrets
in JSON, command arguments, source control, terminal history or diagnostic logs.

## Build and offline validation

From the repository root in PowerShell:

```powershell
dotnet build src\BulkCreateCustom\BulkCreateCustom.csproj
dotnet run --project src\BulkCreateCustom\BulkCreateCustom.csproj --no-build -- --validate
```

If a machine-level NuGet configuration disables the repository's nuget.org feed:

```powershell
dotnet restore src\BulkCreateCustom\BulkCreateCustom.csproj --source https://api.nuget.org/v3/index.json
dotnet build src\BulkCreateCustom\BulkCreateCustom.csproj --no-restore
```

`--validate` uses synthetic configuration, SDK wire serialization/model factories
and an in-memory fake client. It does not construct a real Azure credential or
contact Azure. It asserts Batch A-only submission and totals for normal execution,
plus both retained examples' exact counts and override locations, 150 unique names,
Windows length boundaries, UUID operation names, ten-candidate configuration,
ranks, top-level zones, retry values, required input validation and credential
redaction. A submission barrier proves both calls begin concurrently;
scenarios cover success, one submission failing, partial failure/cancellation,
one poll failing, timeout, local cancellation and malformed status data.
Additional SDK adapter checks use a synthetic token and an HTTP handler that
never opens a socket. They exercise concurrent PUTs, the pinned
`2026-08-06-preview` route, per-VM success/failure, HTTP 409 rejection with an
accepted sibling, and two-page per-VM status responses.
Regressions also cover pending VM operations, temporary resource/status 404s,
delayed per-VM results, recoverable status-service errors, bounded persistent
404s, and a missing second status page. The stub rejects any attempt to poll
the async-operation URL: progress comes from the custom per-VM endpoint.
These checks cannot establish real capacity, applied overrides or service acceptance.

## Live execution (creates resources)

Only after separate deployment approval, environment configuration and sign-in:

```powershell
dotnet run --project src\BulkCreateCustom\BulkCreateCustom.csproj
```

From `src\BulkCreateCustom`, simply use `dotnet run`. Both commands use the
sample's local `config.json`. There is no preview default. Missing/invalid configuration exits before
submission. Each run generates new names, so rerunning creates another batch;
it does **not** resume the previous run.

`BulkCreateDemo.cs` runs only the batches supplied by the builder (currently A).
It prints the selected final SDK request body as indented JSON before submission.
Passwords and secret-bearing fields are replaced with
`[REDACTED]` in a separate JSON copy; the requests sent to Azure are unchanged.
The SDK calls `GetLocationBasedBulkCreateCustoms(region).CreateOrUpdateAsync`
with `WaitUntil.Started`, then directly polls
the per-VM status endpoint. The submission log includes the initial response's
HTTP status from `operation.GetRawResponse().Status`; this is not VM completion.
Monitoring uses
`VirtualMachinesGetOperationStatusAsync` (including all pages). The returned
async-operation ID is logged for investigation only; `UpdateStatusAsync` and
the `/asyncOperations/` endpoint are not used for monitoring. Each cycle also
reads the bulk resource's provisioning state to detect terminal batch failure.
It polls
every ten seconds until all 100 per-VM results are terminal and accounted for.
Missing results or known transient `BulkActionNotFoundException`/`ResourceNotFound`
404s after acceptance cause another observation attempt, not a new create request
or premature success. Temporary 408/429/500/502/503/504 errors during observation
are also retried within the same local deadline.

Acceptance is not reported as successful VM creation. A terminal failed/canceled
bulk resource stops that batch's polling with failure rather than waiting for VMs
that might never be created. Per-VM error codes and messages are read from
both top-level and nested operation errors. Missing status remains unknown,
not successful; failure of one selected batch does not cancel another.
An `InternalExecutionError` is a server-reported terminal failure, not something
client polling can repair. Investigate the async operation and Azure Activity
Log using the printed IDs; never assume that a generic backend error means no
resources were created.

The sample disables automatic HTTP retries to avoid blind create replay; the
five-minute **service** retry window is separate from the local polling deadline
(`pollTimeoutMinutes`, default 30, covering submission and observation). Ctrl+C
or a deadline stops local observation, **not** Azure work. No Cancel/Delete API is
called. A transport error can leave acceptance unknown. Use the operation
resource IDs printed before submission to investigate; do not blindly rerun.

Exit codes: `0` only if the selected operation succeeds and all 100 per-VM results are
successful; `1` for incomplete/failing/unknown outcomes; `2` for invalid usage
or configuration. Each batch prints succeeded, failed, cancelled, pending and
unreported counts, plus fulfilled capacity when supplied. Missing results are
explicitly unreported, not invented rejection/failure counts. Rejected requests
report their HTTP/error code; absent per-VM outcomes remain unknown.

## Output for later resource inspection

VM resource names are `<prefix>-<32-character-run-id>-<batch>-<index:000>`.
Windows computer names use the first prefix letter, a random ten-character
run token, the batch letter and a three-digit index (exactly 15 characters).
No suffix is truncated. Operation resource names are distinct UUIDs as required
by the custom endpoint; correlation tags are also distinct per batch. The SDK
supplies its normal HTTP request identifiers.

The console prints operation/resource IDs and each returned VM's state, size,
zone, **expected** computer name and **expected** OS disk size. Expected values
are labeled as such: this sample does not GET the created Compute VM/disks to
assert applied properties. These IDs and mappings support later portal evidence.
After configuration validation, every live run creates a unique
`bulk-create-<UTC timestamp>-<GUID>.log` beside the executable and prints its path.
For a default Debug build this is `src\BulkCreateCustom\bin\Debug\net10.0`.
The file receives the same redacted request JSON and status output as the
console, flushed after each write so it can be read while polling continues.
The two batches share synchronized logging. Existing `.gitignore` rules exclude
`.log` files. Configuration errors before the log is opened remain console-only;
failure to open the log prevents submission. Keep logs secure because they still
contain resource IDs and infrastructure details.

Only the explicitly redacted request preview should be logged; raw
serialized requests contain the administrator password. The sample keeps SDK
HTTP body logging disabled and omits exception messages.
Cleanup ownership and any live verification must be arranged separately.

## Public contract references

- [Pinned SDK source](https://github.com/Azure/azure-sdk-for-net/tree/470fcf36991d2b32caa7ee434c54d1a518b9c3fd/sdk/compute/Azure.ResourceManager.Compute.BulkActions):
  `LocationBasedBulkCreateCustomCollection`, `BulkCreateCustomProperties`,
  `BulkCreateCustomOverridesProfile`, `BulkCreateCustomVmSizeProfile`,
  `BulkCreateCustomOverrideBase`, `BulkOperationRetryPolicy`.
- [Matching custom-endpoint TypeSpec](https://github.com/Azure/azure-rest-api-specs/blob/8681ba204905f0aad414f3a0e2e687cc5fcb2453/specification/compute/resource-manager/Microsoft.Compute/Bulkactions/bulkCreateCustom.tsp):
  naming/count rules, override precedence and service-owned allocation fields.
