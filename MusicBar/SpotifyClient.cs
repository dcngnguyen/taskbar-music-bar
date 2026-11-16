using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace MusicBar;

public sealed class SpotifyException(string message, string? code = null) : Exception(message)
{
    /// <summary>Spotify's error code (for example "invalid_grant"), or "not_connected".</summary>
    public string? Code { get; } = code;
}

/// <summary>One song in the popup list. Only <see cref="IsCurrent"/> changes after loading.</summary>
public sealed class TrackRow(string title, string subtitle, string? imageUrl, string uri, int position,
                             string album, int durationMs) : INotifyPropertyChanged
{
    private bool _isCurrent;

    public string Title { get; } = title;
    /// <summary>The artists (or the podcast name).</summary>
    public string Subtitle { get; } = subtitle;
    public string? ImageUrl { get; } = imageUrl;
    public string Uri { get; } = uri;
    /// <summary>Place in the playlist or album (used to start playback there).</summary>
    public int Position { get; } = position;
    public string Album { get; } = album;
    public int DurationMs { get; } = durationMs;
    /// <summary>When the song was added to the playlist (playlists only).</summary>
    public DateTime? AddedAt { get; set; }
    /// <summary>Spotify only queues real tracks and episodes, not local files.</summary>
    public bool CanQueue => Uri.StartsWith("spotify:track:") || Uri.StartsWith("spotify:episode:");

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record QueueResult(bool SpotifyActive, IReadOnlyList<TrackRow> Items);

/// <summary>What Spotify is playing: the song, and the playlist or album it's playing from.</summary>
public sealed record NowPlaying(string? ContextType, string? ContextUri, string? ItemUri, string? AlbumName, string? AlbumImageUrl);

/// <summary>All songs of the playlist or album that is playing.</summary>
public sealed record ContextTracks(string Name, IReadOnlyList<TrackRow> Rows);

/// <summary>
/// Talks to the Spotify Web API to read the play queue.
/// Login uses "Authorization Code with PKCE", so no secret is stored in the app.
/// </summary>
public sealed class SpotifyClient
{
    public const int Port = 43821;
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    private const string Scopes = "user-read-currently-playing user-read-playback-state user-modify-playback-state " +
                                  "playlist-read-private playlist-read-collaborative";
    private const int MaxListSongs = 500;
    private static readonly TimeSpan ListCacheTime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (DateTime LoadedUtc, ContextTracks? Tracks)> _listCache = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string? _accessToken;
    private DateTime _accessExpiresUtc;

    public string? ClientId { get; set; }

    public bool IsConnected => !string.IsNullOrEmpty(ClientId) && TokenStore.Load() is not null;

    /// <summary>Bumped on Disconnect so a login refresh already in flight is thrown away.</summary>
    private int _generation;

    public void Disconnect()
    {
        _generation++;
        TokenStore.Clear();
        _accessToken = null;
        _myId = null;
        _myPlaylists = null;
        _listCache.Clear();
    }

    // ---------- Login ----------

    public async Task ConnectAsync(string clientId, CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch (SocketException)
        {
            throw new SpotifyException($"Another program is using port {Port}. Close it and try again.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var url = "https://accounts.spotify.com/authorize?" + Form(new()
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = RedirectUri,
                ["scope"] = Scopes,
                ["state"] = state,
                ["code_challenge_method"] = "S256",
                ["code_challenge"] = challenge,
            });
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            string code;
            try { code = await WaitForCodeAsync(listener, state, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SpotifyException("Timed out waiting for Spotify. Try again.");
            }

            var json = await PostTokenAsync(new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
            }, ct);
            ClientId = clientId;
            StoreTokens(json);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Waits for the browser to come back to http://127.0.0.1:43821/callback with the login code.</summary>
    private static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => result.TrySetCanceled(ct));
        _ = AcceptLoopAsync();
        return await result.Task;

        // Browsers sometimes open extra empty connections, so each one is handled on its own.
        async Task AcceptLoopAsync()
        {
            while (!result.Task.IsCompleted)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch { return; }
                _ = HandleAsync(client);
            }
        }

        async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var stream = client.GetStream();
                    string? requestLine;
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true))
                    {
                        requestLine = await reader.ReadLineAsync(readTimeout.Token);
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(readTimeout.Token))) { }
                    }

                    var parts = requestLine?.Split(' ');
                    if (parts is not { Length: >= 2 } || parts[0] != "GET" || !parts[1].StartsWith('/'))
                    {
                        await RespondAsync(stream, 400, "Bad request.", ct);
                        return;
                    }

                    var uri = new Uri("http://127.0.0.1" + parts[1]);
                    if (uri.AbsolutePath != "/callback")
                    {
                        await RespondAsync(stream, 404, "Not found.", ct);
                        return;
                    }

                    var query = HttpUtility.ParseQueryString(uri.Query);
                    if (query["state"] != expectedState)
                    {
                        await RespondAsync(stream, 400, "This sign-in link is out of date. Go back to Music Bar and press Connect again.", ct);
                        return;
                    }
                    if (query["error"] is { } error)
                    {
                        await RespondAsync(stream, 200, "Spotify connection cancelled. You can close this tab.", ct);
                        result.TrySetException(new SpotifyException(
                            error == "access_denied" ? "You declined access in Spotify." : $"Spotify said: {error}"));
                        return;
                    }
                    if (string.IsNullOrEmpty(query["code"]))
                    {
                        await RespondAsync(stream, 400, "Spotify did not send a login code. Try again.", ct);
                        return;
                    }

                    await RespondAsync(stream, 200, "Music Bar is connected to Spotify. You can close this tab.", ct);
                    result.TrySetResult(query["code"]!);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or UriFormatException)
                {
                    // An empty or broken browser connection. Ignore it and keep waiting.
                }
            }
        }
    }

    private static async Task RespondAsync(Stream stream, int status, string message, CancellationToken ct)
    {
        var html = "<!doctype html><meta charset=utf-8><title>Music Bar</title>" +
                   "<body style=\"font-family:Segoe UI,sans-serif;background:#1f1f1f;color:#fff;" +
                   "display:grid;place-items:center;height:100vh;margin:0\"><p>" +
                   WebUtility.HtmlEncode(message) + "</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    // ---------- Tokens ----------

    private static async Task<JsonElement> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
            {
                Content = new FormUrlEncodedContent(form),
            }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException)
        {
            throw new SpotifyException($"Unexpected reply from Spotify ({(int)response.StatusCode}). Try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = GetString(json, "error");
            var description = GetString(json, "error_description");
            if (error == "invalid_client")
                throw new SpotifyException("Spotify doesn't recognise that Client ID. Copy it again from the developer dashboard.", error);
            throw new SpotifyException($"Spotify login failed: {description ?? error ?? ((int)response.StatusCode).ToString()}", error);
        }
        return json;
    }

    private void StoreTokens(JsonElement json)
    {
        // An older connection may lack permissions added in a later version of Music Bar.
        if (GetString(json, "scope") is { } granted &&
            Scopes.Split(' ').Except(granted.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Any())
        {
            Disconnect();
            throw new SpotifyException("Music Bar needs a few more Spotify permissions. Connect again.", "not_connected");
        }

        _accessToken = GetString(json, "access_token")
                       ?? throw new SpotifyException("Spotify did not return a login token. Try again.");
        int seconds = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        _accessExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60));

        // Spotify may send a new refresh token; the old one then stops working.
        if (GetString(json, "refresh_token") is { } refresh) TokenStore.Save(refresh);
    }

    private Task<string>? _refreshing;

    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _accessToken is not null && DateTime.UtcNow < _accessExpiresUtc) return _accessToken;

        // One refresh at a time, and never cancelled halfway: Spotify may replace the refresh
        // token, and losing the reply would log you out.
        var refreshing = _refreshing ??= RefreshAccessTokenAsync();
        // If it finished instantly (e.g. not connected), its own cleanup ran before it was stored.
        if (refreshing.IsCompleted && _refreshing == refreshing) _refreshing = null;
        return await refreshing.WaitAsync(ct);
    }

    private async Task<string> RefreshAccessTokenAsync()
    {
        int generation = _generation;
        try
        {
            var refresh = TokenStore.Load();
            if (refresh is null || string.IsNullOrEmpty(ClientId))
                throw new SpotifyException("Not connected to Spotify.", "not_connected");

            var json = await PostTokenAsync(new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = ClientId,
            }, CancellationToken.None);

            // You pressed Disconnect while this was on its way: don't log back in.
            if (generation != _generation) throw new SpotifyException("Not connected to Spotify.", "not_connected");
            StoreTokens(json);
            return _accessToken!;
        }
        catch (SpotifyException ex) when (ex.Code is "invalid_grant" or "invalid_client")
        {
            Disconnect();
            throw new SpotifyException("The Spotify connection expired. Connect again.", "not_connected");
        }
        finally
        {
            // Finished (or failed): the next caller starts a fresh refresh instead of reusing this one.
            _refreshing = null;
        }
    }

    // ---------- Web API calls ----------

    /// <summary>Calls the Web API, refreshing the login once if Spotify says it expired.</summary>
    private async Task<(HttpStatusCode Status, string Body)> ApiAsync(
        HttpMethod method, string pathAndQuery, string? jsonBody, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(forceRefresh: attempt > 0, ct);
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(method, "https://api.spotify.com/v1" + pathAndQuery);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                return request;
            }, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new SpotifyException("Spotify asked us to slow down. Try again in a minute.");
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
    }

    /// <summary>Turns a failed reply into a message a person can act on.</summary>
    private static void EnsureSuccess(HttpStatusCode status, string body)
    {
        if ((int)status is >= 200 and < 300) return;
        Log.Write($"Spotify {(int)status}: {body}");

        string? message = null, reason = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                message = GetString(error, "message");
                reason = GetString(error, "reason");
            }
        }
        catch (JsonException) { }

        if (reason == "NO_ACTIVE_DEVICE" || (status == HttpStatusCode.NotFound && message?.Contains("device", StringComparison.OrdinalIgnoreCase) == true))
            throw new SpotifyException("Spotify isn't open on any device. Start playing something in Spotify first.");
        if (reason == "PREMIUM_REQUIRED")
            throw new SpotifyException("Spotify says this needs Premium.");
        if (status == HttpStatusCode.Forbidden)
            throw new SpotifyException(
                "Spotify refused the request" + (message is null ? "" : $" ({message})") +
                ". Check that your account is added under User Management in your Spotify developer app.");
        throw new SpotifyException($"Spotify error {(int)status}" + (message is null ? "" : $": {message}") + ". Try again later.");
    }

    public async Task<QueueResult> GetQueueAsync(CancellationToken ct)
    {
        var (status, body) = await ApiAsync(HttpMethod.Get, "/me/player/queue", null, ct);
        if (status == HttpStatusCode.NoContent || body.Length == 0) return new QueueResult(false, []);
        EnsureSuccess(status, body);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool active = root.TryGetProperty("currently_playing", out var current) && current.ValueKind == JsonValueKind.Object;

            var rows = new List<TrackRow>();
            if (root.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in queue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object) rows.Add(ParseRow(item, rows.Count));
                }
            }
            return new QueueResult(active, rows);
        }
        catch (JsonException)
        {
            throw new SpotifyException("Unexpected reply from Spotify. Try again later.");
        }
    }

    /// <summary>The song playing and where it's playing from, or null when Spotify is idle.</summary>
    public async Task<NowPlaying?> GetNowPlayingAsync(CancellationToken ct)
    {
        var (status, body) = await ApiAsync(HttpMethod.Get, "/me/player/currently-playing?additional_types=episode", null, ct);
        if (status == HttpStatusCode.NoContent || body.Length == 0) return null;
        EnsureSuccess(status, body);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            root.TryGetProperty("context", out var context);
            root.TryGetProperty("item", out var item);
            JsonElement album = default;
            if (item.ValueKind == JsonValueKind.Object) item.TryGetProperty("album", out album);
            return new NowPlaying(
                GetString(context, "type"),
                GetString(context, "uri"),
                GetString(item, "uri"),
                GetString(album, "name"),
                SmallestImage(album));
        }
        catch (JsonException)
        {
            throw new SpotifyException("Unexpected reply from Spotify. Try again later.");
        }
    }

    /// <summary>
    /// All songs of the playing playlist or album. Null when Spotify won't share them
    /// (personal developer apps can only read playlists you own or collaborate on).
    /// </summary>
    public async Task<ContextTracks?> GetContextTracksAsync(NowPlaying now, CancellationToken ct)
    {
        if (now.ContextUri is not { } uri) return null;
        if (_listCache.TryGetValue(uri, out var cached) && DateTime.UtcNow - cached.LoadedUtc < ListCacheTime)
            return cached.Tracks;

        var id = Uri.EscapeDataString(uri.Split(':')[^1]);
        ContextTracks? tracks = now.ContextType switch
        {
            "playlist" => await LoadPlaylistAsync(id, ct),
            "album" => await LoadAlbumAsync(id, now, ct),
            _ => null,
        };
        _listCache[uri] = (DateTime.UtcNow, tracks);
        return tracks;
    }

    private async Task<ContextTracks?> LoadPlaylistAsync(string id, CancellationToken ct)
    {
        var rows = new List<TrackRow>();
        int position = 0;
        for (int offset = 0; offset < MaxListSongs; offset += 50)
        {
            var (status, body) = await ApiAsync(HttpMethod.Get, $"/playlists/{id}/items?limit=50&offset={offset}", null, ct);
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
            EnsureSuccess(status, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            foreach (var entry in items.EnumerateArray())
            {
                // Newer replies call it "item", older ones "track". Unavailable songs are null but keep their place.
                if ((entry.TryGetProperty("item", out var song) || entry.TryGetProperty("track", out song)) &&
                    song.ValueKind == JsonValueKind.Object)
                {
                    var row = ParseRow(song, position);
                    if (DateTime.TryParse(GetString(entry, "added_at"), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var added))
                        row.AddedAt = added;
                    rows.Add(row);
                }
                position++;
            }
            if (GetString(root, "next") is null) break;
        }
        if (rows.Count == 0) return null; // nothing readable; show "Up next" instead

        var (nameStatus, nameBody) = await ApiAsync(HttpMethod.Get, $"/playlists/{id}?fields=name", null, ct);
        string name = "Playlist";
        if ((int)nameStatus is >= 200 and < 300)
        {
            try
            {
                using var doc = JsonDocument.Parse(nameBody);
                name = GetString(doc.RootElement, "name") ?? name;
            }
            catch (JsonException) { }
        }
        return new ContextTracks(name, rows);
    }

    private async Task<ContextTracks?> LoadAlbumAsync(string id, NowPlaying now, CancellationToken ct)
    {
        var rows = new List<TrackRow>();
        for (int offset = 0; offset < MaxListSongs; offset += 50)
        {
            var (status, body) = await ApiAsync(HttpMethod.Get, $"/albums/{id}/tracks?limit=50&offset={offset}", null, ct);
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
            EnsureSuccess(status, body);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            foreach (var song in items.EnumerateArray())
            {
                // Album track lists don't include the cover, so reuse the playing song's cover.
                if (song.ValueKind == JsonValueKind.Object)
                    rows.Add(ParseRow(song, rows.Count, now.AlbumImageUrl, now.AlbumName));
            }
            if (GetString(root, "next") is null) break;
        }
        return rows.Count == 0 ? null : new ContextTracks(now.AlbumName ?? "Album", rows);
    }

    /// <summary>Plays the playlist or album from the song at <paramref name="position"/>.</summary>
    public Task PlayInContextAsync(string contextUri, int position, CancellationToken ct) =>
        CommandAsync(HttpMethod.Put, "/me/player/play",
            JsonSerializer.Serialize(new { context_uri = contextUri, offset = new { position } }), ct);

    /// <summary>Plays the given songs in order.</summary>
    public Task PlayTracksAsync(IEnumerable<string> uris, CancellationToken ct) =>
        CommandAsync(HttpMethod.Put, "/me/player/play",
            JsonSerializer.Serialize(new { uris = uris.Take(100).ToArray() }), ct);

    public Task AddToQueueAsync(string uri, CancellationToken ct) =>
        CommandAsync(HttpMethod.Post, "/me/player/queue?uri=" + Uri.EscapeDataString(uri), "", ct);

    public Task SetVolumeAsync(int percent, CancellationToken ct) =>
        CommandAsync(HttpMethod.Put, $"/me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}", "", ct);

    /// <summary>Spotify's volume on the playing device, or null if that device can't change volume.</summary>
    public async Task<int?> GetVolumeAsync(CancellationToken ct)
    {
        var (status, body) = await ApiAsync(HttpMethod.Get, "/me/player", null, ct);
        if (status == HttpStatusCode.NoContent || body.Length == 0)
            throw new SpotifyException("Spotify isn't playing on any device.");
        EnsureSuccess(status, body);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.Object) return null;
            if (device.TryGetProperty("supports_volume", out var supports) && supports.ValueKind == JsonValueKind.False) return null;
            return device.TryGetProperty("volume_percent", out var v) && v.TryGetInt32(out var percent) ? percent : null;
        }
        catch (JsonException)
        {
            throw new SpotifyException("Unexpected reply from Spotify. Try again later.");
        }
    }

    private string? _myId;
    private (DateTime LoadedUtc, IReadOnlyList<(string Name, string Uri)> Lists)? _myPlaylists;

    /// <summary>
    /// Your playlists whose songs Spotify will share: ones you own or collaborate on.
    /// Cached for 5 minutes.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, string Uri)>> GetMyPlaylistsAsync(CancellationToken ct)
    {
        if (_myPlaylists is { } cached && DateTime.UtcNow - cached.LoadedUtc < ListCacheTime) return cached.Lists;

        if (_myId is null)
        {
            var (meStatus, meBody) = await ApiAsync(HttpMethod.Get, "/me", null, ct);
            EnsureSuccess(meStatus, meBody);
            using var me = JsonDocument.Parse(meBody);
            _myId = GetString(me.RootElement, "id");
        }

        var lists = new List<(string Name, string Uri)>();
        for (int offset = 0; offset < 500; offset += 50)
        {
            var (status, body) = await ApiAsync(HttpMethod.Get, $"/me/playlists?limit=50&offset={offset}", null, ct);
            EnsureSuccess(status, body);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            foreach (var p in items.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                bool mine = p.TryGetProperty("owner", out var owner) && GetString(owner, "id") is { } ownerId && ownerId == _myId;
                bool shared = p.TryGetProperty("collaborative", out var c) && c.ValueKind == JsonValueKind.True;
                if ((mine || shared) && GetString(p, "uri") is { } uri)
                    lists.Add((GetString(p, "name") ?? "Untitled playlist", uri));
            }
            if (GetString(root, "next") is null) break;
        }
        _myPlaylists = (DateTime.UtcNow, lists);
        return lists;
    }

    private async Task CommandAsync(HttpMethod method, string pathAndQuery, string body, CancellationToken ct)
    {
        var (status, text) = await ApiAsync(method, pathAndQuery, body, ct);
        EnsureSuccess(status, text);
    }

    private static TrackRow ParseRow(JsonElement item, int position, string? imageOverride = null, string? albumOverride = null)
    {
        var title = GetString(item, "name") ?? "Unknown";
        var uri = GetString(item, "uri") ?? "";
        int durationMs = item.TryGetProperty("duration_ms", out var d) && d.TryGetInt32(out var ms) ? ms : 0;
        string subtitle;
        string? imageUrl;
        string? albumName;

        if (GetString(item, "type") == "episode")
        {
            subtitle = item.TryGetProperty("show", out var show) ? GetString(show, "name") ?? "" : "";
            imageUrl = item.TryGetProperty("images", out var images) ? SmallestImage(images) : null;
            albumName = subtitle;
        }
        else
        {
            subtitle = item.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join(", ", artists.EnumerateArray().Select(a => GetString(a, "name")).OfType<string>())
                : "";
            item.TryGetProperty("album", out var album);
            imageUrl = SmallestImage(album);
            albumName = GetString(album, "name");
        }
        return new TrackRow(title, subtitle, imageUrl ?? imageOverride, uri, position,
                            albumName ?? albumOverride ?? "", durationMs);
    }

    /// <summary>Images are listed widest first; the last one is the small thumbnail.</summary>
    private static string? SmallestImage(JsonElement albumOrImages)
    {
        var images = albumOrImages;
        if (images.ValueKind == JsonValueKind.Object && !images.TryGetProperty("images", out images)) return null;
        return images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0
            ? GetString(images[images.GetArrayLength() - 1], "url")
            : null;
    }

    // ---------- Helpers ----------

    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        try
        {
            using var request = makeRequest();
            return await Http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new SpotifyException("Can't reach Spotify. Check your internet connection.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpotifyException("Spotify took too long to answer. Try again.");
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Form(Dictionary<string, string> values) =>
        string.Join("&", values.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
