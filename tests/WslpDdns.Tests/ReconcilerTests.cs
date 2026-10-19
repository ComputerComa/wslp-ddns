using System.Net;
using WslpDdns;

namespace WslpDdns.Tests;

public class ReconcilerTests
{
    static readonly IPAddress Current = IPAddress.Parse("10.0.0.5");

    static DnsRecord A(string ip, string? comments = Reconciler.Marker) =>
        new("web01.internal.wslp.cc", "A", 300, false, comments, new RecordData(ip), 21600, DateTime.UtcNow);

    [Fact]
    public void No_record_means_create() =>
        Assert.Equal(Change.Create, Reconciler.Decide([], Current, takeover: false));

    [Fact]
    public void Same_address_means_refresh() =>
        Assert.Equal(Change.Refresh, Reconciler.Decide([A("10.0.0.5")], Current, takeover: false));

    [Fact]
    public void Different_address_means_update() =>
        Assert.Equal(Change.Update, Reconciler.Decide([A("10.0.0.4")], Current, takeover: false));

    [Fact]
    public void Extra_managed_records_are_collapsed_by_update() =>
        Assert.Equal(Change.Update, Reconciler.Decide([A("10.0.0.5"), A("10.0.0.6")], Current, takeover: false));

    [Fact]
    public void Foreign_record_is_left_alone()
    {
        var e = Assert.Throws<ForeignRecordException>(
            () => Reconciler.Decide([A("10.0.0.9", comments: null)], Current, takeover: false));

        Assert.Contains("DDNS_TAKEOVER", e.Message);
    }

    [Fact]
    public void Foreign_record_is_replaced_with_takeover() =>
        Assert.Equal(Change.Update, Reconciler.Decide([A("10.0.0.9", comments: "static")], Current, takeover: true));

    [Theory]
    [InlineData(21600u, "6h")]
    [InlineData(300u, "5m")]
    [InlineData(90u, "90s")]
    public void Durations_are_formatted_compactly(uint seconds, string expected) =>
        Assert.Equal(expected, Reconciler.FormatSeconds(seconds));
}
