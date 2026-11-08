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
                await writer.WriteLineAsync("CAP REQ :twitch.tv/tags");
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
