using System;
using System.Collections.Generic;
using AIChara;
using Kumiho.UI;
using UnityEngine;

// Constants for the footer height live next to the window so layout math
// reads naturally inline rather than as a flock of magic numbers.

namespace GDCplugin
{
    // Standalone IMGUI window styled with the KumihoUI skin pack. Independent
    // of the maker list filter: the filter hotkey and this window's hotkey
    // are separate, per GDC's preference.
    //
    // Layout deliberately differs from FavorHide's folder window so users can
    // tell the two plugins apart at a glance: FavorHide uses a left-attached
    // sidebar + folder tree, this uses a free-floating window with section
    // panels for Selected item / Material sliders / Blendshapes / Presets.
    internal static class SliderWindow
    {
        private const int WindowId = 0x6DC91A11; // arbitrary, just needs to be stable

        private static bool   _open;
        private static Rect   _rect;
        private static bool   _rectInitialised;
        private static Vector2 _scroll;

        // Throwaway state for placeholder sliders so dragging actually moves
        // the knob. Real sliders will bind to material properties / blendshape
        // weights once the metadata system is in.
        private static readonly Dictionary<string, float> _placeholderValues
            = new Dictionary<string, float>();

        // Exposed for the perf diagnostic so a per-second log line can say
        // whether the IMGUI draw loop was running while a stutter occurred.
        internal static bool IsOpen => _open;

        public static void Toggle()
        {
            _open = !_open;
            if (_open) EnsureRect();
        }

        public static void Draw()
        {
            if (!_open) return;
            EnsureRect();

            // Title text left empty; the GDC logo drawn in DrawContents is the
            // title-bar branding. KumihoUI.Window still styles the frame.
            _rect = GUI.Window(WindowId, _rect, DrawContents, GUIContent.none, KumihoUI.Window);

            EatInputInRect(_rect);

            if (Input.GetMouseButtonUp(0)) PersistRect();
        }

        private static void EnsureRect()
        {
            if (_rectInitialised) return;
            _rectInitialised = true;

            var w = GDCPlugin.SliderWindowW.Value;
            var h = GDCPlugin.SliderWindowH.Value;
            var x = GDCPlugin.SliderWindowX.Value;
            var y = GDCPlugin.SliderWindowY.Value;

            if (x < 0f) x = (Screen.width  - w) * 0.5f;
            if (y < 0f) y = (Screen.height - h) * 0.5f;

            _rect = new Rect(x, y, w, h);
        }

        private static void PersistRect()
        {
            GDCPlugin.SliderWindowX.Value = _rect.x;
            GDCPlugin.SliderWindowY.Value = _rect.y;
            GDCPlugin.SliderWindowW.Value = _rect.width;
            GDCPlugin.SliderWindowH.Value = _rect.height;
        }

        // Layout constants. Title bar across the top, footer bar across the
        // bottom, scrollable content fills whatever's between.
        private const float TitleH       = 28f;
        private const float FooterH      = 44f;
        // tabheader sprite is native 32px tall. Draw the strip at native height
        // so the 9-slice isn't squashed and the white corner outline stays crisp
        // (the 30px squash bilinear-blurred the corners into a wash).
        private const float TabH         = 32f;
        private const float ResizeGripPx = 26f;
        private const float MinWindowW   = 320f;
        private const float MinWindowH   = 280f;
        // Horizontal inset so content/scrollbar clear the window's painted
        // 9-slice frame (Window style border ~8px) instead of butting under it.
        private const float SidePad      = 14f;

        // Vertical scrollbar channel width (ScrollV is 7px) plus a little slack.
        // Subtracted from the content area to get the width that button rows
        // and thumbnail grids may actually fill. Sizing them to the full
        // _rect.width (the old "- 16f") overran the viewport, which both
        // spawned a horizontal scrollbar and grew the ExpandWidth section bars
        // so their inline Reset button clipped off the right edge.
        private const float ScrollbarW   = 12f;
        private static float ContentWidth =>
            Mathf.Max(0f, _rect.width - SidePad * 2f - ScrollbarW);

        // Shared height for a label+slider+value row. The slider reserves a
        // 14px rect by default (track centered ~7px down) while the labels
        // reserve their taller intrinsic font height, so their vertical
        // centers diverge and the value reads low against the track. Forcing
        // the same height on all three lands every center on rowH/2.
        private const float SliderRowH   = 22f;

        // Fallback height of the animated Snow/Rain effect preview when the
        // frame aspect can't be read. Normally the height is computed from the
        // sheet's true frame aspect (full width / aspect) so nothing stretches;
        // the min/max clamp keeps a near-square or ultra-wide cell sane.
        private const float EnvPreviewH    = 80f;
        private const float EnvPreviewMinH = 48f;
        private const float EnvPreviewMaxH = 240f;

        // Right-aligned numeric readout style for slider value columns, so the
        // decimals line up vertically instead of sitting ragged-left. Lazily
        // built off the kit's Label so it inherits font + color.
        private static GUIStyle _valueStyle;
        private static GUIStyle ValueStyle =>
            _valueStyle ??= new GUIStyle(KumihoUI.Label) { alignment = TextAnchor.MiddleRight };

        // Word-wrapping label for the narrow/stacked slider layout, so a long
        // blendshape name flows onto a second line instead of shoving the
        // slider off the right edge.
        private static GUIStyle _wrapLabelStyle;
        private static GUIStyle WrapLabelStyle =>
            _wrapLabelStyle ??= new GUIStyle(KumihoUI.Label) { wordWrap = true };

        // Resize drag state. Canonical IMGUI hotControl pattern: on MouseDown
        // inside the grip I claim a unique controlId via GUIUtility.hotControl.
        // Unity then routes subsequent MouseDrag/MouseUp events to that
        // control even when the cursor leaves the window's bounds, which is
        // the failure mode the older Input.mousePosition poll was working
        // around. _resizing mirrors hotControl == _resizeControlId so the
        // outer EatInputInRect guard can still see "we're mid-drag" without
        // recomputing hotControl identity every frame.
        private static int     _resizeControlId = -1;
        private static bool    _resizing;
        private static Vector2 _resizeStartMouse;  // window-local GUI pixels
        private static Vector2 _resizeStartSize;   // window w/h at drag start

        // Tabs split the editing surface into four panels so the long lists
        // of sliders don't all stack into one scrolling page. The Selected
        // item header stays visible above the tab strip as global context.
        private enum Tab { Items, Materials, Textures, Shapes }
        private static Tab _currentTab = Tab.Items;

        // Branding logo shown in the title bar's top-left corner. Embedded
        // PNG (not part of the KumihoUI bundle), loaded + cached on first draw.
        private const string LogoResource = "GDCplugin.Resources.GDCLogo.png";

        private static void DrawContents(int id)
        {
            // GDC logo, top-left of the title bar. Repaint-only so it never
            // eats events from the drag area underneath it. Height fixed to
            // the title bar, width derived from the source aspect so it never
            // distorts.
            if (Event.current.type == EventType.Repaint)
            {
                // Title-bar background strip so the draggable header reads as a
                // distinct bar. Without a fill it was bare window background with
                // a floating logo + close button, and testers couldn't tell where
                // to grab the window. Surface fill + an accent line on the bottom
                // edge frame it like the footer.
                var prev  = GUI.color;
                // SurfaceHi (not SurfaceLow): the darker fill killed the logo's
                // contrast. This is light enough that the gold logo reads while
                // still clearly a distinct bar (the accent bottom line frames it).
                GUI.color = KumihoUI.Colors.SurfaceHi;
                GUI.DrawTexture(new Rect(0f, 0f, _rect.width, TitleH), Texture2D.whiteTexture);
                GUI.color = KumihoUI.Colors.Accent;
                GUI.DrawTexture(new Rect(0f, TitleH - 1f, _rect.width, 1f), Texture2D.whiteTexture);
                // Three grip dashes left of the close button hint "drag me".
                // Block (y 7..17) sits a touch high in the bar so it reads level
                // with the close glyph rather than low.
                GUI.color = KumihoUI.Colors.TextDim;
                for (var g = 0; g < 3; g++)
                    GUI.DrawTexture(new Rect(_rect.width - 44f, 7f + g * 4f, 10f, 2f), Texture2D.whiteTexture);
                GUI.color = prev;

                var logo = EmbeddedTexture.Load(LogoResource);
                if (logo != null && logo.height > 0)
                {
                    // 20px logo sitting high in the 28px bar (1px top margin) so it
                    // reads level with the close glyph instead of low.
                    const float logoH = 20f;
                    var logoW = logoH * (logo.width / (float)logo.height);
                    GUI.DrawTexture(new Rect(8f, 1f, logoW, logoH), logo, ScaleMode.ScaleToFit, alphaBlend: true);
                }
            }

            // Close button sits in the top-right inside the title bar area,
            // hugging the right edge (4px margin).
            var closeRect = new Rect(_rect.width - 28f, 4f, 24f, 24f);
            if (GUI.Button(closeRect, GUIContent.none, KumihoUI.CloseButton))
            {
                _open = false;
            }

            // Main content area between title and footer.
            var contentRect = new Rect(SidePad, TitleH, _rect.width - SidePad * 2f, _rect.height - TitleH - FooterH);
            GUILayout.BeginArea(contentRect);

            // Always-visible selection header (character + slot + item name).
            // Lives outside the scroll view so it stays as context regardless
            // of which tab is active or how far the user has scrolled.
            DrawSelectionSection();

            // Divider above the tab strip + below it brackets the tabs into a
            // visible bar, matching the footer's accent-line treatment.
            DrawHDivider();
            DrawTabStrip();
            DrawHDivider();

            _scroll = KumihoUI.BeginScrollView(_scroll);

            // Route to the active tab's content. Each tab fills the scroll
            // area independently. Presets row will return per-tab alongside
            // the real preset system in v0.5.
            switch (_currentTab)
            {
                case Tab.Items:     DrawItemShapesSection();      break;
                case Tab.Materials: DrawMaterialSection();        break;
                case Tab.Textures:  DrawTexturesSection();        break;
                case Tab.Shapes:    DrawCharacterShapesSection(); break;
            }

            KumihoUI.EndScrollView();
            GUILayout.EndArea();

            // Resize input runs BEFORE the footer so the grip claims
            // MouseDown before the Reset button does. Repaint runs AFTER
            // the footer so the grip lines aren't hidden under it.
            HandleResizeInput();

            DrawFooter();

            DrawResizeGrip();

            // Title-bar drag area. Kept last so it doesn't steal events from
            // the close button or footer controls. Width stops short of the
            // close button's new tighter position.
            GUI.DragWindow(new Rect(0, 0, _rect.width - 30f, 28f));
        }

        // Thin full-width accent divider drawn in the current GUILayout flow.
        // Reserves 5px of vertical space and paints a 1px accent line centered
        // in it, so it reads like the footer's top divider.
        private static void DrawHDivider()
        {
            var r = GUILayoutUtility.GetRect(0f, 5f, GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;
            var prev  = GUI.color;
            GUI.color = KumihoUI.Colors.Accent;
            GUI.DrawTexture(new Rect(r.x, r.y + 2f, r.width, 1f), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        // Resize grip is a 26x26 rect anchored to the window's bottom-right
        // corner, drawn as a stack of diagonal accent lines. Split into two
        // helpers so input can be claimed BEFORE the footer renders and the
        // grip itself can be drawn AFTER the footer, so it stays visible
        // on top.
        private static Rect ResizeGripRect()
        {
            return new Rect(_rect.width - ResizeGripPx - 2f,
                            _rect.height - ResizeGripPx - 2f,
                            ResizeGripPx, ResizeGripPx);
        }

        // Full IMGUI hotControl pattern. MouseDown claims hotControl, then
        // Unity routes every following MouseDrag/MouseUp to that control
        // regardless of cursor position, so dragging outside the window
        // keeps the resize alive. e.Use() on each handled event prevents
        // the footer Reset button (which sits underneath the grip) from
        // also reacting.
        private static void HandleResizeInput()
        {
            _resizeControlId = GUIUtility.GetControlID(FocusType.Passive);
            var e    = Event.current;
            var grip = ResizeGripRect();

            switch (e.GetTypeForControl(_resizeControlId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && grip.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = _resizeControlId;
                        _resizing             = true;
                        _resizeStartMouse     = e.mousePosition;
                        _resizeStartSize      = new Vector2(_rect.width, _rect.height);
                        GDCPlugin.Logger?.LogDebug($"[resize] begin mouse={e.mousePosition} grip={grip}");
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == _resizeControlId)
                    {
                        var dx = e.mousePosition.x - _resizeStartMouse.x;
                        var dy = e.mousePosition.y - _resizeStartMouse.y;
                        _rect.width  = Mathf.Clamp(_resizeStartSize.x + dx, MinWindowW, Screen.width);
                        _rect.height = Mathf.Clamp(_resizeStartSize.y + dy, MinWindowH, Screen.height);
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == _resizeControlId)
                    {
                        GUIUtility.hotControl = 0;
                        _resizing             = false;
                        PersistRect();
                        GDCPlugin.Logger?.LogDebug($"[resize] end size=({_rect.width:0},{_rect.height:0})");
                        e.Use();
                    }
                    break;
            }
        }

        // Repaint-only pass. Runs after the footer renders so the grip
        // pixels sit on top of everything else in the window.
        private static void DrawResizeGrip()
        {
            if (Event.current.type != EventType.Repaint) return;

            var grip = ResizeGripRect();
            var prev = GUI.color;

            // No backplate: the opaque dark square read as an ugly black box
            // behind the grip. Just the diagonal accent stripes on the bare
            // window background.

            // Three thick diagonal accent stripes sloping "/" from
            // bottom-left toward top-right of the grip. Each stripe is 2px
            // wide so it survives at the resolutions players actually run
            // the game at. The longest stripe never exits the grip rect.
            GUI.color = KumihoUI.Colors.Accent;
            var max = grip.width - 4f;
            for (var i = 0; i < 3; i++)
            {
                var len = max - i * 7f;
                if (len <= 0f) break;
                for (var t = 0f; t < len; t += 1f)
                {
                    var x = grip.xMin + 2f + i * 7f + t;
                    var y = grip.yMax - 2f - t;
                    // 2x2 pixel block per step gives a clean thick stripe.
                    GUI.DrawTexture(new Rect(x, y, 2f, 2f), Texture2D.whiteTexture);
                }
            }
            GUI.color = prev;
        }


        // Persistent control strip pinned to the bottom. Always reachable
        // regardless of scroll position. v1 holds the GDC filter switch,
        // a gear icon for settings, and a Reset button for clearing the
        // current session's runtime overrides.
        private static void DrawFooter()
        {
            var footerRect = new Rect(0, _rect.height - FooterH, _rect.width, FooterH);

            // Thin accent divider along the top edge so the footer reads as
            // its own region rather than bleeding into the scroll content.
            if (Event.current.type == EventType.Repaint)
            {
                var divider = new Rect(0, footerRect.y, _rect.width, 1f);
                var prev    = GUI.color;
                GUI.color   = KumihoUI.Colors.Accent;
                GUI.DrawTexture(divider, Texture2D.whiteTexture);
                GUI.color   = prev;
            }

            GUILayout.BeginArea(footerRect);
            GUILayout.BeginVertical();
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f);

            // GDC filter animated switch. Reads and writes Plugin.FilterEnabled
            // directly so the in-window switch and the Ctrl+G hotkey stay
            // in sync at all times.
            var oldFilter = GDCPlugin.FilterEnabled.Value;
            var newFilter = KumihoDraw.AnimatedSwitch(oldFilter, "footer-gdc-filter");
            if (newFilter != oldFilter)
            {
                GDCPlugin.FilterEnabled.Value = newFilter;
                FilterHooks.RequestListRefresh();
            }
            GUILayout.Space(8f);
            GUILayout.Label("GDC only", KumihoUI.LabelMuted, GUILayout.ExpandWidth(false));

            GUILayout.Space(16f);

            // Second, independent filter: items whose manifest carries the
            // plugin marker (presets / def_tex). Same live state as the
            // Ctrl+H hotkey.
            var oldCompat = GDCPlugin.CompatFilterEnabled.Value;
            var newCompat = KumihoDraw.AnimatedSwitch(oldCompat, "footer-compat-filter");
            if (newCompat != oldCompat)
            {
                GDCPlugin.CompatFilterEnabled.Value = newCompat;
                FilterHooks.RequestListRefresh();
            }
            GUILayout.Space(8f);
            GUILayout.Label("Compatible", KumihoUI.LabelMuted, GUILayout.ExpandWidth(false));

            GUILayout.FlexibleSpace();

            // Right padding leaves clearance for the resize corner grip.
            // Per-tab Reset buttons now live inside each tab's section
            // header so the user always knows what's being cleared. The
            // global "Reset everything" button is gone — it was too easy
            // to lose unrelated edits with one accidental click.
            GUILayout.Space(ResizeGripPx + 8f);

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        // Per-tab Reset helpers. Each clears just the overrides for its
        // own data type, so the user can roll back item shapes without
        // losing their material slider work or their texture swaps.
        private static void ResetItemShapesTab()
        {
            BlendshapeBinding.ClearOverrides();
            GDCPlugin.Logger?.LogInfo("[reset] Cleared item shape overrides.");
        }

        private static void ResetMaterialsTab()
        {
            MaterialBinding.ClearOverrides();
            GDCPlugin.Logger?.LogInfo("[reset] Cleared material slider overrides.");
        }

        private static void ResetTexturesTab()
        {
            TextureBinding.ClearOverrides();
            if (_lastSelection.HasValue)
            {
                PresetBinding.Reset(_lastSelection.Value);
                ForceRediscover();
            }
            GDCPlugin.Logger?.LogInfo("[reset] Cleared texture swaps + presets.");
        }

        // Section bar height. Tall enough to seat the inline Reset button
        // (22px) with breathing room inside the Box fill.
        private const float SectionBarH = 28f;

        // Title style for the section bar: accent bold, vertically centered,
        // left-padded inside the Box fill. Built off LabelSection so it tracks
        // the kit's accent color and bold font.
        private static GUIStyle _sectionTitleStyle;
        private static GUIStyle SectionTitleStyle =>
            _sectionTitleStyle ??= new GUIStyle(KumihoUI.LabelSection)
            {
                alignment = TextAnchor.MiddleLeft,
                padding   = new RectOffset(0, 0, 0, 0),
            };

        // Section header with an optional inline "Reset" button on the right.
        // Draws a Box-sprite bar (5px 9-slice, tiles cleanly at any width) so
        // the fill no longer smears the way the stretched TabHeader tab sprite
        // did. Title text + Reset are overlaid on top of the bar.
        private static void SectionWithReset(string label, System.Action onReset)
        {
            GUILayout.Space(6f);
            var rect = GUILayoutUtility.GetRect(0f, SectionBarH, GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
                KumihoUI.Box.Draw(rect, false, false, false, false);

            const float btnW = 70f;
            // ToolbarButton's native sprite is 32x16. Its fixedHeight is now
            // 16 (native) so the 9-slice stays 1:1 vertical and the white
            // outline doesn't smear into a wash.
            var btnH = KumihoUI.ToolbarButton.fixedHeight;
            var hasReset = onReset != null;

            var titleRect = new Rect(rect.x + 10f, rect.y,
                rect.width - 20f - (hasReset ? btnW + 8f : 0f), rect.height);
            GUI.Label(titleRect, label, SectionTitleStyle);

            if (hasReset)
            {
                // Snap to whole pixels. A fractional x/y makes bilinear smear
                // the sprite's 1px white outline into the "wash" Sly keeps
                // seeing; the 9-slice border tweak alone didn't fix it because
                // the cause is sub-pixel position, not the stretch.
                var btnRect = new Rect(
                    Mathf.Round(rect.xMax - btnW - 6f),
                    Mathf.Round(rect.y + (rect.height - btnH) * 0.5f),
                    btnW, btnH);
                if (GUI.Button(btnRect, "Reset", KumihoUI.ToolbarButton))
                    onReset();
            }
        }

        // Section header without a reset target. Same Box bar as
        // SectionWithReset, title only.
        private static void Section(string label)
        {
            SectionWithReset(label, null);
        }

        // Tab strip drawn below the selection header. Uses the bundled
        // TabSmall style so the on-state shows the teal fill GDC's design
        // marks "selected" with.
        private static void DrawTabStrip()
        {
            GUILayout.BeginHorizontal();
            DrawTab(Tab.Items,     "Blendshapes");
            DrawTab(Tab.Materials, "Sliders");
            DrawTab(Tab.Textures,  "Presets");
            DrawTab(Tab.Shapes,    "Clothing Stack");
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
        }

        private static void DrawTab(Tab tab, string label)
        {
            var was = _currentTab == tab;
            // Render with empty content so we get just the background, then
            // overlay our own outlined label on top. Tab backgrounds are
            // colored (yellow/teal) so the default text doesn't always have
            // enough contrast; the outline guarantees legibility.
            // TabHeader (64x32 texture, designed for 30px-tall tabs) not
            // TabSmall (64x16): drawing the 16px-tall TabSmall at TabH stretches
            // it ~2x vertically and smears its accent into the "stretch wash".
            // TabHeader renders near-native height, matching KumihoUITestPlugin.
            var now = GUILayout.Toggle(was, GUIContent.none, KumihoUI.TabHeader,
                GUILayout.ExpandWidth(true), GUILayout.Height(TabH));
            if (now && !was) _currentTab = tab;

            if (Event.current.type == EventType.Repaint)
            {
                var rect = GUILayoutUtility.GetLastRect();
                DrawOutlinedLabel(rect, label, KumihoUI.Colors.Text, Color.black, 1);
            }
        }

        // BepisPlugins-style outline: render the text at each offset around
        // the target rect with the outline color, then once in the center
        // with the foreground color. Cached style instances would be ideal
        // but a per-call alloc here is cheap relative to the 4-9 GUI.Label
        // calls we do anyway.
        private static GUIStyle _outlineLabelStyle;

        private static void DrawOutlinedLabel(Rect rect, string text, Color textColor, Color outlineColor, int thickness)
        {
            if (_outlineLabelStyle == null)
            {
                _outlineLabelStyle = new GUIStyle
                {
                    alignment = TextAnchor.MiddleCenter,
                    font      = KumihoUI.Mono ?? KumihoUI.Regular,
                    fontSize  = 13,
                    // Small top padding pulls the baseline up a touch so
                    // Mono's descender quirk doesn't crowd the bottom edge.
                    padding   = new RectOffset(0, 0, 0, 2),
                };
            }
            var style = _outlineLabelStyle;

            // Shrink the font until the label fits inside its tab button, so a
            // long label ("Blendshapes") doesn't spill past the button bounds
            // when the window is narrowed. CalcSize does a full text layout and
            // the loop ran it up to 5x PER TAB PER OnGUI PASS; with 4 tabs and
            // several passes a frame that was the bulk of the window's CPU + the
            // text-mesh garbage (it ran every frame even when nothing changed).
            // Cache the fitted size + GUIContent per label and only re-fit when
            // the tab width actually changes.
            const int baseSize = 13;
            const int minSize  = 8;

            if (!_labelContentCache.TryGetValue(text, out var content))
            {
                content = new GUIContent(text);
                _labelContentCache[text] = content;
            }

            var widthKey = Mathf.Round(rect.width);
            if (!_labelFit.TryGetValue(text, out var fit) || fit.Width != widthKey)
            {
                style.fontSize = baseSize;
                var avail = rect.width - 4f;
                while (style.fontSize > minSize && style.CalcSize(content).x > avail)
                    style.fontSize--;
                fit = new LabelFit { Width = widthKey, Size = style.fontSize };
                _labelFit[text] = fit;
            }
            style.fontSize = fit.Size;

            style.normal.textColor = outlineColor;

            // Draw the outline pass at every offset in the (thickness x
            // thickness) neighborhood, skipping the center which gets the
            // real text on top.
            for (var dy = -thickness; dy <= thickness; dy++)
            for (var dx = -thickness; dx <= thickness; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var r = new Rect(rect.x + dx, rect.y + dy, rect.width, rect.height);
                GUI.Label(r, content, style);
            }

            style.normal.textColor = textColor;
            GUI.Label(rect, content, style);
        }

        private struct LabelFit { public float Width; public int Size; }
        private static readonly Dictionary<string, GUIContent> _labelContentCache
            = new Dictionary<string, GUIContent>();
        private static readonly Dictionary<string, LabelFit> _labelFit
            = new Dictionary<string, LabelFit>();

        // Slot labels match HS2's clothing kind order. Helps the user confirm
        // which item the sliders below are driving.
        private static readonly string[] _slotNames = {
            "Top", "Bottom", "Bra", "Panties", "Gloves", "Pantyhose", "Socks", "Shoes"
        };

        // Cached binding lists, rebuilt only when the selection changes.
        // Discovery walks SkinnedMeshRenderers / Materials under both the
        // clothing and the body. Cheap but not free, so per-frame is wasteful.
        private static SelectionTracker.Selection? _lastSelection;
        private static BlendshapeBinding.DiscoveryResult _discovery         = new BlendshapeBinding.DiscoveryResult();
        private static MaterialBinding.DiscoveryResult   _materialDiscovery = new MaterialBinding.DiscoveryResult();
        private static TextureBinding.DiscoveryResult    _textureDiscovery  = new TextureBinding.DiscoveryResult();
        // Index into _textureDiscovery.Slots of the currently selected target
        // slot. -1 means nothing's picked; clicking a thumbnail then is a
        // no-op (user needs to pick a slot first).
        private static int _selectedSlotIdx = -1;

        // The MainTex bound when this item was selected, captured once so the
        // grid can always offer "back to original" as a stable tile. Without
        // it, the grid only ever showed the *currently* bound texture, so the
        // moment the user swapped to a Def_Tex alternate the original tile
        // vanished and the grid reflowed (the "rightmost thumbnail disappears"
        // report). Reset on selection change, recaptured on first grid draw.
        private static Texture2D _originalMainTex;
        private static bool      _originalMainTexCaptured;

        // Accumulator of every MainTex alternate seen since this item was
        // selected, keyed by variant name. Discovery is intermittent (the
        // def_tex alternates sit in a sibling bundle Sideloader pages in
        // lazily), so a given rediscover can return fewer variants than a
        // prior one and the grid would blink tiles out. Merging into this set
        // and rendering from it means a tile, once seen, stays. Reset on
        // selection change.
        private static readonly List<TextureBinding.Variant> _mainTexSeen = new List<TextureBinding.Variant>();

        private static void RefreshBindingsIfNeeded()
        {
            var current = SelectionTracker.Current;
            var changed = (_lastSelection == null) != (current == null)
                          || (current != null && (_lastSelection == null || !current.Value.Matches(_lastSelection.Value)));
            if (!changed) return;

            _lastSelection   = current;
            _selectedSlotIdx = -1; // reset picker on selection change
            _originalMainTex          = null;
            _originalMainTexCaptured  = false;
            _mainTexSeen.Clear();
            ForceRediscover();
        }

        // Re-runs discovery without the selection-changed gate. Called by
        // the preset buttons because swapping a material reference changes
        // the available shader properties + texture slots, but the
        // SelectionTracker.Selection itself (character, slot, item GO) is
        // unchanged, so the normal gate would skip the refresh.
        internal static void ForceRediscover()
        {
            if (_lastSelection.HasValue)
            {
                _discovery         = BlendshapeBinding.DiscoverAll(_lastSelection.Value);
                _materialDiscovery = MaterialBinding.Discover(_lastSelection.Value);
                _textureDiscovery  = TextureBinding.Discover(_lastSelection.Value);
            }
            else
            {
                _discovery         = new BlendshapeBinding.DiscoveryResult();
                _materialDiscovery = new MaterialBinding.DiscoveryResult();
                _textureDiscovery  = new TextureBinding.DiscoveryResult();
            }
        }

        private static void DrawSelectionSection()
        {
            Section("Selected item");
            RefreshBindingsIfNeeded();

            var inStudio = KKAPI.Studio.StudioAPI.InsideStudio;

            if (_lastSelection == null)
            {
                GUILayout.Label(inStudio
                    ? "Select a character in Studio."
                    : "Open Maker and select a clothing slot.", KumihoUI.Label);
                return;
            }

            var sel = _lastSelection.Value;

            // Studio has no per-slot canvas tabs, so the user picks the slot
            // here. Maker drives the slot from the active CvsC_Clothes instead.
            if (inStudio) DrawStudioSlotPicker(sel);

            var slot = sel.SlotNo >= 0 && sel.SlotNo < _slotNames.Length
                ? _slotNames[sel.SlotNo]
                : $"Slot {sel.SlotNo}";
            var charaName = sel.Character != null ? sel.Character.fileParam?.fullname ?? "(unnamed)" : "(no character)";
            var itemName  = string.IsNullOrEmpty(_discovery.ItemMeshName) ? "(empty)" : _discovery.ItemMeshName;
            GUILayout.Label($"{charaName} - {slot}", KumihoUI.Label);
            GUILayout.Label($"Item: {itemName}", KumihoUI.Label);
        }

        // Reused buffers for the Studio slot dropdown so a per-frame draw
        // doesn't allocate. _studioSlotMap[i] is the real objClothes slot index
        // behind dropdown row i (the dropdown lists only occupied slots).
        private static readonly List<int>    _studioSlotMap    = new List<int>();
        private static readonly List<string> _studioSlotLabels = new List<string>();
        // Cached label array handed to the dropdown + the (char, occupancy)
        // signature it was built for. Rebuilt only when that signature changes,
        // so the per-OnGUI draw doesn't allocate a fresh string[] each pass.
        private static string[]    _studioSlotLabelsArr = new string[0];
        private static ChaControl? _studioPickerChar;
        private static int         _studioOccMask = -1;

        // Dropdown of the selected Studio character's occupied clothing slots.
        // Writes the pick to SelectionTracker.StudioSlot, which the Studio
        // selection path reads next frame, triggering a rediscover for that slot.
        private static void DrawStudioSlotPicker(in SelectionTracker.Selection sel)
        {
            var cha = sel.Character;
            if (cha?.objClothes == null) return;

            // Occupied-slot bitmask (cheap, no alloc). Rebuild the map/labels
            // only when the character or its occupancy actually changes.
            var n    = Mathf.Min(cha.objClothes.Length, _slotNames.Length);
            var mask = 0;
            for (var s = 0; s < n; s++)
                if (cha.objClothes[s] != null) mask |= 1 << s;

            if (!ReferenceEquals(cha, _studioPickerChar) || mask != _studioOccMask)
            {
                _studioPickerChar = cha;
                _studioOccMask    = mask;
                _studioSlotMap.Clear();
                _studioSlotLabels.Clear();
                for (var s = 0; s < n; s++)
                {
                    if ((mask & (1 << s)) == 0) continue;
                    _studioSlotMap.Add(s);
                    _studioSlotLabels.Add(_slotNames[s]);
                }
                _studioSlotLabelsArr = _studioSlotLabels.ToArray();
            }

            if (_studioSlotMap.Count == 0)
            {
                GUILayout.Label("This character has no clothing equipped.", KumihoUI.Label);
                return;
            }

            var cur = _studioSlotMap.IndexOf(SelectionTracker.StudioSlot);
            if (cur < 0)
            {
                // Picked slot no longer occupied (item removed): fall back to
                // the first occupied slot so the window keeps showing something.
                cur = 0;
                SelectionTracker.StudioSlot = _studioSlotMap[0];
            }

            GUILayout.BeginHorizontal();
            // Height matches the dropdown field (24px) so the MiddleLeft label
            // text centers on the same line as the dropdown's text instead of
            // sitting higher (the row top-aligns elements of differing height).
            GUILayout.Label("Slot:", KumihoUI.Label, GUILayout.Width(40f), GUILayout.Height(24f));
            var next = KumihoUI.Dropdown(cur, _studioSlotLabelsArr,
                "gdc-studio-slot", GUILayout.Width(160f));
            GUILayout.EndHorizontal();
            // Bound-check: if occupancy shrank while the popup was open, the
            // committed index can exceed the rebuilt map. Ignore stale picks.
            if (next != cur && next >= 0 && next < _studioSlotMap.Count)
                SelectionTracker.StudioSlot = _studioSlotMap[next];
        }

        private static void DrawMaterialSection()
        {
            SectionWithReset("Material sliders", ResetMaterialsTab);

            if (_lastSelection == null)
            {
                GUILayout.Label("Nothing selected.", KumihoUI.Label);
                return;
            }

            // Defensive prune for destroyed materials/renderers, same as
            // PruneDead does for blendshapes. A clothing item swap can leave
            // dangling references mid-frame.
            for (var i = _materialDiscovery.Floats.Count - 1; i >= 0; i--)
            {
                if (!_materialDiscovery.Floats[i].IsAlive) _materialDiscovery.Floats.RemoveAt(i);
            }

            if (_materialDiscovery.Floats.Count == 0)
            {
                GUILayout.Label("No editable material properties on this item.", KumihoUI.Label);
                return;
            }

            // Group into Material / Snow / Rain. Snow + Rain are the shader's
            // integrated environmental controls; each gets its own subheader so
            // they read as a distinct block rather than blending into the
            // material sliders. A group is skipped entirely when the active
            // item's shader exposes none of its floats.
            DrawFloatGroup(MaterialBinding.FloatCategory.Material, null);
            DrawFloatGroup(MaterialBinding.FloatCategory.Snow,     "Snow");
            DrawFloatGroup(MaterialBinding.FloatCategory.Rain,     "Rain");
        }

        // Draws every float in one category, sorted by label for stable order,
        // under an optional subheader. The Material group has no subheader (the
        // "Material sliders" section bar already labels it); Snow / Rain do.
        // Reused across the three DrawFloatGroup calls per pass (each call fills,
        // sorts, and consumes it fully before returning), so the grouping no
        // longer allocates a fresh List + sort delegate every OnGUI pass. The
        // comparison is a cached static delegate for the same reason.
        private static readonly List<MaterialBinding.Binding> _floatGroupBuf
            = new List<MaterialBinding.Binding>();
        private static readonly Comparison<MaterialBinding.Binding> FloatLabelComparison
            = (a, c) => string.CompareOrdinal(a.Label, c.Label);

        private static void DrawFloatGroup(MaterialBinding.FloatCategory category, string header)
        {
            _floatGroupBuf.Clear();
            foreach (var b in _materialDiscovery.Floats)
                if (b.Category == category) _floatGroupBuf.Add(b);
            if (_floatGroupBuf.Count == 0) return;
            _floatGroupBuf.Sort(FloatLabelComparison);

            if (header != null)
            {
                // Divider + gap above each environmental subheader so Snow and
                // Rain read as distinct blocks instead of one long list.
                GUILayout.Space(6f);
                DrawHDivider();
                GUILayout.Space(2f);
                GUILayout.Label(header, KumihoUI.LabelSection);

                // Animated effect preview above the group's sliders. Only
                // reserves space when the sheet art exists, so groups without
                // art (or before Sly paints them) show just the sliders. Height
                // is derived from the frame's true aspect (full width, height =
                // width / aspect) so frames never stretch, whatever cell shape
                // the art uses. Clamped so a near-square cell can't blow the
                // preview up to a huge block.
                if (EnvPreview.Has(header))
                {
                    var w  = ContentWidth;
                    var ph = EnvPreviewH;
                    if (EnvPreview.TryGetFrameAspect(header, out var aspect) && aspect > 0.01f)
                        ph = Mathf.Clamp(w / aspect, EnvPreviewMinH, EnvPreviewMaxH);

                    var pr = GUILayoutUtility.GetRect(w, ph,
                        GUILayout.ExpandWidth(false), GUILayout.Height(ph));
                    if (Event.current.type == EventType.Repaint)
                        DrawRectOutline(pr, KumihoUI.Colors.Accent, 1f);
                    var inner = new Rect(pr.x + 1f, pr.y + 1f, pr.width - 2f, pr.height - 2f);
                    EnvPreview.Draw(inner, header);
                    GUILayout.Space(4f);
                }
            }

            foreach (var b in _floatGroupBuf) LiveMaterialSlider(b, GroupLabel(b));
        }

        // Strips the family prefix off an environmental slider so the row reads
        // "CoverageControl" under the Snow subheader instead of repeating
        // "SnowCoverageControl". Material sliders keep their full property name.
        private static string GroupLabel(MaterialBinding.Binding b)
        {
            switch (b.Category)
            {
                case MaterialBinding.FloatCategory.Snow: return StripPrefix(b.Label, "Snow");
                case MaterialBinding.FloatCategory.Rain: return StripPrefix(b.Label, "GDCRain");
                default: return b.Label;
            }
        }

        private static string StripPrefix(string s, string prefix)
        {
            return s != null && s.Length > prefix.Length
                   && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? s.Substring(prefix.Length)
                : s;
        }

        // Same shape as LiveBlendshapeSlider, just bound to a material float
        // property. Ranges come from MaterialEditor's XML metadata so the
        // slider's 0..N matches what other shader-editing tools expect.
        private static void LiveMaterialSlider(MaterialBinding.Binding b, string label)
        {
            var current = b.Get();

            GUILayout.BeginHorizontal(GUILayout.Height(SliderRowH));
            GUILayout.Label(label, KumihoUI.Label, GUILayout.Width(180), GUILayout.Height(SliderRowH));
            var next = KumihoUI.HorizontalSliderWithFill(current, b.Min, b.Max, GUILayout.Height(SliderRowH));
            GUILayout.Label(next.ToString("0.00"), ValueStyle, GUILayout.Width(46), GUILayout.Height(SliderRowH));

            // Per-slider Reset: restores just this property to its pre-edit
            // value. Disabled (greyed) until the slider's been moved, so it
            // reads as "nothing to undo here" rather than a dead button.
            var prevEnabled = GUI.enabled;
            if (!b.IsOverridden) GUI.enabled = false;
            if (GUILayout.Button(new GUIContent("R", "Reset to original"),
                    MiniResetStyle, GUILayout.Width(MiniResetW),
                    GUILayout.Height(KumihoUI.ToolbarButton.fixedHeight)))
            {
                b.ResetToOriginal();
            }
            GUI.enabled = prevEnabled;

            GUILayout.EndHorizontal();

            if (!Mathf.Approximately(next, current))
            {
                b.Set(next);
            }
        }

        // Compact one-glyph button for the per-slider Reset. Cloned off
        // ToolbarButton (so it keeps the kit's chrome + the wash-free 9-slice)
        // but with zero padding so a single "R" fits in a ~22px square.
        private const float MiniResetW = 22f;
        private static GUIStyle _miniResetStyle;
        private static GUIStyle MiniResetStyle =>
            _miniResetStyle ??= new GUIStyle(KumihoUI.ToolbarButton)
            {
                padding   = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                fontSize  = 10,
            };

        private static void DrawItemShapesSection()
        {
            if (_lastSelection == null)
            {
                Section("Item shapes");
                GUILayout.Label("Nothing selected.", KumihoUI.Label);
                return;
            }

            SectionWithReset("Item shapes", ResetItemShapesTab);
            PruneDead(_discovery.ItemShapes);
            if (_discovery.ItemShapes.Count == 0)
            {
                GUILayout.Label("This item has no item-level shapes.", KumihoUI.Label);
            }
            else
            {
                foreach (var b in _discovery.ItemShapes) LiveBlendshapeSlider(b);
            }
        }

        // Shapes tab: clothing-to-accessory layering. Spawns a body-skinned
        // copy of the currently selected slot's item (LayerBinding, copyWeights:1
        // so it follows the body weightpaint), held outside objClothes. The
        // original slot then frees up for another piece: the user changes the
        // slot to item Y in the normal Maker list and the layered copy stays on
        // top. Persistence + re-mount live in GDCharaController.
        private static void DrawCharacterShapesSection()
        {
            Section("Clothing Stack");

            if (_lastSelection == null)
            {
                GUILayout.Label("Open Maker and select a clothing slot.", KumihoUI.Label);
                return;
            }

            var sel  = _lastSelection.Value;
            var ctrl = sel.Character != null ? sel.Character.GetComponent<GDCharaController>() : null;
            if (ctrl == null)
            {
                GUILayout.Label("No GDC controller on this character.", KumihoUI.Label);
                return;
            }

            var slotName = sel.SlotNo >= 0 && sel.SlotNo < _slotNames.Length
                ? _slotNames[sel.SlotNo]
                : $"Slot {sel.SlotNo}";
            var hasItem = LayerBinding.TryGetSlotItem(sel.Character, sel.SlotNo, out _, out _, out _);

            var prevEnabled = GUI.enabled;
            if (!hasItem) GUI.enabled = false;
            if (GUILayout.Button($"Layer {slotName} item as accessory", KumihoUI.Button,
                    GUILayout.Height(KumihoUI.Button.fixedHeight)))
            {
                if (ctrl.AddLayerForSlot(sel.SlotNo))
                    GDCPlugin.Logger?.LogInfo($"[layer] Layered {slotName} item as accessory.");
                else
                    GDCPlugin.Logger?.LogWarning($"[layer] Could not layer {slotName} item.");
            }
            GUI.enabled = prevEnabled;

            GUILayout.Label(hasItem
                    ? "The copy follows the body. Now change this slot to another piece to layer them."
                    : "Select a slot that has an item equipped.",
                KumihoUI.LabelMuted);

            GUILayout.Space(8f);
            Section("Current layers");

            if (ctrl.Layers.Count == 0)
            {
                GUILayout.Label("No layered pieces on this character.", KumihoUI.Label);
                return;
            }

            // Group rows under the slot each layer was created from (Top,
            // Bottom, ...) with anything unknown under "Other". Removal is
            // deferred until after the draw so the list isn't mutated between
            // IMGUI's Layout and Repaint passes.
            var removeIdx = -1;
            foreach (var slot in _layerGroupOrder)
            {
                var headerDrawn = false;
                for (var i = 0; i < ctrl.Layers.Count; i++)
                {
                    if (ctrl.Layers[i].SourceSlot != slot) continue;
                    if (!headerDrawn)
                    {
                        GUILayout.Space(4f);
                        GUILayout.Label(slot >= 0 ? LayerBinding.SlotDisplayName(slot) : "Other",
                            KumihoUI.LabelSection);
                        headerDrawn = true;
                    }
                    if (DrawLayerRow(ctrl.Layers[i], i)) removeIdx = i;
                }
            }
            if (removeIdx >= 0) ctrl.RemoveLayer(removeIdx);
        }

        // Slot grouping order for the layers list: clothing kinds in HS2 order,
        // then -1 ("Other") for layers with no recorded source slot.
        private static readonly int[] _layerGroupOrder = { 0, 1, 2, 3, 4, 5, 6, 7, -1 };

        // Max vertex-normal push for the per-layer clipping slider, as a FRACTION
        // of the mesh's own size (LayerInflater scales it by each mesh's local
        // half-diagonal, so the slider means the same visible inflation no matter
        // the authoring scale). 0.10 = up to ~10% outward, enough to clear a thick
        // outer garment. The old value was an absolute 0.02 local units, which on
        // GDC's ~6-12 unit meshes was ~0.3% and invisible.
        private const float LayerInflateMax = 0.10f;

        // One layer row: editable name, show/hide switch, Remove. Returns true
        // when Remove was clicked this frame.
        private static bool DrawLayerRow(GDCharaController.LayerEntry e, int i)
        {
            var remove = false;
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal(GUILayout.Height(SliderRowH));

            // Editable name. Writes straight back to the entry so it persists.
            var newName = GUILayout.TextField(e.Name ?? "", KumihoUI.TextField,
                GUILayout.ExpandWidth(true), GUILayout.Height(SliderRowH));
            if (newName != e.Name) e.Name = newName;

            GUILayout.Space(6f);

            // Show/hide switch. Flips the live object immediately; persists.
            var wasVisible = e.Visible;
            var nowVisible = KumihoDraw.AnimatedSwitch(wasVisible, $"layer-vis-{i}");
            if (nowVisible != wasVisible)
            {
                e.Visible = nowVisible;
                if (e.Live != null) e.Live.SetActive(nowVisible);
            }

            GUILayout.Space(6f);
            if (GUILayout.Button("Remove", KumihoUI.ToolbarButton,
                    GUILayout.Width(72f), GUILayout.Height(KumihoUI.ToolbarButton.fixedHeight)))
            {
                remove = true;
            }
            GUILayout.EndHorizontal();

            // Clipping push: offsets the copy's mesh verts outward along their
            // normals so it stops poking through an inner garment. Object
            // move/rotate/scale can't do this (the copy is body-weightpainted
            // and ignores its transform), so the fix is vertex-level. The region
            // button scopes the push to a body area so only the clipping part of
            // the garment moves, not the whole piece.
            GUILayout.BeginHorizontal(GUILayout.Height(SliderRowH));
            GUILayout.Label("Push", KumihoUI.Label, GUILayout.Width(40f), GUILayout.Height(SliderRowH));
            var wasInflate = e.Inflate;
            var nowInflate = KumihoUI.HorizontalSliderWithFill(wasInflate, 0f, LayerInflateMax,
                GUILayout.Height(SliderRowH));
            if (!Mathf.Approximately(nowInflate, wasInflate))
            {
                e.Inflate = nowInflate;
                if (e.Live != null) LayerBinding.SetInflate(e.Live, nowInflate, e.Region);
            }
            GUILayout.Label(nowInflate.ToString("0.000"), ValueStyle,
                GUILayout.Width(46f), GUILayout.Height(SliderRowH));

            // Region cycle: click to advance Push scope (All -> Chest -> ...).
            // Reapplies immediately when the layer is live and being pushed.
            if (GUILayout.Button(LayerBinding.RegionNames[(int)e.Region], KumihoUI.ToolbarButton,
                    GUILayout.Width(66f), GUILayout.Height(KumihoUI.ToolbarButton.fixedHeight)))
            {
                e.Region = LayerBinding.NextRegion(e.Region);
                if (e.Live != null && !Mathf.Approximately(e.Inflate, 0f))
                    LayerBinding.SetInflate(e.Live, e.Inflate, e.Region);
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            return remove;
        }

        // Preset cache so DiscoverAvailable doesn't run every OnGUI event.
        // Refreshed on selection change (set null) and re-populated on the
        // next DrawPresetRow call.
        private static HashSet<string> _availablePresets;
        private static SelectionTracker.Selection? _presetsBuiltFor;

        private static void DrawPresetRow()
        {
            if (_lastSelection == null) return;

            // Refresh cache when selection changes. Comparing Matches() so a
            // slot/item swap inside the same character invalidates too.
            if (_presetsBuiltFor == null
                || !_lastSelection.Value.Matches(_presetsBuiltFor.Value)
                || _availablePresets == null)
            {
                _availablePresets = PresetBinding.DiscoverAvailable(_lastSelection.Value);
                _presetsBuiltFor  = _lastSelection;
            }

            Section("Presets");

            var active     = PresetBinding.GetActivePreset(_lastSelection.Value.Character, _lastSelection.Value.SlotNo);
            var presets    = PresetBinding.GetOrderedDisplay(_availablePresets);
            var availableW = ContentWidth;
            var perRow     = Mathf.Max(1, presets.Count);
            var btnWidth   = (availableW - (perRow - 1) * 8f) / perRow;
            // Square preview tile above each button, capped so a wide window
            // doesn't blow the orbs up to absurd sizes. Bigger than before
            // (was 84) per Sly; the orb fills the tile now that the frame's
            // gone.
            var previewH   = Mathf.Min(btnWidth, 120f);

            GUILayout.BeginHorizontal();
            for (var i = 0; i < presets.Count; i++)
            {
                var name      = presets[i];
                var hasName   = !string.IsNullOrEmpty(name);
                var available = hasName && _availablePresets.Contains(name);
                var label     = hasName ? name : "—";
                var isOn      = hasName && string.Equals(active, name, StringComparison.OrdinalIgnoreCase);

                GUILayout.BeginVertical(GUILayout.Width(btnWidth));

                // Preview tile. Framed like the MainTex thumbnails; teal idle,
                // magenta when this preset is the active one. Shows the sphere
                // render when a source Material exists, empty frame otherwise.
                var pr = GUILayoutUtility.GetRect(btnWidth, previewH,
                    GUILayout.Width(btnWidth), GUILayout.Height(previewH));
                if (Event.current.type == EventType.Repaint)
                {
                    // Only the active preset gets a frame (magenta), as a
                    // selection cue. Idle presets draw frameless: the old
                    // yellow idle border was just visual noise around the orb.
                    if (isOn) DrawRectOutline(pr, KumihoUI.Colors.Active, 2f);

                    // Premade orb from the bundle. Shown for any named preset;
                    // dimmed when this item doesn't actually ship that preset
                    // (button stays disabled) so the art still reads as "this
                    // is what Leather looks like" without implying it's usable.
                    var preview = hasName ? PresetPreview.Get(name) : null;
                    if (preview != null)
                    {
                        var prevCol  = GUI.color;
                        if (!available) GUI.color = new Color(1f, 1f, 1f, 0.35f);
                        GUI.DrawTexture(pr, preview, ScaleMode.ScaleToFit, alphaBlend: true);
                        GUI.color = prevCol;
                    }
                }

                var prevEnabled = GUI.enabled;
                var prevContent = GUI.contentColor;
                if (!available) GUI.enabled = false;
                if (isOn)       GUI.contentColor = KumihoUI.Colors.Active;

                // Height pinned to the Button sprite's native size so the
                // 9-slice doesn't scale and smear its corners.
                if (GUILayout.Button(label, KumihoUI.Button,
                        GUILayout.Width(btnWidth), GUILayout.Height(KumihoUI.Button.fixedHeight)))
                {
                    // Drop this slot's material float overrides BEFORE applying
                    // so PushOverrides stops re-stamping any slider the user
                    // dragged; otherwise the preset's float values would revert
                    // next frame. User-click only (not the per-frame reapply
                    // loop), so saved material overrides on reload are untouched.
                    MaterialBinding.DropOverridesForSlot(_lastSelection.Value);
                    if (PresetBinding.Apply(_lastSelection.Value, name))
                    {
                        ForceRediscover();
                    }
                }

                GUI.contentColor = prevContent;
                GUI.enabled      = prevEnabled;

                GUILayout.EndVertical();
                if (i < presets.Count - 1) GUILayout.Space(8f);
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);
        }

        // Textures tab: slot-first picker on top, then 96px thumbnail grid.
        // User flow: click a target slot button (becomes teal-highlighted),
        // then click any thumbnail to swap that texture into that slot.
        // Selecting a different slot resets the picker; the swap is
        // immediate visually.
        private const float ThumbSize    = 96f;
        private const float ThumbSpacing = 8f;

        // "Show every variant in this bundle" toggle. Off by default so the
        // grid only shows variants whose name suggests they belong to the
        // currently-selected slot (e.g. "bumpmap" only shows on BumpMap).
        // Turn on if you want the unfiltered bundle list.
        private static bool _showAllVariants;

        private static void DrawTexturesSection()
        {
            if (_lastSelection == null)
            {
                Section("Slots");
                GUILayout.Label("Nothing selected.", KumihoUI.Label);
                return;
            }

            // Textures tab is GDC-only. Other modders' items use different
            // texture pipelines, and Sly wants this tab to stay focused on
            // GDC's workflow rather than become a generic texture editor.
            if (!TextureBinding.IsGDCItem(_lastSelection.Value))
            {
                Section("Slots");
                GUILayout.Label("Texture swapping is only available on GDC-authored items.", KumihoUI.Label);
                return;
            }

            DrawPresetRow();

            SectionWithReset("Main texture", ResetTexturesTab);

            var mainSlots = FindMainTexSlots();
            if (mainSlots.Count == 0)
            {
                GUILayout.Label("This item doesn't expose a MainTex slot.", KumihoUI.Label);
                return;
            }

            DrawMainTexGrid(mainSlots);
        }

        // True when an asset path is in the def_tex folder for the active slot's
        // Part ("def_tex_top" for the jacket, "def_tex_bottom" for pants, ...),
        // or a legacy bare "def_tex". Scoping by Part keeps a multi-part mod from
        // showing the pants alternates while the jacket slot is selected.
        private static bool MainTexPathForActivePart(string path)
        {
            var part = _lastSelection != null
                ? ModConvention.PartForSlot(_lastSelection.Value.SlotNo)
                : null;
            return ModConvention.PathInDefTexForPart(path, part);
        }

        // Returns every live Slot whose property is MainTex. The item's
        // mesh is usually split across multiple renderers (Half + Shape)
        // that each get their own Material instance via r.materials —
        // writing to just one leaves the other half stale.
        private static List<TextureBinding.Slot> FindMainTexSlots()
        {
            for (var i = _textureDiscovery.Slots.Count - 1; i >= 0; i--)
            {
                if (!_textureDiscovery.Slots[i].IsAlive) _textureDiscovery.Slots.RemoveAt(i);
            }
            var found = new List<TextureBinding.Slot>();
            foreach (var s in _textureDiscovery.Slots)
            {
                if (string.Equals(s.PropertyName, "MainTex", StringComparison.OrdinalIgnoreCase))
                    found.Add(s);
            }
            return found;
        }

        // Filtered variant grid: textures the user can drop into the
        // MainTex slot(s). GDC's convention: alternates live in a "Def_Tex"
        // folder inside the zipmod (sibling to ExtraTextures, which holds
        // detail textures bundled with presets). Any texture whose path
        // segment is "def_tex" / "deftex" is a candidate. Filename match
        // ("maintex" / "maincolor") still works as a secondary signal so
        // older mods without Def_Tex still surface their MainTex.
        //
        // A click writes to every MainTex slot found on the item so a
        // dress whose mesh is split across multiple renderers (each with
        // its own r.materials instance) stays visually consistent.
        private static void DrawMainTexGrid(List<TextureBinding.Slot> slots)
        {
            // Merge this frame's discovered MainTex variants into the
            // per-selection accumulator (by name), so alternates that drop out
            // of a later rediscover stay on the grid.
            foreach (var v in _textureDiscovery.Variants)
            {
                if (v.Texture == null) continue;
                if (!MainTexPathForActivePart(v.SourcePath)
                    && !TextureBinding.VariantMatchesSlot(v.Name, "MainTex")) continue;
                var known = false;
                foreach (var s in _mainTexSeen) if (s.Name == v.Name) { known = true; break; }
                if (!known) _mainTexSeen.Add(v);
            }

            var visible = new List<TextureBinding.Variant>(_mainTexSeen);
            // Capture the texture bound at selection time, once. This is the
            // "original" the user can always return to.
            var current = slots[0].Get() as Texture2D;
            if (!_originalMainTexCaptured)
            {
                _originalMainTex         = current;
                _originalMainTexCaptured = true;
            }

            // Pin the currently-applied MainTex as a tile. Discovery is
            // intermittent: the def_tex alternates live in a sibling bundle
            // that Sideloader pages in lazily, so a rediscover (e.g. right
            // after a preset apply) can run before that bundle is loaded and
            // drop the alternate the user just selected. Pinning the live
            // texture keeps its tile present (and highlighted) regardless.
            if (current != null)
            {
                var listed = false;
                foreach (var v in visible) if (v.Texture == current) { listed = true; break; }
                if (!listed) visible.Insert(0, new TextureBinding.Variant(current));
            }

            // Pin the original MainTex as a stable first tile too, so the user
            // can always get back to the base look and the grid never shrinks
            // on the first swap.
            if (_originalMainTex != null && _originalMainTex != current)
            {
                var listed = false;
                foreach (var v in visible) if (v.Texture == _originalMainTex) { listed = true; break; }
                if (!listed) visible.Insert(0, new TextureBinding.Variant(_originalMainTex));
            }

            if (visible.Count == 0)
            {
                GUILayout.Label("No MainTex variants found in this mod's Def_Tex folder.", KumihoUI.Label);
                return;
            }

            var availableW = ContentWidth;
            var perRow     = Mathf.Max(1, Mathf.FloorToInt(availableW / (ThumbSize + ThumbSpacing)));
            var rows       = Mathf.CeilToInt(visible.Count / (float)perRow);

            for (var row = 0; row < rows; row++)
            {
                GUILayout.BeginHorizontal();
                for (var col = 0; col < perRow; col++)
                {
                    var idx = row * perRow + col;
                    if (idx >= visible.Count) break;
                    DrawMainTexTile(slots, visible[idx], current);
                    GUILayout.Space(ThumbSpacing);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(ThumbSpacing);
            }
        }

        private static void DrawMainTexTile(List<TextureBinding.Slot> slots, TextureBinding.Variant v, Texture2D current)
        {
            GUILayout.BeginVertical(GUILayout.Width(ThumbSize));
            var rect = GUILayoutUtility.GetRect(ThumbSize, ThumbSize,
                GUILayout.Width(ThumbSize), GUILayout.Height(ThumbSize));

            var isCurrent = ReferenceEquals(v.Texture, current);

            if (Event.current.type == EventType.Repaint)
            {
                var hovered = rect.Contains(Event.current.mousePosition);
                var border = isCurrent
                    ? KumihoUI.Colors.Active
                    : (hovered ? KumihoUI.Colors.AccentHi : KumihoUI.Colors.Accent);
                DrawRectOutline(rect, border, isCurrent ? 2f : 1f);

                if (v.Texture != null)
                {
                    var inner = new Rect(rect.x + 2f, rect.y + 2f, rect.width - 4f, rect.height - 4f);
                    GUI.DrawTexture(inner, v.Texture, ScaleMode.ScaleToFit);
                }
            }

            // GUI.Button claims hot control + handles MouseDown/MouseUp
            // sequencing correctly. Empty content + GUIStyle.none keeps it
            // invisible so the manual frame + thumbnail above show through.
            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
            {
                foreach (var s in slots) s.Set(v.Texture);
                GDCPlugin.Logger?.LogInfo($"[texture] Swapped '{v.Name}' into MainTex on {slots.Count} renderer slot(s)");
            }

            // The base MainTex is HS2-composited into a RenderTexture at load
            // (region colors baked in), so its Variant has no meaningful asset
            // name and it isn't a def_tex swatch. Label that pinned tile
            // "Original" so the revert target is always clear; everything else
            // keeps its texture name.
            var isOriginal = _originalMainTex != null && ReferenceEquals(v.Texture, _originalMainTex);
            var label = isOriginal ? "Original" : v.Name;
            GUILayout.Label(new GUIContent(label, v.Name), KumihoUI.LabelMuted, GUILayout.Width(ThumbSize));
            GUILayout.EndVertical();
        }

        // Per-frame cache of finalized button labels. Rebuilt only when the
        // selection changes so we don't pay for the duplicate-detection
        // pass on every draw.
        private static List<string> _slotButtonLabels = new List<string>();
        private static SelectionTracker.Selection? _slotLabelsBuiltFor;

        private static void BuildSlotButtonLabels()
        {
            _slotButtonLabels.Clear();
            var slots = _textureDiscovery.Slots;
            if (slots.Count == 0) { _slotLabelsBuiltFor = _lastSelection; return; }

            // Detect whether all slots share one material — if so the
            // shorter "MainTex" form is enough.
            var firstMat  = slots[0].MaterialName;
            var singleMat = true;
            for (var i = 1; i < slots.Count; i++)
            {
                if (slots[i].MaterialName != firstMat) { singleMat = false; break; }
            }

            // Build initial labels with the right base.
            for (var i = 0; i < slots.Count; i++)
            {
                _slotButtonLabels.Add(singleMat ? slots[i].ShortLabel : slots[i].Label);
            }

            // Resolve duplicates with a numeric index per base label. So if
            // MainTex appears three times across different materials, they
            // become MainTex #1 / MainTex #2 / MainTex #3. Material names
            // make poor discriminators here because GDC's mods often leave
            // them as the default "New Material".
            var counts = new Dictionary<string, int>();
            var totals = new Dictionary<string, int>();
            for (var i = 0; i < _slotButtonLabels.Count; i++)
            {
                if (totals.ContainsKey(_slotButtonLabels[i])) totals[_slotButtonLabels[i]]++;
                else totals[_slotButtonLabels[i]] = 1;
            }
            for (var i = 0; i < _slotButtonLabels.Count; i++)
            {
                var baseLabel = _slotButtonLabels[i];
                if (totals[baseLabel] <= 1) continue;
                var n = counts.TryGetValue(baseLabel, out var c) ? c + 1 : 1;
                counts[baseLabel] = n;
                _slotButtonLabels[i] = $"{baseLabel} #{n}";
            }

            _slotLabelsBuiltFor = _lastSelection;
        }

        // Slot picker: a 2-column grid of clearly framed Button widgets. I
        // dropped the Toggle widget because its idle background renders
        // invisible against the dark window, which made every label look
        // like loose text. Using Button (which always has a visible chrome)
        // with manual on/off coloring solves both readability and the
        // overlap effect from cramped 4-column layouts.
        private static void DrawSlotPicker()
        {
            var slots = _textureDiscovery.Slots;
            if (slots.Count == 0) return;

            if (_slotLabelsBuiltFor == null || _lastSelection == null
                || !_lastSelection.Value.Matches(_slotLabelsBuiltFor.Value)
                || _slotButtonLabels.Count != slots.Count)
            {
                BuildSlotButtonLabels();
            }

            var availableW = ContentWidth;
            // 2 columns when window is wide enough, 1 column otherwise.
            var perRow     = availableW >= 520f ? 2 : 1;
            var btnWidth   = (availableW - (perRow - 1) * 8f) / perRow;
            var rows       = Mathf.CeilToInt(slots.Count / (float)perRow);

            for (var row = 0; row < rows; row++)
            {
                GUILayout.BeginHorizontal();
                for (var col = 0; col < perRow; col++)
                {
                    var idx = row * perRow + col;
                    if (idx >= slots.Count) break;
                    var label = _slotButtonLabels[idx];
                    var isOn  = _selectedSlotIdx == idx;

                    // Highlight selected row with the accent tint so the
                    // user can see which slot the variant grid below
                    // applies to.
                    var prevContent = GUI.contentColor;
                    if (isOn) GUI.contentColor = KumihoUI.Colors.Active;

                    if (GUILayout.Button(label, KumihoUI.Button,
                        GUILayout.Width(btnWidth), GUILayout.Height(30f)))
                    {
                        _selectedSlotIdx = idx;
                    }

                    GUI.contentColor = prevContent;
                    if (col < perRow - 1) GUILayout.Space(8f);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
            }
        }

        // Thumbnail grid: 96px tiles with name labels below. Auto-flows to
        // window width. Click a tile to swap when a slot is picked.
        private static void DrawVariantGrid()
        {
            // Apply slot-intent filtering. When a slot is selected and
            // _showAllVariants is off, only variants whose name suggests
            // they belong to that slot are shown. Falls back to the full
            // list when no slot is picked or the user opts in to all.
            var visible = new List<TextureBinding.Variant>();
            string slotProp = null;
            if (_selectedSlotIdx >= 0 && _selectedSlotIdx < _textureDiscovery.Slots.Count)
                slotProp = _textureDiscovery.Slots[_selectedSlotIdx].PropertyName;

            if (_showAllVariants || string.IsNullOrEmpty(slotProp))
            {
                visible.AddRange(_textureDiscovery.Variants);
            }
            else
            {
                foreach (var v in _textureDiscovery.Variants)
                {
                    if (TextureBinding.VariantMatchesSlot(v.Name, slotProp)) visible.Add(v);
                }
                // If nothing matches by intent, fall back to the full list
                // and let the user judge. Better than a blank grid.
                if (visible.Count == 0) visible.AddRange(_textureDiscovery.Variants);
            }

            var availableW = ContentWidth;
            var perRow     = Mathf.Max(1, Mathf.FloorToInt(availableW / (ThumbSize + ThumbSpacing)));
            var rows       = Mathf.CeilToInt(visible.Count / (float)perRow);

            for (var row = 0; row < rows; row++)
            {
                GUILayout.BeginHorizontal();
                for (var col = 0; col < perRow; col++)
                {
                    var idx = row * perRow + col;
                    if (idx >= visible.Count) break;
                    DrawVariantTile(visible[idx]);
                    GUILayout.Space(ThumbSpacing);
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(ThumbSpacing);
            }
        }

        private static void DrawVariantTile(TextureBinding.Variant v)
        {
            GUILayout.BeginVertical(GUILayout.Width(ThumbSize));

            // Thumbnail rect. Reserve space deterministically so the layout
            // doesn't shift between Layout and Repaint events.
            var thumbRect = GUILayoutUtility.GetRect(ThumbSize, ThumbSize,
                GUILayout.Width(ThumbSize), GUILayout.Height(ThumbSize));

            var e = Event.current;
            var hovered = thumbRect.Contains(e.mousePosition);

            if (e.type == EventType.Repaint)
            {
                // Frame: thin yellow border that brightens on hover.
                var border = hovered ? KumihoUI.Colors.AccentHi : KumihoUI.Colors.Accent;
                DrawRectOutline(thumbRect, border, 1f);

                // Thumbnail itself, inset 2px so the border shows clean.
                if (v.Texture != null)
                {
                    var inner = new Rect(thumbRect.x + 2f, thumbRect.y + 2f,
                                         thumbRect.width - 4f, thumbRect.height - 4f);
                    GUI.DrawTexture(inner, v.Texture, ScaleMode.ScaleToFit);
                }
            }

            if (e.type == EventType.MouseUp && e.button == 0 && hovered)
            {
                if (_selectedSlotIdx >= 0 && _selectedSlotIdx < _textureDiscovery.Slots.Count)
                {
                    _textureDiscovery.Slots[_selectedSlotIdx].Set(v.Texture);
                    GDCPlugin.Logger?.LogInfo($"[texture] Swapped '{v.Name}' into {_textureDiscovery.Slots[_selectedSlotIdx].Label}");
                }
                e.Use();
            }

            // Name label under the thumbnail. Truncated visually by the
            // narrow column width; full name available on hover via tooltip.
            GUILayout.Label(new GUIContent(v.Name, v.Name), KumihoUI.LabelMuted,
                GUILayout.Width(ThumbSize));

            GUILayout.EndVertical();
        }

        // Quick 4-edge outline using the white texture tinted via GUI.color.
        // Slightly cheaper than instantiating textures per tile.
        private static void DrawRectOutline(Rect rect, Color color, float thickness)
        {
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        // Defensive prune: the underlying SkinnedMeshRenderer can be
        // destroyed mid-frame if the user swaps clothing between Update
        // and OnGUI, which would null-ref the slider draw.
        private static void PruneDead(System.Collections.Generic.List<BlendshapeBinding.Binding> list)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].IsAlive) list.RemoveAt(i);
            }
        }

        // Reads current weight, draws the slider, writes back if the user
        // moved it. Unity's blendshape weight range is 0..100 by convention
        // but the API accepts any float, so I clamp visually to 0..100 for
        // the slider while leaving the underlying setter as-is.
        // Fixed columns for the wide single-row layout.
        private const float ShapeLabelW = 180f;
        private const float ShapeValueW = 46f;
        // Minimum slider track before the row stops fitting side-by-side. Below
        // label + value + this, the layout stacks the label above the slider so
        // nothing spills past the window's right edge.
        private const float ShapeMinSliderW = 90f;

        private static void LiveBlendshapeSlider(BlendshapeBinding.Binding b)
        {
            var current = b.Get();

            // Stack the label above the slider once the window is too narrow to
            // hold label + slider + value on one line.
            var wrap = ContentWidth < ShapeLabelW + ShapeValueW + ShapeMinSliderW;

            // Per-slider Reset: restores just this shape to its pre-edit weight.
            // Greyed until the slider's been moved, matching the material sliders.
            void DrawShapeReset()
            {
                var prevEnabled = GUI.enabled;
                if (!b.IsOverridden) GUI.enabled = false;
                if (GUILayout.Button(new GUIContent("R", "Reset to original"),
                        MiniResetStyle, GUILayout.Width(MiniResetW),
                        GUILayout.Height(KumihoUI.ToolbarButton.fixedHeight)))
                {
                    b.ResetToOriginal();
                }
                GUI.enabled = prevEnabled;
            }

            float next;
            if (wrap)
            {
                GUILayout.BeginVertical();
                GUILayout.Label(b.Label, WrapLabelStyle);
                GUILayout.BeginHorizontal(GUILayout.Height(SliderRowH));
                next = KumihoUI.HorizontalSliderWithFill(current, 0f, 100f, GUILayout.Height(SliderRowH));
                GUILayout.Label(next.ToString("0.0"), ValueStyle, GUILayout.Width(ShapeValueW), GUILayout.Height(SliderRowH));
                DrawShapeReset();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.BeginHorizontal(GUILayout.Height(SliderRowH));
                GUILayout.Label(b.Label, KumihoUI.Label, GUILayout.Width(ShapeLabelW), GUILayout.Height(SliderRowH));
                next = KumihoUI.HorizontalSliderWithFill(current, 0f, 100f, GUILayout.Height(SliderRowH));
                GUILayout.Label(next.ToString("0.0"), ValueStyle, GUILayout.Width(ShapeValueW), GUILayout.Height(SliderRowH));
                DrawShapeReset();
                GUILayout.EndHorizontal();
            }

            // Only push when the value actually moved, otherwise SetBlendShapeWeight
            // fires every frame at the cost of a vertex re-skin pass.
            if (!Mathf.Approximately(next, current))
            {
                b.Set(next);
            }
        }

        private static void PlaceholderSlider(string label, float initial)
        {
            if (!_placeholderValues.TryGetValue(label, out var v)) v = initial;

            GUILayout.BeginHorizontal(GUILayout.Height(SliderRowH));
            GUILayout.Label(label, KumihoUI.Label, GUILayout.Width(140), GUILayout.Height(SliderRowH));
            // KumihoUI.HorizontalSliderWithFill draws the styled track,
            // overlays SliderHFill up to the current value, and hands the
            // thumb to Unity's HorizontalSlider with SliderThumb style.
            // Bare GUILayout.HorizontalSlider ignores all of that and falls
            // back to the stock Unity skin.
            v = KumihoUI.HorizontalSliderWithFill(v, 0f, 1f, GUILayout.Height(SliderRowH));
            GUILayout.Label(v.ToString("0.00"), ValueStyle, GUILayout.Width(46), GUILayout.Height(SliderRowH));
            GUILayout.EndHorizontal();

            _placeholderValues[label] = v;
        }

        private static void EatInputInRect(Rect rect)
        {
            // Skip the reset while any of these are happening, otherwise
            // sliders / scrollbars / window drag / our corner resize stop
            // responding because ResetInputAxes wipes the mouse button
            // state mid-drag.
            if (GUIUtility.hotControl != 0) return;
            if (_resizing) return;

            var mouseGuiSpace = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (rect.Contains(mouseGuiSpace))
                Input.ResetInputAxes();
        }
    }
}
