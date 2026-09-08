# Batch A in Bruno

Open this directory as a collection in Bruno, select the **Local** environment,
and open **Batch A - Create 100 VMs**. Local contains the same target as the
sample's current local configuration and is gitignored. On another checkout,
copy `environments\Example.bru` to `environments\Local.bru` and fill the target.
Bruno does not automatically reload settings from the .NET `config.json`.

Set `accessToken` and `vmAdminPassword` in the Local environment, marking them
as secret variables in Bruno. Do not commit/export credentials or real request
bodies. To copy a short-lived ARM token to the clipboard in PowerShell:

```powershell
az account get-access-token --subscription 1d04e8f1-ee04-4056-b0b2-718f5bb45b04 --resource https://management.azure.com/ --query accessToken --output tsv | Set-Clipboard
```

Paste the token as `accessToken` without the `Bearer` prefix. Supply your Windows
administrator password as `vmAdminPassword`. Refresh the token when it expires.
The clipboard contains a credential; clear it after pasting.

**Send creates a new 100-VM batch and billable NICs/disks.** The pre-request
script fills the visible base JSON, creates a fresh operation UUID and generates
100 unique resource/computer names (Windows computer names are 15 characters).
It preserves the .NET sample's four ranked size candidates, zones 1-3,
BestEffortBalanced policy, 127-GiB base OS disk and five-minute retry window.
There are no per-size overrides and no Batch B.

The response and post-response console message show the initial HTTP status,
not completed VM creation. This collection does not automatically poll, resubmit
or clean up. The runtime variables `operationName`, `sampleCorrelationId` and
`bulkResourceUrl` support investigation; the sample correlation is only a tag.
Each subsequent Send generates a new batch, so never use Send to check progress.

## Check an existing request

Send **Batch A - VM Status (first page)**, not the Create request. It calls the
custom endpoint's pageable `virtualMachinesGetOperationStatus` action and reuses
the `operationName` runtime variable from Create. After restarting Bruno, add
`operationName` to the Local environment using the existing bulk-resource UUID
from the original request URL (not the async-operation ID). No VM password is needed.

Inspect `results[].operation.state` and
`results[].operation.resourceOperationError`. HTTP 200 means the status lookup
succeeded, not that creation succeeded. The console summary counts this page
only. If `nextLink` exists, send **Batch A - VM Status (next page)** repeatedly
until no next page remains. To refresh progress, send the first-page status
request again; it resets pagination. A missing/invalid next page blocks sending.

**Do not use Run Collection to check status**: that also runs the create request.
Send only the individual status requests. They do not automatically loop or
change/delete Azure resources. Returned next-page links must target the same
ARM host and operation path before the bearer token is sent.

The known service-side failures seen with the .NET sample are not repaired by
switching clients. No request has been sent as part of creating this collection.
