using System;
using System.Collections;
using System.Reflection;
using CharaCustom;
using KKAPI.Maker;
using UnityEngine;
using UnityEngine.UI;

namespace GDCplugin
{
    // Adds a GDC logo button to HS2 maker's top category tab strip, sitting
    // next to the stock tabs (Clothes / Hair / Accessory / ...). The stock
    // tabs are the `items` of CustomChangeMainMenu (a UI_ToggleGroupCtrl),
    // each item a Toggle in one exclusive group that swaps the active editing
    // window. We deliberately do NOT add a real toggle to that group — that
    // would blank the current category canvas on click. Instead we clone one
    // tab's transform for size/placement, strip the toggle, and drop a plain
    // Button on top whose only job is to open the floating GDC window.
    internal static class MakerTab
    {
        private const string ButtonName     = "GDCMenuButton";
        // Dedicated art for the maker tab button (Sly's GDCButton.png,
        // embedded from src/Resources). Swap that file for new art when it
        // lands, no code change needed.
        private const string IconResource   = "GDCplugin.Resources.GDCButton.png";

        private static Sprite? _iconSprite;

        // The injected button, kept so the live config toggle can remove it
        // without re-finding it. Null between maker sessions (dies with scene).
        private static GameObject? _buttonGo;

        public static void Initialize()
        {
            MakerAPI.MakerFinishedLoading += (_, __) => OnMakerLoaded();

            // Live response to the F1 "Show menu button" toggle: add or remove
            // the button on the spot when the user is already in the maker.
            GDCPlugin.ShowMenuButton.SettingChanged += (_, __) =>
            {
                if (!MakerAPI.InsideMaker) return;
                if (GDCPlugin.ShowMenuButton.Value) TryInject();
                else RemoveButton();
            };
        }

        private static void OnMakerLoaded()
        {
            // The category strip exists by the time MakerFinishedLoading
            // fires, but a coroutine retry keeps us robust against ordering
            // changes from other maker plugins.
            var plugin = GDCPlugin.Instance;
            if (plugin == null) { TryInject(); return; }
            plugin.StartCoroutine(InjectWhenReady());
        }

        private static IEnumerator InjectWhenReady()
        {
            for (var i = 0; i < 30; i++)
            {
                if (TryInject()) yield break;
                yield return null;
            }
            GDCPlugin.Logger?.LogWarning("[makertab] Gave up adding the GDC tab; CustomChangeMainMenu never appeared.");
        }

        private static bool TryInject()
        {
            try
            {
                // Respect the user's preference; treat "hidden" as done so the
                // retry loop stops cleanly.
                if (!GDCPlugin.ShowMenuButton.Value) { RemoveButton(); return true; }

                var menu = UnityEngine.Object.FindObjectOfType<CustomChangeMainMenu>();
                if (menu == null || menu.items == null || menu.items.Length == 0) return false;

                // Collect the live tab toggles in their visual order.
                var tabs = new System.Collections.Generic.List<RectTransform>();
                foreach (var item in menu.items)
                {
                    if (item?.tglItem == null) continue;
                    var rt = item.tglItem.transform as RectTransform;
                    if (rt != null) tabs.Add(rt);
                }
                if (tabs.Count == 0) return false;

                var refRT  = tabs[0];
                var parent = refRT.parent;
                if (parent == null) return false;

                // Already added (maker re-entered without scene teardown).
                var existing = parent.Find(ButtonName);
                if (existing != null) { _buttonGo = existing.gameObject; return true; }

                var sprite = GetIconSprite();
                if (sprite == null)
                {
                    GDCPlugin.Logger?.LogWarning("[makertab] Icon sprite missing; skipping tab button.");
                    return true; // don't spin the retry loop on a missing asset
                }

                // Build a fresh button rather than cloning a toggle so we
                // don't inherit the tab's selected/hover state machine or its
                // child icon layout.
                var go = new GameObject(ButtonName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                var rtNew = (RectTransform)go.transform;
                rtNew.SetParent(parent, worldPositionStays: false);

                // Match a sibling tab's footprint so it reads as one of them.
                rtNew.localScale   = refRT.localScale;
                rtNew.localRotation = refRT.localRotation;
                rtNew.anchorMin    = refRT.anchorMin;
                rtNew.anchorMax    = refRT.anchorMax;
                rtNew.pivot        = refRT.pivot;
                rtNew.sizeDelta    = refRT.sizeDelta;
                rtNew.anchoredPosition = NextSlotPosition(tabs);
                rtNew.SetAsLastSibling();

                var img = go.GetComponent<Image>();
                img.sprite        = sprite;
                img.preserveAspect = true;   // don't squish whatever art ships
                img.raycastTarget  = true;

                var btn = go.GetComponent<Button>();
                btn.targetGraphic = img;
                btn.onClick.AddListener(() => SliderWindow.Toggle());

                // If the strip is driven by a layout group, force a reflow so
                // the new child lands in the flow instead of overlapping.
                var layout = parent.GetComponent<LayoutGroup>();
                if (layout != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)parent);

                _buttonGo = go;
                GDCPlugin.Logger?.LogInfo($"[makertab] Added GDC tab button next to {tabs.Count} category tab(s).");
                return true;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogError($"[makertab] Inject failed: {ex.GetType().Name}: {ex.Message}");
                return true; // stop retrying on a hard error
            }
        }

        // Removes the injected button if present. Uses the stored reference,
        // falling back to a name search in case the reference was lost (e.g.
        // toggled off after a maker re-entry the inject path didn't see).
        private static void RemoveButton()
        {
            if (_buttonGo != null)
            {
                UnityEngine.Object.Destroy(_buttonGo);
                _buttonGo = null;
                GDCPlugin.Logger?.LogInfo("[makertab] Removed GDC tab button.");
                return;
            }

            var menu = UnityEngine.Object.FindObjectOfType<CustomChangeMainMenu>();
            var parent = menu?.items?.Length > 0 ? menu.items[0]?.tglItem?.transform.parent : null;
            var found = parent?.Find(ButtonName);
            if (found != null)
            {
                UnityEngine.Object.Destroy(found.gameObject);
                GDCPlugin.Logger?.LogInfo("[makertab] Removed GDC tab button.");
            }
        }

        // Picks an anchoredPosition one slot past the last tab, along whichever
        // axis the strip runs. Spacing is the average gap between the existing
        // tabs so the new button lines up evenly; direction follows item order
        // so it extends past the end rather than before the start.
        private static Vector2 NextSlotPosition(System.Collections.Generic.List<RectTransform> tabs)
        {
            var first = tabs[0].anchoredPosition;
            var last  = tabs[tabs.Count - 1].anchoredPosition;

            // Single tab: fall back to stepping one full width along x.
            if (tabs.Count < 2)
                return new Vector2(last.x + tabs[0].rect.width, last.y);

            var dx = last.x - first.x;
            var dy = last.y - first.y;
            var n  = tabs.Count - 1;

            // Horizontal strip when x varies more than y, vertical otherwise.
            if (Mathf.Abs(dx) >= Mathf.Abs(dy))
            {
                var step = dx / n;
                if (Mathf.Approximately(step, 0f)) step = tabs[0].rect.width;
                return new Vector2(last.x + step, last.y);
            }
            else
            {
                var step = dy / n;
                if (Mathf.Approximately(step, 0f)) step = -tabs[0].rect.height;
                return new Vector2(last.x, last.y + step);
            }
        }

        private static Sprite? GetIconSprite()
        {
            if (_iconSprite != null) return _iconSprite;

            var tex = EmbeddedTexture.Load(IconResource);
            if (tex == null) return null;

            _iconSprite = Sprite.Create(tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            _iconSprite.hideFlags = HideFlags.HideAndDontSave;
            return _iconSprite;
        }
    }
}
