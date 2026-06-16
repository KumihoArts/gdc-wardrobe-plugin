using System;
using System.Collections.Generic;
using AIChara;
using MaterialEditorAPI;
using UnityEngine;

namespace GDCplugin
{
    // Material presets ("Leather", "Knit", "Latex", "Denim") packaged inside the
    // mod bundle. Clicking a preset button does NOT replace the live
    // Material reference (that would lose the user's Color1/2/3 edits and
    // break the colormask region wiring that HS2's maker depends on).
    // Instead the plugin mutates the existing material in place: copies the
    // preset's shader and all "surface look" properties onto it, while
    // preserving the user's color-region setup. The preserve list covers
    // ColorMask + Color1/2/3 + the detail-texture slots, so the dress's
    // color zones and detail variants survive the swap.
    internal static class PresetBinding
    {
        // Display list for the preset row: the known names (so the art always
        // reads "this is Leather" even when this item doesn't ship it) followed
        // by any discovered presets not in the known set. Availability gating
        // (enabled/dimmed) is the caller's job via the `available` set.
        public static List<string> GetOrderedDisplay(HashSet<string> available)
        {
            var ordered = new List<string>();
            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in ModConvention.KnownPresetOrder)
                if (seen.Add(k)) ordered.Add(k);
            if (available != null)
                foreach (var a in available)
                    if (!string.IsNullOrEmpty(a) && seen.Add(a)) ordered.Add(a);
            return ordered;
        }

        // Keeps a material if its name matches the active Part ("<preset>_<part>")
        // or is a bare known preset usable on any slot. Returns the display
        // preset name via `preset`.
        private static bool MatchesActivePart(string matName, string activePart, out string preset)
        {
            preset = null;
            if (ModConvention.TrySplitMaterial(matName, out var p, out var part))
            {
                if (!string.Equals(part, activePart, StringComparison.OrdinalIgnoreCase)) return false;
                preset = p;
                return true;
            }
            return ModConvention.IsBareKnownPreset(matName, out preset);
        }

        // Properties whose values stay on the live material when a preset
        // is applied. Only HS2's region-color pipeline (ColorMask + Color1/2/3)
        // is kept so the user's per-region colors survive the swap. Everything
        // else comes from the preset.
        //
        // Detail* textures are intentionally NOT preserved: GDC's presets are
        // distinguished by their _DetailGlossMap (the fabric pattern), so
        // preserving it made every preset look identical past the first swap.
        // They're no longer user-pickable in the Textures tab, so nothing else
        // depends on keeping them.
        private static readonly HashSet<string> _preservedProperties =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ColorMask",
                "Color1", "Color2", "Color3",
            };

        // Per-(renderer, materialIndex) snapshot of every property we
        // overwrote, plus the original shader. Reset writes these back so
        // the live material returns to its pre-preset state.
        private sealed class OriginalKey
        {
            public readonly int RendererInstanceId;
            public readonly int MaterialIndex;
            public OriginalKey(int r, int i) { RendererInstanceId = r; MaterialIndex = i; }
            public override bool Equals(object obj) =>
                obj is OriginalKey o
                && o.RendererInstanceId == RendererInstanceId
                && o.MaterialIndex == MaterialIndex;
            public override int GetHashCode() => RendererInstanceId * 397 ^ MaterialIndex;
        }

        private sealed class OriginalSnapshot
        {
            public Shader Shader;
            // Property name (without leading "_") -> value snapshot.
            public readonly Dictionary<string, object> Properties =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<OriginalKey, OriginalSnapshot> _originals
            = new Dictionary<OriginalKey, OriginalSnapshot>();

        private static readonly Dictionary<long, string> _activePresets
            = new Dictionary<long, string>();

        // The clothing-item GameObject instance id each active preset was
        // applied to. A slot swap-out/in rebuilds objClothes[slot] as a fresh
        // GameObject whose materials revert to the bundle originals, but the
        // _activePresets entry (keyed only by character+slot) survives. Without
        // this the preset reads "active" while the live item shows original, so
        // the UI highlight and the saved card disagree with what's on screen.
        // Comparing the current slot GameObject's id against this detects the
        // stale state. Set in Apply, cleared in Reset.
        private static readonly Dictionary<long, int> _activePresetItems
            = new Dictionary<long, int>();

        private static long ActiveKey(ChaControl c, int slot)
            => ((long)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c) << 8) | (uint)(slot & 0xFF);

        // True when the slot still holds the same item GameObject the preset was
        // applied to. A rebuilt (swapped) item has original materials, so an
        // active-preset entry tied to the old GameObject is stale. Returns true
        // when no id was recorded (legacy entry) so we don't second-guess it.
        private static bool PresetItemStillCurrent(ChaControl c, int slot, long key)
        {
            if (!_activePresetItems.TryGetValue(key, out var appliedId)) return true;
            var go = c?.objClothes != null && slot >= 0 && slot < c.objClothes.Length
                ? c.objClothes[slot] : null;
            return go != null && go.GetInstanceID() == appliedId;
        }

        public static string GetActivePreset(ChaControl c, int slot)
        {
            if (c == null) return null;
            var key = ActiveKey(c, slot);
            if (!_activePresets.TryGetValue(key, out var name)) return null;
            if (!PresetItemStillCurrent(c, slot, key))
            {
                // Item was swapped out and back: live materials are original
                // again, so drop the stale active-preset state. This keeps the
                // preset-row highlight and the save snapshot honest.
                _activePresets.Remove(key);
                _activePresetItems.Remove(key);
                return null;
            }
            return name;
        }

        // Enumerates every (slot, presetName) currently active on the given
        // character. Used by GDCharaController to snapshot active presets into
        // the card's ExtData. ActiveKey packs the character hash in the high
        // bits and the slot in the low byte, so we filter by hash and unpack
        // the slot back out.
        public static IEnumerable<KeyValuePair<int, string>> GetActivePresetsForCharacter(ChaControl c)
        {
            if (c == null) yield break;
            var hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c);
            foreach (var kv in _activePresets)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if ((int)(kv.Key >> 8) != hash) continue;
                var slot = (int)(kv.Key & 0xFF);
                // Skip stale entries (item swapped out and back): saving them
                // would write the preset onto a card that visually shows the
                // original textures. Don't remove during iteration; GetActivePreset
                // prunes them lazily on the UI side.
                if (!PresetItemStillCurrent(c, slot, kv.Key)) continue;
                yield return new KeyValuePair<int, string>(slot, kv.Value);
            }
        }

        // Single-item cache of collected preset Materials, keyed by the
        // clothing GameObject's instance id. The persistence reapply-loop
        // calls Apply every frame for ~2 seconds to survive Sideloader's
        // clothing rebuilds; without this cache that would re-run the heavy
        // CollectPresetMaterials (Resources.FindObjectsOfTypeAll) scan every
        // frame. A new item GO (selection change or rebuild) misses the cache
        // and re-collects, which is exactly when we want a fresh scan.
        private static int _collectCacheItemId;
        private static int _collectCacheSlot = -1;
        private static Dictionary<string, Material> _collectCache;

        private static Dictionary<string, Material> CollectCached(in SelectionTracker.Selection sel, GameObject go)
        {
            var id = go.GetInstanceID();
            // Key on slot too: the collected dict is part-filtered by the
            // active slot, so a stale entry from another slot must never be
            // returned even if a GameObject instance id is somehow reused.
            if (_collectCache != null && _collectCacheItemId == id && _collectCacheSlot == sel.SlotNo)
                return _collectCache;
            _collectCache       = CollectPresetMaterials(sel);
            _collectCacheItemId = id;
            _collectCacheSlot   = sel.SlotNo;
            return _collectCache;
        }

        public static HashSet<string> DiscoverAvailable(in SelectionTracker.Selection sel)
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var found = CollectPresetMaterials(sel);
                foreach (var kv in found) available.Add(kv.Key);
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[preset] DiscoverAvailable failed: {ex.Message}");
            }
            return available;
        }

        // Locate preset Material assets in the item's bundles. Tries every
        // discovery path Unity offers: top-level .mat entries, prefab sub-
        // assets via LoadAssetWithSubAssets, LoadAllAssets<Material>, raw
        // LoadAllAssets, and finally Resources.FindObjectsOfTypeAll. GDC's
        // current mods ship the Materials as PPtrs inside helper prefabs
        // (data1.prefab, data2.prefab) that don't surface in the asset
        // table; the Resources scan is what actually catches them.
        private static Dictionary<string, Material> CollectPresetMaterials(in SelectionTracker.Selection sel)
        {
            // Result keyed by display preset name ("Leather", "Knit", ...). A
            // material qualifies when its name splits to "<preset>_<part>" with
            // <part> matching the active slot's Part, or it's a bare known preset.
            var result     = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            var activePart = ModConvention.PartForSlot(sel.SlotNo);
            var bundles    = TextureBinding.GetCandidateBundlesForPresets(sel);

            void TryAdd(Material mat)
            {
                if (mat == null || string.IsNullOrEmpty(mat.name)) return;
                if (!MatchesActivePart(mat.name, activePart, out var preset)) return;
                if (!result.ContainsKey(preset)) result[preset] = mat;
            }

            foreach (var bundle in bundles)
            {
                if (bundle == null) continue;

                string[] paths = null;
                try { paths = bundle.GetAllAssetNames(); }
                catch { paths = null; }
                if (paths != null)
                {
                    foreach (var path in paths)
                    {
                        if (string.IsNullOrEmpty(path)) continue;
                        if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                        {
                            Material mat = null;
                            try { mat = bundle.LoadAsset<Material>(path); }
                            catch { mat = null; }
                            TryAdd(mat);
                        }
                    }

                    foreach (var path in paths)
                    {
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;

                        UnityEngine.Object[] subs = null;
                        try { subs = bundle.LoadAssetWithSubAssets(path); }
                        catch { subs = null; }
                        if (subs != null)
                        {
                            foreach (var obj in subs)
                                if (obj is Material m) TryAdd(m);
                        }

                        GameObject go = null;
                        try { go = bundle.LoadAsset<GameObject>(path); }
                        catch { go = null; }
                        if (go == null) continue;

                        var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
                        foreach (var r in renderers)
                        {
                            if (r == null) continue;
                            var mats = r.sharedMaterials;
                            if (mats == null) continue;
                            foreach (var mat in mats) TryAdd(mat);
                        }
                    }
                }

                try
                {
                    var topLevel = bundle.LoadAllAssets<Material>();
                    if (topLevel != null)
                        foreach (var mat in topLevel) TryAdd(mat);
                }
                catch { /* non-fatal */ }
            }

            // NO global Resources.FindObjectsOfTypeAll fallback here. It is
            // unscoped: it returns every Material loaded in the process, so a
            // GDC preset (Leather_Top etc.) from one mod leaks onto an
            // unrelated garment in the same slot, and first-wins over the
            // global pool can pick the wrong-part material. The scoped passes
            // above already cover GDC's current layout (preset .mat ship as
            // real assets in the item's sibling bundle) AND the legacy
            // PPtr-subasset case (pass over each prefab's LoadAssetWithSubAssets
            // + renderer.sharedMaterials, still bound to THIS item's bundles).
            if (GDCPlugin.Logger != null)
            {
                var names = string.Join(", ", new List<string>(result.Keys).ToArray());
                GDCPlugin.Logger.LogDebug($"[preset] discovered {result.Count} for part '{activePart}' (slot {sel.SlotNo}): {names}");
            }

            return result;
        }

        // Apply a preset: mutate every live material on the item with the
        // preset's shader + non-preserved properties. The live material
        // reference does NOT change, so HS2's color slots / colormask
        // wiring stays intact and the user's prior Color1/2/3 + detail
        // texture overrides survive the swap.
        public static bool Apply(in SelectionTracker.Selection sel, string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return false;
            if (sel.Character == null) return false;
            if (sel.Character.objClothes == null) return false;
            if (sel.SlotNo < 0 || sel.SlotNo >= sel.Character.objClothes.Length) return false;

            var go = sel.Character.objClothes[sel.SlotNo];
            if (go == null) return false;

            var collected = CollectCached(sel, go);
            if (!collected.TryGetValue(presetName, out var presetMat) || presetMat == null)
            {
                GDCPlugin.Logger?.LogWarning($"[preset] No Material asset named '{presetName}' in this item's bundles");
                return false;
            }

            // Diagnostic: the clicked preset name vs the actual material asset
            // resolved for the active slot's Part. If these ever disagree
            // (wrong part / wrong preset), it shows up here in one line.
            GDCPlugin.Logger?.LogInfo(
                $"[preset] slot {sel.SlotNo} part '{ModConvention.PartForSlot(sel.SlotNo)}': clicked '{presetName}' -> material '{presetMat.name}'");

            var presetShaderName = StripInstanceSuffix(presetMat.shader != null ? presetMat.shader.name : "");
            var presetProperties = MaterialBinding.ResolveShaderProps(presetShaderName);
            if (presetProperties == null)
                GDCPlugin.Logger?.LogWarning($"[preset] shader '{presetShaderName}' not in MaterialEditor catalog; only the shader ref will swap, no per-property copy (this is why presets look identical)");

            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers.Length == 0)
            {
                // Clothing meshes haven't been instantiated yet (common when
                // the persistence loop fires right after a card load). Report
                // not-applied so the caller retries on a later frame.
                return false;
            }

            var changed = 0;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.materials;
                if (mats == null || mats.Length == 0) continue;

                for (var i = 0; i < mats.Length; i++)
                {
                    var live = mats[i];
                    if (live == null) continue;

                    var key = new OriginalKey(r.GetInstanceID(), i);
                    OriginalSnapshot snap;
                    if (!_originals.TryGetValue(key, out snap))
                    {
                        snap = new OriginalSnapshot { Shader = live.shader };
                        _originals[key] = snap;
                    }

                    if (CopyPresetOntoLive(presetMat, live, presetProperties, snap))
                        changed++;
                }
            }

            // Mark active as soon as we have a live target. Returning true even
            // when changed == 0 (steady state after the first apply) is what
            // lets the persistence reapply-loop treat an already-applied preset
            // as success instead of retrying for the full window. changed > 0
            // only happens on the first apply or after a rebuild reverted the
            // materials, which is exactly when a log line is informative.
            _activePresets[ActiveKey(sel.Character, sel.SlotNo)] = presetName;
            // Remember which item GameObject this preset landed on, so a later
            // slot swap-out/in (which rebuilds the GameObject back to original
            // materials) can be detected as stale instead of silently saved.
            _activePresetItems[ActiveKey(sel.Character, sel.SlotNo)] = go.GetInstanceID();
            if (changed > 0)
                GDCPlugin.Logger?.LogInfo($"[preset] Applied '{presetName}' to slot {sel.SlotNo} ({changed} material slot(s))");
            return true;
        }

        // Copies preset's shader + every non-preserved property from
        // shaderProps onto live. Snapshots the pre-write value into snap
        // (first touch only) so Reset can restore.
        private static bool CopyPresetOntoLive(
            Material preset, Material live,
            Dictionary<string, MaterialEditorPluginBase.ShaderPropertyData> presetProperties,
            OriginalSnapshot snap)
        {
            if (preset == null || live == null) return false;

            var modified = false;
            if (preset.shader != null && live.shader != preset.shader)
            {
                live.shader = preset.shader;
                modified = true;
            }

            if (presetProperties == null) return modified;

            foreach (var pd in presetProperties.Values)
            {
                if (pd == null || string.IsNullOrEmpty(pd.Name)) continue;
                if (_preservedProperties.Contains(pd.Name)) continue;
                var full = "_" + pd.Name;
                if (!preset.HasProperty(full)) continue;
                if (!live.HasProperty(full)) continue;

                try
                {
                    switch (pd.Type)
                    {
                        case MaterialAPI.ShaderPropertyType.Texture:
                            // GDC's preset Materials intentionally leave
                            // textures like _MainTex / _BumpMap as null —
                            // those should keep the live material's value.
                            // Only writing non-null preset textures means
                            // Detail* + any other texture the preset
                            // actually sets gets through, while base maps
                            // stay intact.
                            var presetTex = preset.GetTexture(full);
                            if (presetTex == null) break;
                            // Skip when already applied so the persistence
                            // reapply-loop is a cheap no-op in steady state.
                            if (live.GetTexture(full) == presetTex) break;
                            if (!snap.Properties.ContainsKey(pd.Name))
                                snap.Properties[pd.Name] = live.GetTexture(full);
                            live.SetTexture(full, presetTex);
                            modified = true;
                            break;
                        case MaterialAPI.ShaderPropertyType.Color:
                            var presetCol = preset.GetColor(full);
                            if (live.GetColor(full) == presetCol) break;
                            if (!snap.Properties.ContainsKey(pd.Name))
                                snap.Properties[pd.Name] = live.GetColor(full);
                            live.SetColor(full, presetCol);
                            modified = true;
                            break;
                        case MaterialAPI.ShaderPropertyType.Float:
                            var presetF = preset.GetFloat(full);
                            if (Mathf.Approximately(live.GetFloat(full), presetF)) break;
                            if (!snap.Properties.ContainsKey(pd.Name))
                                snap.Properties[pd.Name] = live.GetFloat(full);
                            live.SetFloat(full, presetF);
                            modified = true;
                            break;
                        case MaterialAPI.ShaderPropertyType.Keyword:
                            // Keywords aren't material properties per se;
                            // skip until needed.
                            break;
                    }
                }
                catch (Exception ex)
                {
                    GDCPlugin.Logger?.LogDebug($"[preset] CopyPresetOntoLive '{pd.Name}': {ex.Message}");
                }
            }

            return modified;
        }

        // Reset: restore each captured snapshot back onto its live
        // material. Drops the snapshot once written so a second Reset is
        // a no-op.
        public static bool Reset(in SelectionTracker.Selection sel)
        {
            if (sel.Character == null) return false;
            if (sel.Character.objClothes == null) return false;
            if (sel.SlotNo < 0 || sel.SlotNo >= sel.Character.objClothes.Length) return false;
            var go = sel.Character.objClothes[sel.SlotNo];
            if (go == null) return false;

            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            var restored  = 0;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.materials;
                if (mats == null) continue;

                for (var i = 0; i < mats.Length; i++)
                {
                    var live = mats[i];
                    if (live == null) continue;
                    var key = new OriginalKey(r.GetInstanceID(), i);
                    if (!_originals.TryGetValue(key, out var snap)) continue;

                    if (snap.Shader != null) live.shader = snap.Shader;

                    foreach (var pv in snap.Properties)
                    {
                        var full = "_" + pv.Key;
                        if (!live.HasProperty(full)) continue;
                        try
                        {
                            switch (pv.Value)
                            {
                                case Texture t: live.SetTexture(full, t); break;
                                case Color  c: live.SetColor(full, c);   break;
                                case float  f: live.SetFloat(full, f);   break;
                                case null:     live.SetTexture(full, null); break;
                            }
                        }
                        catch { /* ignore */ }
                    }

                    _originals.Remove(key);
                    restored++;
                }
            }

            _activePresets.Remove(ActiveKey(sel.Character, sel.SlotNo));
            _activePresetItems.Remove(ActiveKey(sel.Character, sel.SlotNo));
            GDCPlugin.Logger?.LogInfo($"[preset] Reset slot {sel.SlotNo} ({restored} material slot(s) restored)");
            return restored > 0;
        }

        private static string StripInstanceSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Replace("(Instance)", "").Replace(" Instance", "").Trim();
        }
    }
}
