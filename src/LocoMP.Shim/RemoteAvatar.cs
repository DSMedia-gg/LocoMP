using LocoMP.Core.Presence;
using UnityEngine;

// UnityEngine defines its own Pose struct; the alias pins the unqualified name to Core's wire type.
using Pose = LocoMP.Core.Presence.Pose;

namespace LocoMP.Shim;

/// <summary>
/// The visual stand-in for one remote player: a capsule body plus a name tag that always faces the
/// local camera. M1 placeholder visuals (03 §8's proper avatar rig comes later) — the point is
/// proving the presence pipeline end-to-end. Poses arrive in absolute coordinates and are
/// re-localized every tick, because the local origin can shift between frames.
/// </summary>
public sealed class RemoteAvatar
{
    /// <summary>Per-second smoothing rate toward the latest snapshot (~2-tick interpolation feel).</summary>
    private const float LerpRate = 12f;

    /// <summary>Beyond this, the remote player teleported (or we just spawned them) — snap, don't glide.</summary>
    private const float SnapDistance = 50f;

    private readonly GameObject _root;
    private readonly Transform _label;
    private Pose _target;
    private bool _hasTarget;

    public RemoteAvatar(string playerName)
    {
        _root = new GameObject($"LocoMP Avatar ({playerName})");

        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(_root.transform, worldPositionStays: false);
        body.transform.localPosition = new Vector3(0f, 1f, 0f); // capsule pivot is its centre; root sits at the feet
        Object.Destroy(body.GetComponent<Collider>());          // visual only — never collide with the world
        body.GetComponent<Renderer>().material.color = new Color(0.25f, 0.65f, 1f);

        // Billboard root with two stacked TextMeshes: the text itself in soft grey (pure white glares
        // against the skybox) over a black drop-shadow copy nudged down-right and slightly behind —
        // TextMesh has no shadow/outline support, so the shadow is literally a second mesh.
        var labelGo = new GameObject("NameTag");
        labelGo.transform.SetParent(_root.transform, worldPositionStays: false);
        labelGo.transform.localPosition = new Vector3(0f, 2.4f, 0f);
        MakeTagText(labelGo.transform, "Text", playerName,
            new Color(0.78f, 0.78f, 0.78f), Vector3.zero);
        // Shadow offset stays tight: big offsets read as doubled text up close, and any real depth
        // separation parallaxes visibly off-axis — keep z just past z-fighting range.
        MakeTagText(labelGo.transform, "Shadow", playerName,
            new Color(0f, 0f, 0f, 0.85f), new Vector3(0.012f, -0.012f, 0.004f));
        _label = labelGo.transform;
    }

    /// <summary>Record the latest synced pose (absolute coords). Applied smoothly on Tick.</summary>
    public void SetTarget(Pose pose)
    {
        _target = pose;
        // A pose after an interest-hide (D10) re-shows us; the player will have moved far while out of
        // range, so the next Apply snaps rather than glides.
        if (!_root.activeSelf) _root.SetActive(true);
        if (!_hasTarget)
        {
            _hasTarget = true;
            Apply(snap: true);
        }
    }

    /// <summary>Hide the avatar because this player left our spatial relevance set (D10). The
    /// GameObject is kept (deactivated), so a later <see cref="SetTarget"/> re-shows it cheaply — this
    /// is a presence hint, not a leave (that path calls <see cref="Destroy"/>).</summary>
    public void Hide() => _root.SetActive(false);

    /// <summary>Advance smoothing + billboard the name tag. Call once per frame.</summary>
    public void Tick(float dt)
    {
        if (!_hasTarget) return;
        Apply(snap: false, dt);

        Camera? cam = PresenceShim.ActiveCamera;
        if (cam != null)
        {
            // TextMesh renders along +Z, so look AWAY from the camera to read correctly.
            _label.rotation = Quaternion.LookRotation(_label.position - cam.transform.position);
        }
    }

    private static void MakeTagText(Transform parent, string name, string content, Color color, Vector3 offset)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, worldPositionStays: false);
        go.transform.localPosition = offset; // +z = behind the text, since the tag faces away from the camera
        TextMesh text = go.AddComponent<TextMesh>();
        text.text = content;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.fontSize = 48;              // rasterization detail — keep high so big glyphs stay sharp
        text.characterSize = 0.06f;      // world-space scale; sized to read from a train length away
        text.color = color;
        // A raw TextMesh renders nothing without a font AND its material on the renderer.
        Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.font = font;
        go.GetComponent<MeshRenderer>().material = font.material;
    }

    private void Apply(bool snap, float dt = 0f)
    {
        Vector3 local = PresenceShim.ToLocalPosition(_target);
        var rot = new Quaternion(_target.Rx, _target.Ry, _target.Rz, _target.Rw);

        if (snap || (local - _root.transform.position).sqrMagnitude > SnapDistance * SnapDistance)
        {
            _root.transform.SetPositionAndRotation(local, rot);
            return;
        }

        float t = Mathf.Clamp01(LerpRate * dt);
        _root.transform.SetPositionAndRotation(
            Vector3.Lerp(_root.transform.position, local, t),
            Quaternion.Slerp(_root.transform.rotation, rot, t));
    }

    public void Destroy() => Object.Destroy(_root);
}
