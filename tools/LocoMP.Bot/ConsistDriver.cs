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
    // Ghost geometry: DV cars are 10–25 m; one uniform size is fine for a test rig. The bogie sits
    // inset from each car end, and consecutive cars queue nose-to-tail along the same path.
    private const double CarLength = 16.0;
    private const double BogieInset = 3.5;

    private static int _nextToken = 1000; // distinct per driver so parallel bots correlate cleanly

    private readonly TopologyWalker _walker;
    private readonly int _carCount;
    private readonly double _speed;
    private readonly Action<string> _log;
    private readonly string _name;
    private readonly uint _token;
    private readonly Queue<(uint junctionId, byte branch)> _pendingThrows = new();

    private readonly string[] _liveries;
    private readonly string _cargoId;
    private readonly float _cargoAmount;

    private NetClient? _bound;
    private int _trainsetId = -1;
    private bool _registerSent;
    private bool _streaming;

    public ConsistDriver(WorldTopology topology, int carCount, double speed, int seed, string name, Action<string> log,
                         uint? startEdgeId = null, string[]? liveries = null, string cargoId = "", float cargoAmount = 0f)
    {
        _walker = new TopologyWalker(topology, seed, tailCapacityM: carCount * CarLength + 100, startEdgeId);
        _carCount = Math.Max(1, carCount);
        _speed = speed;
        _name = name;
        _log = log;
        _liveries = liveries ?? Array.Empty<string>();
        _cargoId = cargoId;
        _cargoAmount = cargoAmount;
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
            // Our set vanished (retired by a transaction we're not party to, or a fresh session
            // after churn) — register a new one next tick.
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
            double offset = i * CarLength;
            BogieState? front = _walker.Behind(offset + BogieInset, (float)_speed);
            BogieState? rear = _walker.Behind(offset + CarLength - BogieInset, (float)_speed);
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
            _log($"[{_name}] consist: registered as trainset {def.Id} (epoch {def.Epoch})");
        };
    }
}
