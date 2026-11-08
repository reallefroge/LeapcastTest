using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LeapcastStudioAI;

/// <summary>Which alerts to show.</summary>
public sealed record AlertOptions(bool Follows = true, bool Subs = true, bool Gifts = true, bool Bits = true, int MinBits = 1,
    bool Raids = true, bool SuperChats = true, bool Donations = true, double Seconds = 6, bool Sound = true)
{
    public bool Wants(AlertEvent a) => a.Kind switch
    {
        AlertKind.Follow or AlertKind.Subscriber => Follows,
        AlertKind.Sub or AlertKind.Resub or AlertKind.Member or AlertKind.MemberMilestone => Subs,
        AlertKind.GiftSub or AlertKind.GiftMembers => Gifts,
        AlertKind.Bits => Bits && a.Count >= Math.Max(1, MinBits),
        AlertKind.Raid => Raids,
        AlertKind.SuperChat or AlertKind.SuperSticker => SuperChats,
        AlertKind.Donation => Donations,
        _ => true,
    };
}

/// <summary>
/// Twitch + YouTube chat in one stream of messages (the chat panel and the chat overlay), and the alerts that come
/// with them (plus Streamlabs, if connected): matched across sources so nothing pops twice, then shown one at a
/// time on the alerts overlay.
/// </summary>
public sealed class ChatHub : IDisposable
{
    private readonly TwitchChat _twitch = new();
    private readonly YouTubeChat _youtube;
    private readonly StreamlabsSocket _streamlabs = new();
    private readonly object _lock = new();
    private readonly List<ChatItem> _recent = new();
    private readonly Dictionary<string, DateTime> _alertSeen = new();
    private readonly Queue<AlertEvent> _alertQueue = new();
    private AlertOptions _options = new();
    private string _key = "";

    public event Action<ChatItem>? ItemAdded;
    /// <summary>An alert going on screen now (one at a time, for <see cref="AlertOptions.Seconds"/>).</summary>
    public event Action<AlertEvent>? AlertShown;
    public event Action? StateChanged;
    public event Action<string>? Debug;
    public OverlayServer Overlay { get; }

    public string TwitchChannel { get; private set; } = "";
    public string YouTubeInput { get; private set; } = "";
    public bool TwitchConnected => _twitch.Connected;
    public string YouTubeState => _youtube.State;
    public bool StreamlabsConnected => _streamlabs.Connected;
    public bool StreamlabsOn { get; private set; }
    public AlertOptions Options => _options;

    public ChatHub(YouTubeChat? youtube = null, int overlayPort = 17580)
    {
        _youtube = youtube ?? new YouTubeChat();
        Overlay = new OverlayServer(overlayPort, () => Recent().TakeLast(20).ToList());
        _twitch.Item += Add;
        _twitch.Debug += m => Debug?.Invoke(m);
        _youtube.Item += Add;
        _youtube.Debug += m => Debug?.Invoke(m);
        _youtube.StateChanged += () => StateChanged?.Invoke();
        _streamlabs.Alert += a => RaiseAlert(a);
        _streamlabs.Debug += m => Debug?.Invoke(m);
        _streamlabs.StateChanged += () => StateChanged?.Invoke();
        _ = AlertLoopAsync();
    }

    /// <summary>Connects to what's set (anything blank is left off); unchanged settings keep their connections.</summary>
    public void Configure(string? twitch, string? youtube, string? streamlabsToken, AlertOptions options)
    {
        _options = options;
        string tw = TwitchChat.ChannelFrom(twitch) ?? "";
        string yt = (youtube ?? "").Trim();
        string sl = (streamlabsToken ?? "").Trim();
        string key = $"{tw}|{yt}|{sl}";
        if (key == _key) return;
        string[] old = _key.Split('|');
        _key = key;
        if (old.Length < 3 || old[0] != tw) { _twitch.Stop(); TwitchChannel = tw; if (tw.Length > 0) _twitch.Start(tw, "!clip", ChatClipWho.Mods); }
        if (old.Length < 3 || old[1] != yt) { _youtube.Stop(); YouTubeInput = yt; if (yt.Length > 0) _youtube.Start(yt); }
        if (old.Length < 3 || old[2] != sl) { _streamlabs.Stop(); StreamlabsOn = sl.Length > 0; if (StreamlabsOn) _streamlabs.Start(sl); }
        Overlay.Start();
        StateChanged?.Invoke();
    }

    public IReadOnlyList<ChatItem> Recent() { lock (_lock) return _recent.ToList(); }

    /// <summary>A message or event arrived (from chat).</summary>
    public void Add(ChatItem item)
    {
        lock (_lock)
        {
            _recent.Add(item);
            if (_recent.Count > 300) _recent.RemoveRange(0, _recent.Count - 300);
        }
        ItemAdded?.Invoke(item);
        Overlay.Send(OverlayServer.ChatJson(item));
        if (item.Alert is { } a) RaiseAlert(a);
    }

    /// <summary>Queues an alert unless the same one was just seen (chat and Streamlabs both report subs, bits…).</summary>
    public bool RaiseAlert(AlertEvent a)
    {
        if (!_options.Wants(a)) return false;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var k in _alertSeen.Where(x => now - x.Value > TimeSpan.FromSeconds(60)).Select(x => x.Key).ToList()) _alertSeen.Remove(k);
            if (_alertSeen.TryGetValue(a.Key, out var at) && now - at < TimeSpan.FromSeconds(30)) return false;
            _alertSeen[a.Key] = now;
            if (_alertQueue.Count < 50) _alertQueue.Enqueue(a);
        }
        return true;
    }

    /// <summary>Fires a sample alert (the "Test alert" button).</summary>
    public void Test(AlertKind kind)
    {
        var now = DateTime.UtcNow;
        string who = "Leapfrog" + Random.Shared.Next(10, 99);
        RaiseAlert(kind switch
        {
            AlertKind.Bits => new AlertEvent(ChatPlatform.Twitch, kind, who, now, "500 bits", 500, "Nice play!"),
            AlertKind.Raid => new AlertEvent(ChatPlatform.Twitch, kind, who, now, Count: 42),
            AlertKind.SuperChat => new AlertEvent(ChatPlatform.YouTube, kind, who, now, "$5.00", Message: "Love the stream!"),
            AlertKind.Donation => new AlertEvent(null, kind, who, now, "$10.00", Message: "Keep it up"),
            AlertKind.GiftSub => new AlertEvent(ChatPlatform.Twitch, kind, who, now, Count: 5),
            AlertKind.Member => new AlertEvent(ChatPlatform.YouTube, kind, who, now),
            AlertKind.Resub => new AlertEvent(ChatPlatform.Twitch, kind, who, now, Count: 7, Message: "Still here!"),
            _ => new AlertEvent(ChatPlatform.Twitch, kind, who, now),
        } with { User = who });
    }

    private async Task AlertLoopAsync()
    {
        while (!_disposed)
        {
            AlertEvent? next = null;
            lock (_lock) if (_alertQueue.Count > 0) next = _alertQueue.Dequeue();
            if (next is null) { await Task.Delay(200); continue; }
            try
            {
                AlertShown?.Invoke(next);
                Overlay.Send(OverlayServer.AlertJson(next, _options.Seconds));
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.Seconds, 3, 20) + 0.8));
        }
    }

    private volatile bool _disposed;
    public void Dispose()
    {
        _disposed = true;
        _twitch.Dispose(); _youtube.Dispose(); _streamlabs.Dispose(); Overlay.Dispose();
    }
}

/// <summary>
/// The overlays for your scenes, served on this PC only (http://127.0.0.1:port): /alerts and /chat, transparent
/// pages a Browser source shows; they follow /events (server-sent events).
/// </summary>
public sealed class OverlayServer : IDisposable
{
    private readonly int _preferred;
    private readonly Func<IReadOnlyList<ChatItem>> _history;
    private TcpListener? _listener;
    private readonly List<Stream> _clients = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    public int Port { get; private set; }
    public string AlertsUrl => $"http://127.0.0.1:{Port}/alerts";
    public string ChatUrl => $"http://127.0.0.1:{Port}/chat";

    public OverlayServer(int preferredPort, Func<IReadOnlyList<ChatItem>> history) { _preferred = preferredPort; _history = history; }

    public void Start()
    {
        if (_listener is not null) return;
        for (int p = _preferred; p < _preferred + 20 && _listener is null; p++)
        {
            try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); _listener = l; Port = p; }
            catch (SocketException) { }
        }
        if (_listener is null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { var c = await _listener.AcceptTcpClientAsync(ct); _ = Task.Run(() => ServeAsync(c, ct)); }
                catch { if (ct.IsCancellationRequested) break; }
            }
        });
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(15000, ct); } catch { break; }
                Write(": keep-alive\n\n");
            }
        });
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        try
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string? first = await reader.ReadLineAsync(ct);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { } // headers
            string path = first?.Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
            string route = path.Split('?')[0].TrimEnd('/');
            if (route == "/events")
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\nAccess-Control-Allow-Origin: *\r\n\r\nretry: 2000\n\n"), ct);
                foreach (var it in _history()) await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {ChatJson(it, old: true)}\n\n"), ct);
                lock (_lock) _clients.Add(stream);
                return; // stays open; written to by Send
            }
            string? html = route switch { "/alerts" => OverlayPages.Alerts, "/chat" => OverlayPages.Chat, "" => OverlayPages.Index, _ => null };
            byte[] body = Encoding.UTF8.GetBytes(html ?? "Not found");
            string head = $"HTTP/1.1 {(html is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            await stream.WriteAsync(body, ct);
            client.Close();
        }
        catch { try { client.Close(); } catch { } }
    }

    public void Send(string json) => Write($"data: {json}\n\n");

    private void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        List<Stream> clients;
        lock (_lock) clients = _clients.ToList();
        foreach (var c in clients)
        {
            try { lock (c) c.Write(bytes); }
            catch { lock (_lock) _clients.Remove(c); try { c.Dispose(); } catch { } }
        }
    }

    public static string ChatJson(ChatItem it, bool old = false) => JsonSerializer.Serialize(new
    {
        t = "chat", id = it.Id, p = it.Platform.ToString().ToLowerInvariant(), u = it.User, c = it.NameColor, b = it.Badges,
        parts = it.Parts.Select(x => new { t = x.Text, img = x.ImageUrl }), alert = it.Alert is not null, old,
    });

    public static string AlertJson(AlertEvent a, double seconds) => JsonSerializer.Serialize(new
    {
        t = "alert", kind = a.Kind.ToString(), p = a.Platform?.ToString().ToLowerInvariant() ?? "donation", u = a.User,
        head = a.Headline, sub = a.Subline, amount = a.Amount, ms = (int)(Math.Clamp(seconds, 3, 20) * 1000),
    });

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        lock (_lock) { foreach (var c in _clients) try { c.Dispose(); } catch { } _clients.Clear(); }
    }
}
