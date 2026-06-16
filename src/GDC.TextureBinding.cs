using System;
using System.Collections.Generic;
using System.IO;
using AIChara;
using Ionic.Zip;
using Manager;          // Singleton<Character> for ChaListCtrl access
using MaterialEditorAPI;
using Sideloader.AutoResolver;
using UnityEngine;

namespace GDCplugin
{
    // Texture variant discovery + apply for the Textures tab.
    //
    // Discovery: walks the selected item's materials, finds their source
    // AssetBundle by matching currently-bound textures against loaded
    // bundles, then enumerates every Texture2D in that bundle as a possible
    // swap target. Also lists the available shader texture slots so the UI
    // can present a slot picker.
    //
    // Apply: MaterialAPI-style direct SetTexture against the material(s).
    // Runtime override push runs in Plugin.LateUpdate so the swap survives
    // game-driven shader refresh passes (Sideloader reloads, etc.).
    //
    // Persistence shape mirrors MaterialBinding: keyed by
    // "slot|materialName|propertyName -> textureAssetName". On reload we
    // re-resolve the texture by name from the same bundle.
    internal static class TextureBinding
    {
        // Runtime overrides keyed by material reference, like the float
        // override system. Pushed every frame from Plugin.LateUpdate.
        private static readonly Dictionary<Material, Dictionary<string, Texture>> _overrides
            = new Dictionary<Material, Dictionary<string, Texture>>();

        // First-time-set snapshot of each material/property's original
        // texture. Reset reads from this to restore the visible textures
        // back to whatever was bound when we first overrode them. Without
        // this Reset just clears the override dict but the materials keep
        // whatever variant was last applied.
        private static readonly Dictionary<Material, Dictionary<string, Texture>> _originals
            = new Dictionary<Material, Dictionary<string, Texture>>();

        public static void PushOverrides()
        {
            foreach (var kv in _overrides)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                foreach (var inner in kv.Value)
                {
                    var full = "_" + inner.Key;
                    if (!mat.HasProperty(full)) continue;
                    try { mat.SetTexture(full, inner.Value); }
                    catch { /* destroyed mid-frame */ }
                }
            }
        }

        // Reset path: walk every snapshotted original, write it back to the
        // material, then drop both maps. Has to run before _overrides is
        // cleared so PushOverrides doesn't immediately re-stamp the variant.
        public static void ClearOverrides()
        {
            foreach (var kv in _originals)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                foreach (var inner in kv.Value)
                {
                    var full = "_" + inner.Key;
                    if (!mat.HasProperty(full)) continue;
                    try { mat.SetTexture(full, inner.Value); }
                    catch { /* destroyed mid-frame */ }
                }
            }
            _originals.Clear();
            _overrides.Clear();
        }

        internal static IEnumerable<KeyValuePair<Material, Dictionary<string, Texture>>> IterateOverrides()
            => _overrides;

        internal static void RecordRuntimeOverride(Material m, string property, Texture tex)
        {
            if (!_overrides.TryGetValue(m, out var perMat))
            {
                perMat = new Dictionary<string, Texture>();
                _overrides[m] = perMat;
            }
            perMat[property] = tex;
        }

        // Captures the material's existing texture for a property BEFORE we
        // overwrite it. Idempotent: first capture wins, subsequent calls
        // are no-ops so we don't shift the original to "whatever we last
        // set" — that would defeat the purpose on Reset.
        internal static void CaptureOriginal(Material m, string property, Texture existing)
        {
            if (!_originals.TryGetValue(m, out var perMat))
            {
                perMat = new Dictionary<string, Texture>();
                _originals[m] = perMat;
            }
            if (!perMat.ContainsKey(property)) perMat[property] = existing;
        }

        // Per-slot revert: writes back the snapshotted original texture for
        // just this material/property and removes both the override and
        // the snapshot. Lets the user undo one slot without nuking the
        // rest of their edits.
        public static void RevertSlot(Slot slot)
        {
            if (slot?.Material == null || string.IsNullOrEmpty(slot.PropertyName)) return;
            var full = "_" + slot.PropertyName;
            if (_originals.TryGetValue(slot.Material, out var perMat) &&
                perMat.TryGetValue(slot.PropertyName, out var original))
            {
                if (slot.Material.HasProperty(full))
                {
                    try { slot.Material.SetTexture(full, original); } catch { }
                }
                perMat.Remove(slot.PropertyName);
                if (perMat.Count == 0) _originals.Remove(slot.Material);
            }
            if (_overrides.TryGetValue(slot.Material, out var ovMat))
            {
                ovMat.Remove(slot.PropertyName);
                if (ovMat.Count == 0) _overrides.Remove(slot.Material);
            }
        }

        // A swappable texture slot on a specific material. The UI shows one
        // button per Slot; clicking it sets _selectedSlot, after which a
        // thumbnail click writes its texture into the slot.
        public sealed class Slot
        {
            // Two label forms: Label is the full "matName / propName" shown
            // when an item has multiple materials, ShortLabel drops the
            // matName entirely. The UI picks whichever fits — single-material
            // items use Short so the buttons stay scannable.
            public readonly string   Label;
            public readonly string   ShortLabel;
            public readonly string   MaterialName;   // for grouping/dedup
            public readonly Renderer Renderer;
            public readonly Material Material;
            public readonly string   PropertyName;   // without underscore

            public Slot(string label, string shortLabel, string materialName, Renderer r, Material m, string prop)
            {
                Label        = label;
                ShortLabel   = shortLabel;
                MaterialName = materialName;
                Renderer = r; Material = m; PropertyName = prop;
            }

            public Texture Get()
            {
                if (Material == null) return null;
                var full = "_" + PropertyName;
                return Material.HasProperty(full) ? Material.GetTexture(full) : null;
            }

            public void Set(Texture tex)
            {
                if (Material == null) { GDCPlugin.Logger?.LogDebug($"[texture set] Material is null for {PropertyName}"); return; }
                var full = "_" + PropertyName;
                if (!Material.HasProperty(full)) { GDCPlugin.Logger?.LogDebug($"[texture set] {Material.name} has no property '{full}'"); return; }

                // Snapshot the existing texture once so Reset can restore it.
                var existing = Material.GetTexture(full);
                CaptureOriginal(Material, PropertyName, existing);

                try { Material.SetTexture(full, tex); }
                catch (Exception ex) { GDCPlugin.Logger?.LogWarning($"[texture set] SetTexture threw: {ex.Message}"); return; }
                RecordRuntimeOverride(Material, PropertyName, tex);
                GDCPlugin.Logger?.LogDebug($"[texture set] {Material.name}.{PropertyName} <- {(tex != null ? tex.name : "null")}");
            }

            public bool IsAlive => Renderer != null && Material != null;
        }

        // A discovered texture asset available to swap into any slot.
        public sealed class Variant
        {
            public readonly string    Name;
            public readonly Texture2D Texture;
            public readonly int       Width;
            public readonly int       Height;
            // Asset path inside the bundle (e.g.
            // "assets/mods/.../def_tex/leopard.jpg"). Empty when the
            // variant came from a currently-bound material reference
            // (no path info available). UI uses this to route textures
            // into the right grid by folder name.
            public readonly string    SourcePath;

            public Variant(Texture2D tex, string path = "")
            {
                Texture    = tex;
                Name       = tex != null ? tex.name : "(null)";
                Width      = tex != null ? tex.width : 0;
                Height     = tex != null ? tex.height : 0;
                SourcePath = path ?? "";
            }
        }

        public sealed class DiscoveryResult
        {
            public readonly List<Slot>    Slots    = new List<Slot>();
            public readonly List<Variant> Variants = new List<Variant>();
        }

        // Slot-intent classification. GDC's naming convention encodes the
        // target shader slot directly in the texture name ("bumpmap" for
        // BumpMap, "detail1" for DetailGlossMap, etc.). When the UI has a
        // slot selected, the variant grid filters to just those textures
        // that look intended for that slot. A "Show all" toggle in the UI
        // overrides this for users who want the full bundle list.
        //
        // Match rules (case-insensitive, applied in order):
        //   1. Exact short-name match (variant.Name == shortPropName).
        //   2. Prefix/contains match against a canonical alias list.
        //
        // Variants that match no slot fall through into a generic "Other"
        // bucket so they can still be picked when "Show all" is on.
        public static bool VariantMatchesSlot(string variantName, string propertyName)
        {
            if (string.IsNullOrEmpty(variantName) || string.IsNullOrEmpty(propertyName)) return false;
            var vn = variantName.ToLowerInvariant();
            var pn = propertyName.ToLowerInvariant();

            // Exact short-name match wins fast for the common case.
            if (vn == pn) return true;

            // Canonical alias table: which variant-name fragments belong to
            // which property. GDC uses short suggestive names; expanded
            // here so substring matching is intent-driven.
            switch (pn)
            {
                case "maintex":
                    return vn.Contains("maintex") || vn.Contains("maincolor") ||
                           vn == "bba" || vn == "colorito" || vn == "coloritos";
                case "bumpmap":
                    return vn.Contains("bump") || vn.Contains("normal");
                case "colormask":
                    return vn.Contains("colormask") || vn.Contains("colorma");
                case "detailmask":
                    return vn.Contains("detailmask") || vn.Contains("detailm");
                case "detailglossmap":
                    return vn == "detail1" || vn.Contains("detailgloss") && !vn.EndsWith("2");
                case "detailglossmap2":
                    return vn == "detail2" || vn.Contains("detailgloss") && vn.EndsWith("2");
                case "metallicglossmap":
                    return vn.Contains("metallic") || vn.Contains("metalgloss");
                case "occlusionmap":
                    return vn.Contains("occlusion") || vn == "ao" || vn.EndsWith("_ao");
                case "weatheringmap":
                    return vn.Contains("weather") && !vn.Contains("mask");
                case "weatheringmask":
                    return vn.Contains("weather") && vn.Contains("mask");
                case "noise":
                    return vn.Contains("noise");
                case "specularmap":
                    return vn.Contains("specular") || vn.Contains("spec");
                case "wetnessmap":
                    return vn.Contains("wet");
            }
            return false;
        }

        // Property names exposed in the slot picker. Sly wants the Textures
        // tab focused: only the main color map and the three detail-channel
        // slots. Bump/metallic/spec/weathering/etc. textures still ship in
        // the bundle as alternates (they appear in the variant grid) but
        // there's no slot for the user to drop them into. Match is case-
        // insensitive against MaterialEditor's property name.
        private static readonly HashSet<string> _exposedSlotProperties =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MainTex",
                "DetailMask",
                "DetailGlossMap",
                "DetailGlossMap2",
                "DetailMainTex",
            };

        // Pulls (category, id) out of the live selection so we can ask the
        // SideloaderBridge whether the item came from a GDC zipmod. Returns
        // false for stock items and any unresolvable slot.
        public static bool IsGDCItem(in SelectionTracker.Selection sel)
        {
            try
            {
                if (sel.Character == null) return false;
                var coord = sel.Character.nowCoordinate;
                if (coord?.clothes?.parts == null) return false;
                if (sel.SlotNo < 0 || sel.SlotNo >= coord.clothes.parts.Length) return false;

                var sex  = sel.Character.sex;
                var cats = sex == 0 ? _maleSlotCategories : _femaleSlotCategories;
                if (sel.SlotNo >= cats.Length) return false;
                var category = cats[sel.SlotNo];
                if (category == ChaListDefine.CategoryNo.unknown) return false;

                var id = coord.clothes.parts[sel.SlotNo].id;
                return SideloaderBridge.IsGDC((int)category, id);
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[texture] IsGDCItem failed: {ex.Message}");
                return false;
            }
        }

        public static DiscoveryResult Discover(in SelectionTracker.Selection sel)
        {
            GDCPlugin.DiscoverTicks++;   // perf diagnostic: count heavy discovery runs
            var result = new DiscoveryResult();
            if (sel.Character == null) return result;
            if (sel.Character.objClothes == null) return result;
            if (sel.SlotNo < 0 || sel.SlotNo >= sel.Character.objClothes.Length) return result;

            // GDC-only gate. Stock items and other modders' items have their
            // own texture pipelines; mixing them with this UI was confusing
            // users into thinking the tab was broken.
            if (!IsGDCItem(sel)) return result;

            var go = sel.Character.objClothes[sel.SlotNo];
            if (go == null) return result;

            // Step 1: walk materials, collect texture slots + currently-bound
            // textures (which we'll use to identify the source bundle).
            var sourceTextures = new List<Texture>();

            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var materials = r.materials; // instance, not shared
                if (materials == null) continue;

                foreach (var material in materials)
                {
                    if (material == null || material.shader == null) continue;

                    var shaderName = StripInstanceSuffix(material.shader.name);
                    var props = MaterialBinding.ResolveShaderProps(shaderName);
                    if (props == null) continue;

                    var matLabel = StripInstanceSuffix(material.name);

                    foreach (var pd in props.Values)
                    {
                        if (pd == null) continue;
                        if (pd.Type != MaterialAPI.ShaderPropertyType.Texture) continue;
                        if (string.IsNullOrEmpty(pd.Name)) continue;
                        var full = "_" + pd.Name;
                        if (!material.HasProperty(full)) continue;

                        // Still walk every texture property to harvest the
                        // currently-bound texture (used for bundle discovery
                        // AND as a Variant seed below). Only the main/detail
                        // properties become user-pickable Slots.
                        var current = material.GetTexture(full);
                        if (current != null && !sourceTextures.Contains(current))
                            sourceTextures.Add(current);

                        if (!_exposedSlotProperties.Contains(pd.Name)) continue;

                        var matShort  = ShortMatName(matLabel);
                        var propShort = ShortPropName(pd.Name);

                        result.Slots.Add(new Slot(
                            label:        string.IsNullOrEmpty(matShort) ? propShort : $"{matShort} / {propShort}",
                            shortLabel:   propShort,
                            materialName: matLabel,
                            r:            r,
                            m:            material,
                            prop:         pd.Name));
                    }
                }
            }

            // Step 2: find every AssetBundle that belongs to the item's
            // zipmod. For Sideloader mods the prefab and textures sit in
            // separate sibling bundles inside the same folder, so we need
            // to scan all of them, not just the one MainAB points at.
            var bundles = FindBundlesForItem(sel, sourceTextures);
            GDCPlugin.Logger?.LogDebug($"[texture] Discovered {bundles.Count} candidate bundle(s) for slot {sel.SlotNo}");

            // Step 3: enumerate swappable textures.
            //
            // Convention with GDC: alternates live in an "ExtraTextures"
            // folder inside the zipmod (assets/.../extratextures/*.png).
            // Anything outside that folder is internal to the mod and not
            // meant to be user-swappable. The currently-bound textures on
            // the item's materials count as "originals" so the user can
            // always swap back to them — they're added unconditionally.
            //
            // Dedupe by Texture2D reference so a texture referenced by both
            // the material and the ExtraTextures folder only appears once.
            var seen = new HashSet<Texture2D>();
            foreach (var src in sourceTextures)
            {
                if (src is Texture2D t2 && seen.Add(t2))
                {
                    result.Variants.Add(new Variant(t2));
                }
            }

            foreach (var bundle in bundles)
            {
                if (bundle == null) continue;

                string[] paths = null;
                try { paths = bundle.GetAllAssetNames(); }
                catch (Exception ex)
                {
                    GDCPlugin.Logger?.LogDebug($"[texture] GetAllAssetNames failed on {bundle.name}: {ex.Message}");
                }
                if (paths == null) continue;

                foreach (var path in paths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!ModConvention.PathHasSwapFolder(path)) continue;

                    Texture2D tex = null;
                    try { tex = bundle.LoadAsset<Texture2D>(path); }
                    catch { /* not a Texture2D, skip */ }
                    if (tex == null) continue;
                    if (!seen.Add(tex)) continue;
                    GDCPlugin.Logger?.LogDebug($"[texture] Extra pick: '{path}' -> {tex.name}");
                    result.Variants.Add(new Variant(tex, path));
                }
            }

            return result;
        }

        // Sex-aware slot -> category map, copy of MaterialBinding's table.
        // Used to derive the ChaListDefine.CategoryNo we need for the
        // ChaListInfo lookup.
        private static readonly ChaListDefine.CategoryNo[] _femaleSlotCategories = {
            ChaListDefine.CategoryNo.fo_top, ChaListDefine.CategoryNo.fo_bot,
            ChaListDefine.CategoryNo.fo_inner_t, ChaListDefine.CategoryNo.fo_inner_b,
            ChaListDefine.CategoryNo.fo_gloves, ChaListDefine.CategoryNo.fo_panst,
            ChaListDefine.CategoryNo.fo_socks, ChaListDefine.CategoryNo.fo_shoes,
        };
        private static readonly ChaListDefine.CategoryNo[] _maleSlotCategories = {
            ChaListDefine.CategoryNo.mo_top, ChaListDefine.CategoryNo.mo_bot,
            ChaListDefine.CategoryNo.unknown, ChaListDefine.CategoryNo.unknown,
            ChaListDefine.CategoryNo.mo_gloves, ChaListDefine.CategoryNo.unknown,
            ChaListDefine.CategoryNo.unknown, ChaListDefine.CategoryNo.mo_shoes,
        };

        // Returns the AssetBundle(s) that hold the loaded item's textures.
        //
        // I learned the hard way that the previous "sweep all siblings via
        // Sideloader.BundleManager" approach was catastrophic: it triggered
        // mass force-loads of hundreds of zipmod bundles whenever the user
        // selected a stock item (whose MainAB lives in the shared
        // "chara/00/fo_top_00.unity3d" bundle that every mod attaches to).
        // The game froze and threw CAB conflicts. Never again.
        //
        // Strategy now: stick to bundles that are ALREADY loaded. For
        // both stock and sideloader items, the bundle backing the loaded
        // GameObject is by definition already loaded. Match its name
        // against MainAB and we're done. No reflection, no force-loads.
        // Cache of bundles the plugin force-loaded from a zipmod ZIP. Held
        // indefinitely once loaded so subsequent Discover() calls reuse the
        // same AssetBundle reference instead of triple-loading on every
        // selection change. Key is the bundle path (without the "abdata/"
        // prefix), which matches AssetBundle.GetAllLoadedAssetBundles names.
        private static readonly Dictionary<string, AssetBundle> _forceLoaded
            = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        // Sibling bundle names already attempted this session (success OR fail).
        // GDC ships data_prefab_00N with non-unique CAB strings; Sideloader
        // recovers via randomized-CAB retry but a raw LoadFromMemory can't, so
        // the load fails and was never cached -> every Discover retried it and
        // flooded the log with CAB-conflict errors (plus an NRE in Sideloader's
        // RedirectHook). Recording the attempt bounds it to one try per bundle.
        private static readonly HashSet<string> _forceLoadAttempted
            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Public accessor for the same bundle set Discover walks, so the
        // preset system can find Material assets ("Leather", "Knit", "Latex", "Denim")
        // shipped alongside the prefab without re-implementing the
        // MainAB-resolution + sibling-walk + force-load chain.
        public static List<AssetBundle> GetCandidateBundlesForPresets(in SelectionTracker.Selection sel)
        {
            return FindBundlesForItem(sel, new List<Texture>());
        }

        private static List<AssetBundle> FindBundlesForItem(in SelectionTracker.Selection sel, List<Texture> sourceTextures)
        {
            var result = new List<AssetBundle>();
            var seen   = new HashSet<AssetBundle>();
            var mainAB = TryGetMainAB(sel);

            if (!string.IsNullOrEmpty(mainAB))
            {
                var bundle = FindLoadedBundleByPath(mainAB);
                if (bundle != null && seen.Add(bundle)) result.Add(bundle);

                // GDC ships some mods with the prefab in data_prefab_*.unity3d
                // and standalone textures in a sibling data_texture_*.unity3d
                // (or any other file inside the same zipmod folder). Walk
                // loaded bundles whose name shares the MainAB's parent
                // directory and add them too. Read-only walk — no Sideloader
                // force-loads.
                foreach (var sibling in FindLoadedSiblingBundles(mainAB))
                {
                    if (seen.Add(sibling)) result.Add(sibling);
                }

                // Sideloader lazy-loads sibling bundles only when something
                // references them; data_prefab_001.unity3d holding extra
                // textures won't be in memory yet at this point. Pull them
                // straight out of the zipmod ZIP and LoadFromMemory.
                foreach (var forced in ForceLoadMissingSiblings(mainAB, sel))
                {
                    if (seen.Add(forced)) result.Add(forced);
                }
            }

            // Fallback: stock items, or MainAB resolution returned a path
            // that doesn't match any loaded bundle's name. Walk the loaded
            // bundle set and match by currently-bound texture name overlap.
            if (result.Count == 0)
            {
                var fallback = FindBundleForItem(sourceTextures);
                if (fallback != null) result.Add(fallback);
            }

            return result;
        }

        // Makes sure the selected item's sibling bundles (e.g.
        // data_prefab_001.unity3d, where GDC puts presets / def_tex) are
        // loaded so discovery can read them. Sideloader only pages a sibling
        // in when something references it, so a preset bundle can sit unloaded.
        //
        // Names come from the zipmod's central directory (cheap index read,
        // no inflation); each bundle is then loaded through the game's
        // Sideloader-aware AssetBundleManager, which keeps a single,
        // ref-counted, Sideloader-managed copy. We do NOT raw-LoadFromMemory:
        // that made a second copy of the same bundle and triggered Sideloader's
        // duplicate-load recovery (the Preset-tab stall + CAB errors).
        // Caches borrowed/loaded bundles in _forceLoaded for the session.
        private static List<AssetBundle> ForceLoadMissingSiblings(string mainAB, in SelectionTracker.Selection sel)
        {
            var result = new List<AssetBundle>();
            try
            {
                var guid = TryGetGuid(sel);
                if (string.IsNullOrEmpty(guid)) return result;

                var zipArchives = Sideloader.Sideloader.ZipArchives;
                if (zipArchives == null) return result;

                string zipName;
                if (!zipArchives.TryGetValue(guid, out zipName) || string.IsNullOrEmpty(zipName))
                    return result;

                var lastSlash = mainAB.LastIndexOf('/');
                if (lastSlash <= 0) return result;
                var dirPart    = mainAB.Substring(0, lastSlash + 1);
                var entryPrefix = "abdata/" + dirPart;

                var zipPath = zipName;
                if (!File.Exists(zipPath) && !string.IsNullOrEmpty(Sideloader.Sideloader.ModsDirectory))
                {
                    zipPath = Path.Combine(Sideloader.Sideloader.ModsDirectory, zipName);
                }
                if (!File.Exists(zipPath))
                {
                    GDCPlugin.Logger?.LogDebug($"[texture] Sibling load: zip not found '{zipName}'");
                    return result;
                }

                // Enumerate the sibling bundle NAMES from the zipmod's central
                // directory only (Ionic reads the index, never inflates the
                // 53MB of bundle bytes). We no longer extract or raw-load here.
                var siblingNames = new List<string>();
                try
                {
                    using (var zf = ZipFile.Read(zipPath))
                    {
                        foreach (var entry in zf)
                        {
                            var fn = entry.FileName.Replace('\\', '/');
                            if (!fn.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!fn.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase)) continue;
                            if (fn.IndexOf("thumbnail", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            siblingNames.Add(fn.Substring("abdata/".Length));
                        }
                    }
                }
                catch (Exception ex)
                {
                    GDCPlugin.Logger?.LogDebug($"[texture] Sibling load: zip read failed '{zipPath}': {ex.Message}");
                    return result;
                }

                // Load each sibling through the GAME's Sideloader-aware
                // AssetBundleManager instead of a raw AssetBundle.LoadFromMemory.
                // The old raw load produced a SECOND in-memory copy of the same
                // bundle; the first LoadAsset off it then fired XUnity's redirect
                // hook, Sideloader tried to load its own copy, hit "another
                // AssetBundle with the same files is already loaded", retried with
                // a randomized CAB and NRE'd in TryGetObjectFromName. That whole
                // recovery storm is the 3-4s stall + the red errors on the Preset
                // tab. Going through the manager loads ONE Sideloader-managed,
                // ref-counted copy (it reads from Sideloader's already-mounted
                // archive, no zip inflation), so there is nothing to conflict.
                foreach (var bundleName in siblingNames)
                {
                    if (_forceLoaded.TryGetValue(bundleName, out var cached) && cached != null)
                    {
                        if (!result.Contains(cached)) result.Add(cached);
                        continue;
                    }

                    try
                    {
                        AssetBundle ab = null;

                        // Already mounted by the game/Sideloader: borrow it, do
                        // NOT take a reference we'd be responsible for releasing.
                        var names = AssetBundleManager.AllLoadedAssetBundleNames;
                        if (names != null && names.Contains(bundleName))
                        {
                            ab = AssetBundleManager.GetLoadedAssetBundle(bundleName)?.Bundle;
                        }
                        else if (!_forceLoadAttempted.Contains(bundleName))
                        {
                            // Attempt at most once per session.
                            _forceLoadAttempted.Add(bundleName);
                            ab = AssetBundleManager.LoadAssetBundle(bundleName)?.Bundle;
                            if (ab != null) _forceLoaded[bundleName] = ab;
                        }

                        if (ab != null && !result.Contains(ab))
                        {
                            result.Add(ab);
                            GDCPlugin.Logger?.LogDebug($"[texture] Loaded sibling '{bundleName}' via AssetBundleManager");
                        }
                    }
                    catch (Exception ex)
                    {
                        GDCPlugin.Logger?.LogDebug($"[texture] Sibling load failed '{bundleName}': {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[texture] ForceLoadMissingSiblings failed: {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        // Resolves the Sideloader GUID for the currently-selected item.
        // Falls back to null for stock items and unresolved IDs.
        private static string TryGetGuid(in SelectionTracker.Selection sel)
        {
            try
            {
                if (sel.Character == null) return null;
                var coord = sel.Character.nowCoordinate;
                if (coord?.clothes?.parts == null) return null;
                if (sel.SlotNo < 0 || sel.SlotNo >= coord.clothes.parts.Length) return null;
                var sex  = sel.Character.sex;
                var cats = sex == 0 ? _maleSlotCategories : _femaleSlotCategories;
                if (sel.SlotNo >= cats.Length) return null;
                var category = cats[sel.SlotNo];
                if (category == ChaListDefine.CategoryNo.unknown) return null;
                var id = coord.clothes.parts[sel.SlotNo].id;
                if (id < UniversalAutoResolver.BaseSlotID) return null;
                var resolve = UniversalAutoResolver.TryGetResolutionInfo(category, id);
                return resolve?.GUID;
            }
            catch { return null; }
        }

        // Returns every currently-loaded AssetBundle whose name shares the
        // MainAB's parent directory path. For
        //   mainAB = "gdpt_classy_dress_bundles/gdpt_classy_dress/data_prefab_000.unity3d"
        // dirPart becomes
        //   "gdpt_classy_dress_bundles/gdpt_classy_dress/"
        // and we return every bundle whose name starts with that prefix
        // (case-insensitive). This catches sibling bundles like
        // data_texture_000.unity3d that GDC ships alongside the prefab.
        //
        // The match is anchored on the full directory prefix so cross-mod
        // collisions are impossible: only bundles inside the same zipmod
        // folder pass.
        private static IEnumerable<AssetBundle> FindLoadedSiblingBundles(string mainAB)
        {
            var lastSlash = mainAB.LastIndexOf('/');
            if (lastSlash <= 0) yield break;
            var dirPart = mainAB.Substring(0, lastSlash + 1);
            if (string.IsNullOrEmpty(dirPart)) yield break;

            // Folder marker without trailing slash so a Sideloader-mounted
            // bundle whose runtime name strips the trailing slash still
            // matches. Also try the parent folder ("gdpt_classy_dress")
            // because Sideloader sometimes renames bundles to drop the
            // _bundles/ wrapper layer.
            var trimmed = dirPart.TrimEnd('/');
            var parts   = trimmed.Split('/');
            var innerFolder = parts.Length >= 1 ? parts[parts.Length - 1] : "";

            GDCPlugin.Logger?.LogDebug($"[texture] Sibling scan dirPart='{dirPart}' inner='{innerFolder}'");
            var totalLoaded = 0;
            var matches     = 0;
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null) continue;
                totalLoaded++;
                var bn = bundle.name;
                if (string.IsNullOrEmpty(bn)) continue;

                var hit = bn.IndexOf(dirPart, StringComparison.OrdinalIgnoreCase) >= 0
                          || (!string.IsNullOrEmpty(innerFolder)
                              && bn.IndexOf(innerFolder, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit)
                {
                    matches++;
                    GDCPlugin.Logger?.LogDebug($"[texture] Sibling bundle: '{bn}'");
                    yield return bundle;
                }
            }
            GDCPlugin.Logger?.LogDebug($"[texture] Sibling scan: {matches} match(es) of {totalLoaded} loaded bundle(s)");
        }

        // Pulls the MainAB path or returns null. Stays log-quiet on the
        // happy path; only logs at Debug for diagnostics.
        private static string TryGetMainAB(in SelectionTracker.Selection sel)
        {
            try
            {
                if (sel.Character == null) return null;
                var coord = sel.Character.nowCoordinate;
                if (coord?.clothes?.parts == null) return null;
                if (sel.SlotNo < 0 || sel.SlotNo >= coord.clothes.parts.Length) return null;

                var sex  = sel.Character.sex;
                var cats = sex == 0 ? _maleSlotCategories : _femaleSlotCategories;
                if (sel.SlotNo >= cats.Length) return null;
                var category = cats[sel.SlotNo];
                if (category == ChaListDefine.CategoryNo.unknown) return null;

                var id           = coord.clothes.parts[sel.SlotNo].id;
                var categoryDict = Singleton<Character>.Instance?.chaListCtrl?.GetCategoryInfo(category);
                if (categoryDict == null) return null;
                if (!categoryDict.TryGetValue(id, out var info) || info == null) return null;
                if (!info.dictInfo.TryGetValue((int)ChaListDefine.KeyType.MainAB, out var mainAB)) return null;
                if (string.IsNullOrEmpty(mainAB)) return null;

                GDCPlugin.Logger?.LogDebug($"[texture] MainAB slot {sel.SlotNo} id {id}: {mainAB}");
                return mainAB;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[texture] TryGetMainAB failed: {ex.Message}");
                return null;
            }
        }

        // Walks AssetBundle.GetAllLoadedAssetBundles() and returns the one
        // whose name matches mainAB. Case-insensitive substring match so we
        // tolerate the various path normalisations Sideloader applies
        // ("abdata/" prefix, slash vs backslash, etc.). Read-only walk:
        // does NOT touch Sideloader's lazy bundle dict, so no surprise
        // force-loads.
        private static AssetBundle FindLoadedBundleByPath(string mainAB)
        {
            try
            {
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;
                    var bn = bundle.name;
                    if (string.IsNullOrEmpty(bn)) continue;
                    if (bn.IndexOf(mainAB, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        mainAB.IndexOf(bn, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        GDCPlugin.Logger?.LogDebug($"[texture] Bundle match: bundle.name='{bn}' against MainAB='{mainAB}'");
                        return bundle;
                    }
                }
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[texture] FindLoadedBundleByPath failed: {ex.Message}");
            }
            return null;
        }

        // Kept around for diagnostic continuity but no longer the primary
        // path; FindBundlesForItem supersedes it. Returns the first loaded
        // bundle that matches an asset-path marker derived from MainAB.
        private static AssetBundle FindBundleByItemMainAB(in SelectionTracker.Selection sel)
        {
            try
            {
                if (sel.Character == null) return null;
                var coord = sel.Character.nowCoordinate;
                if (coord?.clothes?.parts == null) return null;
                if (sel.SlotNo < 0 || sel.SlotNo >= coord.clothes.parts.Length) return null;

                var sex  = sel.Character.sex;
                var cats = sex == 0 ? _maleSlotCategories : _femaleSlotCategories;
                if (sel.SlotNo >= cats.Length) return null;
                var category = cats[sel.SlotNo];
                if (category == ChaListDefine.CategoryNo.unknown) return null;

                var id = coord.clothes.parts[sel.SlotNo].id;

                // GetCategoryInfo returns the whole category dict
                // (Dictionary<int, ListInfoBase>); I then pick out the
                // specific item by id.
                var categoryDict = Singleton<Character>.Instance?.chaListCtrl?.GetCategoryInfo(category);
                if (categoryDict == null)
                {
                    GDCPlugin.Logger?.LogWarning($"[texture] No category dict for {category}");
                    return null;
                }
                if (!categoryDict.TryGetValue(id, out var info) || info == null)
                {
                    GDCPlugin.Logger?.LogWarning($"[texture] No ListInfo for {category} id={id}");
                    return null;
                }

                if (!info.dictInfo.TryGetValue((int)ChaListDefine.KeyType.MainAB, out var mainAB))
                {
                    GDCPlugin.Logger?.LogWarning($"[texture] No MainAB key on item {id}");
                    return null;
                }
                if (string.IsNullOrEmpty(mainAB))
                {
                    GDCPlugin.Logger?.LogWarning($"[texture] Empty MainAB on item {id}");
                    return null;
                }
                GDCPlugin.Logger?.LogInfo($"[texture] MainAB for slot {sel.SlotNo} id {id}: {mainAB}");

                // mainAB looks like
                //   "gdpt_classy_dress_bundles/gdpt_classy_dress/data_prefab_000.unity3d"
                // Sideloader sets AssetBundle.name to this same path when it
                // mounts the bundle, so the most reliable match is against
                // bundle.name. Fall back to asset-path substring matches if
                // a sideloader rebuild altered the name format.
                var markers = new List<string>();
                var parts   = mainAB.Split('/');
                markers.Add(mainAB);
                if (parts.Length >= 1) markers.Add(parts[0]);                       // gdpt_classy_dress_bundles
                if (parts.Length >= 2) markers.Add(parts[parts.Length - 2]);        // gdpt_classy_dress

                var scanned = 0;
                foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (bundle == null) continue;
                    scanned++;

                    // 1. Try the bundle.name directly. Sideloader bundles
                    //    typically carry the zipmod-relative path here.
                    var bn = bundle.name;
                    if (!string.IsNullOrEmpty(bn))
                    {
                        foreach (var marker in markers)
                        {
                            if (string.IsNullOrEmpty(marker)) continue;
                            if (bn.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                GDCPlugin.Logger?.LogInfo($"[texture] Matched bundle.name '{bn}' via marker '{marker}'");
                                return bundle;
                            }
                        }
                    }

                    // 2. Asset-path substring match as fallback.
                    string[] names;
                    try { names = bundle.GetAllAssetNames(); }
                    catch { continue; }
                    if (names == null) continue;

                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = names[i];
                        if (string.IsNullOrEmpty(name)) continue;
                        foreach (var marker in markers)
                        {
                            if (string.IsNullOrEmpty(marker)) continue;
                            if (name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                GDCPlugin.Logger?.LogInfo($"[texture] Matched bundle '{bn}' via asset-path marker '{marker}' on '{name}'");
                                return bundle;
                            }
                        }
                    }
                }
                GDCPlugin.Logger?.LogWarning($"[texture] No match across {scanned} loaded bundles for {mainAB}");
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[texture] FindBundleByItemMainAB failed: {ex.Message}");
            }
            return null;
        }

        // From a MainAB string like "foo_bar_bundles/foo_bar/data_prefab_000.unity3d"
        // returns "foo_bar" — the parent folder of the data file, which
        // tends to be unique to the item's bundle.
        private static string ExtractBundleMarker(string mainAB)
        {
            var parts = mainAB.Split('/');
            // Heuristic: penultimate segment is usually the most specific.
            // For Classy dress it's "gdpt_classy_dress".
            if (parts.Length >= 2) return parts[parts.Length - 2];
            return parts[0];
        }

        // Walks every loaded AssetBundle in the process and returns the
        // first one that contains any of the source textures. Fallback for
        // base-game items whose bundles don't match the sideloader pattern.
        private static AssetBundle FindBundleForItem(List<Texture> sourceTextures)
        {
            if (sourceTextures == null || sourceTextures.Count == 0) return null;

            // Collect candidate names from the bound textures. If a bundle
            // contains any of these named assets, it's our match.
            var sourceNames = new HashSet<string>();
            foreach (var t in sourceTextures)
            {
                if (t != null && !string.IsNullOrEmpty(t.name)) sourceNames.Add(t.name);
            }
            if (sourceNames.Count == 0) return null;

            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
            {
                if (bundle == null) continue;
                string[] names = null;
                try { names = bundle.GetAllAssetNames(); }
                catch { continue; }
                if (names == null) continue;

                foreach (var path in names)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    // GetAllAssetNames returns lowercase full paths like
                    // "assets/.../maintex.png". The trailing filename
                    // without extension is what matches Texture2D.name.
                    var slash = path.LastIndexOf('/');
                    var dot   = path.LastIndexOf('.');
                    var nameOnly = (slash >= 0 && dot > slash)
                        ? path.Substring(slash + 1, dot - slash - 1)
                        : path;
                    if (sourceNames.Contains(nameOnly)) return bundle;
                }
            }
            return null;
        }

        private static string StripInstanceSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            return name.Replace("(Instance)", "").Replace(" Instance", "").Trim();
        }

        // Builds a compact label for a slot button. Drops the standard HS2
        // mesh-name prefix (cf_m_top_, ct_, etc.) so we don't waste 12-15
        // characters on per-button text that's the same for every slot.
        // Also strips the item-id suffix some property names carry, leaving
        // just "MainTex" or "BumpMap" — the property the user actually picks.
        private static string BuildSlotLabel(string matName, string propName)
        {
            // Material side: strip common HS2 prefixes and trailing item ID.
            var matShort  = ShortMatName(matName);
            // Property side: many shaders embed the item id as a suffix
            // ("MainTex_camisole2"). Strip everything after the last "_"
            // when it looks like an item id (lowercase or numeric).
            var propShort = ShortPropName(propName);

            // When the material name still has length after shortening, prefix
            // it. Most single-material items end up with an empty matShort
            // and the label collapses to just the property name.
            return string.IsNullOrEmpty(matShort)
                ? propShort
                : $"{matShort} / {propShort}";
        }

        private static string ShortMatName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // Drop the common HS2 mesh prefix "cf_m_top_", "cf_m_bot_",
            // "ct_", etc. Anything before the final "_" that's all-lowercase
            // ASCII is treated as the prefix.
            var lastUnderscore = s.LastIndexOf('_');
            if (lastUnderscore > 0 && lastUnderscore < s.Length - 1)
                return s.Substring(lastUnderscore + 1);
            return s;
        }

        private static string ShortPropName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            // If the prop has a single trailing "_<itemid>" suffix and the
            // suffix is short, drop it. e.g. "MainTex_camisole2" -> "MainTex".
            var lastUnderscore = s.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var tail = s.Substring(lastUnderscore + 1);
                // Heuristic: tail is plausibly an item id if it's short and
                // doesn't look like a standard property word (DetailMask,
                // DetailGlossMap2, etc are CamelCase + maybe digits).
                if (tail.Length <= 16 && tail.Length >= 3 &&
                    char.IsLower(tail[0]))
                {
                    return s.Substring(0, lastUnderscore);
                }
            }
            return s;
        }
    }
}
