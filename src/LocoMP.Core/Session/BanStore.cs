using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LocoMP.Core.Session;

/// <summary>One persistent ban record (U3): the identity is a SteamId64, minted an opaque surfaced id
/// like a session ban so every unban surface (Host Menu, console, remote admin) targets it the same
/// way. The name is a display snapshot from the moment of the ban.</summary>
public readonly struct PersistentBan
{
    public PersistentBan(int id, ulong steamId, string name, DateTime bannedAtUtc)
    {
        Id = id;
        SteamId = steamId;
        Name = name;
        BannedAtUtc = bannedAtUtc;
    }

    public int Id { get; }
    public ulong SteamId { get; }
    public string Name { get; }
    public DateTime BannedAtUtc { get; }
}

/// <summary>
/// The persistent ban store (M5.5, U3 — Cody 2026-08-04: ban records persist keyed on Steam ID, designed
/// once, at the Steam milestone, with nothing to migrate). Session bans (<see cref="ServerModeration"/>)
/// stay key-scoped and die with the server; THIS list survives it, because a SteamId64 is a
/// platform-authenticated identity the player cannot re-roll by reconnecting — the property the player
/// key deliberately lacks.
///
/// <para>Storage is a human-inspectable text file (an ops surface, like the dedicated console):
/// <c>id&lt;TAB&gt;steamId&lt;TAB&gt;bannedAtUtc&lt;TAB&gt;name</c> per line, <c>#</c> lines ignored,
/// unparseable lines skipped on load (a hand-edit that breaks one line must not void the ban list).
/// Writes go through the same temp-then-move idiom as <see cref="Persistence.FileSaveStorage"/> so a
/// crash mid-write never corrupts the list.</para>
///
/// <para>Surfaced ids mint from <see cref="PersistentIdFloor"/> so they can never collide with session
/// ban ids (which mint from 1) — the merged ban-list view stays a flat (id, name) list on the wire, and
/// an unban-by-id routes unambiguously to whichever store owns the id.</para>
/// </summary>
public sealed class BanStore
{
    /// <summary>Persistent ids live at 1&#160;000&#160;000+; session ids count up from 1. The two ranges
    /// meeting would take a million bans in one sitting.</summary>
    public const int PersistentIdFloor = 1_000_000;

    private readonly string _path;
    private readonly Dictionary<ulong, PersistentBan> _bySteamId = new();
    private int _nextId = PersistentIdFloor;

    /// <summary>Open (and load) the store at <paramref name="path"/>. A missing file is an empty list.</summary>
    public BanStore(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required", nameof(path));
        _path = path;
        Load();
    }

    public bool IsBanned(ulong steamId) => _bySteamId.ContainsKey(steamId);

    /// <summary>All records, in id order (stable for list views).</summary>
    public IReadOnlyList<PersistentBan> Entries
    {
        get
        {
            var list = new List<PersistentBan>(_bySteamId.Values);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }
    }

    /// <summary>Record a ban and persist. False (and no write) if the id is already banned.</summary>
    public bool Add(ulong steamId, string name)
    {
        if (steamId == 0 || _bySteamId.ContainsKey(steamId)) return false;
        _bySteamId[steamId] = new PersistentBan(_nextId++, steamId, Sanitize(name), DateTime.UtcNow);
        Write();
        return true;
    }

    /// <summary>Lift a ban by its surfaced id and persist. False if no record has that id.</summary>
    public bool RemoveById(int id)
    {
        foreach (KeyValuePair<ulong, PersistentBan> kv in _bySteamId)
        {
            if (kv.Value.Id != id) continue;
            _bySteamId.Remove(kv.Key);
            Write();
            return true;
        }
        return false;
    }

    /// <summary>Display names live one-per-line beside tab separators — fold both away, keep it short.</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "(unknown)";
        var sb = new StringBuilder(Math.Min(name.Length, 64));
        foreach (char c in name)
        {
            if (sb.Length == 64) break;
            sb.Append(c == '\t' || c == '\r' || c == '\n' || char.IsControl(c) ? ' ' : c);
        }
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "(unknown)" : s;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (string line in File.ReadAllLines(_path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split(new[] { '\t' }, 4);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id) ||
                !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) ||
                steamId == 0 || id < PersistentIdFloor || _bySteamId.ContainsKey(steamId))
                continue;
            if (!DateTime.TryParse(parts[2], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when))
                when = DateTime.UtcNow;
            _bySteamId[steamId] = new PersistentBan(id, steamId, Sanitize(parts[3]), when);
            if (id >= _nextId) _nextId = id + 1;
        }
    }

    private void Write()
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("# LocoMP persistent bans (U3) — id, steamId64, bannedAtUtc, name. Hand-editable; the server rewrites on change.");
        foreach (PersistentBan b in Entries)
            sb.Append(b.Id.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(b.SteamId.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(b.BannedAtUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
              .AppendLine(b.Name);

        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, sb.ToString()); // fully on disk before it replaces the live list
        if (File.Exists(_path)) File.Delete(_path);
        File.Move(tmp, _path);
    }
}
