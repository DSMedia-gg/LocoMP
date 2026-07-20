using System;
using LocoMP.Core.Presence;

namespace LocoMP.Core.Items;

/// <summary>Where a live item is — the single-location invariant made a type: an item is either
/// lying in the world at a pose, or in exactly one player's possession. Nothing is both.</summary>
public enum ItemLocationKind : byte
{
    World = 0,
    Possessed = 1,
}

/// <summary>
/// One item's stable identity + payload (03 §7 / 02 §5). The item recon (research/item-system-recon
/// .md) found DV has NO per-instance id — only <c>itemPrefabName</c> as the TYPE — so LocoMP mints
/// its own <see cref="Id"/> (the car-id pattern), which is what the whole system keys on. State is an
/// opaque blob the Shim maps to/from the game's per-item JObject; Core never inspects it.
/// </summary>
public sealed class ItemDef
{
    public ItemDef(int id, string prefabName, string state)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
        Id = id;
        PrefabName = prefabName ?? throw new ArgumentNullException(nameof(prefabName));
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    /// <summary>Server-minted network identity — the wire and the save both key on this.</summary>
    public int Id { get; }

    /// <summary>The canonical TYPE id (DV's <c>InventoryItemSpec.itemPrefabName</c>): the spawn
    /// primitive is <c>Resources.Load(prefabName)</c>, so this is all a replica needs to render it.</summary>
    public string PrefabName { get; }

    /// <summary>Opaque per-instance state blob (fuel level, label text, …). "" when there is none.</summary>
    public string State { get; }

    public ItemDef WithState(string state) => new(Id, PrefabName, state);
}

/// <summary>An item plus its current location — the registry's live record. Location changes on
/// pickup/drop while the def's identity/state stays put, so the two are tracked separately (a drop
/// re-poses without re-minting), mirroring how a trainset's owner changes without a new car id.</summary>
public sealed class ItemRecord
{
    internal ItemRecord(ItemDef def, ItemLocationKind location, Pose worldPose, string ownerScope, bool worldLocked = false)
    {
        Def = def;
        Location = location;
        WorldPose = worldPose;
        OwnerScope = ownerScope;
        WorldLocked = worldLocked;
    }

    public ItemDef Def { get; internal set; }
    public ItemLocationKind Location { get; internal set; }

    /// <summary>Valid when <see cref="Location"/> is World; Pose.Identity otherwise.</summary>
    public Pose WorldPose { get; internal set; }

    /// <summary>A "look, but don't touch" world item — a DV personal essential (map, radio, wallet…)
    /// its owner set down. Visible to everyone, but only its owner interacts, so it can never be picked
    /// up over the wire (the owner reclaims it natively, not via a request). Meaningful only in the
    /// World; a picked-up item is never locked because a locked item is never picked up.</summary>
    public bool WorldLocked { get; internal set; }

    /// <summary>Valid when <see cref="Location"/> is Possessed: the policy scope holding it (a player
    /// key, or the shared account in shared-career). NEVER leaves the server as-is — the wire carries
    /// the holder's session peer id + name instead, like a job's claimant (03 §3, 07 §M3). "" in the
    /// world.</summary>
    public string OwnerScope { get; internal set; }
}
