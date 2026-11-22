using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioVibes.Spotify;

public sealed class SpotifyNotConfiguredException(string msg) : Exception(msg);

public sealed record SpotifyProfile(string Id, string DisplayName);

public sealed record PlaylistHit(string Uri, string Name);

/// <summary>Current playback: the playlist/album it's playing from, the track, and how far in.</summary>
public sealed record Playback(string? ContextUri, string? TrackUri, int ProgressMs, bool IsPlaying);

/// <summary>
/// Spotify Web API (Premium): Authorization Code with PKCE, no client secret.
/// Redirect: http://127.0.0.1:8888/callback (Spotify rejects "localhost"; loopback IP literals may use http).
/// Refresh token is stored with DPAPI (CurrentUser).
/// </summary>
public sealed class SpotifyWebApi(string tokenPath)
{
    public const string RedirectUri = "http://127.0.0.1:8888/callback";
    public const string Scopes = "user-modify-playback-state user-read-playback-state user-library-modify";
    private const string Api = "https://api.spotify.com/v1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private string? _accessToken;
    private DateTime _expires;

    public string ClientId { get; set; } = "";
    public bool Enabled { get; set; }
    public bool HasRefreshToken => File.Exists(tokenPath);

    // ---------------- OAuth ----------------

    /// <summary>Opens the browser for consent and waits for the loopback callback.</summary>
    public async Task AuthorizeAsync(Action<string> openBrowser, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId)) throw new SpotifyNotConfiguredException("spotify.clientId is empty in config.json");
        string verifier = B64Url(RandomNumberGenerator.GetBytes(64));
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = B64Url(RandomNumberGenerator.GetBytes(16));

        // TcpListener rather than HttpListener: binding http.sys to 127.0.0.1 needs an admin URL ACL.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 8888);
        listener.Start();
        try
        {
        var url = "https://accounts.spotify.com/authorize?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(ClientId)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&state={state}" +
                  $"&code_challenge_method=S256&code_challenge={challenge}";
        openBrowser(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        System.Collections.Specialized.NameValueCollection q;
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(timeout.Token) ?? "";
            var parts = requestLine.Split(' ');
            var target = parts.Length > 1 ? parts[1] : "/";
            if (!target.StartsWith("/callback", StringComparison.Ordinal))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
                continue; // e.g. favicon
            }
            int qi = target.IndexOf('?');
            q = System.Web.HttpUtility.ParseQueryString(qi >= 0 ? target[qi..] : "");
            bool good = q["state"] == state && q["code"] != null;
            var html = Encoding.UTF8.GetBytes(good
                ? "<h2>WoW Audio Vibes is connected to Spotify. You can close this tab.</h2>"
                : $"<h2>Spotify login failed: {WebUtility.HtmlEncode(q["error"] ?? "state mismatch")}</h2>");
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, timeout.Token);
            await stream.WriteAsync(html, timeout.Token);
            break;
        }
        bool ok = q["state"] == state && q["code"] != null;
        if (!ok) throw new InvalidOperationException("Spotify authorization failed: " + (q["error"] ?? "state mismatch"));

        await TokenRequestAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = q["code"]!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        });
        }
        finally { listener.Stop(); }
    }

    private async Task TokenRequestAsync(Dictionary<string, string> form)
    {
        using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"token request failed {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        _expires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { } refresh)
            SaveRefreshToken(refresh);
    }

    private void SaveRefreshToken(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        File.WriteAllBytes(tokenPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }

    private string? LoadRefreshToken() => File.Exists(tokenPath)
        ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(tokenPath), null, DataProtectionScope.CurrentUser))
        : null;

    private async Task<string> TokenAsync()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(ClientId))
            throw new SpotifyNotConfiguredException("Spotify Web API not set up");
        if (_accessToken != null && DateTime.UtcNow < _expires) return _accessToken;
        var refresh = LoadRefreshToken() ?? throw new SpotifyNotConfiguredException("Spotify not connected (tray → Connect Spotify)");
        await TokenRequestAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = ClientId });
        return _accessToken!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string path, object? json = null)
    {
        var req = new HttpRequestMessage(m, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());
        if (json != null) req.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        else if (m == HttpMethod.Put) req.Content = new StringContent("", Encoding.UTF8, "application/json");
        return await Http.SendAsync(req);
    }

    // ---------------- Account ----------------

    /// <summary>The connected account (GET /v1/me). Display name falls back to the user id.</summary>
    public async Task<SpotifyProfile> GetProfileAsync()
    {
        using var resp = await SendAsync(HttpMethod.Get, "/me");
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"profile request failed {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        string id = doc.RootElement.GetProperty("id").GetString() ?? "";
        string? name = doc.RootElement.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString() : null;
        return new SpotifyProfile(id, string.IsNullOrWhiteSpace(name) ? id : name!);
    }

    /// <summary>Forget the stored login on this PC. (To fully revoke, remove the app at spotify.com/account/apps.)</summary>
    public void Disconnect()
    {
        _accessToken = null;
        if (File.Exists(tokenPath)) File.Delete(tokenPath);
    }

    /// <summary>First usable playlist for a search query (GET /v1/search, type=playlist).
    /// Spotify can return null entries in playlist results; those and empty playlists are skipped.</summary>
    public async Task<PlaylistHit?> SearchPlaylistAsync(string query)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"/search?type=playlist&limit=10&q={Uri.EscapeDataString(query)}");
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"search failed {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("playlists", out var pl) || !pl.TryGetProperty("items", out var items)) return null;
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) continue;
            var uri = it.TryGetProperty("uri", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(uri)) continue;
            if (it.TryGetProperty("tracks", out var tr) && tr.ValueKind == JsonValueKind.Object &&
                tr.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number && tot.GetInt32() < 10) continue;
            var name = it.TryGetProperty("name", out var n) ? n.GetString() ?? uri : uri;
            return new PlaylistHit(uri, name);
        }
        return null;
    }

    // ---------------- Actions ----------------

    /// <summary>Start a playlist/album context. Retries with this PC's device if there's no active device.</summary>
    /// <param name="offsetTrackUri">Start at this track (resume), or null for the start / a shuffled pick.</param>
    /// <param name="positionMs">Position within that track.</param>
    public async Task PlayContextAsync(string contextUri, bool shuffle, string? offsetTrackUri = null, int? positionMs = null)
    {
        // Set shuffle first so the playlist starts on a random track; with no active device that fails,
        // so try again once playback has started.
        bool shuffled = await TrySetShuffleAsync(shuffle);
        await PlayContextAsync(contextUri, offsetTrackUri, positionMs);
        if (!shuffled) await TrySetShuffleAsync(shuffle);
    }

    /// <summary>What's playing now (GET /me/player/currently-playing), or null if nothing is.</summary>
    public async Task<Playback?> GetPlaybackAsync()
    {
        using var resp = await SendAsync(HttpMethod.Get, "/me/player/currently-playing");
        if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string? context = root.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Object &&
                          c.TryGetProperty("uri", out var cu) ? cu.GetString() : null;
        string? track = root.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object &&
                        it.TryGetProperty("uri", out var iu) ? iu.GetString() : null;
        int progress = root.TryGetProperty("progress_ms", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        bool playing = root.TryGetProperty("is_playing", out var ip) && ip.ValueKind == JsonValueKind.True;
        return new Playback(context, track, progress, playing);
    }

    public async Task<bool> TrySetShuffleAsync(bool on)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Put, $"/me/player/shuffle?state={(on ? "true" : "false")}");
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task PlayContextAsync(string contextUri, string? offsetTrackUri = null, int? positionMs = null)
    {
        var body = new Dictionary<string, object> { ["context_uri"] = contextUri };
        if (offsetTrackUri != null) body["offset"] = new Dictionary<string, string> { ["uri"] = offsetTrackUri };
        if (positionMs is > 0) body["position_ms"] = positionMs.Value;
        using var resp = await SendAsync(HttpMethod.Put, "/me/player/play", body);
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            var deviceId = await PickDeviceAsync() ?? throw new InvalidOperationException("No Spotify device found. Open Spotify on this PC.");
            using var retry = await SendAsync(HttpMethod.Put, $"/me/player/play?device_id={Uri.EscapeDataString(deviceId)}", body);
            if (retry.IsSuccessStatusCode) return;
            throw new HttpRequestException($"play failed {(int)retry.StatusCode}: {await retry.Content.ReadAsStringAsync()}");
        }
        throw new HttpRequestException($"play failed {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<string?> PickDeviceAsync()
    {
        using var resp = await SendAsync(HttpMethod.Get, "/me/player/devices");
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var devices = doc.RootElement.GetProperty("devices").EnumerateArray().ToList();
        string machine = Environment.MachineName;
        var pick = devices.FirstOrDefault(d => string.Equals(d.GetProperty("name").GetString(), machine, StringComparison.OrdinalIgnoreCase));
        if (pick.ValueKind == JsonValueKind.Undefined)
            pick = devices.FirstOrDefault(d => d.GetProperty("type").GetString() == "Computer");
        return pick.ValueKind == JsonValueKind.Undefined ? null : pick.GetProperty("id").GetString();
    }

    /// <summary>Saves the currently playing track. Returns its name.</summary>
    public async Task<string> LikeCurrentAsync()
    {
        using var resp = await SendAsync(HttpMethod.Get, "/me/player/currently-playing");
        if (resp.StatusCode == HttpStatusCode.NoContent) throw new InvalidOperationException("Nothing is playing");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Current item is not a track");
        string uri = item.GetProperty("uri").GetString()!;
        string id = item.GetProperty("id").GetString()!;
        string name = item.GetProperty("name").GetString() ?? uri;

        // Current endpoint (Feb 2026 migration): PUT /me/library?uris=...
        using var save = await SendAsync(HttpMethod.Put, $"/me/library?uris={Uri.EscapeDataString(uri)}");
        if (save.IsSuccessStatusCode) return name;
        // Fallback: deprecated PUT /me/tracks?ids=...
        using var legacy = await SendAsync(HttpMethod.Put, $"/me/tracks?ids={Uri.EscapeDataString(id)}");
        if (legacy.IsSuccessStatusCode) return name;
        throw new HttpRequestException($"save failed {(int)save.StatusCode}: {await save.Content.ReadAsStringAsync()}");
    }

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
