using ECommons.DalamudServices;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FaloopScreener;

/// <summary>Minimal client for faloop.app's internal API, mirroring the flow the website
/// itself (and the Divination Faloop Integration plugin) uses: an anonymous refresh hands
/// out a session id + short-lived JWT, login upgrades the session, and GET /api/app
/// returns the tracker's bootstrap state (windows included for accounts with access).</summary>
internal sealed class FaloopClient : IDisposable
{
    private const string BaseUrl = "https://faloop.app/api";

    private readonly HttpClient http;

    public string? SessionId { get; private set; }
    public string? Token { get; private set; }
    public bool IsLoggedIn { get; private set; }

    public FaloopClient()
    {
        http = new HttpClient();
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Add("Origin", "https://faloop.app");
        http.DefaultRequestHeaders.Add("Referer", "https://faloop.app/");
    }

    public void Dispose() => http.Dispose();

    /// <summary>Gets (or renews) the session id and JWT. Works anonymously; with an
    /// existing session it refreshes the token while keeping the login state.</summary>
    public async Task<bool> RefreshAsync()
    {
        var body = JsonSerializer.Serialize(new { sessionId = SessionId });
        using var response = await PostAsync("/auth/user/refresh", body, withAuth: false).ConfigureAwait(false);
        if (response == null)
            return false;

        var data = await ParseDataAsync(response).ConfigureAwait(false);
        if (data == null)
            return false;

        SessionId = GetString(data.Value, "sessionId") ?? SessionId;
        Token = GetString(data.Value, "token") ?? Token;
        return SessionId != null && Token != null;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        if (Token == null && !await RefreshAsync().ConfigureAwait(false))
            return false;

        var body = JsonSerializer.Serialize(new { username, password, rememberMe = false, sessionId = SessionId });
        using var response = await PostAsync("/auth/user/login", body, withAuth: true).ConfigureAwait(false);
        if (response == null)
            return false;

        var data = await ParseDataAsync(response).ConfigureAwait(false);
        if (data == null)
            return false;

        SessionId = GetString(data.Value, "sessionId") ?? SessionId;
        Token = GetString(data.Value, "token") ?? Token;
        IsLoggedIn = true;

        // The login response's token can be pre-upgrade; a refresh against the
        // logged-in session hands out a JWT carrying the account's access.
        await RefreshAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Drops the current session so the next fetch authenticates from scratch
    /// (used when the user changes credentials).</summary>
    public void ResetAuth()
    {
        SessionId = null;
        Token = null;
        IsLoggedIn = false;
    }

    /// <summary>Fetches the app bootstrap state (maintenance/restart timeline).</summary>
    public Task<JsonDocument?> GetAppAsync() => GetAsync($"{BaseUrl}/app?sessionId=");

    /// <summary>Fetches a data center's tracker state - the spawn windows live here.
    /// Served to anonymous sessions too; login only adds the account-gated extras.</summary>
    public Task<JsonDocument?> GetDataCenterAsync(string dataCenter)
        => GetAsync($"{BaseUrl}/app/data-center/{dataCenter}?sessionId=");

    /// <summary>GET with the session appended, retrying once through a token refresh on
    /// 401 (the JWTs only live ~15 minutes).</summary>
    private async Task<JsonDocument?> GetAsync(string urlWithSessionParam)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (SessionId == null || Token == null)
            {
                if (!await RefreshAsync().ConfigureAwait(false))
                    return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, urlWithSessionParam + SessionId);
            request.Headers.TryAddWithoutValidation("Authorization", Token);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Svc.Log.Warning($"Faloop app request failed: {ex.Message}");
                return null;
            }

            using (response)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Token = null;
                    IsLoggedIn = false;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Svc.Log.Warning($"Faloop app request returned {(int)response.StatusCode}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonDocument.Parse(json);
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage?> PostAsync(string path, string body, bool withAuth)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
            if (withAuth && Token != null)
                request.Headers.TryAddWithoutValidation("Authorization", Token);
            return await http.SendAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Faloop request {path} failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<JsonElement?> ParseDataAsync(HttpResponseMessage response)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                Svc.Log.Warning($"Faloop API reported failure: {json[..Math.Min(json.Length, 300)]}");
                return null;
            }

            return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : null;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Could not parse Faloop response: {ex.Message}");
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
