using System;
using System.Collections.Generic;
using LocoMP.Core.Items;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;

namespace LocoMP.Core.Session;

/// <summary>
/// The server's item subsystem (M4, 02 §2/§3/§4 — inventory, world items, shops), owned by
/// <see cref="NetServer"/> beside <see cref="ServerCareer"/>. Holds the authoritative
/// <see cref="ItemRegistry"/>; validates pickup/drop/purchase proposals (clients propose, the server
/// commits, 03 §3); mints purchases against the policy wallet via <see cref="ServerCareer"/> so the
/// money leg and the item leg move together (the win condition: a CLIENT's purchase lands in the
/// right wallet). All item traffic is reliable-ordered — it is transactions, not telemetry. A
/// possession's scope key never touches the wire: the holder rides as a session peer id + name, just
/// like a job's claimant (07 §M3).
/// </summary>
public sealed class ServerItems
{
    private readonly ITransport _transport;
    private readonly Func<IEnumerable<int>> _connectedIds;
    private readonly ItemConfig _config;
    private readonly Func<int, Pose?> _poseOf;
    private readonly ServerCareer _career; // wallet charge + peer↔key identity resolution

    // Where a departed holder was last seen, per player key — the release pose their MINTED
    // possessions drop at if the grace lapses. In memory only: across a restart the retained
    // last-world-pose on each record is the fallback, which is the honest answer anyway (the
    // disconnect pose died with the process). Cleared on rejoin.
    private readonly Dictionary<string, Pose> _releasePoseByKey = new(StringComparer.Ordinal);

    internal ServerItems(ITransport transport, Func<IEnumerable<int>> connectedIds, ItemConfig config,
        Func<int, Pose?> poseOf, ServerCareer career, ItemsSaveData? restore)
    {
        _transport = transport;
        _connectedIds = connectedIds;
        _config = config;
        _poseOf = poseOf;
        _career = career;
        Registry = new ItemRegistry(career.Registry.Policy, restore);

        // A restart IS every holder's disconnect. The registry already released possessed
        // host-native items to the world (ApplyRestore); the minted possessions that remain need
        // their scopes under a live grace hold, or a holder who never returns would hold them
        // forever — capture only wrote grace entries for players who had already disconnected.
        // Per-player scope IS the player key (shared scopes, '@'-prefixed, outlive any peer).
        if (restore != null)
            foreach (ItemRecord rec in Registry.Items.Values)
                if (rec.Location == ItemLocationKind.Possessed &&
                    rec.OwnerScope.Length > 0 && rec.OwnerScope[0] != '@')
                    career.Registry.EnsureGrace(rec.OwnerScope);
    }

    /// <summary>The authoritative item store. Exposed for the host UI, admin, and tests.</summary>
    public ItemRegistry Registry { get; }

    /// <summary>An item proposal failed validation: (peerId, reason). Surfaced for the host log
    /// alongside the CareerRejected the requester also receives.</summary>
    public event Action<int, string>? RequestRejected;

    /// <summary>Handle an item message from an ADMITTED peer. Returns false for non-item types.</summary>
    internal bool TryHandle(int peerId, MessageType type, PacketReader r)
    {
        switch (type)
        {
            case MessageType.ItemRegister: HandleRegister(peerId, r); return true;
            case MessageType.ItemPickupRequest: HandlePickup(peerId, r); return true;
            case MessageType.ItemDropRequest: HandleDrop(peerId, r); return true;
            case MessageType.ItemPurchaseRequest: HandlePurchase(peerId, r); return true;
            case MessageType.ItemDespawnRequest: HandleDespawn(peerId, r); return true;
            default: return false;
        }
    }

    /// <summary>Item burst for a newly admitted player: every live item (world + possessions),
    /// reliable-ordered, after the trains/career bursts (03 §10). Then rebind: this player's own
    /// retained possessions resolve to their new peer id, so the room's mirrors stop showing the
    /// dead one (mirrors the career claim rebind).</summary>
    internal void OnPlayerAdmitted(int peerId)
    {
        // Catalog first, so the client can price its Buy UI before any item arrives (the way
        // CareerState precedes the job board). It never changes mid-session in this slice, so a
        // single join-burst send is enough — a live-restock feed is a later stock-sync slice.
        _transport.Send(peerId, BuildShopCatalog(), DeliveryMethod.ReliableOrdered);

        foreach (ItemRecord rec in Registry.Items.Values)
            _transport.Send(peerId, BuildSpawned(0, rec), DeliveryMethod.ReliableOrdered);

        if (_career.KeyOf(peerId) is string key)
        {
            _releasePoseByKey.Remove(key); // back inside grace: their minted possessions stay theirs
            string scope = Registry.Policy.InventoryScopeFor(key);
            foreach (ItemRecord rec in Registry.Items.Values)
                if (rec.Location == ItemLocationKind.Possessed &&
                    string.Equals(rec.OwnerScope, scope, StringComparison.Ordinal))
                    Broadcast(BuildMoved(rec));
        }
    }

    /// <summary>A player left — departure is a RELEASE transaction, split by provenance (the M4
    /// orphan concept's item half). A HOST-NATIVE possession releases to the world right now, where
    /// the holder was last seen: the host's real object was hidden when they took it, and an
    /// involuntary departure with no release would orphan it for the session. A MINTED possession is
    /// RETAINED under the holder's reconnect grace (restored exactly on rejoin, like a career
    /// claim); <see cref="OnGraceLapsed"/> releases it if they never return — the room meanwhile
    /// sees it held by an offline player (peer 0, name kept) so nobody addresses a stale id. In
    /// shared career MINTED possessions are communal and outlive any single peer — but a
    /// HOST-NATIVE possession releases here too (Cody, 2026-08-04): the host's hidden real object
    /// must re-show whoever carried it off, and per-item <see cref="ItemRecord.HolderKey"/>
    /// attribution is what lets the departing player's natives release without dumping the
    /// whole crew's inventory.</summary>
    internal void OnPlayerRemoved(int peerId, Pose? lastPose)
    {
        if (_career.KeyOf(peerId) is not string key) return;
        bool shared = Registry.Policy.LicensesShared;
        string scope = Registry.Policy.InventoryScopeFor(key);

        List<ItemRecord>? natives = null;
        bool holdsMinted = false;
        foreach (ItemRecord rec in Registry.Items.Values)
        {
            if (rec.Location != ItemLocationKind.Possessed ||
                !string.Equals(rec.OwnerScope, scope, StringComparison.Ordinal)) continue;
            if (rec.Provenance == ItemProvenance.HostNative)
            {
                // In shared career the communal scope matches EVERY member's possessions — only
                // the departing player's own carried natives release (HolderKey attribution).
                if (!shared || string.Equals(rec.HolderKey, key, StringComparison.Ordinal))
                    (natives ??= new List<ItemRecord>()).Add(rec);
            }
            else if (!shared)
            {
                holdsMinted = true; // shared-career minted items are the crew's; no offline mirror
            }
        }

        if (natives != null)
            foreach (ItemRecord rec in natives)
            {
                // Fall back to the item's retained last world pose — "back where it was taken
                // from" — when the holder never streamed a position (a fresh join, a bot).
                if (Registry.TryReleaseToWorld(rec.Def.Id, lastPose ?? rec.WorldPose, out _, out _))
                    Broadcast(BuildMoved(rec));
            }

        if (holdsMinted)
        {
            if (lastPose is Pose pose) _releasePoseByKey[key] = pose;
            // NOT BuildMoved: this runs BEFORE the career drops the peer↔key map (KeyOf above needs
            // it alive), so PeerOf would still resolve the DYING peer id and the room would mirror a
            // stale holder. Encode "offline" explicitly — peer 0, display name kept — the same shape
            // a job claimant takes when their peer dies.
            string name = _career.NameOf(key);
            foreach (ItemRecord rec in Registry.Items.Values)
                if (rec.Location == ItemLocationKind.Possessed &&
                    string.Equals(rec.OwnerScope, scope, StringComparison.Ordinal))
                    Broadcast(BuildMovedOffline(rec, name));
        }
    }

    /// <summary>Whether this key still possesses anything, by the same attribution
    /// <see cref="OnGraceLapsed"/> releases by: the key's scope in per-player mode, only items the
    /// key physically carries (HolderKey) in shared career. NetServer consults this BEFORE the
    /// lapse release when judging a profile pristine for D20 eviction — a holder of anything is
    /// not pristine, and checking after the release would never see it.</summary>
    internal bool HasPossessions(string key)
    {
        bool shared = Registry.Policy.LicensesShared;
        string scope = Registry.Policy.InventoryScopeFor(key);
        foreach (ItemRecord rec in Registry.Items.Values)
        {
            if (rec.Location != ItemLocationKind.Possessed ||
                !string.Equals(rec.OwnerScope, scope, StringComparison.Ordinal)) continue;
            if (shared && !string.Equals(rec.HolderKey, key, StringComparison.Ordinal)) continue;
            return true;
        }
        return false;
    }

    /// <summary>A player's reconnect grace lapsed (the career's clock — fanned out by NetServer.Poll):
    /// they are not coming back, so every possession still in their scope releases to the world at
    /// their disconnect pose (or the item's own last world pose when that was never known). This is
    /// the minted items' schedule; a host-native possession normally released at disconnect and only
    /// lands here through belt-and-braces.</summary>
    internal void OnGraceLapsed(string key)
    {
        // Shared career pools MINTED inventory in the communal scope: one player's lapse must not
        // dump the whole crew's items into the world. Host-natives they carried are attributed
        // per item (HolderKey) and normally released at disconnect already — the shared-career
        // branch below is the same belt-and-braces the per-player path carries.
        bool shared = Registry.Policy.LicensesShared;
        string scope = Registry.Policy.InventoryScopeFor(key);
        Pose? atKey = _releasePoseByKey.TryGetValue(key, out Pose stashed) ? stashed : (Pose?)null;
        _releasePoseByKey.Remove(key);

        List<ItemRecord>? held = null;
        foreach (ItemRecord rec in Registry.Items.Values)
        {
            if (rec.Location != ItemLocationKind.Possessed ||
                !string.Equals(rec.OwnerScope, scope, StringComparison.Ordinal)) continue;
            if (shared && !(rec.Provenance == ItemProvenance.HostNative &&
                            string.Equals(rec.HolderKey, key, StringComparison.Ordinal))) continue;
            (held ??= new List<ItemRecord>()).Add(rec);
        }
        if (held == null) return;

        foreach (ItemRecord rec in held)
            if (Registry.TryReleaseToWorld(rec.Def.Id, atKey ?? rec.WorldPose, out _, out _))
                Broadcast(BuildMoved(rec));
    }

    // ── handlers ──

    private void HandleRegister(int peerId, PacketReader r)
    {
        uint token = r.ReadVarUInt();
        ItemDef proposal = ItemCodec.ReadItemDef(r); // id ignored — the registry assigns it
        Pose pose = PresenceCodec.ReadPose(r);
        bool locked = r.ReadByte() != 0;             // a personal essential set down (look-but-don't-touch)
        if (!_config.AcceptExternalItems || peerId != _career.WorldSourcePeer)
        {
            Reject(peerId, "register: only the world source registers items");
            return;
        }
        // Registered items are the world source's REAL objects by construction (only it may
        // register) — the provenance that releases them immediately if a holder departs.
        ItemRecord rec = Registry.SpawnInWorld(proposal.PrefabName, pose, proposal.State, locked, ItemProvenance.HostNative);
        // Echo the token to the registrant (Shim maps its GameObject → server id); others get 0.
        _transport.Send(peerId, BuildSpawned(token, rec), DeliveryMethod.ReliableOrdered);
        byte[] plain = BuildSpawned(0, rec);
        foreach (int id in _connectedIds())
            if (id != peerId) _transport.Send(id, plain, DeliveryMethod.ReliableOrdered);
    }

    private void HandlePickup(int peerId, PacketReader r)
    {
        int itemId = (int)r.ReadVarUInt();
        if (_career.KeyOf(peerId) is not string key) return;
        if (!PickupInReach(peerId, itemId, out string? whereReason))
        {
            Reject(peerId, $"pickup: {whereReason}", itemId);
            return;
        }
        if (Registry.TryPickUp(key, itemId, out ItemRecord? rec, out string? reason))
            Broadcast(BuildMoved(rec!));
        else
            Reject(peerId, $"pickup: {reason}", itemId);
    }

    private void HandleDrop(int peerId, PacketReader r)
    {
        int itemId = (int)r.ReadVarUInt();
        Pose pose = PresenceCodec.ReadPose(r);
        if (_career.KeyOf(peerId) is not string key) return;
        if (Registry.TryDrop(key, itemId, pose, out ItemRecord? rec, out string? reason))
            Broadcast(BuildMoved(rec!));
        else
            Reject(peerId, $"drop: {reason}", itemId);
    }

    private void HandlePurchase(int peerId, PacketReader r)
    {
        string prefabName = r.ReadString();
        if (_career.KeyOf(peerId) is not string key) return;
        if (!_config.ShopPrices.TryGetValue(prefabName, out long price))
        {
            Reject(peerId, $"purchase: {prefabName} is not for sale");
            return;
        }
        // Charge THEN mint: an overdraft is refused before any item exists, so money and item never
        // desync (03 §9 — the ledger is truth). A free item (price 0) skips the burn.
        if (price > 0 && !_career.TryChargeShopPurchase(peerId, price, $"bought {prefabName}", out string? reason))
        {
            Reject(peerId, $"purchase: {reason}");
            return;
        }
        ItemRecord rec = Registry.SpawnInPossession(key, prefabName, string.Empty);
        Broadcast(BuildSpawned(0, rec));
    }

    private void HandleDespawn(int peerId, PacketReader r)
    {
        int itemId = (int)r.ReadVarUInt();
        if (peerId != _career.WorldSourcePeer)
        {
            Reject(peerId, "despawn: only the world source despawns items", itemId);
            return;
        }
        if (Registry.TryDespawn(itemId, out _, out string? reason))
            Broadcast(BuildDespawned(itemId));
        else
            Reject(peerId, $"despawn: {reason}", itemId);
    }

    /// <summary>The pickup proximity gate: with a radius configured, the requester's last pose must
    /// be within it of the item's world pose (horizontal). Missing data — no radius, unknown item,
    /// no pose yet — passes so the check can only ever ADD a refusal (the career gate's posture).</summary>
    private bool PickupInReach(int peerId, int itemId, out string? reason)
    {
        reason = null;
        if (_config.PickupRadiusM <= 0) return true;
        if (!Registry.Items.TryGetValue(itemId, out ItemRecord? rec)) return true; // registry rejects it next
        if (rec.Location != ItemLocationKind.World) return true;                     // ditto ("already held")
        if (_poseOf(peerId) is not Pose pose) return true;

        float dx = pose.Px - rec.WorldPose.Px;
        float dz = pose.Pz - rec.WorldPose.Pz;
        float radius = _config.PickupRadiusM;
        if (dx * dx + dz * dz <= radius * radius) return true;

        reason = $"item {itemId} is {Math.Sqrt(dx * dx + dz * dz):F0} m away";
        return false;
    }

    // ── persistence ──

    internal ItemsSaveData Capture() => Registry.Capture();

    // ── packet builders ──

    private byte[] BuildSpawned(uint token, ItemRecord rec)
    {
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.ItemSpawned)
            .WriteVarUInt(token);
        ItemCodec.WriteItemDef(w, rec.Def);
        w.WriteByte((byte)rec.Provenance); // birth identity rides with the def, not the location (v12)
        WriteLocation(w, rec);
        return w.ToArray();
    }

    private byte[] BuildMoved(ItemRecord rec)
    {
        var w = new PacketWriter(48)
            .WriteByte((byte)MessageType.ItemMoved)
            .WriteVarUInt((uint)rec.Def.Id);
        WriteLocation(w, rec);
        return w.ToArray();
    }

    /// <summary>A possessed item whose holder's peer just died: peer 0 + name kept, bypassing
    /// <see cref="ServerCareer.PeerOf"/> — the departure sequence still has the dying peer mapped
    /// when this is sent, so resolving the scope would mirror a stale live id to the room.</summary>
    private static byte[] BuildMovedOffline(ItemRecord rec, string holderName)
    {
        var w = new PacketWriter(48)
            .WriteByte((byte)MessageType.ItemMoved)
            .WriteVarUInt((uint)rec.Def.Id)
            .WriteByte((byte)rec.Location);
        w.WriteVarUInt(0u);
        w.WriteString(holderName);
        return w.ToArray();
    }

    /// <summary>The shop catalog for the join burst: count then (itemPrefabName, price-cents) pairs
    /// from <see cref="ItemConfig.ShopPrices"/>. A prefab absent here is simply not for sale (the
    /// purchase is refused server-side); an empty catalog is a valid, empty message.</summary>
    private byte[] BuildShopCatalog()
    {
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.ItemShopCatalog)
            .WriteVarUInt((uint)_config.ShopPrices.Count);
        foreach (KeyValuePair<string, long> entry in _config.ShopPrices)
            w.WriteString(entry.Key).WriteInt64(entry.Value);
        return w.ToArray();
    }

    private static byte[] BuildDespawned(int itemId) =>
        new PacketWriter(8)
            .WriteByte((byte)MessageType.ItemDespawned)
            .WriteVarUInt((uint)itemId)
            .ToArray();

    /// <summary>Encode a record's location for the wire: a world pose, or — for a possession — the
    /// holder as (session peer id, name), NEVER the scope key. A shared-career (communal) item has
    /// no single holder, so it rides as peer 0 + "".</summary>
    private void WriteLocation(PacketWriter w, ItemRecord rec)
    {
        w.WriteByte((byte)rec.Location);
        if (rec.Location == ItemLocationKind.World)
        {
            PresenceCodec.WritePose(w, rec.WorldPose);
            w.WriteByte(rec.WorldLocked ? (byte)1 : (byte)0); // look-but-don't-touch essential (v9)
            return;
        }
        bool shared = rec.OwnerScope.Length > 0 && rec.OwnerScope[0] == '@';
        int peer = shared ? 0 : _career.PeerOf(rec.OwnerScope);
        string name = shared ? string.Empty : _career.NameOf(rec.OwnerScope);
        w.WriteVarUInt((uint)peer);
        w.WriteString(name);
    }

    private void Reject(int peerId, string reason, int itemId = 0)
    {
        RequestRejected?.Invoke(peerId, reason);
        byte[] payload = new PacketWriter(32)
            .WriteByte((byte)MessageType.ItemRejected)
            .WriteString(reason)
            .WriteVarUInt((uint)itemId)
            .ToArray();
        _transport.Send(peerId, payload, DeliveryMethod.ReliableOrdered);
    }

    private void Broadcast(byte[] payload)
    {
        foreach (int id in _connectedIds())
            _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }
}
