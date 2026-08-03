using TMPro;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The shared look for every LocoMP screen (M5.0 step 3). Colours echo DV's own UI palette
/// (see <c>DV.UIFramework.UIColors</c> — accents match its BLUE so LocoMP reads as part of the
/// game), and the font is HARVESTED at menu-hook time from a cloned DV button (recon R-UI-5:
/// referenced at runtime, never shipped — CLAUDE #2). A null font falls back to TMP's default,
/// so the UI still renders if the harvest never ran (hook-degraded mode).
/// </summary>
public sealed class LocoMpTheme
{
    public Color Panel = new Color(0.07f, 0.08f, 0.10f, 0.97f);
    public Color PanelLight = new Color(0.13f, 0.15f, 0.18f, 1f);
    public Color Accent = new Color(28f / 255f, 132f / 255f, 205f / 255f, 1f); // UIColors.BLUE
    public Color AccentDisabled = new Color(0.25f, 0.28f, 0.32f, 1f);
    public Color Text = new Color(0.92f, 0.94f, 0.96f, 1f);
    public Color TextDim = new Color(0.55f, 0.60f, 0.65f, 1f);
    public Color Field = new Color(0.16f, 0.18f, 0.21f, 1f);
    public Color CoverDim = new Color(0f, 0f, 0f, 0.88f);

    /// <summary>DV's TMP font, harvested from the cloned menu button; null = TMP default.</summary>
    public TMP_FontAsset? Font;

    public int Size = 22;
    public int TitleSize = 30;
    public float RowHeight = 38f;
}
