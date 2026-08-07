using System.Collections.Generic;
using LocoMP.Core.Session;

namespace LocoMP.Core.Protocol;

/// <summary>
/// Wire (de)serialisation for the M5.2 admin query replies — the diagnostics snapshot and the session
/// ban list a remote admin's panel requests (host-mode reads these in-process; a promoted admin on a
/// dedicated server gets them over the wire). Kept beside the other codecs and round-trip tested; the
/// field order here is the wire contract, so only ever append.
/// </summary>
public static class AdminCodec
{
    /// <summary>Serialise a <see cref="ServerDiagnostics"/> snapshot (payload after the message-type byte).</summary>
    public static void WriteDiagnostics(PacketWriter w, ServerDiagnostics d)
    {
        w.WriteVarUInt((uint)d.Players)
         .WriteVarUInt((uint)d.Queued)
         .WriteVarUInt((uint)d.Trainsets)
         .WriteVarUInt((uint)d.Jobs)
         .WriteVarUInt((uint)d.Items)
         .WriteInt64(d.StaleSnapshotsDropped)
         .WriteVarUInt((uint)d.Admins)
         .WriteVarUInt((uint)d.BannedKeys)
         .WriteByte(d.MoneyConservationHolds ? (byte)1 : (byte)0)
         .WriteByte(d.ItemConservationHolds ? (byte)1 : (byte)0)
         .WriteByte(d.JoinsPaused ? (byte)1 : (byte)0)
         .WriteByte(d.InterestEnabled ? (byte)1 : (byte)0)
         .WriteInt64(d.BytesSent)
         .WriteInt64(d.BytesReceived)
         .WriteInt64(d.MessagesSent)
         .WriteInt64(d.MessagesReceived);
    }

    public static ServerDiagnostics ReadDiagnostics(PacketReader r)
    {
        int players = (int)r.ReadVarUInt();
        int queued = (int)r.ReadVarUInt();
        int trainsets = (int)r.ReadVarUInt();
        int jobs = (int)r.ReadVarUInt();
        int items = (int)r.ReadVarUInt();
        long stale = r.ReadInt64();
        int admins = (int)r.ReadVarUInt();
        int banned = (int)r.ReadVarUInt();
        bool money = r.ReadByte() != 0;
        bool item = r.ReadByte() != 0;
        bool paused = r.ReadByte() != 0;
        bool interest = r.ReadByte() != 0;
        long bytesSent = r.ReadInt64();
        long bytesReceived = r.ReadInt64();
        long messagesSent = r.ReadInt64();
        long messagesReceived = r.ReadInt64();
        return new ServerDiagnostics(players, queued, trainsets, jobs, items, stale,
            money, item, paused, admins, banned, interest,
            bytesSent, bytesReceived, messagesSent, messagesReceived);
    }

    /// <summary>Serialise the session ban list (v20): count then each entry's opaque id + display
    /// name. Keys deliberately never ride this message — a key is a credential (R4-A).</summary>
    public static void WriteBanList(PacketWriter w, IReadOnlyCollection<SessionBan> bans)
    {
        w.WriteVarUInt((uint)bans.Count);
        foreach (SessionBan b in bans)
        {
            w.WriteVarUInt((uint)b.Id);
            w.WriteString(b.Name);
        }
    }

    public static IReadOnlyList<SessionBan> ReadBanList(PacketReader r)
    {
        int count = (int)r.ReadVarUInt();
        var bans = new List<SessionBan>(count);
        for (int i = 0; i < count; i++)
        {
            int id = (int)r.ReadVarUInt();
            string name = r.ReadString();
            bans.Add(new SessionBan(id, name));
        }
        return bans;
    }
}
