using System.Collections.Generic;
using LocoMP.Core.Career;
using LocoMP.Core.Items;
using LocoMP.Core.Presence;
using LocoMP.Core.Trains;

namespace LocoMP.Core.Persistence;

/// <summary>One saved player profile (wallet balances live in the ledger accounts, not here).</summary>
public sealed class ProfileSave
{
    public ProfileSave(string key, string name, List<string> licenses)
    {
        Key = key;
        Name = name;
        Licenses = licenses;
    }

    public string Key { get; }
    public string Name { get; }
    public List<string> Licenses { get; }
}

/// <summary>One saved board job. Deadlines are stored as REMAINING milliseconds because the
/// server's monotonic clock restarts with the process — an absolute deadline from the previous
/// run would be meaningless (or instantly expired) after a restart.</summary>
public sealed class JobSave
{
    public JobSave(JobDef def, JobLifecycle state, string claimantKey, int nextTaskIndex, long claimRemainingMs)
    {
        Def = def;
        State = state;
        ClaimantKey = claimantKey;
        NextTaskIndex = nextTaskIndex;
        ClaimRemainingMs = claimRemainingMs;
    }

    public JobDef Def { get; }
    public JobLifecycle State { get; }

    /// <summary>Empty string = unclaimed (the wire codec has no null strings).</summary>
    public string ClaimantKey { get; }

    public int NextTaskIndex { get; }
    public long ClaimRemainingMs { get; }
}

/// <summary>The career half of a server save (03 §7 contents: jobs + per-player profiles + meta).</summary>
public sealed class CareerSaveData
{
    public ProgressionPreset Preset { get; set; }
    public Dictionary<string, long> Accounts { get; } = new();
    public long Minted { get; set; }
    public long Burned { get; set; }
    public List<ProfileSave> Profiles { get; } = new();
    public List<string> SharedLicenses { get; } = new();
    public bool SharedGrantIssued { get; set; }
    public List<JobSave> Jobs { get; } = new();

    /// <summary>Reconnect-grace holds still pending at save time: playerKey → remaining ms.</summary>
    public Dictionary<string, long> GraceRemainingMs { get; } = new();

    public int NextJobId { get; set; } = 1;
    public uint RngState { get; set; } = 1;
}

/// <summary>The world half of a server save: consists (defs + last known spline positions),
/// junctions, turntables, and the id counters so nothing is ever re-minted onto a live id.</summary>
public sealed class TrainsSaveData
{
    public List<TrainsetDef> Sets { get; } = new();
    public List<TrainsetSnapshot> LatestSnapshots { get; } = new();
    public Dictionary<uint, byte> Junctions { get; } = new();
    public Dictionary<uint, float> Turntables { get; } = new();
    public int NextTrainsetId { get; set; } = 1;
    public int NextCarId { get; set; } = 1;
}

/// <summary>One saved item: identity + state (the <see cref="ItemDef"/>) plus its location. Unlike
/// the wire — which hides the holder behind a session peer id — the SAVE keeps the possession scope
/// key, because that key is exactly what re-binds a player's inventory across a restart (03 §7 /
/// 02 §5). World items store their pose; possessed items store the owning scope.</summary>
public sealed class ItemSave
{
    public ItemSave(ItemDef def, ItemLocationKind location, Pose worldPose, string ownerScope, bool worldLocked = false)
    {
        Def = def;
        Location = location;
        WorldPose = worldPose;
        OwnerScope = ownerScope;
        WorldLocked = worldLocked;
    }

    public ItemDef Def { get; }
    public ItemLocationKind Location { get; }
    public Pose WorldPose { get; }

    /// <summary>Empty string in the world; the policy scope (player key or shared account) when held.</summary>
    public string OwnerScope { get; }

    /// <summary>A set-down personal essential (map/radio/wallet) — restored as look-but-don't-touch.</summary>
    public bool WorldLocked { get; }
}

/// <summary>The items half of a server save (M4): world-dropped items + per-player inventory + the
/// id counter, so a cold restart resumes both exactly (the 02 §5 win-condition tail).</summary>
public sealed class ItemsSaveData
{
    public List<ItemSave> Items { get; } = new();
    public int NextItemId { get; set; } = 1;
}

/// <summary>Everything a cold restart needs to resume the world (07 §M3 exit). Produced by
/// NetServer.CaptureSave, serialized by SaveCodec, restored via the NetServer constructor.</summary>
public sealed class ServerSaveData
{
    public ServerSaveData(CareerSaveData career, TrainsSaveData trains, ItemsSaveData? items = null)
    {
        Career = career;
        Trains = trains;
        Items = items ?? new ItemsSaveData();
    }

    public CareerSaveData Career { get; }
    public TrainsSaveData Trains { get; }
    public ItemsSaveData Items { get; }
}
