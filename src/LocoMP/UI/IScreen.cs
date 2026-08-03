using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// One LocoMP screen (M5.0 step 3). Built lazily on first push; the router toggles the built
/// GameObject on show/hide, so a screen keeps its widget state while buried in the stack.
/// </summary>
public interface IScreen
{
    /// <summary>The built root; null until <see cref="Build"/> has run.</summary>
    GameObject? Go { get; }

    void Build(RectTransform parent, WidgetKit kit);
    void OnShow();
    void OnHide();
}
