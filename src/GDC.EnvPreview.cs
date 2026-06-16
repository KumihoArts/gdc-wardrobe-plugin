using System;
using System.Collections.Generic;
using Kumiho.UI;
using UnityEngine;

namespace GDCplugin
{
    // Animated previews for the environmental slider groups (Snow / Rain) on
    // the Sliders tab. GDC asked for a rectangular looping preview of the
    // effect above its sliders.
    //
    // Each effect is a single horizontal sprite-sheet strip in the UI bundle:
    // N equal-width frames laid left to right. Playback advances the frame from
    // Time.time and draws it with GUI.DrawTextureWithTexCoords. Runtime IMGUI
    // repaints every frame, so it animates with no manual dirtying.
    //
    // This mirrors PresetPreview's "premade art from the bundle, no runtime
    // render" approach (rendering HS2's clothing shaders live failed before:
    // they need the character's lighting rig). Missing art = no preview, no
    // crash, so the feature is inert until Sly paints the strips.
    //
    // Art convention: "env-<group>" in Bundle\Assets\Textures\ (env-snow,
    // env-rain). Frame layout (count, columns) + fps live here in code, tunable
    // without a bundle rebuild; only the pixels need the Unity round-trip.
    internal static class EnvPreview
    {
        // One sheet's playback parameters. The sheet is a grid of Cols columns;
        // frames run left-to-right, top-to-bottom. Frames is the total count
        // (the last row may be partial). A single horizontal strip is just
        // Cols == Frames (one row). Set both to match the delivered PNG.
        private sealed class Def
        {
            public readonly string Asset;
            public readonly int    Frames;
            public readonly int    Cols;
            public readonly float  Fps;
            public Def(string asset, int frames, int cols, float fps)
            {
                Asset  = asset;
                Frames = frames;
                Cols   = cols < 1 ? 1 : cols;
                Fps    = fps;
            }

            public int Rows => (Frames + Cols - 1) / Cols; // ceil
        }

        // Keyed by the slider group's display name ("Snow" / "Rain"), the same
        // string SliderWindow passes. Update to match Sly's art: for a single
        // horizontal strip set Cols == Frames; for a grid set Cols to the
        // sheet's column count (e.g. 64 frames / 8 cols = an 8x8 grid).
        private static readonly Dictionary<string, Def> _defs =
            new Dictionary<string, Def>(StringComparer.OrdinalIgnoreCase)
            {
                { "Snow", new Def("env-snow", 128, 8, 24f) },
                { "Rain", new Def("env-rain", 128, 8, 24f) },
            };

        // Texture cache. Caches misses as null too, but Has/Load re-load when a
        // cached entry has gone null so a scene-reload bundle refresh doesn't
        // leave the preview permanently blank.
        private static readonly Dictionary<string, Texture2D> _cache =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private static Texture2D Load(Def def)
        {
            if (_cache.TryGetValue(def.Asset, out var cached) && cached != null)
                return cached;
            var loaded = KumihoUI.LoadBundleTexture(def.Asset);
            _cache[def.Asset] = loaded;
            return loaded;
        }

        // True when this group has a known def AND its art is present. Callers
        // gate space reservation on this so a group with no art reserves no
        // layout (stable across the Layout and Repaint passes as long as the
        // bundle is loaded).
        public static bool Has(string group)
        {
            return _defs.TryGetValue(group, out var def) && Load(def) != null;
        }

        // Single-frame aspect (width / height) of this group's sheet, derived
        // from the real texture dims and the grid layout. The caller sizes the
        // on-screen preview to this so a frame draws at its true proportions
        // (full width, height = width / aspect) instead of being squashed into
        // a fixed rect. Returns false when the art is absent.
        public static bool TryGetFrameAspect(string group, out float aspect)
        {
            aspect = 4f;
            if (!_defs.TryGetValue(group, out var def)) return false;
            var tex = Load(def);
            if (tex == null || tex.width <= 0 || tex.height <= 0) return false;
            var fw = tex.width  / (float)def.Cols;
            var fh = tex.height / (float)def.Rows;
            if (fh <= 0f) return false;
            aspect = fw / fh;
            return true;
        }

        // Draws the current frame into rect. Repaint-only (Time.time sampling +
        // texture draw); the caller reserves the rect in layout. Safe to call
        // without a Has() guard, it just no-ops when art is absent.
        public static void Draw(Rect rect, string group)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (!_defs.TryGetValue(group, out var def)) return;
            var tex = Load(def);
            if (tex == null || def.Frames <= 0) return;

            var frame = Mathf.FloorToInt(Time.time * def.Fps) % def.Frames;
            if (frame < 0) frame += def.Frames;

            // Snap the destination to whole pixels. GUILayout can hand us a
            // rect on a fractional Y, and bilinear sampling at a sub-pixel
            // offset smears the whole frame into a soft blur. Rounding pins it
            // to the pixel grid so the downscale stays as crisp as bilinear
            // allows.
            rect = new Rect(Mathf.Round(rect.x), Mathf.Round(rect.y),
                            Mathf.Round(rect.width), Mathf.Round(rect.height));

            var col = frame % def.Cols;
            var row = frame / def.Cols;

            // Per-frame UV window. Unity's texture origin is bottom-left, so
            // row 0 (top of the sheet) maps to the highest V. Inset by a
            // half-texel on every side so bilinear sampling at a cell edge
            // doesn't bleed in the neighbouring frame (lets Sly pack frames
            // edge-to-edge with no guard pixels).
            var fw = 1f / def.Cols;
            var fh = 1f / def.Rows;
            var ex = tex.width  > 0 ? 0.5f / tex.width  : 0f;
            var ey = tex.height > 0 ? 0.5f / tex.height : 0f;
            var tc = new Rect(
                col * fw + ex,
                1f - (row + 1) * fh + ey,
                fw - 2f * ex,
                fh - 2f * ey);

            GUI.DrawTextureWithTexCoords(rect, tex, tc, alphaBlend: true);
        }
    }
}
