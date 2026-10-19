using WslpDdns;

namespace WslpDdns.Tests;

public class ConfigTests
{
    static readonly Dictionary<string, string> Minimal = new()
    {
        ["TECHNITIUM_URL"] = "http://10.0.0.53:5380",
        ["TECHNITIUM_TOKEN"] = "abc",
    };

    static Config Resolve(Dictionary<string, string> file, Dictionary<string, string>? env = null, string machine = "web01") =>
        Config.Resolve(file, key => env?.GetValueOrDefault(key), machine, "test.conf");

    [Fact]
    public void Defaults_match_the_wslp_setup()
    {
        var config = Resolve(Minimal);

        Assert.Equal("internal.wslp.cc", config.Zone);
        Assert.Equal("web01.internal.wslp.cc", config.Fqdn);
        Assert.Equal(300u, config.Ttl);
        Assert.Equal(21600u, config.ExpiryTtl);
        Assert.False(config.Takeover);
        Assert.Null(config.Interface);
    }

    [Fact]
    public void Environment_overrides_file()
    {
        var config = Resolve(Minimal, new() { ["DDNS_HOSTNAME"] = "override", ["TECHNITIUM_TOKEN"] = "from-env" });

        Assert.Equal("override", config.Hostname);
        Assert.Equal("from-env", config.Token);
    }

    [Fact]
    public void Empty_values_fall_back_to_defaults()
    {
        var config = Resolve(new(Minimal) { ["DDNS_ZONE"] = "", ["DDNS_HOSTNAME"] = "" });

        Assert.Equal("web01.internal.wslp.cc", config.Fqdn);
    }

    [Theory]
    [InlineData("TECHNITIUM_URL")]
    [InlineData("TECHNITIUM_TOKEN")]
    public void Missing_required_setting_is_an_error(string key)
    {
        var file = new Dictionary<string, string>(Minimal);
        file.Remove(key);

        var e = Assert.Throws<ConfigException>(() => Resolve(file));
        Assert.Contains(key, e.Message);
    }

    [Theory]
    [InlineData("Web_01", "web-01")]
    [InlineData("web01.lan", "web01")]
    [InlineData("-db-", "db")]
    [InlineData("ÜberBox", "berbox")]
    public void Machine_hostname_is_sanitized_to_one_label(string machine, string expected) =>
        Assert.Equal(expected, Config.SanitizeLabel(machine));

    [Fact]
    public void Configured_hostname_may_repeat_the_zone()
    {
        var config = Resolve(new(Minimal) { ["DDNS_HOSTNAME"] = "Web01.internal.wslp.cc." });

        Assert.Equal("web01.internal.wslp.cc", config.Fqdn);
    }

    [Theory]
    [InlineData("DDNS_HOSTNAME", "bad_name")]
    [InlineData("DDNS_ZONE", "internal..wslp.cc")]
    [InlineData("DDNS_TTL", "5m")]
    [InlineData("DDNS_TTL", "0")]
    [InlineData("DDNS_TAKEOVER", "maybe")]
    [InlineData("TECHNITIUM_URL", "dns.wslp.cc:5380")]
    public void Invalid_values_are_rejected(string key, string value) =>
        Assert.Throws<ConfigException>(() => Resolve(new(Minimal) { [key] = value }));

    [Fact]
    public void Url_keeps_path_prefix_with_trailing_slash()
    {
        var config = Resolve(new(Minimal) { ["TECHNITIUM_URL"] = "https://proxy.wslp.cc/technitium" });

        Assert.Equal("https://proxy.wslp.cc/technitium/api/zones/records/get",
            new Uri(config.ServerUrl, "api/zones/records/get").ToString());
    }

    [Fact]
    public void File_parser_handles_comments_and_quotes()
    {
        var values = Config.ParseFile(
            ["# comment", "", "  TECHNITIUM_TOKEN = \"abc=def\"  ", "DDNS_ZONE='lab.wslp.cc'"], "test.conf");

        Assert.Equal("abc=def", values["TECHNITIUM_TOKEN"]);
        Assert.Equal("lab.wslp.cc", values["DDNS_ZONE"]);
    }

    [Fact]
    public void File_parser_reports_line_number()
    {
        var e = Assert.Throws<ConfigException>(() => Config.ParseFile(["DDNS_ZONE=x", "oops"], "test.conf"));

        Assert.StartsWith("test.conf:2:", e.Message);
    }
}
