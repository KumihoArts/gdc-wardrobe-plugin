using System;
using System.Collections.Generic;
using UnityEngine;

namespace GDCplugin
{
    // Given a current selection, returns a flat list of live blendshape
    // bindings I can wire to sliders. Each binding knows how to read and
    // write a single blendshape weight on a specific SkinnedMeshRenderer.
    //
    // Auto-discover only: every blendshape on every SkinnedMeshRenderer
    // under the clothing GameObject becomes a slider. Metadata-driven
    // curation (which shapes to expose, friendly names, value ranges) is
    // the v0.4 layer on top of this.
    internal static class BlendshapeBinding
    {
        // User-set blendshape weights, pushed to the renderer every frame
        // from Plugin.LateUpdate so HS2's facial animation can't overwrite
        // them mid-frame. Nested dict instead of ValueTuple-keyed to keep
        // working on older Mono runtimes that ship without ValueTuple.
        private static readonly Dictionary<SkinnedMeshRenderer, Dictionary<int, float>> _overrides
            = new Dictionary<SkinnedMeshRenderer, Dictionary<int, float>>();

        // Pre-edit weight per (renderer, shapeIndex). ClearOverrides writes
        // these back so the per-tab Reset actually rolls the sliders home.
        // Without it, clearing _overrides only stops the push loop re-stamping;
        // the live renderer kept the dragged weight and the slider (which reads
        // live) never moved, so Reset looked like a no-op (same trap the
        // material sliders had).
        private static readonly Dictionary<SkinnedMeshRenderer, Dictionary<int, float>> _originalWeights
            = new Dictionary<SkinnedMeshRenderer, Dictionary<int, float>>();

        // Records the pre-edit weight for (renderer, shapeIndex) once. Called
        // from Binding.Set BEFORE the SetBlendShapeWeight so the captured value
        // is the original, not the weight the user just dragged to.
        private static void CaptureOriginal(SkinnedMeshRenderer r, int idx, float value)
        {
            if (r == null) return;
            if (!_originalWeights.TryGetValue(r, out var perRenderer))
            {
                perRenderer = new Dictionary<int, float>();
                _originalWeights[r] = perRenderer;
            }
            if (!perRenderer.ContainsKey(idx)) perRenderer[idx] = value;
        }

        // True once the user has dragged this (renderer, shape) at least once
        // this session. Drives the per-slider Reset button's enabled state.
        private static bool HasOriginal(SkinnedMeshRenderer r, int idx)
        {
            return r != null
                && _originalWeights.TryGetValue(r, out var perRenderer)
                && perRenderer.ContainsKey(idx);
        }

        // Restore one shape on one renderer to its captured original, then drop
        // both its snapshot and its override so the push loop stops re-stamping
        // it. Per-slider counterpart to ClearOverrides.
        private static void RestoreOriginal(SkinnedMeshRenderer r, int idx)
        {
            if (r == null) return;
            if (_originalWeights.TryGetValue(r, out var origs)
                && origs.TryGetValue(idx, out var val))
            {
                try { r.SetBlendShapeWeight(idx, val); }
                catch { /* destroyed mid-frame */ }
                origs.Remove(idx);
                if (origs.Count == 0) _originalWeights.Remove(r);
            }
            if (_overrides.TryGetValue(r, out var ovs))
            {
                ovs.Remove(idx);
                if (ovs.Count == 0) _overrides.Remove(r);
            }
        }

        // Called from Plugin.LateUpdate. Re-applies every user-set weight
        // after Unity's animation/face systems have run for the frame.
        // Silently skips destroyed renderers; the entries themselves are
        // small and only get cleared when the user reloads or restarts.
        public static void PushOverrides()
        {
            foreach (var kv in _overrides)
            {
                var renderer = kv.Key;
                if (renderer == null) continue;
                foreach (var inner in kv.Value)
                {
                    try { renderer.SetBlendShapeWeight(inner.Key, inner.Value); }
                    catch { /* destroyed mid-frame, ignore */ }
                }
            }
        }

        // Clears every recorded override. Called when the selection changes
        // significantly (character swap) so stale overrides don't leak
        // onto a fresh character.
        public static void ClearOverrides()
        {
            foreach (var kv in _originalWeights)
            {
                var renderer = kv.Key;
                if (renderer == null) continue;
                foreach (var inner in kv.Value)
                {
                    try { renderer.SetBlendShapeWeight(inner.Key, inner.Value); }
                    catch { /* destroyed mid-frame, ignore */ }
                }
            }
            _originalWeights.Clear();
            _overrides.Clear();
        }

        // Read-only iteration over the runtime overrides for the CharaController
        // to snapshot on save. Returns the live nested dict; do not mutate.
        internal static IEnumerable<KeyValuePair<SkinnedMeshRenderer, Dictionary<int, float>>> IterateOverrides()
            => _overrides;

        // Used by the CharaController to re-establish a runtime override
        // from saved data without going through Binding.Set (which would
        // log spam and double-write to the renderer).
        internal static void RecordRuntimeOverride(SkinnedMeshRenderer r, int idx, float value)
            => RecordOverride(r, idx, value);

        private static void RecordOverride(SkinnedMeshRenderer r, int idx, float value)
        {
            if (!_overrides.TryGetValue(r, out var perRenderer))
            {
                perRenderer = new Dictionary<int, float>();
                _overrides[r] = perRenderer;
            }
            perRenderer[idx] = value;
        }

        public sealed class Binding
        {
            public readonly string                Label;       // displayed name
            public readonly SkinnedMeshRenderer   Renderer;
            public readonly int                   ShapeIndex;

            public Binding(string label, SkinnedMeshRenderer r, int idx)
            {
                Label      = label;
                Renderer   = r;
                ShapeIndex = idx;
            }

            // Unity's blendshape weight is 0..100 by convention (not 0..1).
            public float Get()
            {
                if (Renderer == null) return 0f;
                return Renderer.GetBlendShapeWeight(ShapeIndex);
            }

            public void Set(float weight)
            {
                if (Renderer == null) return;
                // Per-set log silenced; it spammed the log on every frame of
                // every drag. Re-enable as LogDebug if a future input issue
                // makes it useful again.
                CaptureOriginal(Renderer, ShapeIndex, Renderer.GetBlendShapeWeight(ShapeIndex));
                Renderer.SetBlendShapeWeight(ShapeIndex, weight);
                RecordOverride(Renderer, ShapeIndex, weight);
            }

            public bool IsAlive => Renderer != null && Renderer.sharedMesh != null;

            // True when the user has moved this slider this session. Greys the
            // per-slider Reset until there's something to undo.
            public bool IsOverridden => HasOriginal(Renderer, ShapeIndex);

            // Restore this one shape to its pre-edit weight.
            public void ResetToOriginal() => RestoreOriginal(Renderer, ShapeIndex);
        }

        // Holds clothing-specific shapes (often empty in HS2, because the
        // game uses size-variant meshes named "_a"/"_b" instead of true
        // blendshapes on clothing) and body-level shapes (chest, waist, hip,
        // etc.) which the clothing drapes over. Splitting them lets the UI
        // section them so the user knows which ones affect what.
        // Body / character shapes used to live here too, but the Shapes tab
        // that surfaced them was dropped (Sly: never used in the GDC
        // workflow). Only item-level shapes remain, driving the Items tab.
        public sealed class DiscoveryResult
        {
            public readonly List<Binding> ItemShapes = new List<Binding>();
            public string                 ItemMeshName = "";
        }

        public static DiscoveryResult DiscoverAll(in SelectionTracker.Selection sel)
        {
            var result = new DiscoveryResult();
            // Discovery logs were essential during the early "no shapes
            // anywhere" debugging but are pure spam now that the path works.
            // The log? null assignment keeps the call sites intact for easy
            // re-enabling: just remove the null and they fire again.
            BepInEx.Logging.ManualLogSource? log = null;

            if (sel.Character == null)
            {
                log?.LogDebug("[blendshape] DiscoverAll: character is null");
                return result;
            }

            // Item shapes (just the selected clothing slot)
            DiscoverInto(sel, result.ItemShapes, out var meshName);
            result.ItemMeshName = meshName;

            log?.LogDebug($"[blendshape] DiscoverAll: item={result.ItemShapes.Count}, mesh='{result.ItemMeshName}'");
            return result;
        }

        // Kept for compatibility with any callers using the old shape.
        // Returns item shapes only.
        public static List<Binding> Discover(in SelectionTracker.Selection sel)
        {
            var list = new List<Binding>();
            DiscoverInto(sel, list, out _);
            return list;
        }

        private static void DiscoverInto(in SelectionTracker.Selection sel, List<Binding> result, out string itemMeshName)
        {
            itemMeshName = "";
            var log = GDCPlugin.Logger;

            if (sel.Character == null)
            {
                log?.LogDebug("[blendshape] Discover: character is null");
                return;
            }
            if (sel.Character.objClothes == null)
            {
                log?.LogDebug("[blendshape] Discover: objClothes array is null");
                return;
            }
            if (sel.SlotNo < 0 || sel.SlotNo >= sel.Character.objClothes.Length)
            {
                log?.LogDebug($"[blendshape] Discover: slot {sel.SlotNo} out of range");
                return;
            }

            var go = sel.Character.objClothes[sel.SlotNo];
            if (go == null)
            {
                log?.LogDebug($"[blendshape] Discover: objClothes[{sel.SlotNo}] is null");
                go = TryGetFromCmpClothes(sel);
                if (go == null) return;
                log?.LogDebug("[blendshape] Discover: fell back to cmpClothes");
            }

            var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            log?.LogDebug($"[blendshape] Discover: slot={sel.SlotNo} go='{go.name}' renderers={renderers.Length}");

            // Capture the first meaningful renderer name as the item identifier
            // for the "Selected item" label. Format: "o_top_camisole2_a" ->
            // strip the "o_top_" prefix and "_a"/"_b" size suffix to get
            // "camisole2".
            foreach (var r in renderers)
            {
                if (r == null) continue;
                itemMeshName = StripMeshNameDecoration(r.name);
                break;
            }

            foreach (var r in renderers)
            {
                if (r == null || r.sharedMesh == null)
                {
                    log?.LogDebug($"[blendshape]   renderer '{(r != null ? r.name : "null")}' has no sharedMesh, skipped");
                    continue;
                }

                var count = r.sharedMesh.blendShapeCount;
                log?.LogDebug($"[blendshape]   renderer '{r.name}' blendShapeCount={count}");
                if (count <= 0) continue;

                for (var i = 0; i < count; i++)
                {
                    var shapeName = r.sharedMesh.GetBlendShapeName(i);
                    var label     = string.IsNullOrEmpty(shapeName)
                        ? $"shape_{i}"
                        : ShortenItemShapeName(shapeName);

                    result.Add(new Binding(label, r, i));
                }
            }

            log?.LogDebug($"[blendshape] Discover: clothing bindings={result.Count}");
        }

        // GDC's mods name shapes like "<ItemName>_<id>_Adjust<DescriptiveName>".
        // For UI labels we want the trailing descriptive name, with "Adjust"
        // dropped when present. Falls back to the original name if the
        // pattern doesn't match.
        //
        // Examples:
        //   "SU Cute Apron_14912_AdjustBust"   -> "Bust"
        //   "SU Cute Apron_14912_AdjustWaist"  -> "Waist"
        //   "DGCotStunning_AdjustChestSquish"  -> "ChestSquish"
        //   "lash.e00_defo"                    -> "lash.e00_defo" (no pattern, unchanged)
        private static string ShortenItemShapeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            // Find the last "_Adjust" in the name and take everything after.
            const string marker = "_Adjust";
            var markerIdx = raw.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIdx >= 0)
            {
                var tail = raw.Substring(markerIdx + marker.Length);
                // "Adjust" alone is meaningless; only return if there's
                // content after it.
                if (!string.IsNullOrEmpty(tail)) return tail;
            }

            // No "_Adjust" pattern: try splitting on the last underscore as
            // a generic fallback (handles other GDC naming conventions).
            var lastUnderscore = raw.LastIndexOf('_');
            if (lastUnderscore > 0 && lastUnderscore < raw.Length - 1)
            {
                var tail = raw.Substring(lastUnderscore + 1);
                // Only return the tail if it looks descriptive (not just
                // a number). e.g. avoid stripping "lash.e00_defo" -> "defo"
                // since the prefix is meaningful for character shapes.
                // Length > 4 and starts with a letter feels like a good
                // heuristic for item shapes.
                if (tail.Length > 4 && char.IsLetter(tail[0])) return tail;
            }

            return raw;
        }

        // "o_top_camisole2_a" -> "camisole2". Strips the standard HS2 mesh
        // naming pattern (o_<slot>_<itemId>_<sizeVariant>). Falls back to
        // the raw name when the pattern doesn't match.
        private static string StripMeshNameDecoration(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var trimmed = name;
            // Drop the "o_<slot>_" prefix.
            if (trimmed.StartsWith("o_"))
            {
                var firstUnderscoreAfterSlot = trimmed.IndexOf('_', 2);
                if (firstUnderscoreAfterSlot > 0 && firstUnderscoreAfterSlot < trimmed.Length - 1)
                    trimmed = trimmed.Substring(firstUnderscoreAfterSlot + 1);
            }
            // Drop the "_a" / "_b" / "_0" size variant suffix.
            if (trimmed.Length > 2 && trimmed[trimmed.Length - 2] == '_')
                trimmed = trimmed.Substring(0, trimmed.Length - 2);
            return trimmed;
        }

        // HS2 sometimes carries the active clothing instance on the
        // CmpClothes component rather than objClothes (for certain
        // re-skinned outfit variants). This is a defensive fallback.
        private static GameObject? TryGetFromCmpClothes(in SelectionTracker.Selection sel)
        {
            try
            {
                var cmp = sel.Character.cmpClothes;
                if (cmp == null || sel.SlotNo < 0 || sel.SlotNo >= cmp.Length) return null;
                var c = cmp[sel.SlotNo];
                return c != null ? c.gameObject : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
