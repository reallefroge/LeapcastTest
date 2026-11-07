using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeapcastStudioAI;

/// <summary>A YouTube live broadcast (a scheduled or running event on the channel).</summary>
public sealed record YouTubeBroadcast(string Id, string Title, DateTime? ScheduledStartUtc, string LifeCycle, string Privacy,
    string? BoundStreamId, bool AutoStart)
{
    /// <summary>"Live now", "Ready", "Scheduled Sat 7:00 PM"…</summary>
    public string When => LifeCycle switch
    {
        "live" => "Live now",
        "testing" or "testStarting" or "liveStarting" => "Starting",
        _ => ScheduledStartUtc is { } t ? "Scheduled " + t.ToLocalTime().ToString("ddd MMM d, h:mm tt", CultureInfo.CurrentCulture) : "Ready",
    };
}

/// <summary>Where to send video for a broadcast (its bound stream's RTMP ingestion point).</summary>
public sealed record YouTubeIngest(string StreamId, string ServerUrl, string StreamKey);

/// <summary>
/// YouTube Live through the YouTube Data API: sign in with Google (in the browser, PKCE, a loopback redirect: no
/// password ever passes through Leapcast), list the channel's upcoming and live broadcasts, and get the exact stream
/// a chosen broadcast is bound to, so going live lands on that broadcast. Needs an OAuth "Desktop app" client ID.
/// </summary>
public sealed class YouTubeApi
{
    public const string Scope = "https://www.googleapis.com/auth/youtube";
    public string AuthUrl { get; init; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public string TokenUrl { get; init; } = "https://oauth2.googleapis.com/token";
    public string ApiBase { get; init; } = "https://www.googleapis.com/youtube/v3";

    private readonly HttpClient _http;
    private readonly string _clientId, _clientSecret;
    private string? _refresh, _access;
    private DateTime _accessUntil;

    /// <summary>Called with a new refresh token (to keep, encrypted) after signing in.</summary>
    public Action<string>? RefreshTokenChanged { get; set; }

    public YouTubeApi(HttpClient http, string clientId, string clientSecret, string? refreshToken)
    {
        _http = http;
        _clientId = clientId.Trim();
        _clientSecret = clientSecret.Trim();
        _refresh = string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken;
    }

    public bool SignedIn => _refresh is not null;

    // ------------------------------------------------------------------ sign-in

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Opens Google's sign-in in the browser (<paramref name="openBrowser"/>) and waits for it to come back to a
    /// one-time local address. Returns once signed in.
    /// </summary>
    public async Task SignInAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string redirect = $"http://127.0.0.1:{port}/";
            string url = AuthUrl + "?" + Query(new()
            {
                ["client_id"] = _clientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = Scope,
                ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
                ["access_type"] = "offline", ["prompt"] = "consent",
            });
            openBrowser(url);
            string? code = null, error = null;
            while (code is null && error is null)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, ct);
                string request = Encoding.ASCII.GetString(buf, 0, n);
                string path = request.Split(' ').ElementAtOrDefault(1) ?? "/";
                var q = ParseQuery(path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "");
                bool ours = q.TryGetValue("state", out var st) && st == state;
                if (ours && q.TryGetValue("code", out var c)) code = c;
                else if (ours && q.TryGetValue("error", out var e)) error = e;
                string html = code is not null
                    ? "<h2 style='font-family:Segoe UI'>Signed in. You can close this tab and go back to Leapcast.</h2>"
                    : error is not null ? "<h2 style='font-family:Segoe UI'>Sign-in was cancelled.</h2>" : "";
                var body = Encoding.UTF8.GetBytes("<html><body style='background:#0B0E18;color:#E9ECF8'>" + html + "</body></html>");
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, ct);
                await stream.WriteAsync(body, ct);
            }
            if (error is not null) throw new InvalidOperationException(error == "access_denied" ? "Sign-in was cancelled." : "Google sign-in failed: " + error);
            var tok = await PostFormAsync(TokenUrl, new()
            {
                ["client_id"] = _clientId, ["client_secret"] = _clientSecret, ["code"] = code!, ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code", ["redirect_uri"] = redirect,
            }, ct);
            TakeTokens(tok);
            if (_refresh is null) throw new InvalidOperationException("Google didn't return a sign-in that lasts (no refresh token). Try again.");
        }
        finally { listener.Stop(); }
    }

    public void SignOut() { _refresh = null; _access = null; }

    private void TakeTokens(JsonNode tok)
    {
        _access = tok["access_token"]?.GetValue<string>();
        _accessUntil = DateTime.UtcNow + TimeSpan.FromSeconds((tok["expires_in"]?.GetValue<int>() ?? 3600) - 60);
        if (tok["refresh_token"]?.GetValue<string>() is { Length: > 0 } r && r != _refresh)
        {
            _refresh = r;
            RefreshTokenChanged?.Invoke(r);
        }
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_access is not null && DateTime.UtcNow < _accessUntil) return _access;
        if (_refresh is null) throw new InvalidOperationException("Sign in with Google first.");
        var tok = await PostFormAsync(TokenUrl, new()
        {
            ["client_id"] = _clientId, ["client_secret"] = _clientSecret, ["refresh_token"] = _refresh, ["grant_type"] = "refresh_token",
        }, ct);
        TakeTokens(tok);
        return _access ?? throw new InvalidOperationException("Google didn't return an access token.");
    }

    // ------------------------------------------------------------------ broadcasts

    /// <summary>The channel's broadcasts that can be streamed to: live or starting first, then upcoming by start time.</summary>
    public async Task<List<YouTubeBroadcast>> ListBroadcastsAsync(CancellationToken ct)
    {
        var all = new List<YouTubeBroadcast>();
        foreach (var status in new[] { "active", "upcoming" })
        {
            var json = await GetAsync($"liveBroadcasts?part=id,snippet,contentDetails,status&broadcastStatus={status}&broadcastType=all&maxResults=50", ct);
            foreach (var item in json["items"]?.AsArray() ?? new JsonArray())
                if (item is not null) all.Add(ParseBroadcast(item));
        }
        return all.GroupBy(b => b.Id).Select(g => g.First())
            .OrderBy(b => b.LifeCycle is "live" or "liveStarting" or "testing" or "testStarting" ? 0 : 1)
            .ThenBy(b => b.ScheduledStartUtc ?? DateTime.MaxValue).ToList();
    }

    private static YouTubeBroadcast ParseBroadcast(JsonNode item)
    {
        DateTime? start = null;
        if (item["snippet"]?["scheduledStartTime"]?.GetValue<string>() is { } s
            && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) start = t;
        return new YouTubeBroadcast(
            item["id"]?.GetValue<string>() ?? "",
            item["snippet"]?["title"]?.GetValue<string>() ?? "(untitled)",
            start,
            item["status"]?["lifeCycleStatus"]?.GetValue<string>() ?? "",
            item["status"]?["privacyStatus"]?.GetValue<string>() ?? "",
            item["contentDetails"]?["boundStreamId"]?.GetValue<string>(),
            item["contentDetails"]?["enableAutoStart"]?.GetValue<bool>() ?? false);
    }

    /// <summary>A new broadcast starting now (starts and ends with the stream).</summary>
    public async Task<YouTubeBroadcast> CreateBroadcastAsync(string title, string privacy, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["snippet"] = new JsonObject { ["title"] = title, ["scheduledStartTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) },
            ["status"] = new JsonObject { ["privacyStatus"] = privacy, ["selfDeclaredMadeForKids"] = false },
            ["contentDetails"] = new JsonObject { ["enableAutoStart"] = true, ["enableAutoStop"] = true },
        };
        return ParseBroadcast(await SendAsync(HttpMethod.Post, "liveBroadcasts?part=id,snippet,contentDetails,status", body, ct));
    }

    /// <summary>
    /// The stream the broadcast is bound to (its server and key). A broadcast with no stream yet is bound to the
    /// channel's reusable stream (created once, named "Leapcast"), so it's the one YouTube expects video on.
    /// </summary>
    public async Task<YouTubeIngest> IngestForAsync(YouTubeBroadcast b, CancellationToken ct)
    {
        string? streamId = b.BoundStreamId;
        JsonNode? stream = null;
        if (streamId is not null)
            stream = (await GetAsync($"liveStreams?part=id,cdn,snippet,contentDetails&id={Uri.EscapeDataString(streamId)}", ct))["items"]?.AsArray().FirstOrDefault();
        if (stream is null)
        {
            var mine = (await GetAsync("liveStreams?part=id,cdn,snippet,contentDetails&mine=true&maxResults=50", ct))["items"]?.AsArray() ?? new JsonArray();
            stream = mine.FirstOrDefault(s => s?["snippet"]?["title"]?.GetValue<string>() == "Leapcast" && s?["contentDetails"]?["isReusable"]?.GetValue<bool>() == true)
                     ?? mine.FirstOrDefault(s => s?["contentDetails"]?["isReusable"]?.GetValue<bool>() == true && s?["cdn"]?["ingestionType"]?.GetValue<string>() == "rtmp");
            if (stream is null)
            {
                var body = new JsonObject
                {
                    ["snippet"] = new JsonObject { ["title"] = "Leapcast" },
                    ["cdn"] = new JsonObject { ["ingestionType"] = "rtmp", ["resolution"] = "variable", ["frameRate"] = "variable" },
                    ["contentDetails"] = new JsonObject { ["isReusable"] = true },
                };
                stream = await SendAsync(HttpMethod.Post, "liveStreams?part=id,snippet,cdn,contentDetails", body, ct);
            }
            streamId = stream["id"]?.GetValue<string>() ?? throw new InvalidOperationException("YouTube didn't return a stream.");
            await SendAsync(HttpMethod.Post, $"liveBroadcasts/bind?id={Uri.EscapeDataString(b.Id)}&part=id,contentDetails&streamId={Uri.EscapeDataString(streamId)}", null, ct);
        }
        var info = stream["cdn"]?["ingestionInfo"];
        string server = info?["ingestionAddress"]?.GetValue<string>() ?? "rtmp://a.rtmp.youtube.com/live2";
        string key = info?["streamName"]?.GetValue<string>() ?? throw new InvalidOperationException("YouTube didn't return the stream key for this broadcast.");
        return new YouTubeIngest(streamId!, server, key);
    }

    /// <summary>
    /// After video starts flowing: a broadcast that doesn't start by itself is switched to live once YouTube sees the
    /// stream. Gives up after <paramref name="wait"/>. Returns what happened.
    /// </summary>
    public async Task<string> GoLiveWhenReadyAsync(YouTubeBroadcast b, string streamId, TimeSpan wait, CancellationToken ct, TimeSpan? poll = null)
    {
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            var bc = (await GetAsync($"liveBroadcasts?part=id,status,contentDetails,snippet&id={Uri.EscapeDataString(b.Id)}", ct))["items"]?.AsArray().FirstOrDefault();
            string life = bc?["status"]?["lifeCycleStatus"]?.GetValue<string>() ?? "";
            if (life is "live" or "liveStarting") return "live";
            var st = (await GetAsync($"liveStreams?part=id,status&id={Uri.EscapeDataString(streamId)}", ct))["items"]?.AsArray().FirstOrDefault();
            bool receiving = st?["status"]?["streamStatus"]?.GetValue<string>() == "active";
            bool auto = bc?["contentDetails"]?["enableAutoStart"]?.GetValue<bool>() ?? false;
            if (receiving && !auto && life is "ready" or "testing")
            {
                await SendAsync(HttpMethod.Post, $"liveBroadcasts/transition?broadcastStatus=live&id={Uri.EscapeDataString(b.Id)}&part=id,status", null, ct);
                return "switched to live";
            }
            await Task.Delay(poll ?? TimeSpan.FromSeconds(5), ct);
        }
        return "still waiting for YouTube (check YouTube Studio)";
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<JsonNode> GetAsync(string path, CancellationToken ct) => await SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var token = await AccessTokenAsync(ct);
        using var req = new HttpRequestMessage(method, ApiBase.TrimEnd('/') + "/" + path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("YouTube: " + ErrorText(text, resp.StatusCode));
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    private async Task<JsonNode> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (text.Contains("invalid_grant")) { _refresh = null; throw new InvalidOperationException("Your Google sign-in expired. Sign in again."); }
            throw new InvalidOperationException("Google sign-in: " + ErrorText(text, resp.StatusCode));
        }
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private static string ErrorText(string json, HttpStatusCode code)
    {
        try
        {
            var n = JsonNode.Parse(json);
            var msg = n?["error"]?["message"]?.GetValue<string>() ?? n?["error_description"]?.GetValue<string>() ?? n?["error"]?.ToString();
            if (msg is { Length: > 0 }) return msg;
        }
        catch (JsonException) { }
        return $"HTTP {(int)code}";
    }

    private static string Query(Dictionary<string, string> q) =>
        string.Join("&", q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));

    public static Dictionary<string, string> ParseQuery(string q)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            string k = Uri.UnescapeDataString(i < 0 ? part : part[..i]).Replace('+', ' ');
            string v = i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }
}
