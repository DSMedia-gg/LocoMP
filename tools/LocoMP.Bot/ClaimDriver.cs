using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Core.World;

namespace LocoMP.Bot;

/// <summary>
/// The one-PC rig for M6-B.3 (drivable server trains): join a dedicated server that is running its own
/// kinematic trains (<c>LocoMP.Server --spawn-trains N</c>), CLAIM one, drive it along the extracted
/// topology for a spell, then hand it back — so from your own game you watch an ambient server train get
/// borrowed (its trajectory changes as the bot takes over), driven, and released (the server resumes its
/// route). A compact cousin of <see cref="ConsistDriver"/>: it drives an EXISTING server-owned set rather
/// than registering one, so none of the consist-registration / coupling machinery lives here.
/// </summary>
public sealed class ClaimDriver
{
    // Ghost geometry (matches ConsistDriver): DV cars are 10–25 m; one uniform size is fine for a rig.
    private const double CarLength = 16.0;
    private const double BogieInset = 3.5;
    // Trail buffer ceiling: ambient server trains are short coasters, so 20 cars is a generous cap
    // (the walker only needs enough history to place the whole consist behind its head).
    private const int MaxCars = 20;

    private readonly TopologyWalker _walker;
    private readonly double _speed;
    private readonly double _driveSeconds; // hold + drive this long, then release (0 = until the bot exits)
    private readonly Action<string> _log;
    private readonly string _name;
    private readonly Queue<(uint junctionId, byte branch)> _pendingThrows = new();

    private NetClient? _bound;
    private int _setId = -1;   // the server train we're borrowing (-1 = none found on the wire yet)
    private bool _requested;   // ownership request sent (waiting for the flip to us)
    private bool _owned;       // we hold it and are driving
    private bool _released;    // handed back — nothing left to do
    private double _held;
    private bool _streaming;

    public ClaimDriver(WorldTopology topology, double speed, double driveSeconds, int seed, string name,
                       Action<string> log, uint? startEdgeId = null)
    {
        _walker = new TopologyWalker(topology, seed, tailCapacityM: MaxCars * CarLength + 100, startEdgeId);
        _speed = speed;
        _driveSeconds = driveSeconds;
        _name = name;
        _log = log;
        _walker.JunctionCrossed += (id, branch) => _pendingThrows.Enqueue((id, branch));
    }

    public long SnapshotsSent { get; private set; }

    /// <summary>Advance by one tick. Wired to <see cref="BotClient.SessionTick"/>.</summary>
    public void Tick(NetClient client, double dt)
    {
        if (!client.Joined) return;
        if (!ReferenceEquals(_bound, client)) Bind(client);
        if (_released) return;

        // 1) Find a server-owned train to borrow (owner = the server sentinel).
        if (_setId < 0)
        {
            foreach (TrainsetDef set in client.Trains.View.Sets.Values.OrderBy(s => s.Id))
            {
                if (set.OwnerId != ServerTrains.ServerOwnerId) continue;
                _setId = set.Id;
                _log($"[{_name}] found server train {set.Id} ({set.Cars.Count} car(s)) — asking to take it over");
                break;
            }
            if (_setId < 0) return; // none on the wire yet — the server may still be spawning them
        }

        // 2) Claim it once; wait for the server to flip ownership to us.
        if (!_owned)
        {
            if (!_requested)
            {
                _requested = true;
                client.Trains.RequestOwnership(_setId);
                return;
            }
            if (!client.Trains.View.Sets.TryGetValue(_setId, out TrainsetDef? maybe) || maybe.OwnerId != client.LocalId)
                return; // not ours yet (or another player beat us to it) — keep waiting
            _owned = true;
            _log($"[{_name}] now driving server train {_setId} — the server has stopped driving it");
        }

        // 3) Drive it: advance the walker and stream snapshots exactly like a sim owner would.
        if (!client.Trains.View.Sets.TryGetValue(_setId, out TrainsetDef? def) || def.OwnerId != client.LocalId)
        {
            _log($"[{_name}] no longer own server train {_setId} (reclaimed or churn) — re-scanning");
            _bound = null; // Bind() next tick resets and re-finds
            return;
        }

        _walker.Advance(_speed * dt);
        while (_pendingThrows.Count > 0)
        {
            (uint junctionId, byte branch) = _pendingThrows.Dequeue();
            client.Trains.ThrowJunction(junctionId, branch);
        }

        var cars = new CarSnapshot[def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
        {
            double offset = i * CarLength;
            BogieState? front = _walker.Behind(offset + BogieInset, (float)_speed);
            BogieState? rear = _walker.Behind(offset + CarLength - BogieInset, (float)_speed);
            if (front is null || rear is null) return; // trail history still building — wait a few ticks
            cars[i] = CarSnapshot.Railed(front.Value, rear.Value);
        }

        if (!_streaming)
        {
            _streaming = true;
            BogieState head = _walker.HeadState((float)_speed);
            _log($"[{_name}] server train {_setId}: streaming from edge {head.EdgeId} at {_speed:F0} m/s");
        }
        client.Trains.SendSnapshot(new TrainsetSnapshot(_setId, def.Epoch, client.EstimatedServerTimeMs, cars));
        SnapshotsSent++;

        // 4) Hand it back after the drive window (0 = hold until the bot exits — Ctrl+C leaves gracefully,
        // and the server reclaims the train on the disconnect either way).
        if (_driveSeconds > 0)
        {
            _held += dt;
            if (_held >= _driveSeconds)
            {
                _released = true;
                client.Trains.ReleaseOwnership(_setId);
                _log($"[{_name}] released server train {_setId} back to the server (it resumes its route)");
            }
        }
    }

    private void Bind(NetClient client)
    {
        _bound = client;
        _setId = -1;
        _requested = false;
        _owned = false;
        _released = false;
        _held = 0;
        _streaming = false;
        _pendingThrows.Clear();
    }
}
