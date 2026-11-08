using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LeapcastStudioAI;

/// <summary>
/// Optional: your Streamlabs alerts (Streamlabs → Settings → API Tokens → "Your Socket API Token"). Adds what chat
/// can't tell: Twitch follows, YouTube subscribers and Streamlabs donations (plus subs, bits, raids, Super Chats,
/// which are matched with the ones seen in chat so nothing pops twice).
/// </summary>
public sealed class StreamlabsSocket : IDisposable
{
    public event Action<AlertEvent>? Alert;
    public event Action<string>? Debug;
    public bool Connected { get; private set; }
    public event Action? StateChanged;
    private CancellationTokenSource? _cts;
    /// <summary>Tests: where the socket server is.</summary>
    public string Server { get; init; } = "wss://sockets.streamlabs.com";

    public void Start(string token)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(token)) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(token.Trim(), ct));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        if (Connected) { Connected = false; StateChanged?.Invoke(); }
    }

    private async Task RunAsync(string token, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri($"{Server}/socket.io/?token={Uri.EscapeDataString(token)}&EIO=3&transport=websocket"), ct);
                int pingMs = 25000;
                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var buf = new byte[64 * 1024];
                var sb = new StringBuilder();
                Task? pinger = null;
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    sb.Clear();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, ct);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    string msg = sb.ToString();
                    if (msg.StartsWith("0{"))
                    {
                        try { using var d = JsonDocument.Parse(msg[1..]); if (d.RootElement.TryGetProperty("pingInterval", out var pi)) pingMs = pi.GetInt32(); } catch { }
                        // Engine.IO 3: the client pings, the server answers "3"
                        pinger = Task.Run(async () =>
                        {
                            while (!pingCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                            {
                                await Task.Delay(pingMs, pingCts.Token);
                                await ws.SendAsync(Encoding.UTF8.GetBytes("2"), WebSocketMessageType.Text, true, pingCts.Token);
                            }
                        }, pingCts.Token);
                    }
                    else if (msg == "40") { Connected = true; backoff = TimeSpan.FromSeconds(3); StateChanged?.Invoke(); Debug?.Invoke("Streamlabs: connected"); }
                    else if (msg.StartsWith("42")) foreach (var a in Parse(msg[2..], DateTime.UtcNow)) Alert?.Invoke(a);
                    else if (msg.StartsWith("44")) { Debug?.Invoke("Streamlabs: the token was refused"); break; }
                }
                pingCts.Cancel();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Debug?.Invoke("Streamlabs: " + ex.Message); }
            if (Connected) { Connected = false; StateChanged?.Invoke(); }
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(120, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>One socket.io event (["event", {type, for, message:[…]}]) → alerts.</summary>
    public static List<AlertEvent> Parse(string json, DateTime now)
    {
        var list = new List<AlertEvent>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 2 || arr[0].GetString() != "event") return list;
            var ev = arr[1];
            string type = S(ev, "type") ?? "";
            string forWhom = S(ev, "for") ?? "streamlabs";
            ChatPlatform? platform = forWhom switch { "twitch_account" => ChatPlatform.Twitch, "youtube_account" => ChatPlatform.YouTube, _ => null };
            if (!ev.TryGetProperty("message", out var msgs)) return list;
            var items = msgs.ValueKind == JsonValueKind.Array ? msgs.EnumerateArray().ToList() : new List<JsonElement> { msgs };
            foreach (var m in items)
            {
                string name = S(m, "name") ?? S(m, "display_name") ?? S(m, "from") ?? "Someone";
                string? text = S(m, "message") ?? S(m, "comment");
                int months = I(m, "months");
                AlertEvent? a = (type, platform) switch
                {
                    ("donation", _) => new AlertEvent(null, AlertKind.Donation, name, now, S(m, "formatted_amount") ?? Money(m), Message: text),
                    ("follow", ChatPlatform.YouTube) => new AlertEvent(ChatPlatform.YouTube, AlertKind.Subscriber, name, now),
                    ("follow", _) => new AlertEvent(ChatPlatform.Twitch, AlertKind.Follow, name, now),
                    ("subscription", ChatPlatform.YouTube) => new AlertEvent(ChatPlatform.YouTube, months > 1 ? AlertKind.MemberMilestone : AlertKind.Member, name, now, Count: months),
                    ("subscription", _) => new AlertEvent(ChatPlatform.Twitch, months > 1 ? AlertKind.Resub : AlertKind.Sub, name, now, Count: months, Message: text),
                    ("resub", _) => new AlertEvent(ChatPlatform.Twitch, AlertKind.Resub, name, now, Count: months, Message: text),
                    ("bits", _) => new AlertEvent(ChatPlatform.Twitch, AlertKind.Bits, name, now, $"{I(m, "amount")} bits", I(m, "amount"), text),
                    ("raid", _) => new AlertEvent(ChatPlatform.Twitch, AlertKind.Raid, name, now, Count: I(m, "raiders")),
                    ("superchat", _) => new AlertEvent(ChatPlatform.YouTube, AlertKind.SuperChat, name, now, S(m, "displayString") ?? Money(m), Message: text),
                    _ => null,
                };
                if (a is not null) list.Add(a);
            }
        }
        catch (JsonException) { }
        return list;
    }

    private static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
        ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;
    private static int I(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
            ? v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : int.TryParse(v.GetString(), out int x) ? x : 0 : 0;
    private static string Money(JsonElement m)
    {
        string amt = S(m, "amount") ?? "";
        string cur = S(m, "currency") ?? "";
        return double.TryParse(amt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (cur is "" or "USD" ? $"${d:0.00}" : $"{d:0.00} {cur}") : amt;
    }

    public void Dispose() => Stop();
}
