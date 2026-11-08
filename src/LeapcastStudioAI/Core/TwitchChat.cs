using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LeapcastStudioAI;

/// <summary>Who may clip from chat.</summary>
public enum ChatClipWho { Mods, ModsAndVips, Everyone }

/// <summary>One chat message, with what Twitch says about the sender.</summary>
public sealed record ChatMessage(string User, string Text, bool Broadcaster, bool Moderator, bool Vip, DateTime ReceivedUtc);

/// <summary>
/// Reads a Twitch channel's chat anonymously (read-only, no login or key needed) and raises <see cref="Command"/> when
/// someone allowed types the command (e.g. "!clip"). Reconnects by itself.
/// </summary>
public sealed class TwitchChat : IDisposable
{
    public event Action<ChatMessage>? Command;
    /// <summary>Every chat message (the VOD editor uses chat activity to find moments).</summary>
    public event Action<ChatMessage>? Message;
    public event Action<string>? Debug;
    /// <summary>Every message and event, for the chat panel / overlay (names, colours, badges, emotes, subs, bits, raids).</summary>
    public event Action<ChatItem>? Item;

    public string Host { get; init; } = "irc.chat.twitch.tv";
    public int Port { get; init; } = 6697;
    public bool UseTls { get; init; } = true;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    public string Channel { get; private set; } = "";
    public string Word { get; private set; } = "!clip";
    public ChatClipWho Who { get; private set; } = ChatClipWho.Mods;
    public bool Connected { get; private set; }

    /// <summary>A channel name from what the user typed or a chat link ("twitch.tv/popout/name/chat", "#name").</summary>
    public static string? ChannelFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"twitch\.tv/(?:popout/)?([A-Za-z0-9_]{3,25})", RegexOptions.IgnoreCase);
        string name = m.Success ? m.Groups[1].Value : text.Trim().TrimStart('#', '@');
        if (name.Equals("popout", StringComparison.OrdinalIgnoreCase)) return null;
        return Regex.IsMatch(name, "^[A-Za-z0-9_]{3,25}$") ? name.ToLowerInvariant() : null;
    }

    public static bool Allowed(ChatMessage m, ChatClipWho who) => who switch
    {
        ChatClipWho.Everyone => true,
        ChatClipWho.ModsAndVips => m.Broadcaster || m.Moderator || m.Vip,
        _ => m.Broadcaster || m.Moderator,
    };

    /// <summary>The command word on its own at the start of the message ("!clip", "!clip that was great").</summary>
    public static bool IsCommand(string text, string word) =>
        Regex.IsMatch(text.Trim(), $@"^{Regex.Escape(word.Trim())}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase);

    /// <summary>Parses one IRC line ("@badges=…;… :user!user@… PRIVMSG #chan :text"); null if it isn't a chat message.</summary>
    public static ChatMessage? Parse(string line, DateTime now)
    {
        var m = Regex.Match(line, @"^(?:@(?<tags>\S+) )?:(?<user>[^!\s]+)!\S+ PRIVMSG #\S+ :(?<text>.*)$");
        if (!m.Success) return null;
        var tags = m.Groups["tags"].Value.Split(';').Select(t => t.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        string badges = tags.TryGetValue("badges", out var b) ? b : "";
        bool Has(string badge) => badges.Split(',').Any(x => x.StartsWith(badge + "/", StringComparison.Ordinal));
        bool mod = Has("moderator") || tags.TryGetValue("mod", out var md) && md == "1";
        string user = tags.TryGetValue("display-name", out var dn) && dn.Length > 0 ? dn : m.Groups["user"].Value;
        return new ChatMessage(user, m.Groups["text"].Value.TrimEnd('\r'), Has("broadcaster"), mod, Has("vip"), now);
    }

    private static Dictionary<string, string> Tags(string raw) =>
        raw.Split(';').Select(t => t.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0])
           .ToDictionary(g => g.Key, g => Unescape(g.First()[1]));

    private static string Unescape(string v)
    {
        if (!v.Contains('\\')) return v;
        var sb = new StringBuilder();
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] != '\\' || i + 1 >= v.Length) { sb.Append(v[i]); continue; }
            char n = v[++i];
            sb.Append(n switch { 's' => ' ', ':' => ';', 'r' => '\r', 'n' => '\n', _ => n });
        }
        return sb.ToString();
    }

    /// <summary>
    /// A chat line as the chat panel shows it: PRIVMSG (with emotes as pictures, bits as an alert) or USERNOTICE
    /// (sub, resub, gifted subs, raid). Null for anything else.
    /// </summary>
    public static ChatItem? ParseItem(string line, DateTime now)
    {
        var m = Regex.Match(line.TrimEnd('\r'), @"^(?:@(?<tags>\S+) )?:(?<src>\S+) (?<cmd>PRIVMSG|USERNOTICE) #\S+(?: :(?<text>.*))?$");
        if (!m.Success) return null;
        var tags = Tags(m.Groups["tags"].Value);
        string T(string k) => tags.TryGetValue(k, out var v) ? v : "";
        string login = m.Groups["src"].Value.Split('!')[0];
        string user = T("display-name") is { Length: > 0 } dn ? dn : T("login") is { Length: > 0 } lg ? lg : login;
        string text = m.Groups["text"].Value;
        var badges = T("badges").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(b => b.Split('/')[0])
            .Select(b => b switch { "founder" => "subscriber", _ => b })
            .Where(b => b is "broadcaster" or "moderator" or "vip" or "subscriber" or "partner" or "staff").Distinct().ToList();
        if (T("mod") == "1" && !badges.Contains("moderator")) badges.Add("moderator");
        string? color = T("color") is { Length: 7 } c ? c : null;
        string id = T("id") is { Length: > 0 } i ? i : Guid.NewGuid().ToString("N");
        var parts = Emotes(text, T("emotes"));
        AlertEvent? alert = null;
        if (m.Groups["cmd"].Value == "PRIVMSG")
        {
            if (int.TryParse(T("bits"), out int bits) && bits > 0)
                alert = new AlertEvent(ChatPlatform.Twitch, AlertKind.Bits, user, now, $"{bits} bit{(bits == 1 ? "" : "s")}", bits,
                    Regex.Replace(text, @"(?i)\b[a-z]+\d+\b", "").Trim());
            return new ChatItem(ChatPlatform.Twitch, id, user, color, badges, parts, now, alert);
        }
        int.TryParse(T("msg-param-cumulative-months"), out int months);
        alert = T("msg-id") switch
        {
            "sub" => new AlertEvent(ChatPlatform.Twitch, AlertKind.Sub, user, now, Count: Math.Max(1, months), Message: text),
            "resub" => new AlertEvent(ChatPlatform.Twitch, AlertKind.Resub, user, now, Count: months, Message: text),
            "subgift" => new AlertEvent(ChatPlatform.Twitch, AlertKind.GiftSub, user, now, Count: 1, Message: "to " + T("msg-param-recipient-display-name")),
            "submysterygift" => new AlertEvent(ChatPlatform.Twitch, AlertKind.GiftSub, user, now,
                Count: int.TryParse(T("msg-param-mass-gift-count"), out int g) ? g : 1),
            "raid" => new AlertEvent(ChatPlatform.Twitch, AlertKind.Raid, T("msg-param-displayName") is { Length: > 0 } rn ? rn : user, now,
                Count: int.TryParse(T("msg-param-viewerCount"), out int v) ? v : 0),
            _ => null,
        };
        string sys = T("system-msg");
        if (alert is null && sys.Length == 0) return null;
        // what Twitch says happened, then what they wrote with it
        var shown = new List<ChatPart>();
        if (sys.Length > 0) shown.Add(new ChatPart(sys + (text.Length > 0 ? " " : "")));
        shown.AddRange(parts);
        return new ChatItem(ChatPlatform.Twitch, id, user, color, badges, shown, now, alert);
    }

    /// <summary>Splits a message at its emotes ("emotes=25:0-4,12-16/1902:6-10", positions in characters).</summary>
    public static List<ChatPart> Emotes(string text, string emotesTag)
    {
        var parts = new List<ChatPart>();
        if (string.IsNullOrEmpty(emotesTag)) { if (text.Length > 0) parts.Add(new ChatPart(text)); return parts; }
        // Twitch counts Unicode characters, not UTF-16 units
        var chars = new List<string>();
        foreach (var r in text.EnumerateRunes()) chars.Add(r.ToString());
        var spans = new List<(int From, int To, string Id)>();
        foreach (var em in emotesTag.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = em.Split(':');
            if (kv.Length != 2) continue;
            foreach (var range in kv[1].Split(','))
            {
                var ab = range.Split('-');
                if (ab.Length == 2 && int.TryParse(ab[0], out int a) && int.TryParse(ab[1], out int b) && a <= b && b < chars.Count) spans.Add((a, b, kv[0]));
            }
        }
        spans.Sort((x, y) => x.From.CompareTo(y.From));
        int at = 0;
        foreach (var (from, to, eid) in spans)
        {
            if (from < at) continue;
            if (from > at) parts.Add(new ChatPart(string.Concat(chars.Skip(at).Take(from - at))));
            parts.Add(new ChatPart(string.Concat(chars.Skip(from).Take(to - from + 1)), $"https://static-cdn.jtvnw.net/emoticons/v2/{eid}/default/dark/2.0"));
            at = to + 1;
        }
        if (at < chars.Count) parts.Add(new ChatPart(string.Concat(chars.Skip(at))));
        return parts;
    }

    public void Start(string channel, string word, ChatClipWho who)
    {
        Stop();
        Channel = channel; Word = string.IsNullOrWhiteSpace(word) ? "!clip" : word.Trim(); Who = who;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => RunAsync(ct));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        Connected = false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(Host, Port, ct);
                Stream stream = tcp.GetStream();
                if (UseTls)
                {
                    var ssl = new SslStream(stream);
                    await ssl.AuthenticateAsClientAsync(Host);
                    stream = ssl;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                // anonymous, read-only: Twitch allows "justinfan" nicknames without a password
                await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands");
                await writer.WriteLineAsync("PASS SCHMOOPIIE");
                await writer.WriteLineAsync($"NICK justinfan{Random.Shared.Next(10000, 99999)}");
                await writer.WriteLineAsync($"JOIN #{Channel}");
                Connected = true;
                backoff = TimeSpan.FromSeconds(2);
                Debug?.Invoke($"chat: reading #{Channel}");
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break;
                    if (line.StartsWith("PING", StringComparison.Ordinal)) { await writer.WriteLineAsync("PONG" + line[4..]); continue; }
                    if (line.Contains(" RECONNECT", StringComparison.Ordinal)) break;
                    if (Parse(line, DateTime.UtcNow) is { } any) Message?.Invoke(any);
                    if (Item is not null && ParseItem(line, DateTime.UtcNow) is { } item) Item.Invoke(item);
                    if (Parse(line, DateTime.UtcNow) is { } msg && IsCommand(msg.Text, Word))
                    {
                        bool ok = Allowed(msg, Who);
                        Debug?.Invoke($"chat: {msg.User}: {msg.Text} ({(ok ? "clipping" : "not allowed")})");
                        if (ok) Command?.Invoke(msg);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Debug?.Invoke("chat: " + ex.Message); }
            Connected = false;
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(backoff, ct); } catch { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
        Connected = false;
    }

    public void Dispose() => Stop();
}
