using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using ModelContextProtocol;

namespace MfpConnector.Api;

/// <summary>
/// Holds the MyFitnessPal web session cookie: from SSM Parameter Store when MfpCookieParameter is set,
/// otherwise from the MfpCookie setting (local development).
/// </summary>
public sealed class CookieStore(IConfiguration config, IServiceProvider services)
{
    private string? _cookie;
    public DateTimeOffset CheckedAt { get; set; }

    private string? Parameter => config["MfpCookieParameter"];

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_cookie is not null) return _cookie;
        if (Parameter is null)
            return _cookie = config["MfpCookie"] ?? throw new McpException("No MyFitnessPal cookie is configured (MfpCookie).");

        var ssm = services.GetRequiredService<IAmazonSimpleSystemsManagement>();
        var resp = await ssm.GetParameterAsync(new GetParameterRequest { Name = Parameter, WithDecryption = true }, ct);
        return _cookie = resp.Parameter.Value;
    }

    public async Task SetAsync(string cookie, CancellationToken ct)
    {
        _cookie = cookie;
        if (Parameter is null) return;
        var ssm = services.GetRequiredService<IAmazonSimpleSystemsManagement>();
        await ssm.PutParameterAsync(new PutParameterRequest
        {
            Name = Parameter, Value = cookie, Type = ParameterType.SecureString, Overwrite = true,
        }, ct);
    }

    /// <summary>Forget the cached value, so the next call re-reads it (e.g. after you paste a new one in SSM).</summary>
    public void Reset()
    {
        _cookie = null;
        CheckedAt = default;
    }
}

/// <summary>
/// Calls the same-origin /api routes the MyFitnessPal website uses, authenticated by the next-auth session cookie.
/// Verified against a real account on 2026-11-13.
/// </summary>
public sealed class Mfp(HttpClient http, CookieStore cookies)
{
    private const string SessionCookie = "__Secure-next-auth.session-token";
    private static readonly TimeSpan SessionCheckEvery = TimeSpan.FromHours(6);

    public Task<JsonNode?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        await EnsureSessionAsync(ct);
        using var resp = await RawSendAsync(method, path, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new McpException($"MyFitnessPal returned HTTP {(int)resp.StatusCode} for {method} {path.Split('?')[0]}: {text[..Math.Min(text.Length, 300)]}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    /// <summary>
    /// Checks the session at most every few hours. MFP re-issues the cookie with a fresh 30-day expiry on this call,
    /// so it's saved back, and the session never expires while the connector is in use.
    /// </summary>
    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - cookies.CheckedAt < SessionCheckEvery) return;

        using var resp = await RawSendAsync(HttpMethod.Get, "api/auth/session", null, ct);
        var userId = resp.IsSuccessStatusCode ? JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["userId"] : null;
        if (userId is null) throw Expired();

        var fresh = resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? setCookies.Select(c => c.Split(';', 2)[0]).FirstOrDefault(c => c.StartsWith(SessionCookie + "="))?[(SessionCookie.Length + 1)..]
            : null;
        if (fresh is not null && fresh != await cookies.GetAsync(ct)) await cookies.SetAsync(fresh, ct);
        cookies.CheckedAt = DateTimeOffset.UtcNow;
    }

    private async Task<HttpResponseMessage> RawSendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var cookie = await cookies.GetAsync(ct);
        using var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("Cookie", cookie.Contains('=') ? cookie : $"{SessionCookie}={cookie}");
        if (body is not null) req.Content = JsonContent.Create(body);

        var resp = await http.SendAsync(req, ct);
        if (resp.Headers.Contains("cf-mitigated"))
            throw new McpException("MyFitnessPal's Cloudflare protection blocked this server (requests from AWS may be challenged).");
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.Headers.Location?.ToString().Contains("/account/login") == true)
            throw Expired();
        return resp;
    }

    private McpException Expired()
    {
        cookies.Reset();
        return new McpException(
            "The MyFitnessPal session has expired. Ask the user to sign in at myfitnesspal.com, copy the " +
            $"{SessionCookie} cookie, and paste it into the connector's SSM parameter.");
    }
}
