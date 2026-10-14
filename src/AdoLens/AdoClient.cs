using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AdoLens;

public sealed class AdoApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Thin client over the Azure DevOps REST API: one pooled HTTP/2 connection per process, compressed responses,
/// retries on throttling (429 / 503 with Retry-After) and readable errors. Answers are parsed as JsonElement,
/// so callers read only the properties they need.
/// </summary>
public sealed class AdoClient(HttpClient http, string organization)
{
    public const string ApiVersion = "7.1";
    const int MaxAttempts = 3;
    static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    public string Organization => organization;

    /// <summary>Delay used between retries; tests replace it to run without waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public static HttpClient CreateHttpClient(string organization, string pat, string version)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true,
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://dev.azure.com/{Uri.EscapeDataString(organization)}/"),
            Timeout = TimeSpan.FromSeconds(100),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ado-lens/{version}");
        return client;
    }

    /// <summary>A client whose every call fails with <paramref name="reason"/>, so a missing setup shows up in the tool result.</summary>
    public static AdoClient Unconfigured(string reason) =>
        new(new HttpClient(new FailingHandler(reason)) { BaseAddress = new Uri("https://dev.azure.com/unconfigured/") }, "unconfigured");

    sealed class FailingHandler(string reason) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new AdoApiException($"ADO Lens is not configured: {reason}");
    }

    /// <summary>"Project/" for a project-scoped route.</summary>
    public static string Scope(string project) => Uri.EscapeDataString(project.Trim()) + "/";

    /// <summary>Appends api-version unless the URL already carries one.</summary>
    static string WithApiVersion(string url) =>
        url.Contains("api-version=", StringComparison.Ordinal) ? url : url + (url.Contains('?') ? "&" : "?") + "api-version=" + ApiVersion;

    public async Task<JsonElement> GetAsync(string url, CancellationToken ct = default)
    {
        url = WithApiVersion(url);
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (attempt < MaxAttempts && RetryDelay(res) is { } delay)
            {
                await Delay(delay, ct);
                continue;
            }
            await EnsureSuccessAsync(res, ct);
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return doc.RootElement.Clone();
        }
    }

    /// <summary>The "value" array of a list response.</summary>
    public async Task<List<JsonElement>> GetListAsync(string url, CancellationToken ct = default)
    {
        var root = await GetAsync(url, ct);
        return root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray()] : [];
    }

    /// <summary>The signed-in identity: checks the organization and the PAT with one cheap call.</summary>
    public async Task<string> WhoAmIAsync(CancellationToken ct = default)
    {
        // connectionData exists only as a preview resource: a plain "7.1" gets 400.
        var data = await GetAsync("_apis/connectionData?api-version=7.1-preview.1", ct);
        var user = data.TryGetProperty("authenticatedUser", out var u) ? u : default;
        return Json.Str(user, "providerDisplayName") ?? Json.Str(user, "customDisplayName") ?? "(unknown user)";
    }

    static TimeSpan? RetryDelay(HttpResponseMessage res)
    {
        if (res.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return null;
        var after = res.Headers.RetryAfter;
        var delay = after?.Delta ?? (after?.Date is { } d ? d - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage res, CancellationToken ct)
    {
        // An invalid or expired PAT gets a 203 with the HTML sign-in page instead of a 401.
        var mediaType = res.Content.Headers.ContentType?.MediaType;
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation
            || (res.IsSuccessStatusCode && mediaType == "text/html"))
            throw new AdoApiException(
                $"Azure DevOps rejected the PAT ({(int)res.StatusCode}): it is invalid, expired or revoked - replace it with 'adolens login'",
                res.StatusCode);

        if (res.IsSuccessStatusCode)
            return;

        var text = await res.Content.ReadAsStringAsync(ct);
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var m))
                message = m.GetString();
        }
        catch (JsonException) { }
        message ??= text.Length > 300 ? text[..300] + "…" : text;

        var hint = res.StatusCode == HttpStatusCode.Forbidden ? " (the PAT or the account lacks the permission or scope for this)" : "";
        throw new AdoApiException(
            $"Azure DevOps returned {(int)res.StatusCode} {res.ReasonPhrase} for {Describe(res.RequestMessage?.RequestUri)}: {message}{hint}",
            res.StatusCode);
    }

    static string Describe(Uri? uri) => uri is null ? "request" : uri.GetLeftPart(UriPartial.Path);
}

/// <summary>Small helpers for reading JsonElement without exceptions.</summary>
public static class Json
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>A scalar as text (string, number, bool); null for anything else.</summary>
    public static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        _ => null,
    };
}
