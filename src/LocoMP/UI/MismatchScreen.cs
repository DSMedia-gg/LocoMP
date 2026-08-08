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
/// the router above the root screen; Close pops back. D25: warning-accented title, the differing
/// values flagged (yours red, the server's green), advice as one imperative line.
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

        RectTransform titleRow = kit.Row(panel, "Title");
        TMP_Text warn = kit.Label(titleRow, "⚠", size: kit.Theme.TitleSize);
        warn.color = kit.Theme.Warning;
        kit.Label(titleRow, Title(_info.Kind), size: kit.Theme.TitleSize);

        if (_info.ClientHas != null) CompareRow(kit, panel, "You have", _info.ClientHas, kit.Theme.Danger);
        if (_info.ServerNeeds != null) CompareRow(kit, panel, "Server needs", _info.ServerNeeds, kit.Theme.Success);
        kit.Label(panel, _info.Reason, dim: true);
        kit.Subline(panel, Advice(_info.Kind));

        RectTransform actions = kit.Row(panel, "Actions");
        kit.Button(actions, "Close", _close, width: 160f);
    }

    private static void CompareRow(WidgetKit kit, RectTransform panel, string key, string value, Color valueColor)
    {
        RectTransform row = kit.Row(panel, "Compare " + key);
        TMP_Text k = kit.Label(row, key, dim: true);
        k.gameObject.AddComponent<LayoutElement>().preferredWidth = 190f;
        TMP_Text v = kit.Label(row, value);
        v.color = valueColor;
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
        RejectKind.Protocol or RejectKind.ModVersion => "Update the older LocoMP and rejoin.",
        RejectKind.GameBuild => "Update Derail Valley and rejoin.",
        RejectKind.ModList => "Match the installed mod set on both sides and rejoin.",
        _ => "",
    };
}
