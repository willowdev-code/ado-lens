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

    public Task<JsonElement> GetAsync(string url, CancellationToken ct = default) => SendAsync(HttpMethod.Get, url, null, null, ct);

    public Task<JsonElement> PostAsync(string url, object body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, url, body, "application/json", ct);

    /// <summary>JSON Patch (work item create and update).</summary>
    public Task<JsonElement> PatchAsync(string url, object operations, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, url, operations, "application/json-patch+json", ct);

    /// <summary>
    /// Sends one request: relative URLs go to the organization, absolute ones only to Azure DevOps hosts (vssps, vsrm),
    /// so a URL taken from data can never carry the PAT anywhere else.
    /// </summary>
    public async Task<JsonElement> SendAsync(HttpMethod method, string url, object? body, string? contentType, CancellationToken ct = default) =>
        (await SendWithHeadersAsync(method, url, body, contentType, null, ct)).Body;

    /// <summary>Like <see cref="SendAsync"/>, with extra request headers (e.g. If-Match) and the answer's ETag.</summary>
    public async Task<(JsonElement Body, string? ETag)> SendWithHeadersAsync(HttpMethod method, string url, object? body, string? contentType,
        IReadOnlyDictionary<string, string>? headers, CancellationToken ct = default)
    {
        url = WithApiVersion(url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "https" or "http" && !IsAzureDevOpsHost(abs))
            throw new AdoApiException($"refusing to send the PAT to {abs.Host}: only Azure DevOps hosts are allowed");
        var payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, BodyJson);
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (headers is not null)
                foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
            if (payload is not null)
            {
                req.Content = new ByteArrayContent(payload);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json");
            }
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (attempt < MaxAttempts && RetryDelay(res) is { } delay)
            {
                await Delay(delay, ct);
                continue;
            }
            await EnsureSuccessAsync(res, ct);
            var etag = res.Headers.ETag?.Tag ?? (res.Headers.TryGetValues("ETag", out var tags) ? tags.FirstOrDefault() : null);
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            if (res.Content.Headers.ContentLength == 0) return (default, etag);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return (doc.RootElement.Clone(), etag);
        }
    }

    static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    static bool IsAzureDevOpsHost(Uri uri) =>
        uri.Scheme == "https" && (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".dev.azure.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Organization-level host of another Azure DevOps service, e.g. "vssps" or "vsrm".</summary>
    public string ServiceUrl(string service) => $"https://{service}.dev.azure.com/{Uri.EscapeDataString(organization)}/";

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
        message = Explain(message);

        var hint = res.StatusCode == HttpStatusCode.Forbidden ? " (the PAT or the account lacks the permission or scope for this)" : "";
        throw new AdoApiException(
            $"Azure DevOps returned {(int)res.StatusCode} {res.ReasonPhrase} for {Describe(res.RequestMessage?.RequestUri)}: {message}{hint}",
            res.StatusCode);
    }

    static string Describe(Uri? uri) => uri is null ? "request" : uri.GetLeftPart(UriPartial.Path);

    static readonly System.Text.RegularExpressions.Regex RuleError =
        new(@"Rule Error for field (?<field>.+?)\. Error code: (?<code>\w+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Work item rule errors (TF401320) in plain words; Claude can fix the request from them.</summary>
    internal static string Explain(string message)
    {
        var parts = RuleError.Matches(message).Select(m => m.Groups["code"].Value switch
        {
            "Required" => $"field '{m.Groups["field"].Value}' is required for this change",
            "InvalidListValue" or "InvalidNotEmpty" => $"field '{m.Groups["field"].Value}' has a value that is not allowed (ado_fields field=… lists the values)",
            "InvalidEmpty" => $"field '{m.Groups["field"].Value}' must be empty",
            "InvalidNotOldValue" => $"field '{m.Groups["field"].Value}' must change",
            "InvalidChangedValue" or "ReadOnly" => $"field '{m.Groups["field"].Value}' cannot be changed",
            var code => $"field '{m.Groups["field"].Value}': {code}",
        }).Distinct().ToList();
        return parts.Count == 0 ? message : string.Join("; ", parts) + $" ({message.Split('\n')[0].Trim()})";
    }
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
