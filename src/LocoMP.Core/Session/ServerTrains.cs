using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Persistence;
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

    // Last ADMITTED snapshot per trainset — always epoch-current by construction (admission checks
    // the epoch and every transaction prunes). Feeds the join-burst baseline and the world save.
    private readonly Dictionary<int, TrainsetSnapshot> _latest = new();

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
            case MessageType.JunctionThrow: HandleJunctionThrow(peerId, r); return true;
            case MessageType.TurntableRotate: HandleTurntableRotate(peerId, r); return true;
            case MessageType.ControlGrantRequest: HandleGrantRequest(peerId, r); return true;
            case MessageType.ControlGrantRelease: HandleGrantRelease(peerId, r); return true;
            case MessageType.ControlInput: HandleControlInput(peerId, r); return true;
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
    }

    /// <summary>A player left: park their consists (03 §3 — positions freeze until reclaimed) and
    /// free their control grants, broadcasting both to the remaining players.</summary>
    internal void OnPlayerRemoved(int peerId)
    {
        foreach (int trainsetId in Registry.Park(peerId))
            Broadcast(BuildOwner(trainsetId, 0), DeliveryMethod.ReliableOrdered);

        var released = new List<int>();
        foreach (KeyValuePair<int, int> g in _grants)
            if (g.Value == peerId) released.Add(g.Key);
        foreach (int carId in released)
        {
            _grants.Remove(carId);
            Broadcast(BuildGrantState(carId, 0), DeliveryMethod.ReliableOrdered);
        }
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
        var specs = new CarDef[count];
        for (int i = 0; i < count; i++)
        {
            string kind = r.ReadString();
            byte flags = r.ReadByte();
            specs[i] = new CarDef(0, kind, (flags & 0x01) != 0);
        }

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

    // ── packet builders ──

    private static byte[] BuildCreate(uint token, TrainsetDef def)
    {
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.TrainsetCreate)
            .WriteVarUInt(token);
        TrainCodec.WriteDef(w, def);
        return w.ToArray();
    }

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

    private void Broadcast(byte[] payload, DeliveryMethod delivery)
    {
        foreach (int id in _connectedIds())
            _transport.Send(id, payload, delivery);
    }
}
