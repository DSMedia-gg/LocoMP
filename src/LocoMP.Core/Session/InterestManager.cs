using System;
using System.Collections.Generic;

namespace LocoMP.Core.Session;

/// <summary>The kind of a spatially-relevant entity. Wire-stable (the low byte of an
/// <c>InterestHide</c> packet): only append, never renumber.</summary>
public enum EntityKind : byte
{
    /// <summary>Another player (their pose stream). Gated in Burst 1.</summary>
    Player = 0,

    /// <summary>A world-located item. Gated in Burst 2.</summary>
    Item = 1,

    /// <summary>A railed trainset (its snapshot stream — the ~96%). Gated in Burst 2.</summary>
    Trainset = 2,
}

/// <summary>Identity of a spatial entity: its kind + its id (player peer id / item id / trainset id).
/// Value-equality so it keys a relevance <see cref="HashSet{T}"/>.</summary>
public readonly struct EntityKey : IEquatable<EntityKey>
{
    public EntityKey(EntityKind kind, int id) { Kind = kind; Id = id; }

    public EntityKind Kind { get; }
    public int Id { get; }

    public bool Equals(EntityKey other) => Kind == other.Kind && Id == other.Id;
    public override bool Equals(object? obj) => obj is EntityKey k && Equals(k);
    public override int GetHashCode() => ((int)Kind << 28) ^ Id;
    public override string ToString() => $"{Kind}#{Id}";
}

/// <summary>An entity with a horizontal world position, as fed to <see cref="InterestManager"/> for
/// the distance test. Kind-agnostic so items and trains (Burst 2) reuse it verbatim.</summary>
public readonly struct SpatialEntity
{
    public SpatialEntity(EntityKey key, float x, float z) { Key = key; X = x; Z = z; }

    public EntityKey Key { get; }
    public float X { get; }
    public float Z { get; }
}

/// <summary>
/// Per-client spatial relevance (D10). The server keeps, for each connected client, the set of
/// entities currently in scope of its player, and relays a spatial entity's stream to a client only
/// while that entity is in the client's set. It touches no transport — it decides recipients
/// (<see cref="IsRelevant"/>, read on the hot path) and fires enter/leave callbacks (from the
/// periodic <see cref="Recompute"/>) that the owning subsystem turns into "spawn"/"hide" packets. So
/// the subsystems stay the only things that call <c>ITransport</c>, and this whole state machine
/// fuzzes headless (hard rule 8).
///
/// <para><b>Hysteresis:</b> an entity ENTERS at <see cref="InterestConfig.EnterRadiusM"/> and only
/// LEAVES past the wider <see cref="InterestConfig.LeaveRadiusM"/>, so an entity hovering at the
/// boundary doesn't flicker in and out.</para>
///
/// <para><b>Fail-open</b> everywhere data is missing: a disabled config, an unknown observer, or a
/// client with no pose yet ⇒ everything is relevant. A newly-admitted client is over-subscribed (it
/// got the full join burst) until its first real pose lets <see cref="Recompute"/> trim it via leave
/// events — so a client never spawns into a blank world.</para>
/// </summary>
public sealed class InterestManager
{
    private readonly InterestConfig _config;
    private readonly Func<IEnumerable<int>> _observers;
    private readonly Func<int, (float X, float Z)?> _observerPos;
    private readonly Func<IEnumerable<SpatialEntity>>? _worldEntities; // items/trains (Burst 2); null now
    private readonly Action<int, EntityKey> _onEnter;
    private readonly Action<int, EntityKey> _onLeave;
    private readonly Dictionary<int, ClientRelevance> _clients = new();
    private readonly HashSet<EntityKind> _suppressed = new();

    // Entities the last Recompute could actually place. An entity NOT in here has no known position
    // — an unposed player, or a trainset whose edge is missing from the topology — and is therefore
    // relevant to everyone. Same fail-open principle as an observer without an anchor, applied to the
    // other side of the distance test; without it an unplaceable entity would sit in nobody's scope
    // set and be silently hidden from everyone, which is the one failure this system must not have.
    private readonly HashSet<EntityKey> _placed = new();

    /// <param name="observerPos">A client's horizontal position, or null until it has sent a real pose
    /// (the fail-open signal). Doubles as the position of that peer AS an entity for other clients.</param>
    /// <param name="onEnter">(observer, entity) newly in scope — send its current full state. Player
    /// entities need no packet (identity is global; the next relayed pose is the "spawn").</param>
    /// <param name="onLeave">(observer, entity) newly out of scope — send a lightweight hide.</param>
    /// <param name="worldEntities">Non-player spatial entities (items/trains). Null in Burst 1.</param>
    public InterestManager(InterestConfig config, Func<IEnumerable<int>> observers,
        Func<int, (float X, float Z)?> observerPos, Action<int, EntityKey> onEnter,
        Action<int, EntityKey> onLeave, Func<IEnumerable<SpatialEntity>>? worldEntities = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Validate();
        _observers = observers ?? throw new ArgumentNullException(nameof(observers));
        _observerPos = observerPos ?? throw new ArgumentNullException(nameof(observerPos));
        _onEnter = onEnter ?? throw new ArgumentNullException(nameof(onEnter));
        _onLeave = onLeave ?? throw new ArgumentNullException(nameof(onLeave));
        _worldEntities = worldEntities;
    }

    /// <summary>The knobs, read by the owning subsystems to decide whether a given kind is gated.</summary>
    public InterestConfig Config => _config;

    /// <summary>
    /// Permanently fail open for one entity kind at RUNTIME, regardless of config — for when the data
    /// the filter needs turns out not to exist. The motivating case (D10 Burst 2): gating railed trains
    /// requires world geometry in the topology, and a v1 <c>.lmpw</c> (or no topology at all) has none,
    /// so every train would anchor nowhere. Suppressing is strictly safer than guessing a position:
    /// the session degrades to the old broadcast-everything behaviour for that kind instead of hiding
    /// trains that are actually next to you.
    ///
    /// <para>Deliberately NOT a mutation of the caller's <see cref="InterestConfig"/> — the config
    /// records what the operator ASKED for, this records what the server can actually deliver, and the
    /// difference is exactly what an operator needs told (hence <see cref="Suppressed"/>).</para>
    /// </summary>
    public void SuppressKind(EntityKind kind) => _suppressed.Add(kind);

    /// <summary>Kinds that <see cref="SuppressKind"/> has forced open. Surfaced so the server can say
    /// so in its banner rather than leaving an operator wondering why nothing is being filtered.</summary>
    public IReadOnlyCollection<EntityKind> Suppressed => _suppressed;

    /// <summary>Start tracking a newly-admitted client (empty scope, no anchor yet → fail-open until
    /// its first pose). The full join burst still runs; Recompute trims it once it moves.</summary>
    public void AddClient(int peerId)
    {
        if (!_clients.ContainsKey(peerId)) _clients[peerId] = new ClientRelevance(peerId);
    }

    /// <summary>Stop tracking a client (disconnect). Its relevance set is pure session state.</summary>
    public void RemoveClient(int peerId) => _clients.Remove(peerId);

    /// <summary>An entity is gone (a player left, an item despawned, a trainset retired): drop it from
    /// every client's scope WITHOUT firing a leave — the authoritative removal (PlayerLeft /
    /// ItemDespawned / TrainsetRemove) is broadcast globally and already cleans up the replica. This
    /// only prevents a stale key lingering in a set (which would suppress a future re-enter).</summary>
    public void ForgetEntity(EntityKey key)
    {
        foreach (ClientRelevance cr in _clients.Values) cr.InScope.Remove(key);
    }

    /// <summary>Hot-path recipient test: may this entity's stream be sent to this client right now?
    /// Fail-open when disabled, when the kind isn't gated, for an unknown observer, or before the
    /// client's first pose. Otherwise: is the entity in the client's cached scope.</summary>
    public bool IsRelevant(int peerId, EntityKey key)
    {
        if (!_config.Enabled) return true;
        if (!FilterEnabledFor(key.Kind)) return true;
        if (!_clients.TryGetValue(peerId, out ClientRelevance? cr)) return true;
        if (!cr.HasAnchor) return true;
        if (!_placed.Contains(key)) return true; // no known position for it → can't judge, so don't hide
        return cr.InScope.Contains(key);
    }

    /// <summary>Re-evaluate every client's relevance set against the current entity positions, firing
    /// enter/leave callbacks on crossings. Called on a throttle from <c>NetServer.Poll</c> — cheap
    /// O(clients × entities) float work a few times a second. A no-op while disabled.</summary>
    public void Recompute()
    {
        if (!_config.Enabled) return;

        float enter2 = _config.EnterRadiusM * _config.EnterRadiusM;
        float leave2 = _config.LeaveRadiusM * _config.LeaveRadiusM;

        // Collect the entity set once so every observer is evaluated against the same frame.
        List<SpatialEntity> entities = CollectEntities();
        _placed.Clear();
        foreach (SpatialEntity e in entities) _placed.Add(e.Key);

        foreach (int obs in _observers())
        {
            if (!_clients.TryGetValue(obs, out ClientRelevance? cr)) continue;
            (float X, float Z)? op = _observerPos(obs);
            if (op is null) continue; // no pose yet → fail-open (scope untouched, stays over-subscribed)

            // First real pose: the client has been over-subscribed (fail-open relayed EVERY entity's
            // stream to it), so seed its scope with all current entities before trimming. Then the
            // per-entity pass below fires a leave — a hide — for each one now out of range, and leaves
            // the in-range ones as-is (no redundant "enter" packet for a replica already shown).
            bool firstAnchor = !cr.HasAnchor;
            cr.HasAnchor = true;

            float ox = op.Value.X, oz = op.Value.Z;
            foreach (SpatialEntity e in entities)
            {
                if (e.Key.Kind == EntityKind.Player && e.Key.Id == obs) continue; // a player isn't its own entity

                if (firstAnchor) cr.InScope.Add(e.Key);

                float dx = ox - e.X, dz = oz - e.Z;
                float d2 = dx * dx + dz * dz;
                bool inScope = cr.InScope.Contains(e.Key);

                if (!inScope && d2 <= enter2) { cr.InScope.Add(e.Key); _onEnter(obs, e.Key); }
                else if (inScope && d2 > leave2) { cr.InScope.Remove(e.Key); _onLeave(obs, e.Key); }
                // within the hysteresis band (enter2 < d2 ≤ leave2): no change either way.
            }
        }
    }

    private List<SpatialEntity> CollectEntities()
    {
        var list = new List<SpatialEntity>();
        if (FilterEnabledFor(EntityKind.Player))
        {
            foreach (int p in _observers())
            {
                (float X, float Z)? pos = _observerPos(p);
                if (pos is null) continue; // an unposed player has no known position → not yet an entity
                list.Add(new SpatialEntity(new EntityKey(EntityKind.Player, p), pos.Value.X, pos.Value.Z));
            }
        }
        if (_worldEntities != null)
            foreach (SpatialEntity e in _worldEntities())
            {
                // Only track kinds actually being filtered. Without this an ungated kind would still
                // accumulate scope and fire LEAVE callbacks — sending a hide for a stream IsRelevant
                // happily keeps relaying, i.e. an invisible train that is still eating bandwidth.
                if (FilterEnabledFor(e.Key.Kind)) list.Add(e);
            }
        return list;
    }

    private bool FilterEnabledFor(EntityKind kind)
    {
        if (_suppressed.Contains(kind)) return false; // the data this filter needs isn't available
        return kind switch
        {
            EntityKind.Player => _config.FilterPlayers,
            EntityKind.Item => _config.FilterItems,
            EntityKind.Trainset => _config.FilterTrains,
            _ => false,
        };
    }

    private sealed class ClientRelevance
    {
        public ClientRelevance(int peerId) => PeerId = peerId;
        public int PeerId { get; }
        public HashSet<EntityKey> InScope { get; } = new();

        /// <summary>Set once the client has a real pose. Until then it is fail-open (over-subscribed).</summary>
        public bool HasAnchor { get; set; }
    }
}
