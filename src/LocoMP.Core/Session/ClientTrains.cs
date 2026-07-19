using System;
using System.Collections.Generic;
using LocoMP.Core.Net;
using LocoMP.Core.Protocol;
using LocoMP.Core.Trains;

namespace LocoMP.Core.Session;

/// <summary>
/// The client's train subsystem, owned by <see cref="NetClient"/>: a <see cref="TrainsetView"/>
/// mirror plus junction/turntable/grant mirrors on the receive side, and the propose/stream calls
/// the Shim uses on the send side. Everything here is proposals and mirrors — commits only ever
/// come back from the server (03 §3). Game-free: the Shim consumes the events to move game objects
/// and calls the senders from its Harmony hooks.
/// </summary>
public sealed class ClientTrains
{
    private readonly ITransport _transport;
    private readonly Func<bool> _joined;
    private readonly Dictionary<uint, byte> _junctions = new();
    private readonly Dictionary<uint, float> _turntables = new();
    private readonly Dictionary<int, int> _grants = new(); // carId → holding playerId

    internal ClientTrains(ITransport transport, Func<bool> joined)
    {
        _transport = transport;
        _joined = joined;
    }

    /// <summary>The mirrored trainset world (definitions + latest snapshots + discard counters).</summary>
    public TrainsetView View { get; } = new();

    public IReadOnlyDictionary<uint, byte> Junctions => _junctions;
    public IReadOnlyDictionary<uint, float> Turntables => _turntables;
    public IReadOnlyDictionary<int, int> Grants => _grants;

    /// <summary>Our own registration was committed: (token we sent, the assigned definition). The
    /// Shim uses the def's car order to map its local cars onto the server-assigned ids.</summary>
    public event Action<uint, TrainsetDef>? TrainsetRegistered;

    public event Action<uint, byte>? JunctionChanged;    // (junctionId, branch)
    public event Action<uint, float>? TurntableMoved;    // (turntableId, angle)
    public event Action<int, int>? GrantChanged;         // (carId, holderId; 0 = free)

    /// <summary>We simulate this car's consist and a grant holder moved a control (03 §3).</summary>
    public event Action<int, byte, float>? ControlInputReceived; // (carId, controlId, value)

    /// <summary>A sim owner's cab control committed to a value — mirror it onto the replica so
    /// remote levers read true (M3.5c). Also replayed from the join burst.</summary>
    public event Action<int, byte, float>? ControlStateReceived; // (carId, controlId, value)

    /// <summary>A car's load changed (empty cargoId = unloaded). Owner-authoritative (M3.5c).</summary>
    public event Action<int, string, float>? CargoChanged; // (carId, cargoId, amount)

    /// <summary>We simulate carA's consist and a remote player physically chained it to carB —
    /// perform the real couple; the native event then proposes the merge (M3.5c).</summary>
    public event Action<int, CoupleEnd, int, CoupleEnd>? CoupleRequested; // (carA, endA, carB, endB)

    /// <summary>We simulate this car's consist and a remote player physically uncoupled the named
    /// coupler — perform the real uncouple; the native event then proposes the split (M3.5c).</summary>
    public event Action<int, CoupleEnd>? UncoupleRequested; // (carId, end — the car's own coupler)

    // ── send side (all silently no-op until joined, matching NetClient.SendPose) ──

    /// <summary>Offer an existing consist to the server (world source). The token correlates the
    /// eventual <see cref="TrainsetRegistered"/> commit. Specs ride the full CarDef codec — v5
    /// fixed the v4 bug where registration stripped identity/cargo (ids are still server-assigned;
    /// whatever ids the specs carry are ignored).</summary>
    public void RegisterTrainset(uint token, IReadOnlyList<CarDef> carSpecs)
    {
        if (!_joined()) return;
        var w = new PacketWriter(64)
            .WriteByte((byte)MessageType.TrainsetRegister)
            .WriteVarUInt(token)
            .WriteVarUInt((uint)carSpecs.Count);
        foreach (CarDef car in carSpecs) TrainCodec.WriteCarDef(w, car);
        _transport.Send(NetProtocol.ServerPeer, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Stream our owned consist's kinematic frame (sequenced-unreliable — latest wins).</summary>
    public void SendSnapshot(TrainsetSnapshot snap)
    {
        if (!_joined()) return;
        var w = new PacketWriter(64).WriteByte((byte)MessageType.TrainsetSnapshot);
        TrainCodec.WriteSnapshot(w, snap);
        _transport.Send(NetProtocol.ServerPeer, w.ToArray(), DeliveryMethod.SequencedUnreliable);
    }

    /// <summary>Report a coupling contact on a consist we simulate (03 §4 step 1).</summary>
    public void ProposeCouple(int carA, CoupleEnd endA, int carB, CoupleEnd endB, float relV)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.CoupleProposal)
            .WriteVarUInt((uint)carA)
            .WriteByte((byte)endA)
            .WriteVarUInt((uint)carB)
            .WriteByte((byte)endB)
            .WriteSingle(relV)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Propose splitting a consist we simulate between car gapIndex and gapIndex+1.</summary>
    public void ProposeUncouple(int trainsetId, int gapIndex)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.UncoupleProposal)
            .WriteVarUInt((uint)trainsetId)
            .WriteVarUInt((uint)gapIndex)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Report cars leaving the rails on a consist we simulate.</summary>
    public void ReportDerail(int trainsetId, IReadOnlyList<int> carIds)
    {
        if (!_joined()) return;
        var w = new PacketWriter(16)
            .WriteByte((byte)MessageType.DerailReport)
            .WriteVarUInt((uint)trainsetId)
            .WriteVarUInt((uint)carIds.Count);
        foreach (int id in carIds) w.WriteVarUInt((uint)id);
        _transport.Send(NetProtocol.ServerPeer, w.ToArray(), DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Ask the server to rerail a consist (comms-radio path — any player may).</summary>
    public void RequestRerail(int trainsetId) => SendIdOnly(MessageType.RerailRequest, trainsetId);

    /// <summary>Manual escape hatch: ask for the trainset's current definition again (03 §4).</summary>
    public void RequestResync(int trainsetId) => SendIdOnly(MessageType.ResyncRequest, trainsetId);

    /// <summary>Ask to simulate a parked trainset.</summary>
    public void RequestOwnership(int trainsetId) => SendIdOnly(MessageType.OwnershipRequest, trainsetId);

    /// <summary>Propose a junction throw. The commit comes back as JunctionState to everyone.</summary>
    public void ThrowJunction(uint junctionId, byte branch)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.JunctionThrow)
            .WriteVarUInt(junctionId)
            .WriteByte(branch)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Stream a turntable rotation (last-writer-wins for M2).</summary>
    public void RotateTurntable(uint turntableId, float angle)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(12)
            .WriteByte((byte)MessageType.TurntableRotate)
            .WriteVarUInt(turntableId)
            .WriteSingle(angle)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.SequencedUnreliable);
    }

    /// <summary>Request the control grant for a cab/car (on cab entry, 03 §3).</summary>
    public void RequestControlGrant(int carId) => SendIdOnly(MessageType.ControlGrantRequest, carId);

    /// <summary>Release a held control grant (on cab exit).</summary>
    public void ReleaseControlGrant(int carId) => SendIdOnly(MessageType.ControlGrantRelease, carId);

    /// <summary>Send one control movement; the server routes it to the consist's sim owner.</summary>
    public void SendControlInput(int carId, byte controlId, float value)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.ControlInput)
            .WriteVarUInt((uint)carId)
            .WriteByte(controlId)
            .WriteSingle(value)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Owner only: announce a cab control's committed value so replicas mirror it.</summary>
    public void SendControlState(int carId, byte controlId, float value)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.ControlState)
            .WriteVarUInt((uint)carId)
            .WriteByte(controlId)
            .WriteSingle(value)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Owner only: announce a car's load change (empty cargoId = unloaded).</summary>
    public void SendCargoState(int carId, string cargoId, float amount)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(24)
            .WriteByte((byte)MessageType.CargoState)
            .WriteVarUInt((uint)carId)
            .WriteString(cargoId)
            .WriteSingle(amount)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Ask the sim owner to couple two cars a player physically chained (M3.5c). Ends are
    /// CAR-relative couplers here (Front = the car's front coupler), not trainset ends — the owner
    /// resolves the physical couplers; Core stays orientation-blind (M2.1 boundary rule).</summary>
    public void RequestCouple(int carA, CoupleEnd endA, int carB, CoupleEnd endB)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(16)
            .WriteByte((byte)MessageType.CoupleRequest)
            .WriteVarUInt((uint)carA)
            .WriteByte((byte)endA)
            .WriteVarUInt((uint)carB)
            .WriteByte((byte)endB)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Ask the sim owner to uncouple a car's coupler a player physically unhooked.</summary>
    public void RequestUncouple(int carId, CoupleEnd end)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)MessageType.UncoupleRequest)
            .WriteVarUInt((uint)carId)
            .WriteByte((byte)end)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }

    // ── receive side ──

    internal bool TryHandle(MessageType type, PacketReader r)
    {
        switch (type)
        {
            case MessageType.TrainsetCreate:
            {
                uint token = r.ReadVarUInt();
                TrainsetDef def = TrainCodec.ReadDef(r);
                View.ApplyCreate(def);
                if (token != 0) TrainsetRegistered?.Invoke(token, def);
                return true;
            }
            case MessageType.TrainsetRemove:
                View.ApplyRemove((int)r.ReadVarUInt());
                return true;
            case MessageType.TrainsetTransaction:
                View.ApplyTransaction(TrainCodec.ReadTransaction(r));
                return true;
            case MessageType.TrainsetOwner:
                View.ApplyOwner((int)r.ReadVarUInt(), (int)r.ReadVarUInt());
                return true;
            case MessageType.TrainsetSnapshot:
                View.TryApplySnapshot(TrainCodec.ReadSnapshot(r));
                return true;
            case MessageType.JunctionState:
            {
                uint junctionId = r.ReadVarUInt();
                byte branch = r.ReadByte();
                _junctions[junctionId] = branch;
                JunctionChanged?.Invoke(junctionId, branch);
                return true;
            }
            case MessageType.TurntableState:
            {
                uint turntableId = r.ReadVarUInt();
                float angle = r.ReadSingle();
                _turntables[turntableId] = angle;
                TurntableMoved?.Invoke(turntableId, angle);
                return true;
            }
            case MessageType.ControlGrantState:
            {
                int carId = (int)r.ReadVarUInt();
                int holder = (int)r.ReadVarUInt();
                if (holder == 0) _grants.Remove(carId);
                else _grants[carId] = holder;
                GrantChanged?.Invoke(carId, holder);
                return true;
            }
            case MessageType.ControlInput:
            {
                int carId = (int)r.ReadVarUInt();
                byte controlId = r.ReadByte();
                float value = r.ReadSingle();
                ControlInputReceived?.Invoke(carId, controlId, value);
                return true;
            }
            case MessageType.ControlState:
            {
                int carId = (int)r.ReadVarUInt();
                byte controlId = r.ReadByte();
                float value = r.ReadSingle();
                ControlStateReceived?.Invoke(carId, controlId, value);
                return true;
            }
            case MessageType.CargoState:
            {
                int carId = (int)r.ReadVarUInt();
                string cargoId = r.ReadString();
                float amount = r.ReadSingle();
                CargoChanged?.Invoke(carId, cargoId, amount);
                return true;
            }
            case MessageType.CoupleRequest:
            {
                int carA = (int)r.ReadVarUInt();
                var endA = (CoupleEnd)r.ReadByte();
                int carB = (int)r.ReadVarUInt();
                var endB = (CoupleEnd)r.ReadByte();
                CoupleRequested?.Invoke(carA, endA, carB, endB);
                return true;
            }
            case MessageType.UncoupleRequest:
            {
                int carId = (int)r.ReadVarUInt();
                var end = (CoupleEnd)r.ReadByte();
                UncoupleRequested?.Invoke(carId, end);
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Wipe the mirrors on disconnect (the next join's world burst rebuilds them).</summary>
    internal void Reset()
    {
        _junctions.Clear();
        _turntables.Clear();
        _grants.Clear();
        // TrainsetView state is rebuilt by the join burst too; recreate-on-join keeps its counters
        // meaningful per session — but View is a public property, so clear via its own applies:
        foreach (int id in new List<int>(View.Sets.Keys)) View.ApplyRemove(id);
    }

    private void SendIdOnly(MessageType type, int id)
    {
        if (!_joined()) return;
        byte[] payload = new PacketWriter(8)
            .WriteByte((byte)type)
            .WriteVarUInt((uint)id)
            .ToArray();
        _transport.Send(NetProtocol.ServerPeer, payload, DeliveryMethod.ReliableOrdered);
    }
}
