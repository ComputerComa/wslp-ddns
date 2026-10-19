using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WslpDdns;

/// <summary>An API-level failure. <see cref="Transient"/> says whether retrying could help.</summary>
sealed class TechnitiumException(string message, bool transient) : Exception(message)
{
    public bool Transient { get; } = transient;
}

/// <summary>Minimal client for the Technitium DNS Server HTTP API (v15+, bearer-token auth).</summary>
sealed class TechnitiumClient : IDisposable
{
    readonly HttpClient _http;

    public TechnitiumClient(Uri serverUrl, string token, TimeSpan timeout)
    {
        _http = new HttpClient { BaseAddress = serverUrl, Timeout = timeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("wslp-ddns", Program.Version));
    }

    public async Task<IReadOnlyList<DnsRecord>> GetRecordsAsync(string domain, string zone, CancellationToken ct)
    {
        var response = await CallAsync(
            "api/zones/records/get",
            [new("domain", domain), new("zone", zone)],
            JsonContext.Default.ApiResponseRecordsResponse,
            ct);
        return response?.Records ?? [];
    }

    /// <summary>
    /// Replaces every A record at <paramref name="domain"/> with one pointing at <paramref name="address"/>.
    /// Technitium stamps a new last-modified time on every write, which restarts the expiry countdown.
    /// </summary>
    public Task SetARecordAsync(
        string domain, string zone, IPAddress address, uint ttl, uint expiryTtl, string comments, CancellationToken ct) =>
        CallAsync(
            "api/zones/records/add",
            [
                new("domain", domain),
                new("zone", zone),
                new("type", "A"),
                new("ipAddress", address.ToString()),
                new("ttl", ttl.ToString()),
                new("expiryTtl", expiryTtl.ToString()),
                new("overwrite", "true"),
                new("comments", comments),
            ],
            JsonContext.Default.ApiResponseJsonElement,
            ct);

    async Task<T?> CallAsync<T>(
        string path, KeyValuePair<string, string>[] form, JsonTypeInfo<ApiResponse<T>> typeInfo, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(path, content, ct);

        ApiResponse<T>? body = null;
        try
        {
            body = await response.Content.ReadFromJsonAsync(typeInfo, ct);
        }
        catch (JsonException)
        {
        }

        if (body is null)
        {
            var status = (int)response.StatusCode;
            throw new TechnitiumException(
                $"{path}: HTTP {status} with no API response; is TECHNITIUM_URL ({_http.BaseAddress}) the Technitium web console?",
                transient: status >= 500);
        }

        return body.Status switch
        {
            "ok" => body.Response,
            "invalid-token" => throw new TechnitiumException(
                "Technitium rejected the API token (invalid-token); check TECHNITIUM_TOKEN", transient: false),
            _ => throw new TechnitiumException($"{path}: {body.ErrorMessage ?? body.Status}", transient: false),
        };
    }

    public void Dispose() => _http.Dispose();
}

sealed record ApiResponse<T>(string Status, string? ErrorMessage, T? Response);

sealed record RecordsResponse(IReadOnlyList<DnsRecord>? Records);

sealed record DnsRecord(
    string Name,
    string Type,
    uint Ttl,
    bool Disabled,
    string? Comments,
    RecordData? RData,
    uint ExpiryTtl,
    DateTime LastModified);

sealed record RecordData(string? IpAddress);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ApiResponse<RecordsResponse>))]
[JsonSerializable(typeof(ApiResponse<JsonElement>))]
partial class JsonContext : JsonSerializerContext;
