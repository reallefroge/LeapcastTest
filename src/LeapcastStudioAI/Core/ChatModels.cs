namespace LeapcastStudioAI;

public enum ChatPlatform { Twitch, YouTube }

/// <summary>A piece of a chat message: text, or an emote / emoji picture (with its text as the fallback).</summary>
public sealed record ChatPart(string Text, string? ImageUrl = null);

/// <summary>One line in the chat panel and the chat overlay: a message, or an alert (sub, Super Chat…) shown in chat.</summary>
public sealed record ChatItem(
    ChatPlatform Platform,
    string Id,
    string User,
    string? Color,                    // "#RRGGBB" (Twitch's name colour); null = picked from the name
    IReadOnlyList<string> Badges,     // broadcaster, moderator, vip, subscriber, member, verified
    IReadOnlyList<ChatPart> Parts,
    DateTime ReceivedUtc,
    AlertEvent? Alert = null)
{
    public string Text => string.Concat(Parts.Select(p => p.Text));

    /// <summary>A steady, readable colour for names without one (same name → same colour).</summary>
    public string NameColor
    {
        get
        {
            if (Color is { Length: 7 } c && c[0] == '#') return c;
            string[] palette = { "#FF6B6B", "#4ECDC4", "#FFD93D", "#6BCB77", "#4D96FF", "#FF8FAB", "#C77DFF", "#FF9F45", "#00C2D1", "#B8E986" };
            int h = 17; foreach (char ch in User) h = h * 31 + ch;
            return palette[(h & 0x7FFFFFFF) % palette.Length];
        }
    }
}

public enum AlertKind { Follow, Subscriber, Sub, Resub, GiftSub, Bits, Raid, SuperChat, SuperSticker, Member, MemberMilestone, GiftMembers, Donation }

/// <summary>Something to celebrate on stream.</summary>
public sealed record AlertEvent(
    ChatPlatform? Platform,           // null: Streamlabs donation (no platform)
    AlertKind Kind,
    string User,
    DateTime Utc,
    string? Amount = null,            // "$5.00", "500 bits"
    int Count = 0,                    // months, gifts, raiders
    string? Message = null)
{
    public string Headline => Kind switch
    {
        AlertKind.Follow => $"{User} followed!",
        AlertKind.Subscriber => $"{User} subscribed!",
        AlertKind.Sub => $"{User} subscribed!",
        AlertKind.Resub => $"{User} resubscribed!",
        AlertKind.GiftSub => Count > 1 ? $"{User} gifted {Count} subs!" : $"{User} gifted a sub!",
        AlertKind.Bits => $"{User} cheered {Amount}!",
        AlertKind.Raid => $"{User} is raiding!",
        AlertKind.SuperChat => $"{User} sent a Super Chat!",
        AlertKind.SuperSticker => $"{User} sent a Super Sticker!",
        AlertKind.Member => $"{User} became a member!",
        AlertKind.MemberMilestone => $"{User}: {Count} months a member!",
        AlertKind.GiftMembers => Count > 1 ? $"{User} gifted {Count} memberships!" : $"{User} gifted a membership!",
        AlertKind.Donation => $"{User} donated {Amount}!",
        _ => User,
    };

    public string Subline => Kind switch
    {
        AlertKind.Resub when Count > 0 => $"{Count} months" + (string.IsNullOrWhiteSpace(Message) ? "" : " · " + Message),
        AlertKind.Raid => $"with {Count} viewer{(Count == 1 ? "" : "s")}",
        AlertKind.SuperChat or AlertKind.SuperSticker => Amount + (string.IsNullOrWhiteSpace(Message) ? "" : " · " + Message),
        _ => Message ?? "",
    };

    /// <summary>For telling the same alert from two sources apart (chat + Streamlabs).</summary>
    public string Key => $"{Platform}|{(Kind is AlertKind.Resub ? AlertKind.Sub : Kind is AlertKind.Subscriber && Platform == ChatPlatform.Twitch ? AlertKind.Sub : Kind)}|{User.ToLowerInvariant()}";
}
