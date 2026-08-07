using System;
using System.Collections.Generic;

namespace LocoMP.Core.Trains;

/// <summary>The discrete coupler-hardware acts v18 replicates (02 §1 — hoses, anglecocks, MU
/// cables). Wire-stable byte values: only append.</summary>
public enum CouplerHardwareKind : byte
{
    /// <summary>Connect the brake hoses at (carA, endA) ↔ (carB, endB).</summary>
    HoseConnect = 0,

    /// <summary>Disconnect the brake hose at (carA, endA) (carB may be 0 — the partner is implied).</summary>
    HoseDisconnect = 1,

    /// <summary>Set the anglecock at (carA, endA) to <see cref="CouplerHardwareReport.Flag"/>.</summary>
    CockSet = 2,

    /// <summary>Connect the MU cables at (carA, endA) ↔ (carB, endB).</summary>
    MuConnect = 3,

    /// <summary>Disconnect the MU cable at (carA, endA).</summary>
    MuDisconnect = 4,
}

/// <summary>One coupler-hardware act/state line — the payload of both CouplerHardwareReport and
/// CouplerHardwareState (a commit is the act, restated).</summary>
public readonly struct CouplerHardwareReport
{
    public CouplerHardwareReport(CouplerHardwareKind kind, int carA, CoupleEnd endA,
                                 int carB = 0, CoupleEnd endB = CoupleEnd.Front, bool flag = false)
    {
        Kind = kind;
        CarA = carA;
        EndA = endA;
        CarB = carB;
        EndB = endB;
        Flag = flag;
    }

    public CouplerHardwareKind Kind { get; }
    public int CarA { get; }
    public CoupleEnd EndA { get; }

    /// <summary>Partner car for the connect kinds; 0 when the kind has none (cock set, disconnects).</summary>
    public int CarB { get; }

    public CoupleEnd EndB { get; }

    /// <summary>CockSet: true = open.</summary>
    public bool Flag { get; }

    public override string ToString() =>
        $"{Kind} {CarA}/{EndA}" + (CarB != 0 ? $" ↔ {CarB}/{EndB}" : "") +
        (Kind == CouplerHardwareKind.CockSet ? $" = {(Flag ? "open" : "closed")}" : "");
}

/// <summary>What <see cref="CouplerHardwareRegistry.TryApply"/> decided.</summary>
public enum HardwareApplyResult : byte
{
    /// <summary>State changed — store it and broadcast the commit.</summary>
    Committed = 0,

    /// <summary>Already true — absorb silently. This is load-bearing, not an optimisation: a native
    /// auto-break (a consist pulled apart) fires on EVERY client's world, so the server receives the
    /// same disconnect N times and must commit exactly once.</summary>
    Redundant = 1,

    /// <summary>Invalid — refuse with a reason (the incumbent's phantom-MU bug class).</summary>
    Rejected = 2,
}

/// <summary>
/// The server's authoritative coupler-hardware state (v18, 02 §1): per car END, the brake-hose
/// partner, the anglecock, and the MU-cable partner. Game-free — validation runs against the
/// <see cref="TrainsetRegistry"/> consist state (02: "validate against proximity + consist state"):
/// cars must exist, connect targets must be free, and a SAME-SET connect must join def-adjacent
/// cars. A cross-set connect (two touching but unchained cuts — legal in DV) is accepted on
/// existence alone: the acting client's game already required physical reach, and the server has no
/// geometry check yet (banked; the .lmpw v3 geometry makes one possible). Cocks default CLOSED —
/// DV's spawn state — so only open cocks are state. Doubles as the CLIENT mirror via
/// <see cref="ApplyCommitted"/> (the server is always right; no validation on that path).
/// </summary>
public sealed class CouplerHardwareRegistry
{
    private readonly Dictionary<(int car, CoupleEnd end), (int car, CoupleEnd end)> _hoses = new();
    private readonly Dictionary<(int car, CoupleEnd end), (int car, CoupleEnd end)> _mu = new();
    private readonly HashSet<(int car, CoupleEnd end)> _openCocks = new();

    /// <summary>The hose partner at a car end, or null.</summary>
    public (int car, CoupleEnd end)? HoseAt(int car, CoupleEnd end) =>
        _hoses.TryGetValue((car, end), out (int, CoupleEnd) p) ? p : ((int, CoupleEnd)?)null;

    /// <summary>The MU partner at a car end, or null.</summary>
    public (int car, CoupleEnd end)? MuAt(int car, CoupleEnd end) =>
        _mu.TryGetValue((car, end), out (int, CoupleEnd) p) ? p : ((int, CoupleEnd)?)null;

    /// <summary>Is the anglecock at a car end open? (Closed is the default/absent state.)</summary>
    public bool CockOpen(int car, CoupleEnd end) => _openCocks.Contains((car, end));

    /// <summary>Server path: validate a client's report against consist state and apply.</summary>
    public HardwareApplyResult TryApply(in CouplerHardwareReport report, TrainsetRegistry registry,
                                        out string? reason)
    {
        reason = null;
        if (!registry.TryFindCar(report.CarA, out TrainsetDef setA))
        {
            reason = $"unknown car {report.CarA}";
            return HardwareApplyResult.Rejected;
        }

        switch (report.Kind)
        {
            case CouplerHardwareKind.HoseConnect:
            case CouplerHardwareKind.MuConnect:
            {
                if (report.CarB == report.CarA)
                {
                    reason = "a car cannot connect to itself";
                    return HardwareApplyResult.Rejected;
                }
                if (!registry.TryFindCar(report.CarB, out TrainsetDef setB))
                {
                    reason = $"unknown partner car {report.CarB}";
                    return HardwareApplyResult.Rejected;
                }
                Dictionary<(int, CoupleEnd), (int, CoupleEnd)> layer = Layer(report.Kind);
                (int, CoupleEnd) a = (report.CarA, report.EndA);
                (int, CoupleEnd) b = (report.CarB, report.EndB);
                if (layer.TryGetValue(a, out (int, CoupleEnd) existingA) && existingA.Equals(b))
                    return HardwareApplyResult.Redundant;
                if (layer.ContainsKey(a) || layer.ContainsKey(b))
                {
                    reason = "an end is already connected";
                    return HardwareApplyResult.Rejected;
                }
                // Same set → the cars must be def-adjacent (the phantom-MU class: a stale or forged
                // connect across a consist). Different sets → existence-gated (see class doc).
                if (setA.Id == setB.Id && !AreAdjacent(setA, report.CarA, report.CarB))
                {
                    reason = $"cars {report.CarA} and {report.CarB} are not adjacent in set {setA.Id}";
                    return HardwareApplyResult.Rejected;
                }
                layer[a] = b;
                layer[b] = a;
                return HardwareApplyResult.Committed;
            }

            case CouplerHardwareKind.HoseDisconnect:
            case CouplerHardwareKind.MuDisconnect:
            {
                Dictionary<(int, CoupleEnd), (int, CoupleEnd)> layer = Layer(report.Kind);
                if (!layer.TryGetValue((report.CarA, report.EndA), out (int, CoupleEnd) partner))
                    return HardwareApplyResult.Redundant; // native auto-breaks race in from N clients
                layer.Remove((report.CarA, report.EndA));
                layer.Remove(partner);
                return HardwareApplyResult.Committed;
            }

            case CouplerHardwareKind.CockSet:
            {
                (int, CoupleEnd) key = (report.CarA, report.EndA);
                bool open = _openCocks.Contains(key);
                if (open == report.Flag) return HardwareApplyResult.Redundant;
                if (report.Flag) _openCocks.Add(key);
                else _openCocks.Remove(key);
                return HardwareApplyResult.Committed;
            }

            default:
                reason = $"unknown hardware kind {(byte)report.Kind}";
                return HardwareApplyResult.Rejected;
        }
    }

    /// <summary>Client path: the server committed this — mirror it, no questions.</summary>
    public void ApplyCommitted(in CouplerHardwareReport report)
    {
        switch (report.Kind)
        {
            case CouplerHardwareKind.HoseConnect:
            case CouplerHardwareKind.MuConnect:
            {
                Dictionary<(int, CoupleEnd), (int, CoupleEnd)> layer = Layer(report.Kind);
                layer[(report.CarA, report.EndA)] = (report.CarB, report.EndB);
                layer[(report.CarB, report.EndB)] = (report.CarA, report.EndA);
                break;
            }
            case CouplerHardwareKind.HoseDisconnect:
            case CouplerHardwareKind.MuDisconnect:
            {
                Dictionary<(int, CoupleEnd), (int, CoupleEnd)> layer = Layer(report.Kind);
                if (layer.TryGetValue((report.CarA, report.EndA), out (int, CoupleEnd) partner))
                {
                    layer.Remove((report.CarA, report.EndA));
                    layer.Remove(partner);
                }
                break;
            }
            case CouplerHardwareKind.CockSet:
                if (report.Flag) _openCocks.Add((report.CarA, report.EndA));
                else _openCocks.Remove((report.CarA, report.EndA));
                break;
        }
    }

    /// <summary>A car left the world — drop its cock state and both sides of any connection.
    /// No broadcast needed: the car's removal already went out, and every client's native world
    /// breaks its own hoses on despawn.</summary>
    public void PruneCar(int carId)
    {
        _openCocks.Remove((carId, CoupleEnd.Front));
        _openCocks.Remove((carId, CoupleEnd.Rear));
        PruneFromLayer(_hoses, carId);
        PruneFromLayer(_mu, carId);
    }

    /// <summary>Every non-default state line, for the join burst (connects emitted once per pair).</summary>
    public IEnumerable<CouplerHardwareReport> EnumerateState()
    {
        foreach (KeyValuePair<(int car, CoupleEnd end), (int car, CoupleEnd end)> kv in _hoses)
            if (IsCanonical(kv))
                yield return new CouplerHardwareReport(CouplerHardwareKind.HoseConnect,
                    kv.Key.car, kv.Key.end, kv.Value.car, kv.Value.end);
        foreach ((int car, CoupleEnd end) cock in _openCocks)
            yield return new CouplerHardwareReport(CouplerHardwareKind.CockSet, cock.car, cock.end, flag: true);
        foreach (KeyValuePair<(int car, CoupleEnd end), (int car, CoupleEnd end)> kv in _mu)
            if (IsCanonical(kv))
                yield return new CouplerHardwareReport(CouplerHardwareKind.MuConnect,
                    kv.Key.car, kv.Key.end, kv.Value.car, kv.Value.end);
    }

    /// <summary>Wipe everything (client mirror reset on disconnect).</summary>
    public void Clear()
    {
        _hoses.Clear();
        _mu.Clear();
        _openCocks.Clear();
    }

    private Dictionary<(int, CoupleEnd), (int, CoupleEnd)> Layer(CouplerHardwareKind kind) =>
        kind is CouplerHardwareKind.HoseConnect or CouplerHardwareKind.HoseDisconnect ? _hoses : _mu;

    private static bool IsCanonical(KeyValuePair<(int car, CoupleEnd end), (int car, CoupleEnd end)> kv) =>
        kv.Key.car < kv.Value.car || (kv.Key.car == kv.Value.car && kv.Key.end < kv.Value.end);

    private static bool AreAdjacent(TrainsetDef set, int carA, int carB)
    {
        for (int i = 0; i < set.Cars.Count; i++)
        {
            if (set.Cars[i].Id != carA) continue;
            return (i > 0 && set.Cars[i - 1].Id == carB) ||
                   (i + 1 < set.Cars.Count && set.Cars[i + 1].Id == carB);
        }
        return false;
    }

    private static void PruneFromLayer(Dictionary<(int car, CoupleEnd end), (int car, CoupleEnd end)> layer, int carId)
    {
        List<(int, CoupleEnd)>? doomed = null;
        foreach (KeyValuePair<(int car, CoupleEnd end), (int car, CoupleEnd end)> kv in layer)
            if (kv.Key.car == carId || kv.Value.car == carId)
                (doomed ??= new List<(int, CoupleEnd)>()).Add(kv.Key);
        if (doomed == null) return;
        foreach ((int, CoupleEnd) key in doomed) layer.Remove(key);
    }
}
