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
}
