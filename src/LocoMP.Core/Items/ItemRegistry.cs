using System;
using System.Collections.Generic;
using LocoMP.Core.Career;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;

namespace LocoMP.Core.Items;

/// <summary>
/// Server-authoritative item store (03 §3 — clients propose, the server commits). Mirrors
/// <see cref="TrainsetRegistry"/>/<see cref="CareerRegistry"/>'s role for items: it mints ids,
/// enforces the SINGLE-LOCATION invariant (an item is in the world XOR in one scope's possession —
/// never both, never neither), and routes possession through the <see cref="ProgressionPolicy"/> so
/// shared-career pools inventory just as it pools the wallet. Game-free: the whole thing fuzzes
/// headless, and possession is keyed by policy scope (a player key, or the shared account) so it
/// survives reconnects and restarts exactly like a career profile.
/// </summary>
public sealed class ItemRegistry
{
    private readonly ProgressionPolicy _policy;
    private readonly Dictionary<int, ItemRecord> _items = new();
    private int _nextItemId = 1;

    public ItemRegistry(ProgressionPolicy policy, ItemsSaveData? restore = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (restore != null) ApplyRestore(restore);
    }

    /// <summary>The progression policy routing possession (per-player vs shared inventory). The
    /// session layer reads it to resolve a player's inventory scope, same as the career does.</summary>
    public ProgressionPolicy Policy => _policy;

    /// <summary>Every live item by id. Exposed for the host UI, admin, session burst, and tests.</summary>
    public IReadOnlyDictionary<int, ItemRecord> Items => _items;

    /// <summary>Total items ever minted / ever removed. With <see cref="Items"/>.Count these are the
    /// conservation oracle the fuzz asserts after every op: nothing appears or vanishes off-book.</summary>
    public long TotalSpawned { get; private set; }
    public long TotalDespawned { get; private set; }

    /// <summary>The item-count analog of the ledger's money conservation: live == spawned − despawned.</summary>
    public bool ItemConservationHolds => _items.Count == TotalSpawned - TotalDespawned;

    // ── minting ──

    /// <summary>Put a fresh item into the WORLD at a pose (a dropped purchase's alternative, a
    /// restored world item, or a host-captured real item in a later slice). Always succeeds — a mint
    /// is unconditional; only moves are validated. Provenance is stamped here and never changes:
    /// the register path passes HostNative; everything else defaults to Minted.</summary>
    public ItemRecord SpawnInWorld(string prefabName, Pose pose, string state, bool locked = false,
        ItemProvenance provenance = ItemProvenance.Minted)
    {
        var rec = new ItemRecord(new ItemDef(_nextItemId++, prefabName, state), ItemLocationKind.World, pose, string.Empty, locked, provenance);
        _items[rec.Def.Id] = rec;
        TotalSpawned++;
        return rec;
    }

    /// <summary>Put a fresh item straight into a player's POSSESSION (a completed purchase — the
    /// money leg is charged by the caller BEFORE this, so mint-and-charge move together). The scope
    /// is policy-routed, so a shared-career purchase lands in the communal inventory.</summary>
    public ItemRecord SpawnInPossession(string playerKey, string prefabName, string state)
    {
        string scope = _policy.InventoryScopeFor(playerKey);
        var rec = new ItemRecord(new ItemDef(_nextItemId++, prefabName, state), ItemLocationKind.Possessed, Pose.Identity, scope);
        _items[rec.Def.Id] = rec;
        TotalSpawned++;
        return rec;
    }

    // ── moves (validated: clients propose, we commit) ──

    /// <summary>World → this player's possession. Refuses an unknown item, one already held (by
    /// anyone — a physical item is picked up once), enforcing the single-location invariant.</summary>
    public bool TryPickUp(string playerKey, int itemId, out ItemRecord? rec, out string? reason)
    {
        rec = null;
        if (!_items.TryGetValue(itemId, out ItemRecord? item))
        {
            reason = $"unknown item {itemId}";
            return false;
        }
        if (item.Location != ItemLocationKind.World)
        {
            reason = $"item {itemId} is already held";
            return false;
        }
        if (item.WorldLocked)
        {
            reason = $"item {itemId} is a personal item — only its owner can take it";
            return false;
        }
        item.Location = ItemLocationKind.Possessed;
        item.OwnerScope = _policy.InventoryScopeFor(playerKey);
        item.HolderKey = playerKey; // per-item attribution — the communal scope can't say WHO carries it
        // The world pose is deliberately RETAINED (not reset): it is the involuntary-release
        // fallback — if the holder departs and their last position is unknown, the item goes back
        // where it was taken from rather than to the origin. The wire never sends a possessed
        // item's pose, so nothing downstream can read a stale one.
        rec = item;
        reason = null;
        return true;
    }

    /// <summary>This player's possession → the world at a pose. Refuses an item this scope does not
    /// hold (in shared-career any player may drop a communal item — the scope is the shared one).</summary>
    public bool TryDrop(string playerKey, int itemId, Pose pose, out ItemRecord? rec, out string? reason)
    {
        rec = null;
        if (!_items.TryGetValue(itemId, out ItemRecord? item))
        {
            reason = $"unknown item {itemId}";
            return false;
        }
        string scope = _policy.InventoryScopeFor(playerKey);
        if (item.Location != ItemLocationKind.Possessed || !string.Equals(item.OwnerScope, scope, StringComparison.Ordinal))
        {
            reason = $"you are not holding item {itemId}";
            return false;
        }
        item.Location = ItemLocationKind.World;
        item.OwnerScope = string.Empty;
        item.HolderKey = string.Empty;
        item.WorldPose = pose;
        rec = item;
        reason = null;
        return true;
    }

    /// <summary>Possession → the world WITHOUT the holder asking: the involuntary-relinquishment
    /// primitive (the M4 orphan concept — departure is a release transaction). Unlike
    /// <see cref="TryDrop"/> there is no scope check — the server invokes this ON BEHALF of a
    /// departed holder, so the only refusals are an unknown item or one not actually possessed.</summary>
    public bool TryReleaseToWorld(int itemId, Pose pose, out ItemRecord? rec, out string? reason)
    {
        rec = null;
        if (!_items.TryGetValue(itemId, out ItemRecord? item))
        {
            reason = $"unknown item {itemId}";
            return false;
        }
        if (item.Location != ItemLocationKind.Possessed)
        {
            reason = $"item {itemId} is not held";
            return false;
        }
        item.Location = ItemLocationKind.World;
        item.OwnerScope = string.Empty;
        item.HolderKey = string.Empty;
        item.WorldPose = pose;
        rec = item;
        reason = null;
        return true;
    }

    /// <summary>Remove an item from the world entirely (consumed, or its native counterpart is gone
    /// in host-capture). Returns the removed record so the session can broadcast the despawn.</summary>
    public bool TryDespawn(int itemId, out ItemRecord? rec, out string? reason)
    {
        rec = null;
        if (!_items.TryGetValue(itemId, out ItemRecord? item))
        {
            reason = $"unknown item {itemId}";
            return false;
        }
        _items.Remove(itemId);
        TotalDespawned++;
        rec = item;
        reason = null;
        return true;
    }

    /// <summary>Overwrite an item's opaque state blob (a live cargo/fuel/label change from its
    /// authority). Identity and location are untouched — state is not membership.</summary>
    public bool TryUpdateState(int itemId, string state, out ItemRecord? rec, out string? reason)
    {
        rec = null;
        if (!_items.TryGetValue(itemId, out ItemRecord? item))
        {
            reason = $"unknown item {itemId}";
            return false;
        }
        item.Def = item.Def.WithState(state);
        rec = item;
        reason = null;
        return true;
    }

    // ── persistence (v1, schema v4) ──

    /// <summary>Snapshot every item (identity + state + location, possession keyed by SCOPE — the
    /// save legitimately holds keys, unlike the wire) plus the id counter, so a cold restart resumes
    /// the world's items and each player's inventory exactly (07 §M3 / 02 §5 win condition).</summary>
    public ItemsSaveData Capture()
    {
        var save = new ItemsSaveData { NextItemId = _nextItemId };
        foreach (ItemRecord rec in _items.Values)
            save.Items.Add(new ItemSave(rec.Def, rec.Location, rec.WorldPose, rec.OwnerScope, rec.WorldLocked, rec.Provenance));
        return save;
    }

    private void ApplyRestore(ItemsSaveData save)
    {
        _items.Clear();
        foreach (ItemSave s in save.Items)
        {
            var rec = new ItemRecord(s.Def, s.Location, s.WorldPose, s.OwnerScope, s.WorldLocked, s.Provenance);
            // A restart IS every holder's departure (nobody is online yet), so a possessed
            // HOST-NATIVE item releases to the world right here, at its last world pose — the same
            // rule the live path applies on disconnect, so the host's hidden real object re-shows
            // when it re-registers the world. Minted possessions stay held: the session layer grants
            // their scopes a fresh reconnect grace and the lapse releases them if nobody returns.
            if (rec.Provenance == ItemProvenance.HostNative && rec.Location == ItemLocationKind.Possessed)
            {
                rec.Location = ItemLocationKind.World;
                rec.OwnerScope = string.Empty;
            }
            _items[s.Def.Id] = rec;
        }
        _nextItemId = save.NextItemId;
        // The live set IS the saved world; the counters reset to it so ItemConservationHolds is true
        // from the first post-restore op (spawned/despawned are per-process, like the ledger totals).
        TotalSpawned = _items.Count;
        TotalDespawned = 0;
    }
}
