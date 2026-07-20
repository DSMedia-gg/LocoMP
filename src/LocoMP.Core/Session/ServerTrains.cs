using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
using LocoMP.Core.Presence;
using LocoMP.Core.Protocol;
using LocoMP.Core.Trains;

namespace LocoMP.Core.Session;

/// <summary>
/// The server's train subsystem (03 §3–§5), owned by <see cref="NetServer"/> and fed the train
/// message types its dispatcher doesn't handle itself. Holds the authoritative
/// <see cref="TrainsetRegistry"/> plus junction/turntable/control-grant state; validates every
/// client proposal (state authority — clients propose, the server commits); relays owner snapshots
/// after the 03 §4 admission check; sends the world burst to newcomers and parks a leaver's
/// consists. Interest management (D10) will replace the broadcast fan-outs here with per-client
/// relevance sets — the per-recipient send loops are already shaped for it.
/// </summary>
public sealed class ServerTrains
{
    private readonly ITransport _transport;
    private readonly Func<IEnumerable<int>> _connectedIds;
    private readonly Dictionary<uint, byte> _junctions = new();
    private readonly Dictionary<uint, float> _turntables = new();
    private readonly Dictionary<int, int> _grants = new(); // carId → holding playerId

    // Latest committed cab-control values, carId → (controlId → value). Owner-authoritative and
    // replayed in the join burst so a newcomer's replica levers match reality (M3.5c). Session
    // state only — restored worlds come back neutral, like the game's own cold start.
    private readonly Dictionary<int, Dictionary<byte, float>> _controls = new();

    // Last ADMITTED snapshot per trainset — always epoch-current by construction (admission checks
    // the epoch and every transaction prunes). Feeds the join-burst baseline and the world save.
    private readonly Dictionary<int, TrainsetSnapshot> _latest = new();

    // Trainsets the SERVER spawned as its own (M6-B.2/B.3). Stays a member even while a player has it
    // on loan, so a release or a disconnect hands it back to the server rather than parking it dead.
    private readonly HashSet<int> _serverOwnedSets = new();

    internal ServerTrains(ITransport transport, IClock clock, Func<IEnumerable<int>> connectedIds)
    {
        _transport = transport;
        _connectedIds = connectedIds;
        Registry = new TrainsetRegistry(clock);
    }

    /// <summary>The authoritative consist record. Exposed for the host UI, admin, and tests.</summary>
    public TrainsetRegistry Registry { get; }

    public IReadOnlyDictionary<uint, byte> Junctions => _junctions;
    public IReadOnlyDictionary<uint, float> Turntables => _turntables;

    /// <summary>Current control-grant holders, carId → playerId.</summary>
    public IReadOnlyDictionary<int, int> Grants => _grants;

    /// <summary>Owner id for consists the SERVER itself drives (the dedicated server's kinematic
    /// coaster, M6-B.2) — never a real peer (peer ids start at 1) and never 0 (parked), so
    /// <see cref="TrainsetRegistry.TryClaim"/> refuses to hand one to a player and the server stays
    /// their sole authority. A player interacting with one (couple/comms) routes to this dead id and is
    /// a harmless no-op — server trains are ambient; full player-takeover is a later refinement.</summary>
    public const int ServerOwnerId = int.MaxValue;

    /// <summary>Owner snapshots refused by the 03 §4 admission check (stale epoch, retired id, or
    /// non-owner sender) and dropped before relay. The server-side half of the fuzz oracle.</summary>
    public long StaleSnapshotsDropped { get; private set; }

    /// <summary>A client proposal failed validation: (peerId, reason). Surfaced for the host
    /// console/log; the client-facing escape hatch remains ResyncRequest (03 §4).</summary>
    public event Action<int, string>? ProposalRejected;

    /// <summary>Handle a train message from an ADMITTED peer. Returns false for non-train types.
    /// <paramref name="payload"/> is the full original packet, reused verbatim for snapshot relay.</summary>
    internal bool TryHandle(int peerId, MessageType type, PacketReader r, byte[] payload)
    {
        switch (type)
        {
            case MessageType.TrainsetRegister: HandleRegister(peerId, r); return true;
            case MessageType.TrainsetSnapshot: HandleSnapshot(peerId, r, payload); return true;
            case MessageType.CoupleProposal: HandleCouple(peerId, r); return true;
            case MessageType.UncoupleProposal: HandleUncouple(peerId, r); return true;
            case MessageType.DerailReport: HandleDerail(peerId, r); return true;
            case MessageType.RerailRequest: HandleRerail(peerId, r); return true;
            case MessageType.ResyncRequest: HandleResync(peerId, r); return true;
            case MessageType.OwnershipRequest: HandleOwnership(peerId, r); return true;
            case MessageType.OwnershipRelease: HandleOwnershipRelease(peerId, r); return true;
            case MessageType.JunctionThrow: HandleJunctionThrow(peerId, r); return true;
            case MessageType.TurntableRotate: HandleTurntableRotate(peerId, r); return true;
            case MessageType.ControlGrantRequest: HandleGrantRequest(peerId, r); return true;
            case MessageType.ControlGrantRelease: HandleGrantRelease(peerId, r); return true;
            case MessageType.ControlInput: HandleControlInput(peerId, r); return true;
            case MessageType.ControlState: HandleControlState(peerId, r); return true;
            case MessageType.CargoState: HandleCargoState(peerId, r); return true;
            case MessageType.CoupleRequest: HandleCoupleRequest(peerId, r); return true;
            case MessageType.UncoupleRequest: HandleUncoupleRequest(peerId, r); return true;
            case MessageType.CommsActionRequest: HandleCommsActionRequest(peerId, r); return true;
            case MessageType.CarDeleteNotice: HandleCarDeleteNotice(peerId, r); return true;
            default: return false;
        }
    }

    /// <summary>World burst for a newly admitted player: every trainset, junction, turntable, and
    /// held grant, reliable-ordered, before any snapshot for them can arrive (03 §10).</summary>
    internal void OnPlayerAdmitted(int peerId)
    {
        foreach (TrainsetDef def in Registry.Sets.Values)
            _transport.Send(peerId, BuildCreate(0, def), DeliveryMethod.ReliableOrdered);
        // Baseline positions (reliable, AFTER the defs): without these a newcomer sees a parked or
        // restored consist nowhere until its owner's next stream — restored worlds have no owner.
        foreach (TrainsetSnapshot snap in _latest.Values)
            _transport.Send(peerId, BuildSnapshot(snap), DeliveryMethod.ReliableOrdered);
        foreach (KeyValuePair<uint, byte> j in _junctions)
            _transport.Send(peerId, BuildJunctionState(j.Key, j.Value), DeliveryMethod.ReliableOrdered);
        foreach (KeyValuePair<uint, float> t in _turntables)
            _transport.Send(peerId, BuildTurntableState(t.Key, t.Value), DeliveryMethod.ReliableOrdered);
        foreach (KeyValuePair<int, int> g in _grants)
            _transport.Send(peerId, BuildGrantState(g.Key, g.Value), DeliveryMethod.ReliableOrdered);
        foreach (KeyValuePair<int, Dictionary<byte, float>> car in _controls)
            foreach (KeyValuePair<byte, float> ctrl in car.Value)
                _transport.Send(peerId, BuildControlState(car.Key, ctrl.Key, ctrl.Value), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>A player left: park their consists (03 §3 — positions freeze until reclaimed) and
    /// free their control grants, broadcasting both to the remaining players.</summary>
    internal void OnPlayerRemoved(int peerId)
    {
        foreach (int trainsetId in Registry.Park(peerId))
        {
            // A borrowed server train returns to the server (it resumes its ambient drive) rather than
            // parking dead at owner 0 (M6-B.3); a player's own consist parks as before.
            if (_serverOwnedSets.Contains(trainsetId))
            {
                Registry.SetOwner(trainsetId, ServerOwnerId);
                Broadcast(BuildOwner(trainsetId, ServerOwnerId), DeliveryMethod.ReliableOrdered);
            }
            else
            {
                Broadcast(BuildOwner(trainsetId, 0), DeliveryMethod.ReliableOrdered);
            }
        }

        var released = new List<int>();
        foreach (KeyValuePair<int, int> g in _grants)
            if (g.Value == peerId) released.Add(g.Key);
        foreach (int carId in released)
        {
            _grants.Remove(carId);
            Broadcast(BuildGrantState(carId, 0), DeliveryMethod.ReliableOrdered);
        }
    }

    // ── server-owned trains (M6-B.2): the dedicated server as a sim owner ──

    /// <summary>Spawn a consist the SERVER owns and drives (no client needed). Registered under
    /// <see cref="ServerOwnerId"/> so it can't be claimed; delivered to every already-connected client
    /// immediately and to newcomers via the join burst (<see cref="OnPlayerAdmitted"/> sends all sets).
    /// Advance it by feeding positions to <see cref="PushServerSnapshot"/>.</summary>
    public TrainsetDef SpawnServerOwned(IReadOnlyList<CarDef> carSpecs)
    {
        if (carSpecs == null) throw new ArgumentNullException(nameof(carSpecs));
        if (carSpecs.Count < 1 || carSpecs.Count > TrainCodec.MaxCarsPerTrainset)
            throw new ArgumentOutOfRangeException(nameof(carSpecs), $"car count {carSpecs.Count} out of range");

        TrainsetDef def = Registry.Register(ServerOwnerId, carSpecs);
        _serverOwnedSets.Add(def.Id);
        Broadcast(BuildCreate(0, def), DeliveryMethod.ReliableOrdered); // live clients; newcomers get it on join
        return def;
    }

    /// <summary>True while the server is the current sim owner of one of its own trains — i.e. nobody
    /// has it on loan. The kinematic driver checks this to FREEZE (stop advancing/publishing) the
    /// instant a player claims the train, and to resume when it is handed back (M6-B.3).</summary>
    public bool IsServerDriven(int trainsetId) =>
        _serverOwnedSets.Contains(trainsetId)
        && Registry.Sets.TryGetValue(trainsetId, out TrainsetDef? def)
        && def.OwnerId == ServerOwnerId;

    /// <summary>Publish a position for a server-owned consist: stored as the join-burst baseline and
    /// relayed to everyone (sequenced-unreliable, like an owner's stream). The server is the authority,
    /// so no admission check — but stale/foreign snapshots are ignored defensively (a server train never
    /// changes membership, so its epoch is constant).</summary>
    public void PushServerSnapshot(TrainsetSnapshot snap)
    {
        if (!Registry.Sets.TryGetValue(snap.TrainsetId, out TrainsetDef? def)) return;
        if (def.OwnerId != ServerOwnerId || def.Epoch != snap.Epoch) return;
        _latest[snap.TrainsetId] = snap;
        byte[] payload = BuildSnapshot(snap);
        foreach (int id in _connectedIds())
            _transport.Send(id, payload, DeliveryMethod.SequencedUnreliable);
    }

    // ── handlers ──

    private void HandleRegister(int peerId, PacketReader r)
    {
        uint token = r.ReadVarUInt();
        int count = (int)r.ReadVarUInt();
        if (count < 1 || count > TrainCodec.MaxCarsPerTrainset)
        {
            ProposalRejected?.Invoke(peerId, $"register: car count {count} out of range");
            return;
        }
        // Full CarDef codec since v5 (identity + cargo survive registration); the spec's car id is
        // ignored — the registry assigns every id.
        var specs = new CarDef[count];
        for (int i = 0; i < count; i++) specs[i] = TrainCodec.ReadCarDef(r);

        TrainsetDef def = Registry.Register(peerId, specs);

        // The registrant's copy echoes their correlation token so the Shim can map its local cars
        // onto the server-assigned ids; everyone else receives a plain create.
        _transport.Send(peerId, BuildCreate(token, def), DeliveryMethod.ReliableOrdered);
        byte[] plain = BuildCreate(0, def);
        foreach (int id in _connectedIds())
            if (id != peerId) _transport.Send(id, plain, DeliveryMethod.ReliableOrdered);
    }

    private void HandleSnapshot(int peerId, PacketReader r, byte[] payload)
    {
        TrainsetSnapshot snap = TrainCodec.ReadSnapshot(r);
        if (!Registry.IsCurrentFromOwner(peerId, snap.TrainsetId, snap.Epoch))
        {
            StaleSnapshotsDropped++;
            return;
        }
        _latest[snap.TrainsetId] = snap; // join-burst baseline + persisted world position
        // Valid and current: relay the original bytes untouched (the sender is implicit — the
        // trainset's owner is authoritative, so recipients don't need a sender id).
        foreach (int id in _connectedIds())
            if (id != peerId) _transport.Send(id, payload, DeliveryMethod.SequencedUnreliable);
    }

    private void HandleCouple(int peerId, PacketReader r)
    {
        int carA = (int)r.ReadVarUInt();
        var endA = (CoupleEnd)r.ReadByte();
        int carB = (int)r.ReadVarUInt();
        var endB = (CoupleEnd)r.ReadByte();
        float relV = r.ReadSingle();

        if (Registry.TryCouple(peerId, carA, endA, carB, endB, relV, out TrainsetTransaction? txn, out string? reason))
            BroadcastTransaction(txn!);
        else
            ProposalRejected?.Invoke(peerId, $"couple: {reason}");
    }

    private void HandleUncouple(int peerId, PacketReader r)
    {
        int trainsetId = (int)r.ReadVarUInt();
        int gapIndex = (int)r.ReadVarUInt();

        if (Registry.TryUncouple(peerId, trainsetId, gapIndex, out TrainsetTransaction? txn, out string? reason))
            BroadcastTransaction(txn!);
        else
            ProposalRejected?.Invoke(peerId, $"uncouple: {reason}");
    }

    private void HandleDerail(int peerId, PacketReader r)
    {
        int trainsetId = (int)r.ReadVarUInt();
        int count = (int)r.ReadVarUInt();
        if (count < 1 || count > TrainCodec.MaxCarsPerTrainset)
        {
            ProposalRejected?.Invoke(peerId, $"derail: car count {count} out of range");
            return;
        }
        var carIds = new int[count];
        for (int i = 0; i < count; i++) carIds[i] = (int)r.ReadVarUInt();

        if (Registry.TryDerail(peerId, trainsetId, carIds, out TrainsetTransaction? txn, out string? reason))
            BroadcastTransaction(txn!);
        else
            ProposalRejected?.Invoke(peerId, $"derail: {reason}");
    }

    private void HandleRerail(int peerId, PacketReader r)
    {
        int trainsetId = (int)r.ReadVarUInt();

        if (Registry.TryRerail(peerId, trainsetId, out TrainsetTransaction? txn, out string? reason))
            BroadcastTransaction(txn!);
        else
            ProposalRejected?.Invoke(peerId, $"rerail: {reason}");
    }

    private void HandleResync(int peerId, PacketReader r)
    {
        int trainsetId = (int)r.ReadVarUInt();
        if (Registry.Sets.TryGetValue(trainsetId, out TrainsetDef? def))
            _transport.Send(peerId, BuildCreate(0, def), DeliveryMethod.ReliableOrdered);
    }

    private void HandleOwnership(int peerId, PacketReader r)
    {
        int trainsetId = (int)r.ReadVarUInt();
        if (Registry.TryClaim(peerId, trainsetId, out _, out string? reason))
            Broadcast(BuildOwner(trainsetId, peerId), DeliveryMethod.ReliableOrdered);
        else
            ProposalRejected?.Invoke(peerId, $"claim: {reason}");
    }

    /// <summary>A player hands back a set they simulate (M6-B.3). Only the current owner may release;
    /// a borrowed server train returns to the server (owner → <see cref="ServerOwnerId"/>, its
    /// kinematic driver resumes), and a self-registered consist parks (owner → 0).</summary>
    private void HandleOwnershipRelease(int peerId, PacketReader r)
    {
        int trainsetId = (int)r.ReadVarUInt();
        if (!Registry.Sets.TryGetValue(trainsetId, out TrainsetDef? set) || set.OwnerId != peerId)
            return; // not the owner (or unknown) — nothing to hand back; a stale release is a no-op
        int newOwner = _serverOwnedSets.Contains(trainsetId) ? ServerOwnerId : 0;
        Registry.SetOwner(trainsetId, newOwner);
        Broadcast(BuildOwner(trainsetId, newOwner), DeliveryMethod.ReliableOrdered);
    }

    private void HandleJunctionThrow(int peerId, PacketReader r)
    {
        uint junctionId = r.ReadVarUInt();
        byte branch = r.ReadByte();

        // Coalesce TRUE duplicates only (a hook double-fire lands on the same resulting state) —
        // a distinct real throw always changes the branch and always commits (M0 design note).
        if (_junctions.TryGetValue(junctionId, out byte current) && current == branch) return;

        _junctions[junctionId] = branch;
        // Broadcast to everyone INCLUDING the thrower: the committed state is authoritative, and
        // the thrower's Shim treats the echo as a no-op.
        Broadcast(BuildJunctionState(junctionId, branch), DeliveryMethod.ReliableOrdered);
    }

    private void HandleTurntableRotate(int peerId, PacketReader r)
    {
        uint turntableId = r.ReadVarUInt();
        float angle = r.ReadSingle();
        _turntables[turntableId] = angle;
        byte[] payload = BuildTurntableState(turntableId, angle);
        foreach (int id in _connectedIds())
            if (id != peerId) _transport.Send(id, payload, DeliveryMethod.SequencedUnreliable);
    }

    private void HandleGrantRequest(int peerId, PacketReader r)
    {
        int carId = (int)r.ReadVarUInt();
        if (_grants.TryGetValue(carId, out int holder) && holder != peerId)
        {
            // Held by someone else: tell only the requester who has it (their mirror may be stale).
            _transport.Send(peerId, BuildGrantState(carId, holder), DeliveryMethod.ReliableOrdered);
            return;
        }
        _grants[carId] = peerId;
        Broadcast(BuildGrantState(carId, peerId), DeliveryMethod.ReliableOrdered);
    }

    private void HandleGrantRelease(int peerId, PacketReader r)
    {
        int carId = (int)r.ReadVarUInt();
        if (!_grants.TryGetValue(carId, out int holder) || holder != peerId) return;
        _grants.Remove(carId);
        Broadcast(BuildGrantState(carId, 0), DeliveryMethod.ReliableOrdered);
    }

    private void HandleControlInput(int peerId, PacketReader r)
    {
        int carId = (int)r.ReadVarUInt();
        byte controlId = r.ReadByte();
        float value = r.ReadSingle();

        // Only the grant holder may drive this cab's controls (03 §3 GRANT)...
        if (!_grants.TryGetValue(carId, out int holder) || holder != peerId)
        {
            ProposalRejected?.Invoke(peerId, $"input: no control grant for car {carId}");
            return;
        }
        // ...and the input is applied by whoever simulates the consist (03 §3 OWN). Same player →
        // they already applied it locally; nothing to forward.
        if (!Registry.TryFindCar(carId, out TrainsetDef set) || set.OwnerId == peerId || set.OwnerId == 0) return;

        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.ControlInput)
            .WriteVarUInt((uint)carId)
            .WriteByte(controlId)
            .WriteSingle(value)
            .ToArray();
        _transport.Send(set.OwnerId, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Committed cab-control value from the car's SIM OWNER (the authority — grant
    /// holders' inputs arrive via ControlInput and only become state once the owner applies them
    /// and reports back through here). Stored for the join burst, relayed to everyone else.</summary>
    private void HandleControlState(int peerId, PacketReader r)
    {
        int carId = (int)r.ReadVarUInt();
        byte controlId = r.ReadByte();
        float value = r.ReadSingle();

        if (!Registry.TryFindCar(carId, out TrainsetDef set) || set.OwnerId != peerId)
        {
            ProposalRejected?.Invoke(peerId, $"control state: not the sim owner of car {carId}");
            return;
        }

        if (!_controls.TryGetValue(carId, out Dictionary<byte, float>? perCar))
            _controls[carId] = perCar = new Dictionary<byte, float>();
        perCar[controlId] = value;

        byte[] payload = BuildControlState(carId, controlId, value);
        foreach (int id in _connectedIds())
            if (id != peerId) _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Live cargo change from the car's sim owner: folded into the stored CarDef (late
    /// joiners and saves carry the current load — cargo is not membership, the epoch stays) and
    /// relayed to everyone else.</summary>
    private void HandleCargoState(int peerId, PacketReader r)
    {
        int carId = (int)r.ReadVarUInt();
        string cargoId = r.ReadString();
        float amount = r.ReadSingle();

        if (!Registry.TryFindCar(carId, out TrainsetDef owned) || owned.OwnerId != peerId)
        {
            ProposalRejected?.Invoke(peerId, $"cargo: not the sim owner of car {carId}");
            return;
        }
        if (!Registry.TryUpdateCargo(carId, cargoId, amount, out _, out string? reason))
        {
            ProposalRejected?.Invoke(peerId, $"cargo: {reason}");
            return;
        }

        byte[] payload = new PacketWriter(24)
            .WriteByte((byte)MessageType.CargoState)
            .WriteVarUInt((uint)carId)
            .WriteString(cargoId)
            .WriteSingle(amount)
            .ToArray();
        foreach (int id in _connectedIds())
            if (id != peerId) _transport.Send(id, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Route a physical couple request to carA's sim owner — the owner performs the real
    /// couple and its native event drives the normal proposal path (one authority chain, no second
    /// commit path). Any admitted player may ask: chaining cars is world interaction, not a grant.</summary>
    private void HandleCoupleRequest(int peerId, PacketReader r)
    {
        int carA = (int)r.ReadVarUInt();
        byte endA = r.ReadByte();
        int carB = (int)r.ReadVarUInt();
        byte endB = r.ReadByte();

        if (!Registry.TryFindCar(carA, out TrainsetDef setA)) { ProposalRejected?.Invoke(peerId, $"couple request: unknown car {carA}"); return; }
        if (!Registry.TryFindCar(carB, out _)) { ProposalRejected?.Invoke(peerId, $"couple request: unknown car {carB}"); return; }
        if (setA.OwnerId == 0) { ProposalRejected?.Invoke(peerId, $"couple request: trainset {setA.Id} is parked"); return; }
        if (setA.OwnerId == peerId) return; // requester simulates it — they act locally, nothing to route

        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.CoupleRequest)
            .WriteVarUInt((uint)carA)
            .WriteByte(endA)
            .WriteVarUInt((uint)carB)
            .WriteByte(endB)
            .ToArray();
        _transport.Send(setA.OwnerId, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Route a physical uncouple request to the car's sim owner (same shape as couple).</summary>
    private void HandleUncoupleRequest(int peerId, PacketReader r)
    {
        int carId = (int)r.ReadVarUInt();
        byte end = r.ReadByte();

        if (!Registry.TryFindCar(carId, out TrainsetDef set)) { ProposalRejected?.Invoke(peerId, $"uncouple request: unknown car {carId}"); return; }
        if (set.OwnerId == 0) { ProposalRejected?.Invoke(peerId, $"uncouple request: trainset {set.Id} is parked"); return; }
        if (set.OwnerId == peerId) return;

        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.UncoupleRequest)
            .WriteVarUInt((uint)carId)
            .WriteByte(end)
            .ToArray();
        _transport.Send(set.OwnerId, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Route a remote player's comms-radio action (rerail/delete) to the target car's sim
    /// owner — the owner performs the real action, its native event drives the normal path, and the
    /// owner charges the initiator via FeeExternal (M4, the CoupleRequest pattern). The command
    /// carries the initiator peer so the fee lands on the RIGHT wallet.</summary>
    private void HandleCommsActionRequest(int peerId, PacketReader r)
    {
        byte kind = r.ReadByte();
        int carId = (int)r.ReadVarUInt();
        Pose dest = PresenceCodec.ReadPose(r);

        if (!Registry.TryFindCar(carId, out TrainsetDef set)) { ProposalRejected?.Invoke(peerId, $"comms: unknown car {carId}"); return; }
        if (set.OwnerId == 0) { ProposalRejected?.Invoke(peerId, $"comms: trainset {set.Id} is parked — nobody can act on it"); return; }
        if (set.OwnerId == peerId) return; // the requester simulates it — they act locally, nothing to route

        var w = new PacketWriter(32)
            .WriteByte((byte)MessageType.CommsActionCommand)
            .WriteByte(kind)
            .WriteVarUInt((uint)carId);
        PresenceCodec.WritePose(w, dest);
        w.WriteVarUInt((uint)peerId); // initiator — whose wallet the owner charges
        _transport.Send(set.OwnerId, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>The world source deleted a car natively (comms-radio Clear) — remove it everywhere so
    /// no client keeps a ghost replica. Gated to the car's OWNER (in host-native mode that is the
    /// host): a delete is authoritative, unlike a distance stream-out (which keeps the def).</summary>
    private void HandleCarDeleteNotice(int peerId, PacketReader r)
    {
        int carId = (int)r.ReadVarUInt();
        if (!Registry.TryFindCar(carId, out TrainsetDef set) || set.OwnerId != peerId)
        {
            ProposalRejected?.Invoke(peerId, $"delete: only the owner of car {carId} may remove it");
            return;
        }
        if (Registry.TryDeleteCar(carId, out TrainsetTransaction? txn, out int removedSetId, out string? reason))
        {
            if (txn != null) BroadcastTransaction(txn);
            else Broadcast(BuildRemove(removedSetId), DeliveryMethod.ReliableOrdered);
        }
        else
        {
            ProposalRejected?.Invoke(peerId, $"delete: {reason}");
        }
    }

    // ── packet builders ──

    private static byte[] BuildCreate(uint token, TrainsetDef def)
    {
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.TrainsetCreate)
            .WriteVarUInt(token);
        TrainCodec.WriteDef(w, def);
        return w.ToArray();
    }

    private static byte[] BuildRemove(int trainsetId) =>
        new PacketWriter(8)
            .WriteByte((byte)MessageType.TrainsetRemove)
            .WriteVarUInt((uint)trainsetId)
            .ToArray();

    private void BroadcastTransaction(TrainsetTransaction txn)
    {
        // Every transaction makes the stored baselines stale by construction (retired ids are gone;
        // products carry a bumped epoch) — prune so the join burst never replays a dead position.
        foreach (int id in txn.RetiredIds) _latest.Remove(id);
        foreach (TrainsetDef def in txn.Products) _latest.Remove(def.Id);

        var w = new PacketWriter(128).WriteByte((byte)MessageType.TrainsetTransaction);
        TrainCodec.WriteTransaction(w, txn);
        Broadcast(w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    // ── persistence (v1) ──

    /// <summary>Snapshot the world half of the save: defs (owners as-is; restore parks them),
    /// baselines, junctions, turntables, id counters. Grants are session state and are not saved.</summary>
    internal TrainsSaveData Capture()
    {
        var save = new TrainsSaveData();
        (List<TrainsetDef> sets, int nextTrainsetId, int nextCarId) = Registry.CaptureState();
        save.NextTrainsetId = nextTrainsetId;
        save.NextCarId = nextCarId;
        save.Sets.AddRange(sets);
        save.LatestSnapshots.AddRange(_latest.Values);
        foreach (KeyValuePair<uint, byte> j in _junctions) save.Junctions[j.Key] = j.Value;
        foreach (KeyValuePair<uint, float> t in _turntables) save.Turntables[t.Key] = t.Value;
        return save;
    }

    /// <summary>Rebuild the world from a save (cold restart). Runs before any peer connects.</summary>
    internal void Restore(TrainsSaveData save)
    {
        Registry.RestoreState(save.Sets, save.NextTrainsetId, save.NextCarId);
        _latest.Clear();
        foreach (TrainsetSnapshot snap in save.LatestSnapshots) _latest[snap.TrainsetId] = snap;
        _junctions.Clear();
        foreach (KeyValuePair<uint, byte> j in save.Junctions) _junctions[j.Key] = j.Value;
        _turntables.Clear();
        foreach (KeyValuePair<uint, float> t in save.Turntables) _turntables[t.Key] = t.Value;
    }

    private static byte[] BuildSnapshot(TrainsetSnapshot snap)
    {
        var w = new PacketWriter(64).WriteByte((byte)MessageType.TrainsetSnapshot);
        TrainCodec.WriteSnapshot(w, snap);
        return w.ToArray();
    }

    private static byte[] BuildOwner(int trainsetId, int ownerId) =>
        new PacketWriter(8)
            .WriteByte((byte)MessageType.TrainsetOwner)
            .WriteVarUInt((uint)trainsetId)
            .WriteVarUInt((uint)ownerId)
            .ToArray();

    private static byte[] BuildJunctionState(uint junctionId, byte branch) =>
        new PacketWriter(8)
            .WriteByte((byte)MessageType.JunctionState)
            .WriteVarUInt(junctionId)
            .WriteByte(branch)
            .ToArray();

    private static byte[] BuildTurntableState(uint turntableId, float angle) =>
        new PacketWriter(12)
            .WriteByte((byte)MessageType.TurntableState)
            .WriteVarUInt(turntableId)
            .WriteSingle(angle)
            .ToArray();

    private static byte[] BuildGrantState(int carId, int playerId) =>
        new PacketWriter(8)
            .WriteByte((byte)MessageType.ControlGrantState)
            .WriteVarUInt((uint)carId)
            .WriteVarUInt((uint)playerId)
            .ToArray();

    private static byte[] BuildControlState(int carId, byte controlId, float value) =>
        new PacketWriter(16)
            .WriteByte((byte)MessageType.ControlState)
            .WriteVarUInt((uint)carId)
            .WriteByte(controlId)
            .WriteSingle(value)
            .ToArray();

    private void Broadcast(byte[] payload, DeliveryMethod delivery)
    {
        foreach (int id in _connectedIds())
            _transport.Send(id, payload, delivery);
    }
}
