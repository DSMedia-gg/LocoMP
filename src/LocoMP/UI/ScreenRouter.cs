using System.Collections.Generic;
using UnityEngine;

namespace LocoMP.UI;

/// <summary>
/// The screen stack (M5.0 step 3): one screen visible at a time, push shows, pop returns to the
/// one beneath (hardware/ESC back = one Pop from the composition root). Screens build lazily
/// into the host RectTransform with the shared <see cref="WidgetKit"/>.
/// </summary>
public sealed class ScreenRouter
{
    private readonly Stack<IScreen> _stack = new();
    private readonly RectTransform _host;
    private readonly WidgetKit _kit;

    public ScreenRouter(RectTransform host, WidgetKit kit)
    {
        _host = host;
        _kit = kit;
    }

    public IScreen? Top => _stack.Count > 0 ? _stack.Peek() : null;
    public int Count => _stack.Count;

    public void Push(IScreen screen)
    {
        Top?.OnHide();
        if (Top is { Go: { } top }) top.SetActive(false);
        if (screen.Go == null) screen.Build(_host, _kit);
        _stack.Push(screen);
        if (screen.Go != null) screen.Go.SetActive(true);
        screen.OnShow();
    }

    public void Pop()
    {
        if (_stack.Count == 0) return;
        IScreen screen = _stack.Pop();
        screen.OnHide();
        if (screen.Go != null) screen.Go.SetActive(false);
        if (Top is { } next)
        {
            if (next.Go != null) next.Go.SetActive(true);
            next.OnShow();
        }
    }

    public void Clear()
    {
        while (_stack.Count > 0) Pop();
    }
}
