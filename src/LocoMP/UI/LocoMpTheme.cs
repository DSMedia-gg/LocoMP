using System;
using TMPro;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The shared look for every LocoMP screen (M5.0 step 3; visual language locked by D25,
/// 10-plan §10). Colours echo DV's own UI palette (see <c>DV.UIFramework.UIColors</c> — the accent
/// matches its BLUE, and <see cref="AdoptDvPalette"/> harvests RED/YELLOW/GREEN at runtime so the
/// semantic colours speak the game's own colour language, falling back to the constants below in
/// hook-degraded mode). The font is HARVESTED at menu-hook time from a cloned DV button (recon
/// R-UI-5: referenced at runtime, never shipped — CLAUDE #2). A null font falls back to TMP's
/// default, so the UI still renders if the harvest never ran.
///
/// <para><see cref="EnsureRuntimeAssets"/> generates the procedural sprite pair (rounded fill +
/// border 9-slice, plus a dot) in code — the D25 "hierarchy and state" skin ships zero art and
/// keeps U1 (runtime-built uGUI, no AssetBundle) closed. Null sprites degrade to the old flat
/// rectangles, never to a crash.</para>
/// </summary>
public sealed class LocoMpTheme
{
    public Color Panel = new Color(0.07f, 0.08f, 0.10f, 0.97f);
    public Color PanelLight = new Color(0.13f, 0.15f, 0.18f, 1f);
    public Color Accent = new Color(28f / 255f, 132f / 255f, 205f / 255f, 1f); // UIColors.BLUE
    public Color AccentHover = new Color(46f / 255f, 150f / 255f, 223f / 255f, 1f);
    public Color AccentPressed = new Color(23f / 255f, 111f / 255f, 176f / 255f, 1f);
    public Color AccentDisabled = new Color(0.25f, 0.28f, 0.32f, 1f);
    public Color Text = new Color(0.92f, 0.94f, 0.96f, 1f);
    public Color TextDim = new Color(0.55f, 0.60f, 0.65f, 1f);
    public Color TextDisabled = new Color(0.36f, 0.40f, 0.45f, 1f);
    public Color Field = new Color(0.16f, 0.18f, 0.21f, 1f);
    public Color Hairline = new Color(0.20f, 0.23f, 0.28f, 1f);
    public Color CoverDim = new Color(0f, 0f, 0f, 0.88f);

    // Semantic colours (D25): prefer the game's own values via AdoptDvPalette; these are fallbacks.
    public Color Danger = new Color(195f / 255f, 66f / 255f, 63f / 255f, 1f);
    public Color DangerDim = new Color(126f / 255f, 58f / 255f, 54f / 255f, 1f);
    public Color Warning = new Color(217f / 255f, 163f / 255f, 59f / 255f, 1f);
    public Color Success = new Color(88f / 255f, 166f / 255f, 92f / 255f, 1f);

    /// <summary>DV's TMP font, harvested from the cloned menu button; null = TMP default.</summary>
    public TMP_FontAsset? Font;

    public int Size = 22;
    public int TitleSize = 30;
    public int MetaSize = 16;
    public int SectionSize = 14;
    public float RowHeight = 38f;

    /// <summary>Procedural 9-slice: solid rounded rectangle (tinted via Image.color).</summary>
    public Sprite? RoundedFill { get; private set; }

    /// <summary>Procedural 9-slice: ~1.5 px rounded outline, transparent centre.</summary>
    public Sprite? RoundedBorder { get; private set; }

    /// <summary>Procedural dot (ping indicators, gate markers).</summary>
    public Sprite? Dot { get; private set; }

    private bool _assetsBuilt;
    private bool _paletteAdopted;

    /// <summary>Generate the sprite pair once. Safe to call repeatedly; any failure leaves the
    /// sprites null and the kit falls back to flat rectangles (degraded, never crashed).</summary>
    public void EnsureRuntimeAssets()
    {
        if (_assetsBuilt) return;
        _assetsBuilt = true;
        try
        {
            RoundedFill = MakeRounded(border: false);
            RoundedBorder = MakeRounded(border: true);
            Dot = MakeDot();
        }
        catch (Exception)
        {
            RoundedFill = null;
            RoundedBorder = null;
            Dot = null;
        }
    }

    /// <summary>Adopt DV's semantic palette (UIColors.RED/YELLOW/GREEN) so chips/errors match the
    /// game exactly. Isolated + try/caught: a missing/renamed UIColors on a future build keeps the
    /// fallback constants (supported-build doctrine — degrade, never crash).</summary>
    public void AdoptDvPalette()
    {
        if (_paletteAdopted) return;
        _paletteAdopted = true;
        try
        {
            ReadUiColors();
        }
        catch (Exception)
        {
            // Fallback constants stay — visually near-identical, just not harvested.
        }
    }

    private void ReadUiColors()
    {
        Danger = DV.UIFramework.UIColors.RED;
        Warning = DV.UIFramework.UIColors.YELLOW;
        Success = DV.UIFramework.UIColors.GREEN;
        DangerDim = Color.Lerp(Danger, Panel, 0.45f);
    }

    // ── procedural sprites ────────────────────────────────────────────────────────────────────

    private const int TexSize = 16;
    private const float CornerRadius = 4f;
    private const float BorderWidth = 1.6f;

    private static Sprite MakeRounded(bool border)
    {
        var tex = new Texture2D(TexSize, TexSize, TextureFormat.ARGB32, mipChain: false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        float half = TexSize / 2f;
        for (int y = 0; y < TexSize; y++)
        {
            for (int x = 0; x < TexSize; x++)
            {
                float d = RoundRectSdf(x + 0.5f - half, y + 0.5f - half, half - 1f, CornerRadius);
                float outer = Mathf.Clamp01(0.5f - d);
                float alpha = border ? outer - Mathf.Clamp01(0.5f - (d + BorderWidth)) : outer;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        var sprite = Sprite.Create(tex, new Rect(0, 0, TexSize, TexSize), new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f, extrude: 0, SpriteMeshType.FullRect,
            border: new Vector4(6f, 6f, 6f, 6f));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Sprite MakeDot()
    {
        const int size = 12;
        var tex = new Texture2D(size, size, TextureFormat.ARGB32, mipChain: false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
        float half = size / 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - half;
                float dy = y + 0.5f - half;
                float d = Mathf.Sqrt(dx * dx + dy * dy) - (half - 1.5f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
            }
        }
        tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    /// <summary>Signed distance to a rounded rectangle centred at origin (half-extent
    /// <paramref name="halfExtent"/> square, corner radius <paramref name="radius"/>).</summary>
    private static float RoundRectSdf(float px, float py, float halfExtent, float radius)
    {
        float qx = Mathf.Abs(px) - (halfExtent - radius);
        float qy = Mathf.Abs(py) - (halfExtent - radius);
        float ox = Mathf.Max(qx, 0f);
        float oy = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
    }
}
