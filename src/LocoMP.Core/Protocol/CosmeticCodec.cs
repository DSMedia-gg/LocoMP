using System.Collections.Generic;

namespace LocoMP.Core.Protocol;

/// <summary>
/// Wire codec for <see cref="MessageType.CosmeticState"/> (M6-A1.1) — shared by the client sender,
/// the server's validate/store/relay, and the join-burst replay, so the three can never drift.
/// Payload after the type byte: [carId:varuint][seq:byte][count:byte][count × (kind, value)].
/// </summary>
public static class CosmeticCodec
{
    /// <summary>Cap on entries per message — far above any real kind set (03 §9 size caps).</summary>
    public const int MaxEntries = 16;

    public static byte[] Build(int carId, byte seq, IReadOnlyList<(byte kind, byte value)> entries)
    {
        var w = new PacketWriter(8 + entries.Count * 2)
            .WriteByte((byte)MessageType.CosmeticState)
            .WriteVarUInt((uint)carId)
            .WriteByte(seq)
            .WriteByte((byte)entries.Count);
        for (int i = 0; i < entries.Count; i++)
            w.WriteByte(entries[i].kind).WriteByte(entries[i].value);
        return w.ToArray();
    }

    /// <summary>False = malformed (over-cap or truncated); the message is dropped whole.</summary>
    public static bool TryRead(PacketReader r, out int carId, out byte seq, out (byte kind, byte value)[] entries)
    {
        carId = (int)r.ReadVarUInt();
        seq = r.ReadByte();
        int count = r.ReadByte();
        entries = System.Array.Empty<(byte, byte)>();
        if (count < 1 || count > MaxEntries) return false;
        var read = new (byte kind, byte value)[count];
        for (int i = 0; i < count; i++)
        {
            byte kind = r.ReadByte();
            byte value = r.ReadByte();
            read[i] = (kind, value);
        }
        entries = read;
        return true;
    }

    /// <summary>Wrap-safe forward test for the per-car seq byte: does <paramref name="next"/> come
    /// after <paramref name="last"/>? (The Steam-relay latest-wins idiom at byte width — a stale
    /// reliable-unordered arrival must never regress the newest state.)</summary>
    public static bool SeqAdvances(byte last, byte next) => next != last && (byte)(next - last) < 128;
}
