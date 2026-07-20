using LocoMP.Core.Session;
using LocoMP.Core.Trains;
using LocoMP.Core.World;

namespace LocoMP.Server;

/// <summary>
/// A consist the dedicated server owns and drives itself (M6-B.2 kinematic coaster) — so a fresh server
/// has moving trains with no bot or game client. It walks the extracted world topology (the M2 `.lmpw`)
/// and publishes spline-space snapshots through <see cref="ServerTrains.PushServerSnapshot"/>, exactly as
/// a real sim owner would. Server-authoritative: the consist is registered under
/// <see cref="ServerTrains.ServerOwnerId"/>, so no player can claim it (they see it roll past).
/// The topology-walk + snapshot geometry mirror the bot's ConsistDriver, kept server-shaped here (it
/// drives <see cref="ServerTrains"/> directly, not a NetClient).
/// </summary>
public sealed class ServerKinematicTrain
{
    // DV cars are 10–25 m; one uniform size is fine for an ambient coaster. The bogie sits inset from
    // each car end, and consecutive cars queue nose-to-tail along the same path.
    private const double CarLength = 16.0;
    private const double BogieInset = 3.5;

    private readonly ServerTrains _trains;
    private readonly TopologyWalker _walker;
    private readonly double _speed;
    private readonly TrainsetDef _def;
    private bool _streaming;

    public ServerKinematicTrain(ServerTrains trains, WorldTopology topology, int carCount, double speed,
                                int seed, string[]? liveries = null, uint? startEdgeId = null)
    {
        _trains = trains;
        _speed = speed;
        carCount = System.Math.Max(1, carCount);
        _walker = new TopologyWalker(topology, seed, tailCapacityM: carCount * CarLength + 100, startEdgeId);

        var specs = new CarDef[carCount];
        for (int i = 0; i < carCount; i++)
            specs[i] = new CarDef(0, KindFor(i, liveries ?? System.Array.Empty<string>()));
        _def = _trains.SpawnServerOwned(specs);
    }

    public int TrainsetId => _def.Id;
    public long SnapshotsSent { get; private set; }

    /// <summary>Advance the consist by one tick and publish its new position to every client. While a
    /// player has claimed the train (M6-B.3), the server is no longer its owner — freeze: don't advance
    /// the walker or publish (the snapshot would be a no-op anyway), so on hand-back the train resumes
    /// from where it was borrowed rather than snapping to a schedule position that ran on in parallel.</summary>
    public void Tick(double dt)
    {
        if (!_trains.IsServerDriven(_def.Id)) return; // on loan to a player — the server isn't driving it
        _walker.Advance(_speed * dt);

        var cars = new CarSnapshot[_def.Cars.Count];
        for (int i = 0; i < cars.Length; i++)
        {
            double offset = i * CarLength;
            BogieState? front = _walker.Behind(offset + BogieInset, (float)_speed);
            BogieState? rear = _walker.Behind(offset + CarLength - BogieInset, (float)_speed);
            if (front is null || rear is null) return; // trail history still building — wait a few ticks
            cars[i] = CarSnapshot.Railed(front.Value, rear.Value);
        }

        _trains.PushServerSnapshot(new TrainsetSnapshot(_def.Id, _def.Epoch, 0L, cars));
        SnapshotsSent++;
        _streaming = true;
    }

    public bool Streaming => _streaming;

    private static string KindFor(int carIndex, string[] liveries)
    {
        if (liveries.Length == 0) return carIndex == 0 ? "LocoDiesel" : "BoxcarBrown";
        if (carIndex == 0 || liveries.Length == 1) return liveries[0];
        return liveries[1 + (carIndex - 1) % (liveries.Length - 1)];
    }
}
