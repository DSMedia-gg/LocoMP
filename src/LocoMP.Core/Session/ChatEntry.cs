using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>
/// One committed chat line (M5.4) — the decoded form of a <see cref="MessageType.ChatMessage"/>,
/// shared by the server's outbound feed (console logging) and the client's mirror (the overlay's
/// backlog). The name is server-stamped at commit time, so an entry stays renderable after its
/// sender leaves the session.
/// </summary>
public readonly struct ChatEntry
{
    public ChatEntry(ChatMessageKind kind, int senderId, string senderName, string text)
    {
        Kind = kind;
        SenderId = senderId;
        SenderName = senderName;
        Text = text;
    }

    public ChatMessageKind Kind { get; }

    /// <summary>The speaking player's server-assigned id; 0 for a server/system line with no player
    /// (system departure/join lines DO carry the affected player's id).</summary>
    public int SenderId { get; }

    /// <summary>The display name stamped by the server (empty for <see cref="ChatMessageKind.Server"/>).</summary>
    public string SenderName { get; }

    /// <summary>The message body for Player/Server kinds; empty for system kinds (the client
    /// composes those lines from <see cref="Kind"/> + <see cref="SenderName"/>).</summary>
    public string Text { get; }
}
