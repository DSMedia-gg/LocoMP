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

    private readonly NetClient _client;
    private readonly bool _isHost;
    private readonly Action<string> _log;

    // serverItemId ↔ the live GameObject present locally (a host native item, or a spawned replica).
    private readonly Dictionary<int, ItemBase> _itemByServerId = new();
    private readonly Dictionary<ItemBase, int> _serverIdByItem = new();
    private readonly HashSet<int> _spawnedIds = new();           // ids WE materialized (vs host natives)
    private readonly Dictionary<uint, ItemBase> _pendingRegistration = new(); // token → awaiting id echo
    private readonly Dictionary<ItemBase, Action<ItemBase>> _destroyHooks = new();
    private readonly HashSet<string> _missingPrefabWarned = new(StringComparer.Ordinal);

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
        if (!_client.Joined || StorageController.Instance == null) return;
        if (_isHost && !_captureInstalled) InstallCapture();

        _reconcileAccum += dt;
        if (_dirty || _reconcileAccum >= ReconcileIntervalSeconds)
        {
            _dirty = false;
            _reconcileAccum = 0;
            Reconcile();
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

    /// <summary>A player-owned item entered the host's world storage: a native drop. Register it so
    /// remote players see it. Skipped while we're applying a remote change (our own spawn adds to
    /// world storage too) and for items already mapped (a re-add echo).</summary>
    private void OnNativeWorldAdded(ItemBase item)
    {
        if (_applying || item == null || _serverIdByItem.ContainsKey(item)) return;
        RegisterNative(item);
    }

    private bool RegisterNative(ItemBase item)
    {
        string? prefab = PrefabNameOf(item);
        if (prefab == null) return false;
        uint token = _nextToken++;
        _pendingRegistration[token] = item;
        _client.Items.RegisterWorldItem(prefab, PresenceShim.ToAbsolutePose(item.transform), string.Empty, token);
        return true;
    }

    /// <summary>A world item left the host's world storage. If it's one we track and WE didn't cause
    /// it (a native grab / dumpster / install), it's no longer a shared world item — despawn it from
    /// the session. The physical item lives on in the host's hand/inventory (native, its own save);
    /// we only stop mirroring it.</summary>
    private void OnNativeWorldRemoved(ItemBase item)
    {
        if (_applying || item == null) return;
        if (!_serverIdByItem.TryGetValue(item, out int id)) return;
        Unmap(item);
        _client.Items.DespawnItem(id);
        _log($"[items] world item {id} ({PrefabNameOf(item) ?? "?"}) left the world locally — despawning from the session");
    }

    /// <summary>The item's GameObject is being destroyed (dumpster, consumed) — the item-world analog
    /// of TrainCar.OnCarAboutToBeDestroyed. Despawn from the session unless WE are destroying it, or
    /// the whole world is unloading (the session closes on that path anyway).</summary>
    private void OnItemDestroyed(ItemBase item)
    {
        if (_applying || item == null) return;
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
                if (!haveLocal) SpawnReplica(item);
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
            Map(spawned, item.Def.Id);
            _spawnedIds.Add(item.Def.Id);
            HookDestroy(spawned);
            _log($"[items] world item {item.Def.Id} ({item.Def.PrefabName}) materialized");
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

    /// <summary>Remove an item's local GameObject. On the host this can be a native item (carried off
    /// by a remote) or a replica; either way the physical object goes. Guarded so the storage/destroy
    /// events it fires don't loop back as a despawn-to-the-server.</summary>
    private void DespawnLocal(int id)
    {
        if (!_itemByServerId.TryGetValue(id, out ItemBase item)) return;
        UnmapById(id);
        if (item == null) return;
        _applying = true;
        try
        {
            UnhookDestroy(item);
            if (StorageController.Instance != null) StorageController.Instance.RemoveItemFromStorageItemList(item);
            Object.Destroy(item.gameObject);
        }
        catch (Exception e)
        {
            _log($"[items] despawn of item {id} failed (world teardown?): {e.Message}");
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
    }

    private void UnmapById(int id)
    {
        if (!_itemByServerId.TryGetValue(id, out ItemBase item)) return;
        _itemByServerId.Remove(id);
        _spawnedIds.Remove(id);
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
