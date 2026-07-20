namespace LocoMP.Core.Trains;

/// <summary>
/// Which comms-radio action a <see cref="LocoMP.Core.Protocol.MessageType.CommsActionRequest"/> asks
/// a car's sim owner to perform on the initiator's behalf (M4, protocol v8). The wire value is the
/// byte carried by the request/command, so it is STABLE — only ever append.
///
/// Summon (crew vehicle) is deliberately NOT here yet: it spawns a NEW car at a remote location with
/// livery/garage resolution, a materially harder problem than acting on an existing car. Host-side
/// summon works (with its fee); routing a remote summon is a later slice.
/// </summary>
public enum CommsActionKind : byte
{
    /// <summary>Rerail the target derailed car to the destination pose (the request carries it).</summary>
    Rerail = 0,

    /// <summary>Clear/delete the target car (the destination pose is unused).</summary>
    Delete = 1,
}
