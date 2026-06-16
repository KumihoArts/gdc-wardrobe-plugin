using UnityEngine;

namespace Kumiho.UI;

/// <summary>
/// Animated drawing wrappers. Each method handles its own hover/press state
/// via <see cref="KumihoAnim"/> and layers the existing six-state PNGs by
/// alpha to produce smooth transitions. Use these instead of the raw
/// <c>GUILayout.Button</c> / <c>GUILayout.Toggle</c> calls when you want
/// the polish.
///
/// You pass a stable string id per logical control so the animator can
/// track each independently. The id never appears on screen; it's a key
/// into the tween dictionary. Use the label, a logical name, or anything
/// unique within your plugin.
///
/// Pattern for writing your own: read hovered/pressed from the cursor,
/// hand them to <see cref="KumihoAnim.Hover"/> / <see cref="KumihoAnim.Press"/>,
/// then draw the idle background, draw the hover background on top with
/// alpha = the hover t-value, draw the press background on top with
/// alpha = the press t-value, then the label.
/// </summary>
public static class KumihoDraw
{
    /// <summary>
    /// Animated button. Returns true on the frame the mouse releases inside the rect.
    /// Pass <paramref name="isOn"/> for toggle-style buttons that show the on-state.
    /// </summary>
    public static bool Button(Rect rect, string label, string id, bool isOn = false)
    {
        var e = Event.current;
        bool hovered = rect.Contains(e.mousePosition);
        bool mouseDown = Input.GetMouseButton(0);
        bool pressed = hovered && mouseDown;

        // detect click on mouse-up inside the rect
        bool clicked = false;
        if (e.type == EventType.MouseUp && e.button == 0 && hovered)
        {
            clicked = true;
            e.Use();
        }

        float hT = KumihoAnim.Hover(id, hovered && !pressed);
        float pT = KumihoAnim.Press(id, pressed);

        // pick the state stack based on isOn
        var bgIdle  = isOn ? KumihoUI.BtnBgOnIdle  : KumihoUI.BtnBgIdle;
        var bgHover = isOn ? KumihoUI.BtnBgOnHover : KumihoUI.BtnBgHover;
        var bgAct   = isOn ? KumihoUI.BtnBgOnAct   : KumihoUI.BtnBgAct;

        if (e.type == EventType.Repaint)
        {
            DrawBg(rect, bgIdle, 1f);
            if (hT > 0.001f) DrawBg(rect, bgHover, hT);
            if (pT > 0.001f) DrawBg(rect, bgAct, pT);
        }

        GUI.Label(rect, label, KumihoUI.ButtonText);
        return clicked;
    }

    /// <summary>
    /// Animated toggle. Returns the new value (flipped on click).
    /// The toggle box is drawn at the left of the rect; the label fills the rest.
    /// </summary>
    public static bool Toggle(Rect rect, bool value, string label, string id)
    {
        const float Box = 24f;
        var boxRect = new Rect(rect.x, rect.y + (rect.height - Box) * 0.5f, Box, Box);
        var labelRect = new Rect(rect.x + Box + 4f, rect.y,
                                 rect.width - Box - 4f, rect.height);

        var e = Event.current;
        bool hovered = rect.Contains(e.mousePosition);
        bool mouseDown = Input.GetMouseButton(0);
        bool pressed = hovered && mouseDown;

        bool newValue = value;
        if (e.type == EventType.MouseUp && e.button == 0 && hovered)
        {
            newValue = !value;
            e.Use();
        }

        float hT = KumihoAnim.Hover(id, hovered && !pressed);
        float pT = KumihoAnim.Press(id, pressed);

        var bgIdle  = value ? KumihoUI.ToggleBgOnIdle  : KumihoUI.ToggleBgIdle;
        var bgHover = value ? KumihoUI.ToggleBgOnHover : KumihoUI.ToggleBgHover;
        var bgAct   = value ? KumihoUI.ToggleBgOnAct   : KumihoUI.ToggleBgAct;

        if (e.type == EventType.Repaint)
        {
            DrawBg(boxRect, bgIdle, 1f);
            if (hT > 0.001f) DrawBg(boxRect, bgHover, hT);
            if (pT > 0.001f) DrawBg(boxRect, bgAct, pT);
        }

        GUI.Label(labelRect, label, KumihoUI.Label);
        return newValue;
    }

    /// <summary>
    /// Animated switch with a true sliding handle. Returns the new value.
    ///
    /// Track is one of three textures depending on hover/press state,
    /// crossfaded for smoothness. Handle is a white texture drawn at a
    /// position that lerps from left (off) to right (on) using
    /// KumihoAnim.State, tinted from teal (off) to magenta (on) on the same
    /// curve. Hover and press states layer additional color shifts on top.
    /// </summary>
    public static bool AnimatedSwitch(Rect rect, bool value, string id)
    {
        const float HandleW = 26f;   // matches switch-handle.png width
        const float HandleH = 32f;   // matches switch-handle.png height
        const float SlideSpeed = 6f; // ~170ms slide, feels intentional for a switch

        var e = Event.current;
        bool hovered = rect.Contains(e.mousePosition);
        bool mouseDown = Input.GetMouseButton(0);
        bool pressed = hovered && mouseDown;

        bool newValue = value;
        if (e.type == EventType.MouseUp && e.button == 0 && hovered)
        {
            newValue = !value;
            e.Use();
        }

        float hT = KumihoAnim.Hover(id, hovered && !pressed);
        float pT = KumihoAnim.Press(id, pressed);
        float sT = KumihoAnim.State(id + "_state", value, SlideSpeed);

        if (e.type == EventType.Repaint)
        {
            // Track: crossfade idle -> hover -> press as the user interacts.
            DrawBg(rect, KumihoUI.SwitchTrackBg, 1f);
            if (hT > 0.001f) DrawBg(rect, KumihoUI.SwitchTrackHoverBg, hT);
            if (pT > 0.001f) DrawBg(rect, KumihoUI.SwitchTrackActBg, pT);

            // Handle position: left edge at sT=0, right edge at sT=1.
            float xMin = rect.x;
            float xMax = rect.x + rect.width - HandleW;
            float handleX = Mathf.Lerp(xMin, xMax, sT);
            float handleY = rect.y + (rect.height - HandleH) * 0.5f;
            var handleRect = new Rect(handleX, handleY, HandleW, HandleH);

            // Handle tint: teal at sT=0, magenta at sT=1. Hover and press
            // states pull toward the brighter / dimmer accent variants for
            // smooth visual feedback during interaction.
            Color cNormal = Color.Lerp(KumihoUI.Colors.Accent,   KumihoUI.Colors.Active,   sT);
            Color cHover  = Color.Lerp(KumihoUI.Colors.AccentHi, KumihoUI.Colors.ActiveHi, sT);
            Color cPress  = Color.Lerp(KumihoUI.Colors.AccentPr, KumihoUI.Colors.ActivePr, sT);
            Color tint = Color.Lerp(cNormal, cHover, hT);
            tint = Color.Lerp(tint, cPress, pT);

            if (KumihoUI.SwitchHandleTex != null)
            {
                var prev = GUI.color;
                GUI.color = tint;
                GUI.DrawTexture(handleRect, KumihoUI.SwitchHandleTex);
                GUI.color = prev;
            }
        }

        return newValue;
    }

    /// <summary>GUILayout overload for AnimatedSwitch. Reserves a 72x32 rect.</summary>
    public static bool AnimatedSwitch(bool value, string id, params GUILayoutOption[] options)
    {
        var rect = GUILayoutUtility.GetRect(72, 32, options);
        return AnimatedSwitch(rect, value, id);
    }

    /// <summary>
    /// Draw a GUIStyle background-only render at the given alpha. Respects 9-slice.
    /// </summary>
    public static void DrawBg(Rect rect, GUIStyle bgStyle, float alpha)
    {
        if (alpha <= 0f || bgStyle == null) return;
        var prev = GUI.color;
        GUI.color = new Color(prev.r, prev.g, prev.b, prev.a * alpha);
        bgStyle.Draw(rect, false, false, false, false);
        GUI.color = prev;
    }
}
