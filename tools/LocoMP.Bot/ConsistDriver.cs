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
    // 2026-08-04). --car-geometry streams each car at its real coupler pitch AND seats its bogies
    // at their real insets (the host logs a measured paste-me hint) — DV places the spawned body
    // from the bogie track points, so a guessed inset shifts the body within its span (G3′ round 2:
    // rear joint wide). --car-lengths (pitch only) keeps the old min(3.5, len/4) inset guess.
    //
    // Inter-car gap = the COMPRESSED-under-tension coupled rest, NOT DV's spawn separation. DV lays
    // fresh cars SEPARATION_BETWEEN_TRAIN_CARS (0.3 m; CarSpawner) apart and THEN couples them, so a
    // real host streams already-compressed bogies — but the bot SYNTHESISES positions, so the gap it
    // streams IS the coupled rest, and if it is too wide DV's chain-hook proximity check (check
    // position 0.25 m inward per coupler, ~1.4 m range) loses its curvature margin and never hooks:
    // the pair renders gapped + unlinked until claimed (Cody's G3 tuning find, 2026-08-05).
    //
    // Re-derived from Coupler.decompiled.cs (burst-4 follow-up, 2026-08-05 live): a default couple is
    // Attached_Tight (CoupleTo, !viaChainInteraction), and SetChainTight(true) does two things —
    //   (a) StartJointAdaptation(0.2) drives the rigid joint's linearLimit (MAX distance between the
    //       two anchors) to 0.2 m, and each anchor sits couplerPos − forward·(0.2/2) = 0.1 m BEHIND
    //       its own coupler on the body side, so at the limit the two COUPLER transforms meet at
    //       0.2 − 0.1 − 0.1 = 0.0 m (buffer-to-buffer), not 0.2 m; and
    //   (b) EnableBufferSpring compresses further (targetPosition 0.3 m, 4 MN spring) and DISABLES the
    //       fake buffer colliders, so the rest sits slightly NEGATIVE — the coupler transforms overlap
    //       a touch as the physical buffers press. The old 0.2 m was the ANCHOR-separation limit read
    //       as if it were the coupler gap — a tenth-plus too wide, which is exactly why the earlier
    //       fix helped but did not reach flush.
    // Cody's live burst-4 measurement pins the equilibrium: at the 0.2 m gap the streamed replicas sat
    // ~0.34 m too WIDE across DM3/S060/DH4 (DE2 ≈0.30, the short shunter), so the flush coupled gap is
    // 0.2 − 0.34 ≈ −0.14 m — consistent with the decompile's "compressed past tip-contact". That is the
    // new default; a specific curve/livery can still be dialled with --coupled-gap (live, no rebuild)
    // or overridden per car via --car-geometry pitch. Negative is physical here: the buffer colliders
    // are off when tight, so a slight coupler-transform overlap is the buffers touching, not a clip.
    private const double DefaultCarLength = 16.0;
    public const double DefaultCoupledCouplerGap = -0.14;
    private const double BogieInset = 3.5;

    // Distinct per driver within a process AND across processes/restarts: the token names the
    // consist's car ids/guids ("locomp-bot-<token>-<n>"), and a fixed base poisons the well — an
    // orphaned registration that ever real-spawned leaves cars with those guids in the HOST'S
    // WORLD SAVE, and every later bot with the same token then fails spawn ("same key already
    // added") into ghost boxes, permanently (found live 2026-08-05, G3 rig). Millisecond launch
    // time keeps tokens unique across restarts; per-driver increments keep parallel bots distinct.
    private static int _nextToken =
        1000 + (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond % 1_000_000);

    private readonly TopologyWalker _walker;
    private readonly int _carCount;
    private readonly double[] _offsets; // head-of-consist → nose of car i, along the walker's path
    private readonly double[] _lengths; // coupler pitch of car i
    private readonly double[] _frontInsets; // nose → front bogie pivot of car i
    private readonly double[] _rearInsets;  // rear coupler → rear bogie pivot of car i
    private readonly double _coupledGap;    // rear coupler of car i → nose of car i+1 (--coupled-gap)
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
                         int derailCarIndex = -1, Pose derailPose = default, CarGeometry[]? carGeometry = null,
                         double? coupledGap = null)
    {
        _carCount = Math.Max(1, carCount);
        _coupledGap = coupledGap ?? DefaultCoupledCouplerGap;
        _offsets = new double[_carCount];
        _lengths = new double[_carCount];
        _frontInsets = new double[_carCount];
        _rearInsets = new double[_carCount];
        for (int i = 0; i < _carCount; i++)
        {
            // Fewer entries than cars: last repeats (the --car-lengths convention). A default
            // struct (no geometry at all) has Length 0 → the uniform ghost-box pitch.
            CarGeometry g = carGeometry is { Length: > 0 }
                ? carGeometry[Math.Min(i, carGeometry.Length - 1)]
                : default;
            double len = g.Length > 0 ? g.Length : DefaultCarLength;
            _lengths[i] = len;
            _frontInsets[i] = double.IsNaN(g.FrontInset) ? Math.Min(BogieInset, len / 4) : g.FrontInset;
            _rearInsets[i] = double.IsNaN(g.RearInset) ? Math.Min(BogieInset, len / 4) : g.RearInset;
            if (i > 0) _offsets[i] = _offsets[i - 1] + _lengths[i - 1] + _coupledGap;
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
            BogieState? front = _walker.Behind(offset + _frontInsets[gi], (float)_speed);
            BogieState? rear = _walker.Behind(offset + len - _rearInsets[gi], (float)_speed);
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
