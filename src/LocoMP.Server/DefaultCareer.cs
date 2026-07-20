using LocoMP.Core.Career;

namespace LocoMP.Server;

/// <summary>
/// A small, self-contained career board so the dedicated server runs out-of-the-box with no config
/// file and no game. The core deterministic generator (CareerRegistry) needs only two-or-more stations
/// and at least one job type to fill the board on every tick — with zero players connected — so a solo
/// joiner arrives to a populated board (D13 reserved this generator for exactly this).
///
/// These stations/cargo are SYNTHETIC placeholders, not the real Derail Valley map. A genuine career
/// (real yards, cargo economy, license gates, route distances, station world-locations for the task
/// proximity gate) can be LOADED from a .lmpc file via --config (CareerConfigCodec); the tool that
/// EXPORTS that file straight from a running game — a Shim/extractor slice, like the topology .lmpw — is
/// still deferred. This default runs the server out-of-the-box and seeds --dump-config; its jobs require
/// no license so any solo tester can claim and run them.
/// </summary>
public static class DefaultCareer
{
    public static CareerConfig Build(ProgressionPreset preset) => new()
    {
        Preset = preset,
        StartingBalanceCents = 500_00,
        TargetAvailableJobs = 8,
        JobSeed = 1,
        Stations = new[] { "Alpha", "Bravo", "Charlie", "Delta" },
        JobTypes = new[]
        {
            // No required licenses — a fresh solo player can claim anything on the board.
            new JobTypeSpec("ShuntingLoad", "Boxcar", 25_00, 2, 5),
            new JobTypeSpec("FreightHaul", "Tanker", 40_00, 3, 8, payoutPerCarKmCents: 100),
            new JobTypeSpec("LoggingRun", "Flatbed", 35_00, 2, 6, payoutPerCarKmCents: 80),
        },
        // Solo testing: no station world-locations are supplied, so the task-proximity gate would have
        // nothing to check anyway — keep it explicitly off so a bot can report steps from anywhere.
        TaskProximityRadiusM = 0,
        AcceptExternalJobs = false, // the server owns the board; clients don't capture jobs here
    };
}
