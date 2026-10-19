using System.Net.Sockets;
using System.Reflection;

namespace WslpDdns;

static class Program
{
    public static readonly string Version =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    // Covers boot-time races (DHCP lease, DNS server still starting) without leaning on systemd restarts.
    static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    const string Usage = """
        Usage: wslp-ddns [--config PATH] [--dry-run]

        Creates or refreshes this machine's A record in a Technitium DNS zone.

          --config PATH   Config file (default: /etc/wslp-ddns/wslp-ddns.conf).
                          Environment variables with the same names override it.
          --dry-run       Detect the address and read the current record, but write nothing.
          --version       Print the version.
          -h, --help      Show this help.

        Exit codes: 0 success, 1 failure, 2 configuration or usage error.
        """;

    static async Task<int> Main(string[] args)
    {
        string? configPath = null;
        var dryRun = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine($"unrecognized argument: {args[i]}\n\n{Usage}");
                    return 2;
            }
        }

        Config config;
        try
        {
            config = Config.Load(configPath);
        }
        catch (Exception e) when (e is ConfigException or IOException or UnauthorizedAccessException)
        {
            Log.Error(e.Message);
            return 2;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Reconciler.RunAsync(config, dryRun, CancellationToken.None);
                return 0;
            }
            catch (Exception e) when (IsTransient(e) && attempt <= RetryDelays.Length)
            {
                var delay = RetryDelays[attempt - 1];
                Log.Warn($"attempt {attempt} failed: {e.Message}; retrying in {delay.TotalSeconds:0}s");
                await Task.Delay(delay);
            }
            catch (ConfigException e)
            {
                Log.Error(e.Message);
                return 2;
            }
            catch (Exception e)
            {
                Log.Error(IsTransient(e) ? $"giving up after {attempt} attempts: {e.Message}" : e.Message);
                return 1;
            }
        }
    }

    static bool IsTransient(Exception e) => e switch
    {
        TechnitiumException api => api.Transient,
        HttpRequestException or SocketException or TaskCanceledException or NoAddressException => true,
        _ => false,
    };
}
