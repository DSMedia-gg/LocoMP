using LocoMP.Core.Presence;
using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Core.World;

namespace LocoMP.Bot;

/// <summary>
/// The one-PC "ghost train": registers a synthetic consist with the server and drives it along the
/// EXTRACTED world topology (the M2.2 file), streaming spline-space snapshots exactly like a real
/// sim owner would — the host sees a driverless train roll through the valley, throwing every
/// switch it crosses. Survives churn/reconnects by re-registering whenever its trainset id vanishes.
/// </summary>
public sealed class ConsistDriver
{
    // Ghost geometry: DV cars are 10–25 m; a uniform pitch is fine for ghost BOXES, but real cars
    // (--livery) keep their real sizes — a wrong pitch leaves them floating apart (or overlapping),
    // and the forced couple across the gap wedges DV's chain FSM into a half-dead state (G3′,
    // 2026-08-04). --car-lengths streams each car at its real coupler pitch (the host logs a
    // measured paste-me hint); consecutive cars queue nose-to-tail plus DV's own 0.3 m separation
    // (CarSpawner.SEPARATION_BETWEEN_TRAIN_CARS). The bogie sits inset from each car end.
    private const double DefaultCarLength = 16.0;
    private const double CarSeparation = 0.3;
    private const double BogieInset = 3.5;

    private static int _nextToken = 1000; // distinct per driver so parallel bots correlate cleanly

    private readonly TopologyWalker _walker;
    private readonly int _carCount;
    private readonly double[] _offsets; // head-of-consist → nose of car i, along the walker's path
    private readonly double[] _lengths; // coupler pitch of car i
    private double _speed; // mutable: a granted player's throttle input scales it (M3.5c)
    private readonly double _baseSpeed;
    private readonly Action<string> _log;
    private readonly string _name;
    private readonly uint _token;
    private readonly Queue<(uint junctionId, byte branch)> _pendingThrows = new();

    private readonly string[] _liveries;
    private readonly string _cargoId;
    private readonly float _cargoAmount;
    private readonly int _derailCarIndex; // 0-based; -1 = none. Streams that car OffRail (spawn-path rig).
    private readonly Pose _derailPose;

    private NetClient? _bound;
    private int _trainsetId = -1;
    private int _leadCarId = -1; // server id of our registered car 1 — the adoption anchor
    private bool _registerSent;
    private bool _streaming;

    public ConsistDriver(WorldTopology topology, int carCount, double speed, int seed, string name, Action<string> log,
                         uint? startEdgeId = null, string[]? liveries = null, string cargoId = "", float cargoAmount = 0f,
                         int derailCarIndex = -1, Pose derailPose = default, double[]? carLengths = null)
    {
        _carCount = Math.Max(1, carCount);
        _offsets = new double[_carCount];
        _lengths = new double[_carCount];
        for (int i = 0; i < _carCount; i++)
        {
            _lengths[i] = carLengths is { Length: > 0 }
                ? carLengths[Math.Min(i, carLengths.Length - 1)]
                : DefaultCarLength;
            if (i > 0) _offsets[i] = _offsets[i - 1] + _lengths[i - 1] + CarSeparation;
        }
        double totalLength = _offsets[_carCount - 1] + _lengths[_carCount - 1];
        _walker = new TopologyWalker(topology, seed, tailCapacityM: totalLength + 100, startEdgeId);
        _speed = speed;
        _baseSpeed = speed;
        _name = name;
        _log = log;
        _liveries = liveries ?? Array.Empty<string>();
        _cargoId = cargoId;
        _cargoAmount = cargoAmount;
        _derailCarIndex = derailCarIndex;
        _derailPose = derailPose;
        _token = (uint)Interlocked.Increment(ref _nextToken);
        _walker.JunctionCrossed += (id, branch) => _pendingThrows.Enqueue((id, branch));
    }

    public long SnapshotsSent { get; private set; }
    public long JunctionsThrown { get; private set; }

    /// <summary>Advance the ghost by one tick. Wired to <see cref="BotClient.SessionTick"/>.</summary>
    public void Tick(NetClient client, double dt)
    {
        if (!client.Joined) return; // RegisterTrainset would silently no-op and never be retried
        if (!ReferenceEquals(_bound, client)) Bind(client);

        if (_trainsetId < 0)
        {
            if (_registerSent) return; // commit is in flight
            var specs = new CarDef[_carCount];
            for (int i = 0; i < _carCount; i++)
            {
                // With --livery the host spawns REAL cars for us (M3.5b); the synthetic ghost-*
                // kinds keep the box-fallback path alive. Identity is bot-synthetic but stable —
                // enough for the game to name the cars (plates, and later booklets).
                string kind = KindFor(i);
                string cargo = i > 0 ? _cargoId : ""; // the "loco" carries nothing
                specs[i] = new CarDef(0, kind,
                    gameId: $"BOT-{_token}-{i + 1}", gameGuid: $"locomp-bot-{_token}-{i + 1}",
                    cargoId: cargo, cargoAmount: cargo.Length > 0 ? _cargoAmount : 0f);
            }
            client.Trains.RegisterTrainset(_token, specs);
            _registerSent = true;
            _log($"[{_name}] consist: registration sent ({_carCount} car(s), token {_token}" +
                 (_liveries.Length > 0 ? $", liveries {string.Join(",", _liveries)})" : ")"));
            return;
        }

        if (!client.Trains.View.Sets.TryGetValue(_trainsetId, out TrainsetDef? def))
        {
            // Our set vanished. A membership transaction retires the parent id but the CARS live
            // on in product sets — adopt the product holding our lead car instead of registering
            // a duplicate consist (an honored uncouple request would otherwise double the train).
            if (TryAdoptProduct(client)) return;
            // Genuinely gone (fresh session after churn) — register a new one next tick.
            _log($"[{_name}] consist: trainset {_trainsetId} is gone — re-registering");
            _trainsetId = -1;
            _registerSent = false;
            return;
        }

        _walker.Advance(_speed * dt);
        while (_pendingThrows.Count > 0)
        {
            (uint junctionId, byte branch) = _pendingThrows.Dequeue();
            client.Trains.ThrowJunction(junctionId, branch);
            JunctionsThrown++;
        }

        var cars = new CarSnapshot[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
        {
            if (i == _derailCarIndex)
            {
                // Spawn-path rig: this car streams as a 6-DOF off-rail pose at the --at anchor,
                // so a joining client exercises the derailed (null-track) SpawnLoadedCar leg.
                cars[i] = CarSnapshot.OffRail(_derailPose);
                continue;
            }
            // A split product keeps streaming with the head cars' geometry — same approximation
            // the uniform pitch made, and products stop streaming once the bot re-registers.
            int gi = Math.Min(i, _carCount - 1);
            double offset = _offsets[gi];
            double len = _lengths[gi];
            double inset = Math.Min(BogieInset, len / 4); // short cars: keep both bogies inside
            BogieState? front = _walker.Behind(offset + inset, (float)_speed);
            BogieState? rear = _walker.Behind(offset + len - inset, (float)_speed);
            if (front is null || rear is null) return; // trail history still building — wait
            cars[i] = CarSnapshot.Railed(front.Value, rear.Value);
        }

        if (!_streaming)
        {
            _streaming = true;
            BogieState head = _walker.HeadState((float)_speed);
            _log($"[{_name}] consist {_trainsetId}: streaming from edge {head.EdgeId} at {_speed:F0} m/s");
        }

        client.Trains.SendSnapshot(new TrainsetSnapshot(_trainsetId, def.Epoch, client.EstimatedServerTimeMs, cars));
        SnapshotsSent++;
    }

    private string KindFor(int carIndex)
    {
        if (_liveries.Length == 0) return carIndex == 0 ? "ghost-loco" : "ghost-car";
        if (carIndex == 0 || _liveries.Length == 1) return _liveries[0];
        return _liveries[1 + (carIndex - 1) % (_liveries.Length - 1)];
    }

    private void Bind(NetClient client)
    {
        _bound = client;
        _trainsetId = -1;
        _registerSent = false;
        _streaming = false;
        client.Trains.TrainsetRegistered += (token, def) =>
        {
            if (token != _token) return;
            _trainsetId = def.Id;
            _leadCarId = def.Cars.Count > 0 ? def.Cars[0].Id : -1;
            _log($"[{_name}] consist: registered as trainset {def.Id} (epoch {def.Epoch})");
        };
        // M3.5c debt closed: the bot EXECUTES remote chain acts on its consists instead of
        // ignoring them, so the one-PC rig live-fires the full request → owner → transaction →
        // client-mirror round trip. The bot has no native world — honoring a request IS proposing
        // the membership change; the server's commit then applies for everyone, exactly like a
        // Shim owner's native coupler event would.
        client.Trains.UncoupleRequested += (carId, end) => OnUncoupleRequested(client, carId, end);
        client.Trains.CoupleRequested += (carA, endA, carB, endB) => OnCoupleRequested(client, carA, endA, carB, endB);
        // M3.5c: a grant holder's throttle drives OUR speed — in --listen mode a player can sit
        // in the bot-hosted train's cab and actually drive it (throttle id 1 mirrors the Shim's
        // ControlType mapping; full speed at ~2.5× the configured cruise).
        client.Trains.ControlInputReceived += (carId, controlId, value) =>
        {
            if (controlId != 1 || _trainsetId < 0) return;
            if (!client.Trains.View.Sets.TryGetValue(_trainsetId, out TrainsetDef? def)) return;
            bool ours = false;
            foreach (CarDef car in def.Cars)
                if (car.Id == carId) { ours = true; break; }
            if (!ours) return;
            _speed = value * _baseSpeed * 2.5;
            _log($"[{_name}] consist: throttle input {value:F2} → {_speed:F1} m/s");
        };
    }

    /// <summary>After a transaction retired our set id, find the product that inherited our lead
    /// car and keep driving THAT. The walker's geometry stays valid because products preserve car
    /// order and the lead car defines offset 0.</summary>
    private bool TryAdoptProduct(NetClient client)
    {
        if (_leadCarId <= 0) return false;
        foreach (TrainsetDef candidate in client.Trains.View.Sets.Values)
        {
            foreach (CarDef car in candidate.Cars)
            {
                if (car.Id != _leadCarId) continue;
                _log($"[{_name}] consist: trainset {_trainsetId} retired by a transaction — " +
                     $"adopting product {candidate.Id} ({candidate.Cars.Count} car(s))");
                _trainsetId = candidate.Id;
                return true;
            }
        }
        return false;
    }

    private void OnUncoupleRequested(NetClient client, int carId, CoupleEnd end)
    {
        foreach (TrainsetDef def in client.Trains.View.Sets.Values)
        {
            int index = IndexOf(def, carId);
            if (index < 0) continue;
            // The request's end is CAR-relative (the physical coupler the player unhooked). Bot
            // cars face travel, so Front looks toward index 0 — but orientation is a spawn-side
            // detail we can't observe; if the primary side has no gap, the other side is the one.
            int gap = end == CoupleEnd.Front ? index - 1 : index;
            if (gap < 0 || gap >= def.Cars.Count - 1) gap = end == CoupleEnd.Front ? index : index - 1;
            if (gap < 0 || gap >= def.Cars.Count - 1)
            {
                _log($"[{_name}] consist: remote uncouple request on car {carId} ({end}) has no gap to split — ignored");
                return;
            }
            _log($"[{_name}] consist: remote uncouple request honored — splitting set {def.Id} at gap {gap}");
            client.Trains.ProposeUncouple(def.Id, gap);
            return;
        }
        _log($"[{_name}] consist: remote uncouple request for unknown car {carId} — ignored");
    }

    private void OnCoupleRequested(NetClient client, int carA, CoupleEnd endA, int carB, CoupleEnd endB)
    {
        TrainsetDef? setA = FindSetOf(client, carA);
        TrainsetDef? setB = FindSetOf(client, carB);
        if (setA is null || setB is null || setA.Id == setB.Id)
        {
            _log($"[{_name}] consist: remote couple request ({carA}+{carB}) — not two distinct known sets; ignored");
            return;
        }
        if (!TryTrainsetEnd(setA, carA, endA, out CoupleEnd setEndA) ||
            !TryTrainsetEnd(setB, carB, endB, out CoupleEnd setEndB))
        {
            _log($"[{_name}] consist: remote couple request ({carA}+{carB}) — a mid-train car can't take a chain; ignored");
            return;
        }
        _log($"[{_name}] consist: remote couple request honored — proposing merge {setA.Id}/{setEndA} + {setB.Id}/{setEndB}");
        client.Trains.ProposeCouple(carA, setEndA, carB, setEndB, relV: 0f);
    }

    /// <summary>Car-relative end → TRAINSET end (the proposal's dialect, M2.1 boundary rule). An
    /// end car's only free chain is its outward coupler, so position decides; single cars keep the
    /// car end as given.</summary>
    private static bool TryTrainsetEnd(TrainsetDef def, int carId, CoupleEnd carEnd, out CoupleEnd setEnd)
    {
        int index = IndexOf(def, carId);
        setEnd = carEnd;
        if (index < 0) return false;
        if (def.Cars.Count == 1) return true;
        if (index == 0) { setEnd = CoupleEnd.Front; return true; }
        if (index == def.Cars.Count - 1) { setEnd = CoupleEnd.Rear; return true; }
        return false;
    }

    private static int IndexOf(TrainsetDef def, int carId)
    {
        for (int i = 0; i < def.Cars.Count; i++)
        {
            if (def.Cars[i].Id == carId) return i;
        }
        return -1;
    }

    private static TrainsetDef? FindSetOf(NetClient client, int carId)
    {
        foreach (TrainsetDef def in client.Trains.View.Sets.Values)
        {
            if (IndexOf(def, carId) >= 0) return def;
        }
        return null;
    }
}
