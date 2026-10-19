using System.Net;

namespace WslpDdns;

enum Change { Create, Update, Refresh }

/// <summary>The name already has A records that wslp-ddns did not write.</summary>
sealed class ForeignRecordException(string message) : Exception(message);

static class Reconciler
{
    /// <summary>Stored in the record's comments so later runs know the record is theirs to replace.</summary>
    public const string Marker = "managed by wslp-ddns";

    public static async Task RunAsync(Config config, bool dryRun, CancellationToken ct)
    {
        var address = await AddressDetector.DetectAsync(config, ct);

        using var client = new TechnitiumClient(config.ServerUrl, config.Token, TimeSpan.FromSeconds(15));
        var existing = (await client.GetRecordsAsync(config.Fqdn, config.Zone, ct))
            .Where(r => r.Type == "A")
            .ToList();

        var change = Decide(existing, address, config.Takeover);
        var message = change switch
        {
            Change.Create => $"created {config.Fqdn} A {address}",
            Change.Update => $"updated {config.Fqdn} A {Describe(existing)} -> {address}",
            _ => $"refreshed {config.Fqdn} A {address}",
        };
        var expiry = config.ExpiryTtl == 0
            ? "no auto-delete"
            : $"auto-delete {FormatSeconds(config.ExpiryTtl)} after last refresh";
        var details = $"(ttl {FormatSeconds(config.Ttl)}, {expiry})";

        if (dryRun)
        {
            Log.Info($"dry run, nothing written: would have {message} {details}");
            return;
        }

        await client.SetARecordAsync(config.Fqdn, config.Zone, address, config.Ttl, config.ExpiryTtl, Marker, ct);
        Log.Info($"{message} {details}");
    }

    internal static Change Decide(IReadOnlyList<DnsRecord> existingA, IPAddress address, bool takeover)
    {
        if (existingA.Count == 0)
            return Change.Create;

        if (!takeover && existingA.Any(r => r.Comments != Marker))
        {
            throw new ForeignRecordException(
                $"{existingA[0].Name} already has an A record ({Describe(existingA)}) that wslp-ddns did not create, " +
                "so it was left alone. Delete it in Technitium, or set DDNS_TAKEOVER=true to let wslp-ddns manage it.");
        }

        var unchanged = existingA.Count == 1
            && IPAddress.TryParse(existingA[0].RData?.IpAddress, out var current)
            && current.Equals(address);
        return unchanged ? Change.Refresh : Change.Update;
    }

    static string Describe(IReadOnlyList<DnsRecord> records) =>
        string.Join(", ", records.Select(r => r.RData?.IpAddress ?? "?"));

    internal static string FormatSeconds(uint seconds) => seconds switch
    {
        _ when seconds % 3600 == 0 => $"{seconds / 3600}h",
        _ when seconds % 60 == 0 => $"{seconds / 60}m",
        _ => $"{seconds}s",
    };
}
