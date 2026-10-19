using System.Text;

namespace WslpDdns;

sealed class ConfigException(string message) : Exception(message);

sealed record Config(
    Uri ServerUrl,
    string Token,
    string Zone,
    string Hostname,
    uint Ttl,
    uint ExpiryTtl,
    string? Interface,
    bool Takeover)
{
    public const string DefaultPath = "/etc/wslp-ddns/wslp-ddns.conf";

    static readonly string[] KnownKeys =
    [
        "TECHNITIUM_URL", "TECHNITIUM_TOKEN", "DDNS_ZONE", "DDNS_HOSTNAME",
        "DDNS_TTL", "DDNS_EXPIRY_TTL", "DDNS_INTERFACE", "DDNS_TAKEOVER",
    ];

    public string Fqdn => $"{Hostname}.{Zone}";

    /// <summary>Reads the KEY=VALUE config file; environment variables with the same names override it.</summary>
    public static Config Load(string? path)
    {
        var file = new Dictionary<string, string>(StringComparer.Ordinal);
        var effectivePath = path ?? DefaultPath;
        if (File.Exists(effectivePath))
        {
            file = ParseFile(File.ReadLines(effectivePath), effectivePath);
            if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(effectivePath) & UnixFileMode.OtherRead) != 0)
                Log.Warn($"{effectivePath} is world-readable but holds the API token; run: chmod 640 {effectivePath}");
        }
        else if (path is not null)
        {
            throw new ConfigException($"config file not found: {path}");
        }

        return Resolve(file, Environment.GetEnvironmentVariable, Environment.MachineName, effectivePath);
    }

    internal static Dictionary<string, string> ParseFile(IEnumerable<string> lines, string source)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new ConfigException($"{source}:{lineNumber}: expected KEY=VALUE");

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] is ('"' or '\'') && value[^1] == value[0])
                value = value[1..^1];

            if (!KnownKeys.Contains(key))
                Log.Warn($"{source}:{lineNumber}: ignoring unknown setting {key}");
            values[key] = value;
        }
        return values;
    }

    internal static Config Resolve(
        IReadOnlyDictionary<string, string> file, Func<string, string?> env, string machineName, string source)
    {
        string? Get(string key) =>
            env(key) is { Length: > 0 } fromEnv ? fromEnv
            : file.TryGetValue(key, out var fromFile) && fromFile.Length > 0 ? fromFile
            : null;

        var url = Get("TECHNITIUM_URL")
            ?? throw new ConfigException($"TECHNITIUM_URL is not set (in {source} or the environment)");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var serverUrl) || serverUrl.Scheme is not ("http" or "https"))
            throw new ConfigException($"TECHNITIUM_URL must be an http:// or https:// URL, got '{url}'");
        // A trailing slash keeps any reverse-proxy path prefix when the API paths are appended.
        if (!serverUrl.AbsolutePath.EndsWith('/'))
            serverUrl = new Uri(serverUrl + "/");

        var token = Get("TECHNITIUM_TOKEN")
            ?? throw new ConfigException($"TECHNITIUM_TOKEN is not set (in {source} or the environment)");

        var zone = (Get("DDNS_ZONE") ?? "internal.wslp.cc").Trim().TrimEnd('.').ToLowerInvariant();
        if (!IsValidName(zone))
            throw new ConfigException($"DDNS_ZONE '{zone}' is not a valid DNS name");

        string hostname;
        if (Get("DDNS_HOSTNAME") is { } configured)
        {
            hostname = configured.Trim().TrimEnd('.').ToLowerInvariant();
            if (hostname.EndsWith("." + zone, StringComparison.Ordinal))
                hostname = hostname[..^(zone.Length + 1)];
            if (!IsValidName(hostname))
                throw new ConfigException($"DDNS_HOSTNAME '{configured}' is not a valid DNS name");
        }
        else
        {
            hostname = SanitizeLabel(machineName);
            if (hostname.Length == 0)
                throw new ConfigException($"cannot derive a DNS label from hostname '{machineName}'; set DDNS_HOSTNAME");
        }

        var ttl = ParseSeconds(Get("DDNS_TTL"), "DDNS_TTL", 300);
        if (ttl == 0)
            throw new ConfigException("DDNS_TTL must be greater than 0");

        var expiryTtl = ParseSeconds(Get("DDNS_EXPIRY_TTL"), "DDNS_EXPIRY_TTL", 21600);
        if (expiryTtl is > 0 and < 7200)
            Log.Warn($"DDNS_EXPIRY_TTL={expiryTtl}s is shorter than two hourly runs; one missed run will delete the record");

        var takeover = Get("DDNS_TAKEOVER")?.ToLowerInvariant() switch
        {
            null or "false" or "no" or "0" => false,
            "true" or "yes" or "1" => true,
            var other => throw new ConfigException($"DDNS_TAKEOVER must be true or false, got '{other}'"),
        };

        return new Config(serverUrl, token, zone, hostname, ttl, expiryTtl, Get("DDNS_INTERFACE"), takeover);
    }

    static uint ParseSeconds(string? value, string key, uint fallback)
    {
        if (value is null)
            return fallback;
        return uint.TryParse(value, out var seconds)
            ? seconds
            : throw new ConfigException($"{key} must be a whole number of seconds, got '{value}'");
    }

    /// <summary>Turns a machine hostname into a single lowercase DNS label, e.g. "Web_01.lan" becomes "web-01".</summary>
    internal static string SanitizeLabel(string machineName)
    {
        var firstLabel = machineName.Split('.')[0].ToLowerInvariant();
        var sb = new StringBuilder(firstLabel.Length);
        foreach (var c in firstLabel)
            sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-');

        var label = sb.ToString().Trim('-');
        return label.Length > 63 ? label[..63].TrimEnd('-') : label;
    }

    internal static bool IsValidName(string name) =>
        name.Length is > 0 and <= 253 && name.Split('.').All(IsValidLabel);

    static bool IsValidLabel(string label) =>
        label.Length is > 0 and <= 63
        && label[0] != '-' && label[^1] != '-'
        && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
