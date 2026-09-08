using System.Security.Cryptography;
using Azure.ResourceManager.Compute.BulkActions;
using Azure.ResourceManager.Compute.BulkActions.Models;

namespace BulkCreateCustom;

internal sealed record BatchRequest(string Label, string OperationName, string CorrelationId,
    LocationBasedBulkCreateCustomData Data, string VmResourceIdPrefix, IReadOnlyDictionary<string, string> ComputerNames,
    IReadOnlyDictionary<string, int> ExpectedDisks);

internal static class BulkCreateRequestBuilder
{
    public static BatchRequest[] Build(DemoConfig config, string password, bool includePerSizeBatch = false)
    {
        config.Validate(password);
        var runId = Guid.NewGuid().ToString("N");
        // Reserve batch and index suffixes before composing the 15-character Windows names.
        var computerRun = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz012345", 10);
        var first = BuildBatch(config, password, runId, computerRun, "a", 100, perSize: false);
        return includePerSizeBatch
            ? [first, BuildBatch(config, password, runId, computerRun, "b", 50, perSize: true)]
            : [first];
    }

    private static BatchRequest BuildBatch(DemoConfig config, string password, string runId,
        string computerRun, string label, int count, bool perSize)
    {
        var properties = new BulkCreateCustomProperties(count,
            new BulkCreateCustomPriorityProfile
            {
                Type = PriorityType.Regular,
                AllocationStrategy = BulkCreateCustomAllocationStrategy.Prioritized
            },
            new ComputeProfile(BuildBaseProfile(config, password)) { ComputeApiVersion = "2024-11-01" })
        {
            CapacityType = CapacityType.VM,
            PartialFulfillmentPolicy = new PartialFulfillmentPolicy { Mode = PartialFulfillmentMode.Disabled },
            OverridesProfile = new BulkCreateCustomOverridesProfile(),
            ZoneAllocationPolicy = new BulkCreateCustomZoneAllocationPolicy
            {
                DistributionStrategy = BulkCreateCustomDistributionStrategy.BestEffortBalanced
            },
            ExecutionParameters = new BulkActionExecutionParameterDetail
            {
                RetryPolicy = new BulkOperationRetryPolicy { RetryWindowInMinutes = 5 }
            }
        };
        foreach (var size in config.Sizes.OrderBy(s => s.Rank))
        {
            var profile = new BulkCreateCustomVmSizeProfile(size.Name, size.Rank);
            if (perSize)
            {
                profile.Override = new BulkCreateCustomOverrideBase
                {
                    VirtualMachineProfile = new BulkActionVMProperties
                    {
                        StorageProfile = new StorageProfile
                        {
                            OSDisk = new OSDisk(DiskCreateOptionTypes.FromImage) { DiskSizeGB = size.OSDiskGB }
                        }
                    }
                };
            }
            properties.VmSizesProfile.Add(profile);
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var name = $"{config.RunPrefix}-{runId}-{label}-{i:D3}";
            var computerName = $"{config.RunPrefix[0]}{computerRun}{label}{i:D3}";
            names.Add(name, computerName);
            // Even the per-size batch requires capacity-many identity entries. Disk settings
            // are deliberately absent here so they cannot mask the native per-size override.
            properties.OverridesProfile.Overrides.Add(new BulkCreateCustomOverride
            {
                VirtualMachineName = name,
                VirtualMachineProfile = new BulkActionVMProperties
                {
                    OSProfile = new OSProfile { ComputerName = computerName }
                }
            });
        }
        var correlation = Guid.NewGuid().ToString();
        var data = new LocationBasedBulkCreateCustomData { Properties = properties };
        foreach (var zone in config.Zones)
            data.Zones.Add(zone);
        data.Tags.Add("sample", "BulkCreateCustom");
        data.Tags.Add("batch", label);
        data.Tags.Add("correlationId", correlation);
        return new BatchRequest(label, Guid.NewGuid().ToString(), correlation, data,
            $"/subscriptions/{config.SubscriptionId}/resourceGroups/{config.ResourceGroup}/providers/Microsoft.Compute/virtualMachines/", names,
            config.Sizes.ToDictionary(s => s.Name, s => perSize ? s.OSDiskGB : config.ImageMinimumOSDiskGB,
                StringComparer.OrdinalIgnoreCase));
    }

    private static BulkActionVMProperties BuildBaseProfile(DemoConfig config, string password) => new()
    {
        OSProfile = new OSProfile
        {
            AdminUsername = config.AdminUsername,
            AdminPassword = password,
            WindowsConfiguration = new WindowsConfiguration { IsProvisionVMAgent = true, EnableAutomaticUpdates = true }
        },
        StorageProfile = new StorageProfile
        {
            ImageReference = new ImageReference
            {
                Publisher = "MicrosoftWindowsServer", Offer = "WindowsServer",
                Sku = "2022-datacenter-azure-edition", Version = config.ImageVersion
            },
            OSDisk = new OSDisk(DiskCreateOptionTypes.FromImage)
            {
                OSType = OperatingSystemTypes.Windows,
                DiskSizeGB = config.ImageMinimumOSDiskGB,
                Caching = CachingTypes.ReadWrite,
                ManagedDisk = new ManagedDiskParametersContent { StorageAccountType = StorageAccountTypes.StandardSSDLRS }
            }
        },
        NetworkProfile = new NetworkProfile
        {
            NetworkApiVersion = NetworkApiVersion._20221101,
            NetworkInterfaceConfigurations =
            {
                new VirtualMachineNetworkInterfaceConfiguration("nic")
                {
                    Properties = new VirtualMachineNetworkInterfaceConfigurationProperties(
                    [
                        new VirtualMachineNetworkInterfaceIPConfiguration("ipconfig")
                        {
                            Properties = new VirtualMachineNetworkInterfaceIPConfigurationProperties
                            {
                                SubnetId = config.SubnetId, IsPrimary = true
                            }
                        }
                    ]) { IsPrimary = true }
                }
            }
        }
    };
}
