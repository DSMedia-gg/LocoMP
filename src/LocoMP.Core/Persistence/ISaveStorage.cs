using System;
using System.Collections.Generic;
using System.IO;

namespace LocoMP.Core.Persistence;

/// <summary>One rotated backup beside the current save (M5.2 backup view). Index 1 = newest.</summary>
public readonly struct SaveBackupInfo
{
    public SaveBackupInfo(int index, long sizeBytes, DateTime lastWriteUtc)
    {
        Index = index;
        SizeBytes = sizeBytes;
        LastWriteUtc = lastWriteUtc;
    }

    public int Index { get; }
    public long SizeBytes { get; }
    public DateTime LastWriteUtc { get; }
}

/// <summary>Where save bytes live. Abstracted so persistence logic is tested in-memory and the
/// two frontends (host-embedded mod, headless server) choose their own paths (03 §11).</summary>
public interface ISaveStorage
{
    /// <summary>The current save's bytes, or null if none exists yet.</summary>
    byte[]? TryLoad();

    /// <summary>Store a new save; must never destroy the previous one on failure.</summary>
    void Save(byte[] data);
}

/// <summary>
/// File-backed storage with atomic replace + backup rotation (03 §7): the new save is written to a
/// temp file first and only then moved into place, so a crash mid-write can never corrupt the
/// current save; the previous saves survive as .1 … .N beside it (newest first). Corruption
/// recovery is manual by design in v1 — the backups are ordinary files a host can rename back.
/// </summary>
public sealed class FileSaveStorage : ISaveStorage
{
    private readonly string _path;
    private readonly int _backups;

    public FileSaveStorage(string path, int backups = 3)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path required", nameof(path));
        if (backups < 0) throw new ArgumentOutOfRangeException(nameof(backups));
        _path = path;
        _backups = backups;
    }

    public byte[]? TryLoad() => File.Exists(_path) ? File.ReadAllBytes(_path) : null;

    public void Save(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, data); // fully on disk before it can enter the rotation

        if (File.Exists(_path))
        {
            if (_backups > 0)
            {
                string oldest = $"{_path}.{_backups}";
                if (File.Exists(oldest)) File.Delete(oldest);
                for (int i = _backups - 1; i >= 1; i--)
                {
                    string src = $"{_path}.{i}";
                    if (File.Exists(src)) File.Move(src, $"{_path}.{i + 1}");
                }
                File.Move(_path, $"{_path}.1");
            }
            else
            {
                File.Delete(_path);
            }
        }
        File.Move(tmp, _path);
    }

    /// <summary>The rotated backups that exist right now, newest (.1) first (M5.2 backup view).</summary>
    public IReadOnlyList<SaveBackupInfo> ListBackups()
    {
        var result = new List<SaveBackupInfo>();
        for (int i = 1; i <= _backups; i++)
        {
            var f = new FileInfo($"{_path}.{i}");
            if (!f.Exists) continue;
            result.Add(new SaveBackupInfo(i, f.Length, f.LastWriteTimeUtc));
        }
        return result;
    }

    /// <summary>Make backup .<paramref name="index"/> the CURRENT save (M5.2 restore). Runs through
    /// <see cref="Save"/>, so the displaced current enters the rotation as .1 — a restore can itself
    /// be undone and never destroys anything. The caller owns the session semantics: restoring under
    /// a RUNNING server only sticks if nothing autosaves afterwards (the dedicated console's
    /// <c>restore</c> command stops without the final save for exactly this reason).</summary>
    public bool TryRestoreBackup(int index, out string? reason)
    {
        reason = null;
        if (index < 1 || index > _backups)
        {
            reason = $"backup index must be 1-{_backups}";
            return false;
        }
        string file = $"{_path}.{index}";
        if (!File.Exists(file))
        {
            reason = $"no backup {index} exists";
            return false;
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file); // read FIRST — Save() shifts the rotation underneath
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
        Save(bytes);
        return true;
    }
}
