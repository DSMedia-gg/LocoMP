using System;
using LocoMP.Core.Presence;

namespace LocoMP.Core.Protocol;

/// <summary>
/// Shared (de)serialization for the composite presence types, so the server and client encode them
/// identically. Internal: the wire format is an implementation detail behind <c>NetServer</c>/
/// <c>NetClient</c>; only the public session API is consumed by the frontends.
/// </summary>
internal static class PresenceCodec
{
    public static void WritePose(PacketWriter w, Pose p)
    {
        w.WriteSingle(p.Px).WriteSingle(p.Py).WriteSingle(p.Pz)
         .WriteSingle(p.Rx).WriteSingle(p.Ry).WriteSingle(p.Rz).WriteSingle(p.Rw);
    }

    public static Pose ReadPose(PacketReader r) =>
        new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
            r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    public static void WritePlayer(PacketWriter w, PlayerState s)
    {
        w.WriteVarUInt((uint)s.Id);
        w.WriteString(s.Name);
        WritePose(w, s.Pose);
    }

    public static PlayerState ReadPlayer(PacketReader r) =>
        new((int)r.ReadVarUInt(), r.ReadString(), ReadPose(r));

    public static void WriteStatus(PacketWriter w, PlayerStatus s)
    {
        w.WriteVarUInt((uint)s.Id);
        w.WriteByte((byte)s.Role);
        // ping + 1 so 0 can mean "unknown" (a null RTT): a live 0 ms loopback link must stay
        // distinguishable from a peer the transport has no measurement for.
        w.WriteVarUInt(s.PingMs is int ping ? (uint)Math.Min(ping, 600_000) + 1 : 0u);
    }

    public static PlayerStatus ReadStatus(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        var role = (PlayerRole)r.ReadByte();
        uint pingPlus1 = r.ReadVarUInt();
        return new PlayerStatus(id, role, pingPlus1 == 0 ? (int?)null : (int)(pingPlus1 - 1));
    }
}
