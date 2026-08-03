using System;
using System.Collections.Generic;
using System.Linq;
using DV.CabControls;
using LocoMP.Core.Items;
using LocoMP.Core.Session;
using UnityEngine;

// UnityEngine's own Object provides Instantiate/Destroy; System is imported for Action/Exception.
using Object = UnityEngine.Object;
using Pose = LocoMP.Core.Presence.Pose;

namespace LocoMP.Shim;

/// <summary>
/// M4.2: the item seam between DV's handheld objects (lanterns, keys, boots…) and Core's item
/// protocol (v6). Two halves, in the shape M2/M3 established:
///
/// Capture (HOST only, world source — the D13 posture JobCapture/LicenseSync use): DV runs the real
/// world, so the host's own world items ARE the truth. A player-owned item entering
/// <c>StorageWorld</c> (a native drop) is registered onto the server; leaving it (a native grab,
/// dumpster) despawns it from the session. A join-time sweep offers items already lying in the world,
/// exactly as JobCapture sweeps pre-session jobs. No Harmony — every seam is a public event.
///
/// Materialization (BOTH roles): the server's item board is reconciled against the local world. A
/// WORLD item that isn't physically here yet is spawned via the game's own two-liner
/// (<c>Resources.Load(prefabName)</c> + Instantiate + <c>AddItemToWorldStorage</c>); an item that
/// moved into someone's possession, or left entirely, is despawned. Replicas are spawned
/// <c>BelongsToPlayer = true</c>, which the recon confirmed makes <c>ItemDisabler</c> exempt them
/// from distance streaming-off (items are only <c>SetActive(false)</c>'d, never destroyed like cars —
/// so a plain keep-alive suffices, no M3.5b proximity-materialization machinery). Our own native
/// applies are wrapped in a reentrancy guard so they never echo back as captures (the M2 idiom).
///
/// The single-location invariant + persistence live server-side (ItemRegistry, M4.1). On the host,
/// items persist through DV's own save (they're real SP items) and re-sweep next session, so the
/// host restore deliberately starts the LocoMP item store empty — the same call the trainset store
/// makes, and for the same reason (the live world is the physical truth). The dedicated server (M6)
/// is where the LocoMP item save becomes the source.
/// </summary>
public sealed class ItemSync : IDisposable
{
    private const double ReconcileIntervalSeconds = 0.5;

    // R20 (2026-08-04 gauntlet F9): DV's self-placement bounces an item out of and straight back
    // into world storage with no player input, and registering fresh per bounce minted unbounded
    // world-item ids (two paint cans → ~40 ids in minutes; a dedicated server accumulates them
    // forever). Both directions debounce: an unmapped ADD only registers once the item has
    // SETTLED in world storage, and a mapped REMOVE that is not a grab/destroy holds its despawn
    // briefly — a re-add inside the hold keeps the id and mapping with zero server traffic. A
    // real grab despawns immediately (IsGrabbed discriminates), so the possession-race window
    // stays as narrow as it was.
    private const float RegisterSettleSeconds = 1f;
    private const float DespawnHoldSeconds = 1f;

    private readonly NetClient _client;
    private readonly bool _isHost;
    private readonly Action<string> _log;

    // serverItemId ↔ the live GameObject present locally (a host native item, or a spawned replica).
    private readonly Dictionary<int, ItemBase> _itemByServerId = new();
    private readonly Dictionary<ItemBase, int> _serverIdByItem = new();
    private readonly HashSet<int> _spawnedIds = new();           // ids WE materialized (vs host natives)
    private readonly HashSet<int> _pinnedIds = new();            // world items we hold as server-pinned kinematic ghosts (re-asserted per frame; DV re-enables physics on un-held items otherwise)
    private readonly Dictionary<int, (ItemBase Item, Pose AbsPose)> _hiddenNatives = new(); // host natives a remote carried off — hidden (not destroyed) + the absolute pose to restore them to (an inactive item misses OriginShift, so its raw local transform goes stale)
    private readonly Dictionary<uint, ItemBase> _pendingRegistration = new(); // token → awaiting id echo
    private readonly Dictionary<ItemBase, Action<ItemBase>> _destroyHooks = new();
    private readonly HashSet<string> _missingPrefabWarned = new(StringComparer.Ordinal);
    private readonly Dictionary<ItemBase, float> _pendingNativeAdds = new();          // R20: item → register-at
    private readonly Dictionary<ItemBase, (int Id, float At)> _pendingDespawns = new(); // R20: item → (id, despawn-at)

    private bool _captureInstalled;
    private bool _applying;      // native changes WE initiate must not echo back as captures (M2 idiom)
    private uint _nextToken = 1; // token 0 is reserved (a plain broadcast spawn, not a registration echo)
    private double _reconcileAccum;
    private bool _dirty;         // a server item event arrived — reconcile next tick without waiting out the interval
    private StorageBase? _worldStorage;

    public ItemSync(NetClient client, bool isHost, Action<string> log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _isHost = isHost;
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _client.Items.ItemAdded += OnServerItemChanged;
        _client.Items.ItemMoved += OnServerItemChanged;
        _client.Items.ItemRemoved += OnServerItemRemoved;
        _client.Items.RegisterAccepted += OnRegisterAccepted;
        _client.Items.RequestRejected += OnServerRejected;
    }

    /// <summary>Pump from the session update loop. Installs host capture once the storage singleton
    /// exists (it appears after the world loads, like the track registry) and reconciles the local
    /// world to the server board.</summary>
    public void Tick(double dt)
    {
        // WorldAlive comes FIRST and is not merely an optimization: StorageController allows auto-creation,
        // so on a world that has just unloaded the `Instance == null` probe below would itself construct one
        // and throw out of Initialize(). Order matters — see PresenceShim.WorldAlive.
        if (!_client.Joined || !PresenceShim.WorldAlive || StorageController.Instance == null) return;
        if (_isHost && !_captureInstalled) InstallCapture();

        _reconcileAccum += dt;
        if (_dirty || _reconcileAccum >= ReconcileIntervalSeconds)
        {
            _dirty = false;
            _reconcileAccum = 0;
            Reconcile();
        }

        PumpNativeDebounce();
        PinWorldGhosts();
    }

    /// <summary>R20: fire debounced registrations for items that stayed settled in world storage,
    /// and held despawns whose re-add never came. Both maps are small (human-paced drops).</summary>
    private void PumpNativeDebounce()
    {
        float now = Time.unscaledTime;
        if (_pendingNativeAdds.Count > 0)
        {
            foreach (KeyValuePair<ItemBase, float> kv in _pendingNativeAdds.ToList())
            {
                if (kv.Key == null) { _pendingNativeAdds.Remove(kv.Key!); continue; } // destroyed unmapped
                if (now < kv.Value) continue;
                _pendingNativeAdds.Remove(kv.Key);
                if (!_serverIdByItem.ContainsKey(kv.Key)) RegisterNative(kv.Key);
            }
        }
        if (_pendingDespawns.Count > 0)
        {
            foreach (KeyValuePair<ItemBase, (int Id, float At)> kv in _pendingDespawns.ToList())
            {
                if (kv.Key != null && now < kv.Value.At) continue;
                _pendingDespawns.Remove(kv.Key!);
                Unmap(kv.Key!); // reference-keyed — works even for a destroyed ItemBase
                _client.Items.DespawnItem(kv.Value.Id);
                _log($"[items] world item {kv.Value.Id} left the world locally (no re-add within " +
                     $"{DespawnHoldSeconds:F0} s) — despawning from the session");
            }
        }
    }

    /// <summary>Hold each materialized world item at its server pose, kinematic, every frame — DV's
    /// <c>CabItemRigidbody</c> re-enables physics on an un-held item, so a one-time freeze doesn't stick and
    /// the item free-falls through the floor. A synced world item nobody is holding is a server-authoritative
    /// kinematic ghost (the M2 train model). Drops out the moment the item stops being a World item (a local
    /// grab / despawn), which unpins it so native physics resumes.</summary>
    private void PinWorldGhosts()
    {
        if (_pinnedIds.Count == 0) return;
        foreach (int id in _pinnedIds.ToList())
        {
            if (!_itemByServerId.TryGetValue(id, out ItemBase item) || item == null
                || !_client.Items.Items.TryGetValue(id, out ClientItem? live) || live.Location != ItemLocationKind.World)
            {
                _pinnedIds.Remove(id);
                continue;
            }
            item.transform.SetPositionAndRotation(
                PresenceShim.ToLocalPosition(live.WorldPose), PresenceShim.ToRotation(live.WorldPose));
            SettleAtRest(item);
        }
    }

    // ── host capture (world source) ──

    private void InstallCapture()
    {
        StorageController sc = StorageController.Instance;
        if (sc == null || sc.StorageWorld == null) return;
        _worldStorage = sc.StorageWorld;
        _worldStorage.ItemAdded += OnNativeWorldAdded;
        _worldStorage.ItemRemoved += OnNativeWorldRemoved;
        _captureInstalled = true;

        int swept = 0;
        foreach (ItemBase item in _worldStorage.GetStorageItemList().ToList())
        {
            if (item == null || _serverIdByItem.ContainsKey(item)) continue;
            if (RegisterNative(item)) swept++;
        }
        _log($"[items] host item capture installed — offered {swept} world item(s) to the session");
    }

    /// <summary>A player-owned item entered the host's world storage: a native drop. Skipped while
    /// we're applying a remote change (our own spawn adds to world storage too) and for items
    /// already mapped (a re-add echo). R20: registration is DEBOUNCED — DV's self-placement can
    /// bounce the item back out within a frame, and a held despawn cancelled here keeps the
    /// existing id with zero server traffic.</summary>
    private void OnNativeWorldAdded(ItemBase item)
    {
        if (_applying || item == null) return;
        if (_pendingDespawns.Remove(item)) return; // the bounce came home — id + mapping intact
        if (_serverIdByItem.ContainsKey(item)) return;
        _pendingNativeAdds[item] = Time.unscaledTime + RegisterSettleSeconds;
    }

    private bool RegisterNative(ItemBase item)
    {
        string? prefab = PrefabNameOf(item);
        if (prefab == null) return false;
        // A DV personal essential (Map, CommsRadio, wallet, Compass, DVGuide) syncs as LOCKED — visible
        // to everyone, but only its owner can pick it up ("look, but don't touch"). Job paperwork is ALSO
        // flagged essential by DV, but it's shared crew state (both read the brief; anyone can validate a
        // leaflet, the claim following via the career sync — not the paper), so it registers as a normal
        // shareable item. Everything else is shareable too.
        bool locked = IsEssential(item) && !IsJobItem(item);
        uint token = _nextToken++;
        _pendingRegistration[token] = item;
        _client.Items.RegisterWorldItem(prefab, PresenceShim.ToAbsolutePose(item.transform), string.Empty, token, locked);
        return true;
    }

    /// <summary>A world item left the host's world storage. If it's one we track and WE didn't cause
    /// it (a native grab / dumpster / install), it's no longer a shared world item — despawn it from
    /// the session. The physical item lives on in the host's hand/inventory (native, its own save);
    /// we only stop mirroring it. R20: a removal that is NOT a grab is usually DV's self-placement
    /// bounce — hold the despawn briefly so the re-add can keep the id; a grab stays immediate so
    /// the possession-race window doesn't widen.</summary>
    private void OnNativeWorldRemoved(ItemBase item)
    {
        if (_applying || item == null) return;
        _pendingNativeAdds.Remove(item); // an unmapped item that bounced out before settling never registers
        if (!_serverIdByItem.TryGetValue(item, out int id)) return;
        if (IsGrabbedSafe(item))
        {
            Unmap(item);
            _client.Items.DespawnItem(id);
            _log($"[items] world item {id} ({PrefabNameOf(item) ?? "?"}) left the world locally (grabbed) — despawning from the session");
            return;
        }
        _pendingDespawns[item] = (id, Time.unscaledTime + DespawnHoldSeconds);
    }

    /// <summary>IsGrabbed is DV interaction-layer code — treat a throw as "not grabbed" (the
    /// conservative answer here: worst case the despawn is one hold-window late).</summary>
    private static bool IsGrabbedSafe(ItemBase item)
    {
        try { return item.IsGrabbed(); }
        catch { return false; }
    }

    /// <summary>The item's GameObject is being destroyed (dumpster, consumed) — the item-world analog
    /// of TrainCar.OnCarAboutToBeDestroyed. Despawn from the session unless WE are destroying it, or
    /// the whole world is unloading (the session closes on that path anyway).</summary>
    private void OnItemDestroyed(ItemBase item)
    {
        if (_applying || item == null) return;
        _pendingNativeAdds.Remove(item);
        _pendingDespawns.Remove(item); // destruction is final — no re-add is coming (R20)
        if (!_serverIdByItem.TryGetValue(item, out int id)) return;
        Unmap(item);
        _client.Items.DespawnItem(id);
    }

    /// <summary>The server assigned an id to one of our registrations — map the native GameObject onto
    /// it so pickups/despawns can find it, and hook its destruction.</summary>
    private void OnRegisterAccepted(uint token, ClientItem item)
    {
        if (token == 0 || !_pendingRegistration.TryGetValue(token, out ItemBase native)) return;
        _pendingRegistration.Remove(token);
        if (native == null) return;
        Map(native, item.Def.Id);      // a host native — NOT added to _spawnedIds; Reconcile won't respawn it
        HookDestroy(native);
        // Position + distance are kept (cheap, one short string): "where did this item go?" is the question
        // that recurs every time the item loop misbehaves in-game.
        _log($"[items] registered native item {item.Def.Id} ({PrefabNameOf(native) ?? "?"}) at {PosInfo(PresenceShim.ToAbsolutePose(native.transform))}");
    }

    // ── server board → local world ──

    private void OnServerItemChanged(ClientItem _) => _dirty = true;
    private void OnServerItemRemoved(int _) => _dirty = true;

    private void OnServerRejected(string reason, int itemId) =>
        _log($"[items] request refused{(itemId != 0 ? $" (item {itemId})" : "")}: {reason}");

    /// <summary>Bring the local world in line with the server board: every WORLD item should have a
    /// live GameObject here; every possessed/removed one should not. Idempotent — safe to run on any
    /// change. Host natives are already mapped (skip-spawn); only genuinely-remote world items get a
    /// fresh replica.</summary>
    private void Reconcile()
    {
        if (StorageController.Instance == null) return;

        foreach (ClientItem item in _client.Items.Items.Values.ToList())
        {
            bool haveLocal = _itemByServerId.ContainsKey(item.Def.Id);
            if (item.Location == ItemLocationKind.World)
            {
                if (haveLocal) continue;                                  // already present
                if (_hiddenNatives.ContainsKey(item.Def.Id)) ReShowNative(item); // our native, dropped back
                else SpawnReplica(item);                                  // a genuinely-remote item
            }
            else if (haveLocal)
            {
                // Now in someone's possession (a remote picked it up) — it's carried away, so no
                // physical item stays behind here (the host's own dropped item vanishes too).
                DespawnLocal(item.Def.Id);
            }
        }

        // Anything we still hold that the board no longer lists as a world item → despawn (a remote
        // pickup we haven't processed above, or an outright removal).
        foreach (int id in _itemByServerId.Keys.ToList())
        {
            if (!_client.Items.Items.TryGetValue(id, out ClientItem? live) || live.Location != ItemLocationKind.World)
                DespawnLocal(id);
        }
    }

    /// <summary>Materialize a world item from a server record — the game's own spawn path (recon §1/§3):
    /// <c>Resources.Load(prefabName)</c> + Instantiate, mark it player-owned (world-storage acceptance
    /// AND the ItemDisabler keep-alive exemption), and register with world storage so DV's bookkeeping
    /// sees it. <c>ItemBase.Awake</c> self-assembles the rigidbody/ECS/etc., so a bare Instantiate
    /// yields a fully functional item.</summary>
    private void SpawnReplica(ClientItem item)
    {
        if (Resources.Load(item.Def.PrefabName) is not GameObject prefab)
        {
            if (_missingPrefabWarned.Add(item.Def.PrefabName))
                _log($"[items] cannot spawn item '{item.Def.PrefabName}' — Resources.Load found no prefab (modded content?)");
            return;
        }

        Vector3 pos = PresenceShim.ToLocalPosition(item.WorldPose);
        Quaternion rot = PresenceShim.ToRotation(item.WorldPose);
        _applying = true;
        try
        {
            GameObject go = Object.Instantiate(prefab, pos, rot);
            go.name = item.Def.PrefabName;
            ItemBase? spawned = go.GetComponent<ItemBase>();
            InventoryItemSpec? spec = go.GetComponent<InventoryItemSpec>();
            if (spawned == null || spec == null)
            {
                _log($"[items] spawned '{item.Def.PrefabName}' but it lacks ItemBase/InventoryItemSpec — discarding");
                Object.Destroy(go);
                return;
            }
            spec.BelongsToPlayer = true; // world-storage acceptance + ItemDisabler keep-alive exemption
            StorageController.Instance.AddItemToWorldStorage(spawned);
            spawned.RefreshLayersExternal(); // force the World_Item layer — else it's stranded on Grabbed_Item (invisible in world + ungrabbable)
            SettleAtRest(spawned);       // server-authoritative rest pose — don't let it free-fall / tunnel
            Map(spawned, item.Def.Id);
            _spawnedIds.Add(item.Def.Id);
            _pinnedIds.Add(item.Def.Id); // hold it at the server pose every frame (PinWorldGhosts)
            HookDestroy(spawned);
            // A locked personal essential replicates as VISIBLE; the SERVER refuses any pickup of it, so
            // a non-owner can never take it. Making the replica physically non-grabbable in-hand (VR) is a
            // friend-session follow-on (needs the VRTK/interaction ref) — server enforcement stands alone.
            _log($"[items] world item {item.Def.Id} ({item.Def.PrefabName}) materialized{(item.WorldLocked ? " (locked — owner only)" : "")} at {PosInfo(item.WorldPose)}");
        }
        catch (Exception e)
        {
            _log($"[items] spawn of '{item.Def.PrefabName}' failed: {e.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>An item left the shared world (a remote carried it off, or it was removed). A REPLICA we
    /// spawned is destroyed outright. A host NATIVE is only HIDDEN — <c>SetActive(false)</c> + pulled from
    /// world storage — never destroyed: destroying a real DV item fights its lifecycle (RespawnOnDrop
    /// re-parents it mid-destroy → "Cannot set parent while being destroyed", and essential items like
    /// the map respawn), and the recon confirmed DV's own way to make an item vanish is to deactivate it.
    /// A hidden native is re-shown at its new pose if the remote drops it back (<see cref="ReShowNative"/>)
    /// and reactivated on Dispose, so the host's real item is never lost or duplicated. Guarded so the
    /// storage events it fires don't loop back as a despawn-to-the-server.</summary>
    private void DespawnLocal(int id)
    {
        bool ours = _spawnedIds.Contains(id);
        if (!_itemByServerId.TryGetValue(id, out ItemBase item)) return;
        UnmapById(id);
        _pinnedIds.Remove(id);   // no longer a live world ghost we position
        if (item == null) return;
        _applying = true;
        try
        {
            UnhookDestroy(item);
            if (StorageController.Instance != null) StorageController.Instance.RemoveItemFromStorageItemList(item);
            if (ours)
            {
                Object.Destroy(item.gameObject);        // our replica — safe to destroy
                _log($"[items] replica item {id} removed (carried off / gone)");
            }
            else
            {
                Pose absPose = PresenceShim.ToAbsolutePose(item.transform); // snapshot BEFORE hiding — an inactive item misses OriginShift, so we must restore from an absolute pose, not the stale local transform
                item.gameObject.SetActive(false);       // the host's real item — hide, never destroy
                _hiddenNatives[id] = (item, absPose);
                _log($"[items] native item {id} hidden (a remote carried it off) — was at {PosInfo(absPose)}");
            }
        }
        catch (Exception e)
        {
            _log($"[items] removing item {id} failed (world teardown?): {e.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>A host native we hid (a remote carried it off) is a world item again — the remote dropped
    /// it back. Re-show the SAME GameObject at the new pose instead of spawning a replica, so the item is
    /// never duplicated. If DV destroyed it while hidden, fall through so the next reconcile spawns a
    /// replica.</summary>
    private void ReShowNative(ClientItem item)
    {
        if (!_hiddenNatives.TryGetValue(item.Def.Id, out (ItemBase Item, Pose AbsPose) hidden)) return;
        _hiddenNatives.Remove(item.Def.Id);
        ItemBase native = hidden.Item;
        if (native == null) return; // gone while hidden — reconcile will SpawnReplica next pass
        _applying = true;
        try
        {
            native.transform.SetPositionAndRotation(
                PresenceShim.ToLocalPosition(item.WorldPose), PresenceShim.ToRotation(item.WorldPose));
            native.gameObject.SetActive(true);
            if (StorageController.Instance != null) StorageController.Instance.AddItemToWorldStorage(native);
            native.RefreshLayersExternal(); // World_Item layer — a reused native can be stuck on Grabbed_Item (invisible + ungrabbable)
            SettleAtRest(native);       // rest at the drop pose — don't free-fall / tunnel / trip RespawnOnDrop
            Map(native, item.Def.Id);   // native (NOT added to _spawnedIds) — Dispose leaves it be
            _pinnedIds.Add(item.Def.Id); // hold it at the server pose every frame (PinWorldGhosts)
            HookDestroy(native);
            _log($"[items] native item {item.Def.Id} shown again (a remote dropped it back) — at {PosInfo(item.WorldPose)}");
        }
        catch (Exception e)
        {
            _log($"[items] re-show of item {item.Def.Id} failed: {e.Message}");
        }
        finally
        {
            _applying = false;
        }
    }

    // ── maps + hooks ──

    private void Map(ItemBase item, int id)
    {
        _itemByServerId[id] = item;
        _serverIdByItem[item] = id;
    }

    private void Unmap(ItemBase item)
    {
        if (!_serverIdByItem.TryGetValue(item, out int id)) return;
        _serverIdByItem.Remove(item);
        _itemByServerId.Remove(id);
        _spawnedIds.Remove(id);
        _pinnedIds.Remove(id);
    }

    private void UnmapById(int id)
    {
        if (!_itemByServerId.TryGetValue(id, out ItemBase item)) return;
        _itemByServerId.Remove(id);
        _spawnedIds.Remove(id);
        _pinnedIds.Remove(id);
        if (item != null) _serverIdByItem.Remove(item);
    }

    private void HookDestroy(ItemBase item)
    {
        if (_destroyHooks.ContainsKey(item)) return;
        Action<ItemBase> onGone = _ => OnItemDestroyed(item);
        item.AboutToBeDestroyed += onGone;
        _destroyHooks[item] = onGone;
    }

    private void UnhookDestroy(ItemBase item)
    {
        if (!_destroyHooks.TryGetValue(item, out Action<ItemBase> onGone)) return;
        item.AboutToBeDestroyed -= onGone;
        _destroyHooks.Remove(item);
    }

    private static string? PrefabNameOf(ItemBase item)
    {
        try { return item.InventorySpecs != null ? item.InventorySpecs.ItemPrefabName : null; }
        catch { return null; }
    }

    /// <summary>Diagnostic: an absolute item position plus its distance from the local player, so a live
    /// session can tell whether an item event happened right next to the player or streamed-out far away.</summary>
    private static string PosInfo(Pose abs)
    {
        string at = $"({abs.Px:F0},{abs.Py:F0},{abs.Pz:F0})";
        if (!PresenceShim.TryCaptureLocalPose(out Pose me)) return at;
        float dx = abs.Px - me.Px, dy = abs.Py - me.Py, dz = abs.Pz - me.Pz;
        return $"{at}, {System.Math.Sqrt(dx * dx + dy * dy + dz * dz):F0} m from you";
    }

    /// <summary>Settle a just-placed item at rest: zero its velocity and make the Rigidbody kinematic so it
    /// stays exactly where the server said instead of free-falling (a freshly-<c>SetActive</c>'d item wakes
    /// non-kinematic with gravity — recon §1 — and tunnels through the floor before its collider engages,
    /// which also trips <c>RespawnOnDrop</c> into a duplicate). A synced world item is server-authoritative,
    /// so local physics simulation would only desync it; a native grab re-enables physics normally.</summary>
    private static void SettleAtRest(ItemBase item)
    {
        try
        {
            // ItemRigidbody is null when CabItem isn't assembled — go straight to the components. Freeze ALL
            // bodies so DV's per-frame physics (CabItemRigidbody re-enables it on an un-held item) can't drop
            // the item; PinWorldGhosts re-asserts this every frame until the local player takes it.
            foreach (Rigidbody rb in item.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            {
                if (rb == null) continue;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
        }
        catch { /* no rigidbody / not yet assembled — nothing to settle */ }
    }

    /// <summary>DV's own per-player flag — Map/CommsRadio/wallet/Compass/DVGuide and dynamically-marked
    /// items (job booklets) set it. Essentials sync LOCKED (look-but-don't-touch), EXCEPT job paperwork
    /// (see <see cref="IsJobItem"/>), which is shared.</summary>
    private static bool IsEssential(ItemBase item)
    {
        try { return item.InventorySpecs != null && item.InventorySpecs.IsEssential; }
        catch { return false; }
    }

    /// <summary>Job paperwork (booklet / leaflet-overview / report). DV flags it essential, but it's
    /// shared crew state — both crew read the brief, and anyone can insert a leaflet at a validator (the
    /// claim then follows via the career sync, not the paper) — so it syncs like a normal shareable item,
    /// not a locked personal essential.</summary>
    private static bool IsJobItem(ItemBase item)
    {
        try
        {
            return item.GetComponent<JobBooklet>() != null
                || item.GetComponent<JobOverview>() != null
                || item.GetComponent<JobReport>() != null;
        }
        catch { return false; }
    }


    public void Dispose()
    {
        _client.Items.ItemAdded -= OnServerItemChanged;
        _client.Items.ItemMoved -= OnServerItemChanged;
        _client.Items.ItemRemoved -= OnServerItemRemoved;
        _client.Items.RegisterAccepted -= OnRegisterAccepted;
        _client.Items.RequestRejected -= OnServerRejected;

        if (_worldStorage != null)
        {
            _worldStorage.ItemAdded -= OnNativeWorldAdded;
            _worldStorage.ItemRemoved -= OnNativeWorldRemoved;
        }
        _pendingNativeAdds.Clear();
        _pendingDespawns.Clear(); // session over — the board dies with it; nothing to flush

        // Restore any host natives we hid (a remote was holding them at leave) — reposition to the pose we
        // snapshotted when hiding (an inactive item misses OriginShift, so its raw local transform is stale;
        // this mirrors ReShowNative), reactivate, and re-add to world storage so the host's real world is
        // whole again (they belong to its SP save). Logged per item so a failed restore is never silent.
        //
        // Skipped entirely when the world has already unloaded (quit to menu rather than Leave): there is no
        // world left to restore INTO, the items were destroyed with the scene, and touching StorageController
        // here would resurrect the singleton on a dead world (PresenceShim.WorldAlive). Nothing is lost —
        // these are the host's own natives, which come back from its SP save.
        if (!PresenceShim.WorldAlive && _hiddenNatives.Count > 0)
        {
            _log($"[items] world already unloaded — skipping restore of {_hiddenNatives.Count} hidden native " +
                 "item(s); they return with your save, not with the session");
            _hiddenNatives.Clear();
        }

        foreach (KeyValuePair<int, (ItemBase Item, Pose AbsPose)> hidden in _hiddenNatives.ToList())
        {
            int id = hidden.Key;
            ItemBase native = hidden.Value.Item;
            if (native == null)
            {
                _log($"[items] native item {id} was destroyed while hidden — cannot restore it on leave");
                continue;
            }
            try
            {
                native.transform.SetPositionAndRotation(
                    PresenceShim.ToLocalPosition(hidden.Value.AbsPose), PresenceShim.ToRotation(hidden.Value.AbsPose));
                native.gameObject.SetActive(true);
                if (StorageController.Instance != null) StorageController.Instance.AddItemToWorldStorage(native);
                native.RefreshLayersExternal(); // World_Item layer (see ReShowNative)
                SettleAtRest(native);   // rest at the restore pose — don't free-fall / tunnel
                _log($"[items] native item {id} restored to your world on leave — at {PosInfo(hidden.Value.AbsPose)}");
            }
            catch (Exception e)
            {
                _log($"[items] restore of native item {id} on leave failed: {e.Message}");
            }
        }
        _hiddenNatives.Clear();

        // Clean up only the replicas WE created — the host's real native items stay in its world
        // (they belong to its SP save; deleting them on leave would be data loss).
        foreach (int id in _spawnedIds.ToList()) DespawnLocal(id);

        // Unhook whatever native items remain mapped (host natives we never spawned).
        foreach (ItemBase item in _serverIdByItem.Keys.ToList()) UnhookDestroy(item);
        _itemByServerId.Clear();
        _serverIdByItem.Clear();
        _spawnedIds.Clear();
        _destroyHooks.Clear();
        _pendingRegistration.Clear();
    }
}
