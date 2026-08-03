using System;
using LocoMP.Core.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LocoMP.UI;

/// <summary>
/// The M5.1 mismatch modal: renders a structured join refusal as exact have/need rows — never a
/// bare timeout or a log line (03 §10 doctrine). Built as our own panel because DV's PopupManager
/// is localization-key-driven and cannot carry arbitrary LocoMP strings (recon R-UI-5). Pushed on
/// the router above the root screen; Close pops back.
/// </summary>
public sealed class MismatchScreen : IScreen
{
    private readonly RejectInfo _info;
    private readonly Action _close;

    public MismatchScreen(RejectInfo info, Action close)
    {
        _info = info;
        _close = close;
    }

    public GameObject? Go { get; private set; }

    public void Build(RectTransform parent, WidgetKit kit)
    {
        RectTransform panel = kit.Panel(parent, vertical: true, name: "LocoMP Mismatch");
        Go = panel.gameObject;
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(720f, 0f);
        var fitter = panel.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        kit.Label(panel, Title(_info.Kind), size: kit.Theme.TitleSize);

        if (_info.ClientHas != null) kit.Label(panel, $"You have:  {_info.ClientHas}");
        if (_info.ServerNeeds != null) kit.Label(panel, $"Server needs:  {_info.ServerNeeds}");
        kit.Label(panel, _info.Reason, dim: true);
        kit.Label(panel, Advice(_info.Kind), dim: true);

        RectTransform actions = kit.Row(panel, "Actions");
        kit.Button(actions, "Close", _close, width: 160f);
    }

    public void OnShow() { }
    public void OnHide() { }

    private static string Title(RejectKind kind) => kind switch
    {
        RejectKind.Protocol => "Can't join — different LocoMP protocol",
        RejectKind.GameBuild => "Can't join — different game build",
        RejectKind.ModVersion => "Can't join — different LocoMP version",
        RejectKind.ModList => "Can't join — different installed mods",
        _ => "Can't join",
    };

    private static string Advice(RejectKind kind) => kind switch
    {
        RejectKind.Protocol or RejectKind.ModVersion =>
            "Both sides must run the same LocoMP release — update the older one and try again.",
        RejectKind.GameBuild =>
            "Both players must run the same Derail Valley build (Steam usually fixes this by updating).",
        RejectKind.ModList =>
            "Install the same mod set on both sides, or remove the extras, then rejoin.",
        _ => "",
    };
}
