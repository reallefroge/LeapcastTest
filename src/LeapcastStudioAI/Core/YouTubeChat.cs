using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>
/// Reads a YouTube live chat the way the youtube.com chat window does (no API key, no sign-in, no quota): paste
/// the channel (or a live video) link; while the channel is live it follows the chat, Super Chats, Super Stickers
/// and memberships, and checks again by itself when a stream ends or hasn't started yet.
/// </summary>
public sealed class YouTubeChat : IDisposable
{
    public event Action<ChatItem>? Item;
    public event Action<string>? Debug;
    /// <summary>"Live" (reading chat), "Waiting" (not live yet), "Error: …".</summary>
    public string State { get; private set; } = "Off";
    public string? VideoId { get; private set; }
    public event Action? StateChanged;

    private CancellationTokenSource? _cts;
    private readonly HttpClient _http;
    /// <summary>Tests: where youtube.com is.</summary>
    public string Origin { get; init; } = "https://www.youtube.com";

    public YouTubeChat(HttpMessageHandler? handler = null)
    {
        var h = handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        _http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        // skip the EU cookie-consent page
        _http.DefaultRequestHeaders.Add("Cookie", "SOCS=CAI; CONSENT=YES+1");
    }

    /// <summary>A video id from a watch / live / youtu.be link, or null.</summary>
    public static string? VideoIdFrom(string text)
    {
        var m = Regex.Match(text, @"(?:v=|youtu\.be/|/live/|/shorts/|/embed/)([A-Za-z0-9_-]{11})");
        return m.Success ? m.Groups[1].Value : Regex.IsMatch(text.Trim(), "^[A-Za-z0-9_-]{11}$") && !text.Trim().StartsWith("@") ? text.Trim() : null;
    }

    /// <summary>The channel's page from what was typed: "@name", a channel link, or a channel id.</summary>
    public string? ChannelUrl(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var m = Regex.Match(text, @"youtube\.com/((?:@[\w.\-]+)|(?:channel/UC[\w-]{22})|(?:c/[\w.\-]+)|(?:user/[\w.\-]+))", RegexOptions.IgnoreCase);
        if (m.Success) return $"{Origin}/{m.Groups[1].Value}";
        if (text.StartsWith("@")) return $"{Origin}/{text}";
        if (Regex.IsMatch(text, @"^UC[\w-]{22}$")) return $"{Origin}/channel/{text}";
        if (Regex.IsMatch(text, @"^[\w.\-]{3,30}$")) return $"{Origin}/@{text}";
        return null;
    }

    public void Start(string channelOrVideo)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(channelOrVideo, ct));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        SetState("Off");
    }

    private void SetState(string s) { if (State == s) return; State = s; StateChanged?.Invoke(); }

    private async Task RunAsync(string input, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? video = VideoIdFrom(input) ?? await LiveVideoAsync(input, ct);
                if (video is null) { SetState("Waiting"); await Task.Delay(TimeSpan.FromSeconds(45), ct); continue; }
                VideoId = video;
                await FollowChatAsync(video, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { SetState("Error: " + ex.Message); Debug?.Invoke("YouTube chat: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { break; }
        }
    }

    /// <summary>The video the channel is live with right now (null when it isn't).</summary>
    public async Task<string?> LiveVideoAsync(string channel, CancellationToken ct)
    {
        string? url = ChannelUrl(channel);
        if (url is null) throw new InvalidOperationException("That doesn't look like a YouTube channel link.");
        string html = await _http.GetStringAsync(url + "/live", ct);
        var canon = Regex.Match(html, @"<link rel=""canonical"" href=""[^""]*watch\?v=([A-Za-z0-9_-]{11})""");
        if (!canon.Success) return null;
        // the /live page also shows an upcoming stream: only a stream that's on air has a chat worth following now
        bool live = html.Contains("\"isLiveNow\":true") || html.Contains("\"isLive\":true");
        return live ? canon.Groups[1].Value : null;
    }

    private async Task FollowChatAsync(string video, CancellationToken ct)
    {
        string html = await _http.GetStringAsync($"{Origin}/live_chat?is_popout=1&v={video}", ct);
        var (key, version, continuation, initial) = ParseChatPage(html);
        if (continuation is null) { SetState("Waiting"); return; }
        SetState("Live");
        Debug?.Invoke($"YouTube chat: reading {video}");
        // the last few messages already on the page (no alerts for those: they happened before)
        foreach (var it in initial.TakeLast(15)) Item?.Invoke(it with { Alert = null });
        var seen = new HashSet<string>(initial.Select(i => i.Id));
        while (!ct.IsCancellationRequested && continuation is not null)
        {
            var body = JsonSerializer.Serialize(new { context = new { client = new { clientName = "WEB", clientVersion = version, hl = "en" } }, continuation });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Origin}/youtubei/v1/live_chat/get_live_chat?key={key}&prettyPrint=false")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"YouTube answered {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            var (items, next, wait) = ParsePoll(json, DateTime.UtcNow);
            foreach (var it in items) if (seen.Add(it.Id)) Item?.Invoke(it);
            if (seen.Count > 5000) seen.Clear();
            continuation = next;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(wait, 1000, 5000)), ct);
        }
        SetState("Waiting"); // the stream ended
    }

    // ------------------------------------------------------------------ parsing (public for tests)

    /// <summary>From the live_chat page: the API key, client version, the first continuation and the messages on the page.</summary>
    public static (string Key, string Version, string? Continuation, List<ChatItem> Initial) ParseChatPage(string html)
    {
        string key = Regex.Match(html, @"""INNERTUBE_API_KEY""\s*:\s*""([^""]+)""").Groups[1].Value;
        string version = Regex.Match(html, @"""INNERTUBE_CONTEXT_CLIENT_VERSION""\s*:\s*""([^""]+)""").Groups[1].Value;
        if (version.Length == 0) version = "2.20240101.00.00";
        var items = new List<ChatItem>();
        string? continuation = null;
        var m = Regex.Match(html, @"(?:window\[""ytInitialData""\]|var ytInitialData)\s*=\s*(\{.+?\});\s*</script>", RegexOptions.Singleline);
        if (m.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                if (doc.RootElement.TryGetProperty("contents", out var c) && c.TryGetProperty("liveChatRenderer", out var lcr))
                {
                    continuation = Continuation(lcr, out _);
                    if (lcr.TryGetProperty("actions", out var acts)) items.AddRange(Actions(acts, DateTime.UtcNow));
                }
            }
            catch (JsonException) { }
        }
        continuation ??= Regex.Match(html, @"""continuation""\s*:\s*""([^""]+)""") is { Success: true } cm ? cm.Groups[1].Value : null;
        return (key, version, continuation, items);
    }

    /// <summary>One poll's answer: new items, the next continuation (null: the chat is over) and how long to wait.</summary>
    public static (List<ChatItem> Items, string? Next, int WaitMs) ParsePoll(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("continuationContents", out var cc) || !cc.TryGetProperty("liveChatContinuation", out var lcc))
            return (new(), null, 0);
        var next = Continuation(lcc, out int wait);
        var items = lcc.TryGetProperty("actions", out var acts) ? Actions(acts, now) : new List<ChatItem>();
        return (items, next, wait);
    }

    private static string? Continuation(JsonElement e, out int waitMs)
    {
        waitMs = 2000;
        if (!e.TryGetProperty("continuations", out var conts) || conts.ValueKind != JsonValueKind.Array) return null;
        foreach (var c in conts.EnumerateArray())
            foreach (var kind in new[] { "invalidationContinuationData", "timedContinuationData", "reloadContinuationData", "liveChatReplayContinuationData" })
                if (c.TryGetProperty(kind, out var d) && d.TryGetProperty("continuation", out var cont))
                {
                    if (d.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out int ms)) waitMs = ms;
                    return cont.GetString();
                }
        return null;
    }

    private static List<ChatItem> Actions(JsonElement actions, DateTime now)
    {
        var list = new List<ChatItem>();
        foreach (var a in actions.EnumerateArray())
        {
            JsonElement item;
            if (a.TryGetProperty("addChatItemAction", out var add) && add.TryGetProperty("item", out item)) { }
            else if (a.TryGetProperty("replayChatItemAction", out var rep) && rep.TryGetProperty("actions", out var inner)) { list.AddRange(Actions(inner, now)); continue; }
            else continue;
            if (Parse(item, now) is { } ci) list.Add(ci);
        }
        return list;
    }

    private static ChatItem? Parse(JsonElement item, DateTime now)
    {
        foreach (var prop in item.EnumerateObject())
        {
            var r = prop.Value;
            string id = Str(r, "id") ?? Guid.NewGuid().ToString("N");
            string user = Simple(r, "authorName") ?? "";
            var badges = Badges(r);
            var msg = Runs(r, "message");
            switch (prop.Name)
            {
                case "liveChatTextMessageRenderer":
                    return new ChatItem(ChatPlatform.YouTube, id, user, null, badges, msg, now);
                case "liveChatPaidMessageRenderer":
                {
                    string amount = Simple(r, "purchaseAmountText") ?? "";
                    var alert = new AlertEvent(ChatPlatform.YouTube, AlertKind.SuperChat, user, now, amount, Message: string.Concat(msg.Select(p => p.Text)));
                    return new ChatItem(ChatPlatform.YouTube, id, user, null, badges, Prepend($"💰 {amount} ", msg), now, alert);
                }
                case "liveChatPaidStickerRenderer":
                {
                    string amount = Simple(r, "purchaseAmountText") ?? "";
                    var alert = new AlertEvent(ChatPlatform.YouTube, AlertKind.SuperSticker, user, now, amount);
                    return new ChatItem(ChatPlatform.YouTube, id, user, null, badges, new List<ChatPart> { new($"💰 {amount} Super Sticker") }, now, alert);
                }
                case "liveChatMembershipItemRenderer":
                {
                    var header = Runs(r, "headerSubtext");
                    var primary = Runs(r, "headerPrimaryText");
                    string headerText = string.Concat(header.Select(p => p.Text));
                    string primaryText = string.Concat(primary.Select(p => p.Text));
                    // a milestone: "Member for 6 months" (primary) + their message; a new member: "Welcome to …" (subtext)
                    var months = Regex.Match(primaryText, @"(\d+)\s+month");
                    var alert = primaryText.Length > 0
                        ? new AlertEvent(ChatPlatform.YouTube, AlertKind.MemberMilestone, user, now, Count: months.Success ? int.Parse(months.Groups[1].Value) : 0,
                            Message: string.Concat(msg.Select(p => p.Text)))
                        : new AlertEvent(ChatPlatform.YouTube, AlertKind.Member, user, now, Message: headerText);
                    var shown = new List<ChatPart> { new("⭐ " + (primaryText.Length > 0 ? primaryText : headerText.Length > 0 ? headerText : "New member") + (msg.Count > 0 ? " · " : "")) };
                    shown.AddRange(msg);
                    return new ChatItem(ChatPlatform.YouTube, id, user, null, badges, shown, now, alert);
                }
                case "liveChatSponsorshipsGiftPurchaseAnnouncementRenderer":
                {
                    var h = r.TryGetProperty("header", out var hd) && hd.TryGetProperty("liveChatSponsorshipsHeaderRenderer", out var hr) ? hr : r;
                    user = Simple(h, "authorName") ?? user;
                    string text = string.Concat(Runs(h, "primaryText").Select(p => p.Text));
                    var n = Regex.Match(text, @"(\d+)");
                    int count = n.Success ? int.Parse(n.Groups[1].Value) : 1;
                    var alert = new AlertEvent(ChatPlatform.YouTube, AlertKind.GiftMembers, user, now, Count: count);
                    return new ChatItem(ChatPlatform.YouTube, id, user, null, Badges(h), new List<ChatPart> { new("🎁 " + (text.Length > 0 ? text : $"Gifted {count} memberships")) }, now, alert);
                }
            }
        }
        return null;
    }

    private static List<ChatPart> Prepend(string text, List<ChatPart> parts) { var l = new List<ChatPart> { new(text) }; l.AddRange(parts); return l; }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Simple(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.TryGetProperty("simpleText", out var s)) return s.GetString();
        return v.TryGetProperty("runs", out _) ? string.Concat(Runs(e, name).Select(p => p.Text)) : null;
    }

    private static List<ChatPart> Runs(JsonElement e, string name)
    {
        var parts = new List<ChatPart>();
        if (!e.TryGetProperty(name, out var v)) return parts;
        if (v.TryGetProperty("simpleText", out var st)) { parts.Add(new ChatPart(st.GetString() ?? "")); return parts; }
        if (!v.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return parts;
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out var t)) parts.Add(new ChatPart(t.GetString() ?? ""));
            else if (run.TryGetProperty("emoji", out var em))
            {
                string alt = em.TryGetProperty("shortcuts", out var sc) && sc.ValueKind == JsonValueKind.Array && sc.GetArrayLength() > 0
                    ? sc[0].GetString() ?? "" : Str(em, "emojiId") ?? "";
                bool custom = em.TryGetProperty("isCustomEmoji", out var ic) && ic.ValueKind == JsonValueKind.True;
                string? img = null;
                if (em.TryGetProperty("image", out var im) && im.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Array && th.GetArrayLength() > 0)
                    img = Str(th[th.GetArrayLength() - 1], "url");
                // standard emoji are plain Unicode characters: keep them as text; channel emotes are pictures
                string emojiId = Str(em, "emojiId") ?? "";
                parts.Add(custom || emojiId.Length == 0 ? new ChatPart(alt, img) : new ChatPart(emojiId));
            }
        }
        return parts;
    }

    private static List<string> Badges(JsonElement r)
    {
        var list = new List<string>();
        if (!r.TryGetProperty("authorBadges", out var bs) || bs.ValueKind != JsonValueKind.Array) return list;
        foreach (var b in bs.EnumerateArray())
        {
            if (!b.TryGetProperty("liveChatAuthorBadgeRenderer", out var br)) continue;
            string icon = br.TryGetProperty("icon", out var ic) ? Str(ic, "iconType") ?? "" : "";
            string tip = Str(br, "tooltip") ?? "";
            string kind = icon switch { "OWNER" => "broadcaster", "MODERATOR" => "moderator", "VERIFIED" => "verified", _ => br.TryGetProperty("customThumbnail", out _) ? "member" : "" };
            if (kind.Length == 0 && tip.Contains("Member", StringComparison.OrdinalIgnoreCase)) kind = "member";
            if (kind.Length > 0 && !list.Contains(kind)) list.Add(kind);
        }
        return list;
    }

    public void Dispose() { Stop(); _http.Dispose(); }
}
