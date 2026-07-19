using System;
using System.IO;
using UnityEngine;

namespace LocoMP.Shim;

/// <summary>
/// The stable player key (M3 identity): the server keys profiles, wallets, and the reconnect-grace
/// hold on it, so it must survive sessions, mod restages, AND game updates — hence
/// persistentDataPath (AppData/LocalLow), never the mod folder (wiped on every restage). Created
/// once and reused forever; deleting the file is "start a fresh career" by construction. The key
/// is never shown to other players (it doubles as the reclaim credential).
/// </summary>
public static class PlayerKeyStore
{
    public static string GetOrCreate(Action<string> log)
    {
        string path = Path.Combine(Application.persistentDataPath, "locomp-player-key.txt");
        try
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length > 0 && existing.Length <= 64 && existing[0] != '@') return existing;
                log("[career] stored player key was invalid — minting a new one (fresh career)");
            }
            string fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, fresh);
            log($"[career] created your player identity ({path})");
            return fresh;
        }
        catch (Exception e)
        {
            log($"[career] player-key store failed ({e.Message}) — using a session-only identity");
            return Guid.NewGuid().ToString("N");
        }
    }
}
