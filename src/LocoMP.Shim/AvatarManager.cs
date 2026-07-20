using System.Collections.Generic;
using LocoMP.Core.Presence;

namespace LocoMP.Shim;

/// <summary>
/// Owns one <see cref="RemoteAvatar"/> per remote player id, driven by NetClient's roster events.
/// The session controller forwards joins/moves/leaves here and calls <see cref="Tick"/> each frame;
/// nothing above the Shim ever touches a GameObject (hard rule 3).
/// </summary>
public sealed class AvatarManager
{
    private readonly Dictionary<int, RemoteAvatar> _byId = new();

    public int Count => _byId.Count;

    /// <summary>Spawn (or re-target) the avatar for a player. Safe to call for an id we already track.</summary>
    public void AddOrUpdate(int id, string playerName, Pose pose)
    {
        if (!_byId.TryGetValue(id, out RemoteAvatar? avatar))
        {
            avatar = new RemoteAvatar(playerName);
            _byId[id] = avatar;
        }
        avatar.SetTarget(pose);
    }

    public void Move(int id, Pose pose)
    {
        if (_byId.TryGetValue(id, out RemoteAvatar? avatar)) avatar.SetTarget(pose);
    }

    public void Remove(int id)
    {
        if (_byId.TryGetValue(id, out RemoteAvatar? avatar))
        {
            avatar.Destroy();
            _byId.Remove(id);
        }
    }

    /// <summary>Hide (but keep) the avatar for a player who left our spatial relevance set (D10). A
    /// later <see cref="Move"/> re-shows it. No-op for an id we don't track.</summary>
    public void Hide(int id)
    {
        if (_byId.TryGetValue(id, out RemoteAvatar? avatar)) avatar.Hide();
    }

    /// <summary>Advance smoothing + billboards. Call once per frame while a session is live.</summary>
    public void Tick(float dt)
    {
        foreach (RemoteAvatar avatar in _byId.Values) avatar.Tick(dt);
    }

    /// <summary>Destroy every avatar (session ended).</summary>
    public void Clear()
    {
        foreach (RemoteAvatar avatar in _byId.Values) avatar.Destroy();
        _byId.Clear();
    }
}
