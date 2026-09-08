using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BulkCreateCustom;

internal sealed class DemoConfig
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public string SubscriptionId { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string Region { get; init; } = "";
    public string SubnetId { get; init; } = "";
    public string[] Zones { get; init; } = [];
    public string RunPrefix { get; init; } = "";
    public string AdminUsername { get; init; } = "";
    public string ImageVersion { get; init; } = "";
    public int ImageMinimumOSDiskGB { get; init; }
    public int PollTimeoutMinutes { get; init; } = 30;
    public SizeCandidate[] Sizes { get; init; } =
    [
        new("Standard_D4s_v5", 0, 256),
        new("Standard_D8s_v5", 1, 320),
        new("Standard_E4s_v5", 2, 384),
        new("Standard_E8s_v5", 3, 448)
    ];

    public static DemoConfig Load(string path) =>
        JsonSerializer.Deserialize<DemoConfig>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new ArgumentException("Configuration must be a JSON object.");

    public void Validate(string password)
    {
        Require(Guid.TryParse(SubscriptionId, out var subscription) && subscription != Guid.Empty,
            "SubscriptionId must be a nonempty GUID.");
        Require(Matches(ResourceGroup, @"^[a-zA-Z0-9_.()-]{1,90}$") && !ResourceGroup.EndsWith('.'),
            "ResourceGroup must be an existing resource group name.");
        Require(Matches(Region, @"^[a-z][a-z0-9]+$"), "Region must be an explicit Azure region name.");
        Require(Matches(SubnetId, @"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$"),
            "SubnetId must be an existing subnet ARM ID.");
        Require(SubnetId.StartsWith($"/subscriptions/{SubscriptionId}/", StringComparison.OrdinalIgnoreCase),
            "This sample requires a subnet in the same subscription.");
        Require(Zones is { Length: >= 2 and <= 3 } && Zones.All(z => z is "1" or "2" or "3")
            && Zones.Distinct().Count() == Zones.Length, "Zones must contain two or three distinct eligible zones.");
        Require(Matches(RunPrefix, @"^[a-z][a-z0-9-]{0,11}$") && !RunPrefix.EndsWith('-'),
            "RunPrefix must be 1-12 lowercase letters/digits/hyphens, starting with a letter and not ending with a hyphen.");
        Require(Matches(AdminUsername, @"^[a-zA-Z][a-zA-Z0-9_-]{0,19}$")
            && !new[] { "administrator", "admin", "user", "test", "guest", "root" }.Contains(AdminUsername, StringComparer.OrdinalIgnoreCase),
            "AdminUsername must be a non-reserved Windows administrator name (1-20 characters).");
        Require(password.Length is >= 12 and <= 123 && !password.Contains(AdminUsername, StringComparison.OrdinalIgnoreCase)
            && !password.Any(char.IsControl)
            && new[] { password.Any(char.IsUpper), password.Any(char.IsLower), password.Any(char.IsDigit),
                password.Any(c => !char.IsLetterOrDigit(c)) }.Count(x => x) >= 3,
            "BULK_VM_ADMIN_PASSWORD must meet Windows password requirements (12-123 characters, 3 character classes, no username).");
        Require(Matches(ImageVersion, @"^\d+\.\d+\.\d+$"),
            "ImageVersion must pin an existing Windows Server 2022 Datacenter Azure Edition image version (not latest).");
        Require(ImageMinimumOSDiskGB is >= 127 and <= 4095, "ImageMinimumOSDiskGB must be the verified image minimum (127-4095).");
        Require(PollTimeoutMinutes is >= 1 and <= 120, "PollTimeoutMinutes must be 1-120, independent of service retry.");
        Require(Sizes is { Length: >= 1 and <= 10 }, "Configure 1-10 VM size candidates (sample safety cap).");
        Require(Sizes.All(s => s is not null && Matches(s.Name, @"^Standard_[DE][48](s|ds|as|ads)_v5$")),
            "Use 4/8-vCPU D/E s, ds, as or ads v5 candidates compatible with the image; confirm regional eligibility.");
        Require(Sizes.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Sizes.Length,
            "VM size candidates must be unique.");
        Require(Sizes.Select(s => s.Rank).Order().SequenceEqual(Enumerable.Range(0, Sizes.Length)),
            "Ranks must be distinct and contiguous, starting at 0.");
        Require(Sizes.All(s => s.OSDiskGB > ImageMinimumOSDiskGB && s.OSDiskGB <= 4095)
            && Sizes.Select(s => s.OSDiskGB).Distinct().Count() == Sizes.Length,
            "Per-size OS disk sizes must be distinct, larger than the base image disk and at most 4095 GiB.");
    }

    private static bool Matches(string? value, string pattern) => value is not null && Regex.IsMatch(value, pattern);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new ArgumentException(message);
    }
}

internal sealed record SizeCandidate(string Name, int Rank, int OSDiskGB);
