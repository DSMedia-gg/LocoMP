using LocoMP.Core.Items;

namespace LocoMP.Core.Protocol;

/// <summary>
/// Shared (de)serialization for an item's identity + state (hand-rolled, hard rule 4). Like
/// <see cref="CareerCodec"/> this is reused by the wire AND the save codec so the two can't drift —
/// but ONLY the def travels here. An item's LOCATION is encoded differently on each side (the wire
/// carries the holder's session peer id + name for privacy; the save carries the policy scope key),
/// so each side writes its own location, exactly as jobs split JobState (peer) from JobSave (key).
/// Reads are untrusted (03 §9): the string caps in <see cref="PacketReader"/> bound every field.
/// </summary>
internal static class ItemCodec
{
    public static void WriteItemDef(PacketWriter w, ItemDef def)
    {
        w.WriteVarUInt((uint)def.Id);
        w.WriteString(def.PrefabName);
        w.WriteString(def.State);
    }

    public static ItemDef ReadItemDef(PacketReader r)
    {
        int id = (int)r.ReadVarUInt();
        string prefabName = r.ReadString();
        string state = r.ReadString();
        return new ItemDef(id, prefabName, state);
    }
}
