using System.Collections.Generic;
using UnityEngine;

namespace Kumiho.UI;

/// <summary>
/// Per-control hover and press tween state, keyed by string id.
///
/// Call <see cref="Hover"/> or <see cref="Press"/> each OnGUI pass for any
/// element you want to animate; pass a stable id (e.g. the button label or
/// a logical name). The returned float is the eased [0..1] value clamped
/// against frame dt, suitable for alpha crossfades, color lerps, or
/// position offsets.
///
/// Dt is auto-captured once per frame; you don't need to call any setup
/// method. Stalls (alt-tabbed, paused) are capped at 100ms so tweens don't
/// teleport when focus returns.
/// </summary>
public static class KumihoAnim
{
    /// <summary>Roughly 80ms idle-to-hover transition.</summary>
    public const float HoverSpeed = 12f;
    /// <summary>Roughly 40ms idle-to-press transition. Press feels snappier than hover.</summary>
    public const float PressSpeed = 24f;
    /// <summary>Roughly 130ms off-to-on transition. State changes feel intentional and not jittery.</summary>
    public const float StateSpeed = 8f;

    private static readonly Dictionary<string, float> _hoverT = new Dictionary<string, float>(64);
    private static readonly Dictionary<string, float> _pressT = new Dictionary<string, float>(64);
    private static readonly Dictionary<string, float> _stateT = new Dictionary<string, float>(64);

    private static int _lastFrame = -1;
    private static float _lastTime;
    private static float _dt;

    /// <summary>Smooth [0..1] hover value for the given id.</summary>
    public static float Hover(string id, bool hovered, float speed = HoverSpeed)
    {
        EnsureTicked();
        _hoverT.TryGetValue(id, out var t);
        t = Mathf.MoveTowards(t, hovered ? 1f : 0f, _dt * speed);
        _hoverT[id] = t;
        return t;
    }

    /// <summary>Smooth [0..1] press value for the given id.</summary>
    public static float Press(string id, bool pressed, float speed = PressSpeed)
    {
        EnsureTicked();
        _pressT.TryGetValue(id, out var t);
        t = Mathf.MoveTowards(t, pressed ? 1f : 0f, _dt * speed);
        _pressT[id] = t;
        return t;
    }

    /// <summary>Smooth [0..1] value tracking a binary state (off=0, on=1).
    /// Use for state-change transitions like switch on/off, toggle on/off,
    /// or fold/unfold of accordion sections.</summary>
    public static float State(string id, bool active, float speed = StateSpeed)
    {
        EnsureTicked();
        _stateT.TryGetValue(id, out var t);
        t = Mathf.MoveTowards(t, active ? 1f : 0f, _dt * speed);
        _stateT[id] = t;
        return t;
    }

    /// <summary>Read-only current value; doesn't advance the tween.</summary>
    public static float PeekHover(string id) => _hoverT.TryGetValue(id, out var t) ? t : 0f;
    public static float PeekPress(string id) => _pressT.TryGetValue(id, out var t) ? t : 0f;
    public static float PeekState(string id) => _stateT.TryGetValue(id, out var t) ? t : 0f;

    /// <summary>Forget all tracked values. Useful on scene reload.</summary>
    public static void Reset()
    {
        _hoverT.Clear();
        _pressT.Clear();
        _stateT.Clear();
        _lastFrame = -1;
    }

    private static void EnsureTicked()
    {
        int f = Time.frameCount;
        if (f == _lastFrame) return;
        _lastFrame = f;
        float now = Time.realtimeSinceStartup;
        _dt = _lastTime > 0f ? Mathf.Clamp(now - _lastTime, 0f, 0.1f) : 0f;
        _lastTime = now;
    }
}
