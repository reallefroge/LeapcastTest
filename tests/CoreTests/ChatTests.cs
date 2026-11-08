using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using LeapcastStudioAI;

/// <summary>Chat panel + alerts: Twitch and YouTube parsing, Streamlabs, matching alerts, the overlay server.</summary>
static class ChatTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var now = DateTime.UtcNow;
        // ---- Twitch
        var m = TwitchChat.ParseItem("@badges=moderator/1,subscriber/12;color=#1E90FF;display-name=Kappa_Fan;emotes=25:6-10;id=abc;mod=1 :kappa_fan!kappa_fan@kappa_fan.tmi.twitch.tv PRIVMSG #chan :hello Kappa world", now);
        check(m is not null && m.User == "Kappa_Fan" && m.Color == "#1E90FF" && m.Badges.Contains("moderator") && m.Badges.Contains("subscriber")
              && m.Parts.Count == 3 && m.Parts[1].ImageUrl!.Contains("/25/") && m.Parts[1].Text == "Kappa" && m.Parts[2].Text == " world",
            $"Twitch chat: name colour, badges and emotes as pictures ({string.Join("|", m?.Parts.Select(p => p.ImageUrl is null ? p.Text : "[" + p.Text + "]") ?? Array.Empty<string>())})");
        var emoji = TwitchChat.ParseItem("@display-name=E;emotes=25:3-7 :e!e@e PRIVMSG #c :😀😀 Kappa", now);
        check(emoji?.Parts.Count == 2 && emoji.Parts[0].Text == "😀😀 " && emoji.Parts[1].Text == "Kappa", "Twitch chat: emote positions count characters (emoji before an emote)");
        var bits = TwitchChat.ParseItem("@bits=500;display-name=Cheerer :cheerer!c@c PRIVMSG #c :Cheer500 great play", now);
        check(bits?.Alert is { Kind: AlertKind.Bits, Count: 500, Amount: "500 bits" } && bits.Alert.Message == "great play", "Twitch chat: bits become an alert");
        var resub = TwitchChat.ParseItem(@"@msg-id=resub;display-name=Loyal;msg-param-cumulative-months=14;system-msg=Loyal\ssubscribed\sfor\s14\smonths! :tmi.twitch.tv USERNOTICE #c :still here", now);
        check(resub?.Alert is { Kind: AlertKind.Resub, Count: 14, User: "Loyal", Message: "still here" } && resub.Text.StartsWith("Loyal subscribed for 14 months!"),
            "Twitch chat: a resub (months, their message, Twitch's own words in chat)");
        var gifts = TwitchChat.ParseItem("@msg-id=submysterygift;display-name=Santa;msg-param-mass-gift-count=10 :tmi.twitch.tv USERNOTICE #c", now);
        var raid = TwitchChat.ParseItem("@msg-id=raid;display-name=raider;msg-param-displayName=BigStreamer;msg-param-viewerCount=321 :tmi.twitch.tv USERNOTICE #c", now);
        check(gifts?.Alert is { Kind: AlertKind.GiftSub, Count: 10 } && raid?.Alert is { Kind: AlertKind.Raid, User: "BigStreamer", Count: 321 }
              && gifts.Alert.Headline == "Santa gifted 10 subs!" && raid.Alert.Subline == "with 321 viewers",
            "Twitch chat: gifted subs and raids");

        // ---- YouTube
        check(YouTubeChat.VideoIdFrom("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1") == "dQw4w9WgXcQ" && YouTubeChat.VideoIdFrom("https://youtu.be/dQw4w9WgXcQ") == "dQw4w9WgXcQ"
              && YouTubeChat.VideoIdFrom("https://www.youtube.com/live/dQw4w9WgXcQ?si=x") == "dQw4w9WgXcQ" && YouTubeChat.VideoIdFrom("@reallefroge") is null,
            "YouTube chat: video links are recognised");
        var ytc = new YouTubeChat();
        check(ytc.ChannelUrl("@reallefroge") == "https://www.youtube.com/@reallefroge" && ytc.ChannelUrl("https://www.youtube.com/@reallefroge/streams") == "https://www.youtube.com/@reallefroge"
              && ytc.ChannelUrl("UCabcdefghijklmnopqrstuv") == "https://www.youtube.com/channel/UCabcdefghijklmnopqrstuv",
            "YouTube chat: channel links, @handles and channel ids");
        ytc.Dispose();
        var (items, next, wait) = YouTubeChat.ParsePoll(PollJson, now);
        var text = items.FirstOrDefault(i => i.Alert is null);
        var sc = items.FirstOrDefault(i => i.Alert?.Kind == AlertKind.SuperChat);
        var mem = items.FirstOrDefault(i => i.Alert?.Kind == AlertKind.Member);
        var mile = items.FirstOrDefault(i => i.Alert?.Kind == AlertKind.MemberMilestone);
        var gift = items.FirstOrDefault(i => i.Alert?.Kind == AlertKind.GiftMembers);
        check(next == "NEXT123" && wait == 1500 && items.Count == 5, $"YouTube chat: a poll gives its messages, the next continuation and the wait ({items.Count} items, {next}, {wait} ms)");
        check(text is { User: "Viewer One" } && text.Text == "hi 😂 :wave:" && text.Parts[3].ImageUrl == "https://yt3.example/wave.png" && text.Badges.Contains("moderator") && text.Badges.Contains("member"),
            $"YouTube chat: text, standard emoji as characters, channel emotes as pictures, badges ({text?.Text})");
        check(sc?.Alert is { Amount: "$5.00", Message: "great stream" } && sc.Alert.Headline == "Rich sent a Super Chat!",
            "YouTube chat: a Super Chat becomes an alert (amount and message)");
        check(mem?.Alert?.User == "Newbie" && mile?.Alert is { Count: 6, Message: "half a year!" } && gift?.Alert is { Count: 5, User: "Gifter" },
            "YouTube chat: new members, membership milestones and gifted memberships");
        var page = YouTubeChat.ParseChatPage(PageHtml);
        check(page.Key == "AIzaTEST" && page.Version == "2.20260101.01.00" && page.Continuation == "FIRST" && page.Initial.Count == 1,
            "YouTube chat: the chat page gives its key, client version, first continuation and recent messages");

        // ---- following a live chat end to end (a stand-in youtube.com)
        int port = FreePort();
        using var fake = new HttpListener();
        fake.Prefixes.Add($"http://127.0.0.1:{port}/");
        fake.Start();
        int polls = 0;
        _ = Task.Run(async () =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await fake.GetContextAsync(); } catch { break; }
                string path = ctx.Request.Url!.AbsolutePath, body;
                if (path == "/@tester/live") body = "<html><link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=AAAAAAAAAAA\"><script>{\"isLiveNow\":true}</script></html>";
                else if (path == "/live_chat") body = PageHtml;
                else if (path.StartsWith("/youtubei/v1/live_chat/get_live_chat")) { Interlocked.Increment(ref polls); body = polls == 1 ? PollJson.Replace("NEXT123", "N2") : "{\"continuationContents\":{\"liveChatContinuation\":{\"continuations\":[{\"timedContinuationData\":{\"continuation\":\"N2\",\"timeoutMs\":1000}}]}}}"; }
                else { ctx.Response.StatusCode = 404; body = ""; }
                var b = Encoding.UTF8.GetBytes(body);
                ctx.Response.OutputStream.Write(b);
                ctx.Response.Close();
            }
        });
        using var live = new YouTubeChat { Origin = $"http://127.0.0.1:{port}" };
        var got = new List<ChatItem>();
        live.Item += i => { lock (got) got.Add(i); };
        live.Start("https://www.youtube.com/@tester");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && (got.Count < 6 || live.State != "Live")) await Task.Delay(100);
        live.Stop();
        fake.Stop();
        check(live.VideoId == "AAAAAAAAAAA" && got.Count >= 6 && got.Count(i => i.Alert is not null) == 4 && got[0].Alert is null,
            $"YouTube chat: finds the channel's live stream and follows its chat ({got.Count} items, {polls} polls; messages already on the page don't alert)");

        // ---- Streamlabs
        var don = StreamlabsSocket.Parse("[\"event\",{\"type\":\"donation\",\"for\":\"streamlabs\",\"message\":[{\"name\":\"Kind Soul\",\"amount\":\"10\",\"currency\":\"USD\",\"formatted_amount\":\"$10.00\",\"message\":\"gl\"}]}]", now);
        var fol = StreamlabsSocket.Parse("[\"event\",{\"type\":\"follow\",\"for\":\"twitch_account\",\"message\":[{\"name\":\"NewFriend\"}]}]", now);
        var ysub = StreamlabsSocket.Parse("[\"event\",{\"type\":\"follow\",\"for\":\"youtube_account\",\"message\":[{\"name\":\"YtFan\"}]}]", now);
        var sbits = StreamlabsSocket.Parse("[\"event\",{\"type\":\"bits\",\"for\":\"twitch_account\",\"message\":[{\"name\":\"Cheerer\",\"amount\":\"500\"}]}]", now);
        check(don.Single() is { Kind: AlertKind.Donation, Amount: "$10.00", User: "Kind Soul", Platform: null } && fol.Single() is { Kind: AlertKind.Follow, Platform: ChatPlatform.Twitch }
              && ysub.Single() is { Kind: AlertKind.Subscriber, Platform: ChatPlatform.YouTube } && sbits.Single() is { Kind: AlertKind.Bits, Count: 500 },
            "Streamlabs: donations, Twitch follows, YouTube subscribers and bits");

        // a stand-in Streamlabs socket (Engine.IO 3 over a WebSocket)
        int sport = FreePort();
        using var sfake = new HttpListener();
        sfake.Prefixes.Add($"http://127.0.0.1:{sport}/");
        sfake.Start();
        string? tokenSeen = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await sfake.GetContextAsync();
                tokenSeen = ctx.Request.QueryString["token"];
                var wsc = await ctx.AcceptWebSocketAsync(null);
                async Task Send(string s) => await wsc.WebSocket.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, default);
                await Send("0{\"sid\":\"x\",\"upgrades\":[],\"pingInterval\":25000,\"pingTimeout\":60000}");
                await Send("40");
                await Task.Delay(200);
                await Send("42[\"event\",{\"type\":\"follow\",\"for\":\"twitch_account\",\"message\":[{\"name\":\"SocketFollower\"}]}]");
                await Task.Delay(3000);
            }
            catch { }
        });
        using var sl = new StreamlabsSocket { Server = $"ws://127.0.0.1:{sport}" };
        AlertEvent? fromSocket = null;
        sl.Alert += a => fromSocket = a;
        sl.Start("TOKEN-123");
        until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && fromSocket is null) await Task.Delay(100);
        bool slConnected = sl.Connected;
        sl.Stop(); sfake.Stop();
        check(tokenSeen == "TOKEN-123" && slConnected && fromSocket is { Kind: AlertKind.Follow, User: "SocketFollower" },
            $"Streamlabs: connects with the socket token and delivers alerts ({fromSocket?.Headline})");

        // ---- the hub: chat + Streamlabs report the same sub → one alert; options; overlay server
        using var hub = new ChatHub(overlayPort: FreePort());
        var shown = new List<AlertEvent>();
        hub.AlertShown += a => { lock (shown) shown.Add(a); };
        hub.Configure(null, null, null, new AlertOptions(Seconds: 3, MinBits: 100));
        hub.Add(TwitchChat.ParseItem("@msg-id=sub;display-name=Dupe;msg-param-cumulative-months=1 :tmi.twitch.tv USERNOTICE #c", now)!);
        bool second = hub.RaiseAlert(new AlertEvent(ChatPlatform.Twitch, AlertKind.Subscriber, "dupe", now));
        bool small = hub.RaiseAlert(new AlertEvent(ChatPlatform.Twitch, AlertKind.Bits, "Tiny", now, "10 bits", 10));
        check(!second && !small, "Alerts: the same sub from chat and Streamlabs shows once; bits under the minimum don't alert");

        using var http = new HttpClient();
        string alertsPage = await http.GetStringAsync(hub.Overlay.AlertsUrl);
        string chatPage = await http.GetStringAsync(hub.Overlay.ChatUrl);
        check(alertsPage.Contains("EventSource(\"/events\")") && chatPage.Contains("EventSource(\"/events\")"), $"Overlays: /alerts and /chat are served on this PC ({hub.Overlay.AlertsUrl})");
        using var sse = await http.GetStreamAsync($"http://127.0.0.1:{hub.Overlay.Port}/events");
        using var reader = new StreamReader(sse);
        hub.Test(AlertKind.Raid);
        hub.Add(new ChatItem(ChatPlatform.YouTube, "zz", "Overlay Tester", null, new List<string>(), new List<ChatPart> { new("hello overlay") }, now));
        var lines = new List<string>();
        var readUntil = DateTime.UtcNow.AddSeconds(8);
        Task<string?>? pending = null;
        while (DateTime.UtcNow < readUntil && !(lines.Any(l => l.Contains("\"t\":\"alert\"") && l.Contains("Raid")) && lines.Any(l => l.Contains("hello overlay"))))
        {
            pending ??= reader.ReadLineAsync();
            if (await Task.WhenAny(pending, Task.Delay(500)) != pending) continue;
            if (pending.Result is { } l && l.StartsWith("data: ")) lines.Add(l);
            pending = null;
        }
        check(lines.Any(l => l.Contains("hello overlay")) && lines.Any(l => l.Contains("\"t\":\"alert\"") && l.Contains("is raiding")),
            $"Overlays: chat and alerts reach the pages live ({lines.Count} events)");
        check(shown.Count >= 1 && shown[0].User == "Dupe", "Alerts: queued alerts go on screen one at a time");
    }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    private const string PollJson = """
{"continuationContents":{"liveChatContinuation":{"continuations":[{"invalidationContinuationData":{"continuation":"NEXT123","timeoutMs":1500}}],
"actions":[
{"addChatItemAction":{"item":{"liveChatTextMessageRenderer":{"id":"m1","authorName":{"simpleText":"Viewer One"},
  "message":{"runs":[{"text":"hi "},{"emoji":{"emojiId":"😂","shortcuts":[":joy:"]}},{"text":" "},{"emoji":{"emojiId":"UCx/wave","shortcuts":[":wave:"],"isCustomEmoji":true,"image":{"thumbnails":[{"url":"https://yt3.example/wave_s.png"},{"url":"https://yt3.example/wave.png"}]}}}]},
  "authorBadges":[{"liveChatAuthorBadgeRenderer":{"icon":{"iconType":"MODERATOR"},"tooltip":"Moderator"}},{"liveChatAuthorBadgeRenderer":{"customThumbnail":{"thumbnails":[]},"tooltip":"Member (6 months)"}}]}}}},
{"addChatItemAction":{"item":{"liveChatPaidMessageRenderer":{"id":"m2","authorName":{"simpleText":"Rich"},"purchaseAmountText":{"simpleText":"$5.00"},"message":{"runs":[{"text":"great stream"}]}}}}},
{"addChatItemAction":{"item":{"liveChatMembershipItemRenderer":{"id":"m3","authorName":{"simpleText":"Newbie"},"headerSubtext":{"runs":[{"text":"Welcome to "},{"text":"Frog Club"},{"text":"!"}]}}}}},
{"addChatItemAction":{"item":{"liveChatMembershipItemRenderer":{"id":"m4","authorName":{"simpleText":"Oldie"},"headerPrimaryText":{"runs":[{"text":"Member for "},{"text":"6"},{"text":" months"}]},"message":{"runs":[{"text":"half a year!"}]}}}}},
{"addChatItemAction":{"item":{"liveChatSponsorshipsGiftPurchaseAnnouncementRenderer":{"id":"m5","header":{"liveChatSponsorshipsHeaderRenderer":{"authorName":{"simpleText":"Gifter"},"primaryText":{"runs":[{"text":"Gifted "},{"text":"5"},{"text":" Frog Club memberships"}]}}}}}}},
{"markChatItemAsDeletedAction":{"targetItemId":"old"}}
]}}}
""";

    private const string PageHtml = """
<html><head><script>ytcfg.set({"INNERTUBE_API_KEY":"AIzaTEST","INNERTUBE_CONTEXT_CLIENT_VERSION":"2.20260101.01.00"});</script>
<script>window["ytInitialData"] = {"contents":{"liveChatRenderer":{"continuations":[{"invalidationContinuationData":{"continuation":"FIRST","timeoutMs":5000}}],
"actions":[{"addChatItemAction":{"item":{"liveChatTextMessageRenderer":{"id":"p1","authorName":{"simpleText":"Earlier"},"message":{"runs":[{"text":"from before"}]}}}}}]}}};</script></head></html>
""";
}
