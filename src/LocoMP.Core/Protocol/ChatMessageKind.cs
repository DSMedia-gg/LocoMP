namespace LocoMP.Core.Protocol;

/// <summary>
/// What a <see cref="MessageType.ChatMessage"/> line IS (M5.4). Wire values are STABLE — only append.
/// System kinds carry an empty text field: the client composes the display line from the kind + the
/// stamped name ("Alice joined"), which keeps every system string client-side and localization-ready
/// (the unscheduled P1 the audit named) instead of baking English into the wire.
/// </summary>
public enum ChatMessageKind : byte
{
    /// <summary>An ordinary player line. Sender id/name identify the speaker; text is the message.</summary>
    Player = 0,

    /// <summary>System: the named player was admitted to the session.</summary>
    Joined = 1,

    /// <summary>System: the named player left (graceful leave or a dropped link — bystanders never
    /// could tell these apart, and the departure feed deliberately doesn't either).</summary>
    Left = 2,

    /// <summary>System: the named player was kicked by an admin.</summary>
    Kicked = 3,

    /// <summary>System: the named player was kicked AND session-banned by an admin.</summary>
    Banned = 4,

    /// <summary>A line from the server itself (dedicated-console <c>say</c>, or a sender-only service
    /// notice such as the rate-limit warning). Sender id is 0 and the name is empty; text is the line.</summary>
    Server = 5,
}
