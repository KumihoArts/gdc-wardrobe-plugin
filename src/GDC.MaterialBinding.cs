using System;
using System.Collections.Generic;
using AIChara;          // ChaListDefine.CategoryNo
using MaterialEditorAPI;
using UnityEngine;

namespace GDCplugin
{
    // Material auto-discovery for the selected clothing slot.
    //
    // Strategy: I lean on MaterialEditor's curated XMLShaderProperties dict
    // (shaderName -> propertyName -> metadata) instead of trying to enumerate
    // shader fields at runtime. Unity 2018.4 doesn't fully expose property
    // metadata at runtime, and the curated list catches exactly the
    // properties shader authors meant to be editable. As a bonus this means
    // anything Material Editor itself can edit, we can edit too.
    //
    // Shape mirrors BlendshapeBinding so SliderWindow can render both
    // sections the same way: Discover returns a flat list of Binding
    // objects, each knowing how to Get/Set its own value and remembering
    // its min/max for the slider track.
    internal static class MaterialBinding
    {
        // User-set overrides for material float properties. Pushed every
        // frame in Plugin.LateUpdate so values survive game-driven shader
        // refreshes (Sideloader rebuilds, etc.).
        private static readonly Dictionary<Material, Dictionary<string, float>> _overrides
            = new Dictionary<Material, Dictionary<string, float>>();

        // Pre-edit float values, captured the first time the user drags a
        // given property on a given material. ClearOverrides writes these back
        // so the per-tab Reset actually restores the slider. Without this,
        // clearing _overrides only stops the push loop re-stamping; the live
        // material kept the dragged value and the slider (which reads live)
        // never moved, so Reset looked like a no-op.
        private static readonly Dictionary<Material, Dictionary<string, float>> _originalFloats
            = new Dictionary<Material, Dictionary<string, float>>();

        // Records the pre-edit value for (material, property) once. Called from
        // Binding.Set BEFORE the SetFloat so the captured value is the original,
        // not the value the user just dragged to.
        private static void CaptureOriginal(Material m, string property, float value)
        {
            if (m == null) return;
            if (!_originalFloats.TryGetValue(m, out var perMat))
            {
                perMat = new Dictionary<string, float>();
                _originalFloats[m] = perMat;
            }
            if (!perMat.ContainsKey(property)) perMat[property] = value;
        }

        public static void PushOverrides()
        {
            foreach (var kv in _overrides)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                foreach (var inner in kv.Value)
                {
                    var full = "_" + inner.Key;
                    if (mat.HasProperty(full))
                    {
                        try { mat.SetFloat(full, inner.Value); }
                        catch { /* material destroyed mid-frame */ }
                    }
                }
            }
        }

        // Restore every captured original onto its live material, then drop
        // both the overrides and the snapshots. This is what makes the
        // Materials tab Reset visibly roll the sliders back.
        public static void ClearOverrides()
        {
            foreach (var kv in _originalFloats)
            {
                var mat = kv.Key;
                if (mat == null) continue;
                foreach (var inner in kv.Value)
                {
                    var full = "_" + inner.Key;
                    if (mat.HasProperty(full))
                    {
                        try { mat.SetFloat(full, inner.Value); }
                        catch { /* material destroyed mid-frame */ }
                    }
                }
            }
            _originalFloats.Clear();
            _overrides.Clear();
        }

        internal static IEnumerable<KeyValuePair<Material, Dictionary<string, float>>> IterateOverrides()
            => _overrides;

        // Drops every float override + captured original for the materials on
        // the given slot's clothing item, WITHOUT restoring (unlike
        // ClearOverrides). Called when the user clicks a preset: PushOverrides
        // re-stamps any float the user dragged every frame, so without this a
        // preset can't change a property the user already touched (it reverts
        // next frame). Dropping the overrides lets the preset's float writes
        // stick and the sliders snap to the preset's values. The live values
        // stay whatever the preset just wrote; only our bookkeeping is cleared.
        //
        // This must NOT run inside PresetBinding.Apply (which the persistence
        // reapply-loop calls every frame): that would wipe legitimately-saved
        // material float overrides reapplied just before presets in
        // DeferredApply. It belongs on the one-shot user click only.
        public static void DropOverridesForSlot(in SelectionTracker.Selection sel)
        {
            if (sel.Character?.objClothes == null) return;
            if (sel.SlotNo < 0 || sel.SlotNo >= sel.Character.objClothes.Length) return;
            var go = sel.Character.objClothes[sel.SlotNo];
            if (go == null) return;

            var dropped = 0;
            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var mats = r.materials;
                if (mats == null) continue;
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    if (_overrides.Remove(m)) dropped++;
                    _originalFloats.Remove(m);
                }
            }
            if (dropped > 0)
                GDCPlugin.Logger?.LogDebug($"[material] Dropped float overrides on {dropped} material(s) for slot {sel.SlotNo} (preset apply)");
        }

        // True if (material, property) has a captured original, i.e. the user
        // has dragged this slider at least once this session. Drives the
        // per-slider Reset button's enabled state.
        internal static bool HasOriginal(Material m, string property)
        {
            return m != null
                && _originalFloats.TryGetValue(m, out var perMat)
                && perMat.ContainsKey(property);
        }

        // Restore one property on one material to its captured original, then
        // drop both its snapshot and its override so the push loop stops
        // re-stamping it. The per-slider counterpart to ClearOverrides.
        internal static void RestoreOriginal(Material m, string property)
        {
            if (m == null) return;
            if (_originalFloats.TryGetValue(m, out var origs)
                && origs.TryGetValue(property, out var val))
            {
                var full = "_" + property;
                if (m.HasProperty(full))
                {
                    try { m.SetFloat(full, val); }
                    catch { /* material destroyed mid-frame */ }
                }
                origs.Remove(property);
                if (origs.Count == 0) _originalFloats.Remove(m);
            }
            if (_overrides.TryGetValue(m, out var ovs))
            {
                ovs.Remove(property);
                if (ovs.Count == 0) _overrides.Remove(m);
            }
        }

        internal static void RecordRuntimeOverride(Material m, string property, float value)
        {
            if (!_overrides.TryGetValue(m, out var perMat))
            {
                perMat = new Dictionary<string, float>();
                _overrides[m] = perMat;
            }
            perMat[property] = value;
        }

        // One write target: a specific material on a specific renderer.
        // Struct (no ValueTuple) to stay compatible with the HS2 Mono builds
        // that lack System.ValueTuple in mscorlib, matching FavorHide's
        // existing caveat.
        public struct Target
        {
            public Renderer Renderer;
            public Material Material;
        }

        // Which subheader group a float belongs to on the Sliders tab. Snow and
        // Rain are the shader's integrated environmental controls: Snow is the
        // _Snow* family, Rain is the _GDCRain* family (GDC namespaced the rain
        // props so they're unambiguous). The shader's _Wet* floats are a
        // SEPARATE thing (not rain) and stay unexposed. Everything else GDC
        // curates is Material.
        public enum FloatCategory { Material, Snow, Rain }

        public sealed class Binding
        {
            public readonly string       Label;
            public readonly string       PropertyName; // without the leading "_"
            public readonly List<Target> Targets;
            public readonly float        Min;
            public readonly float        Max;
            public readonly float        DefaultValue;
            public readonly FloatCategory Category;

            public Binding(string label, string prop, List<Target> targets, float min, float max, float def, FloatCategory category)
            {
                Label        = label;
                PropertyName = prop;
                Targets      = targets;
                Min          = min;
                Max          = max;
                DefaultValue = def;
                Category     = category;
            }

            // Read from the first surviving target. All targets are intended
            // to hold the same value (we write to all simultaneously) so
            // reading from the first is correct.
            public float Get()
            {
                var full = "_" + PropertyName;
                for (var i = 0; i < Targets.Count; i++)
                {
                    var t = Targets[i];
                    if (t.Material == null || t.Renderer == null) continue;
                    if (!t.Material.HasProperty(full)) continue;
                    return t.Material.GetFloat(full);
                }
                return DefaultValue;
            }

            // Write to every target. Handles the multi-material case where
            // only one of the materials is actually visible: the user
            // doesn't have to figure out which one, dragging affects them
            // all and the visible one always reflects the change.
            public void Set(float value)
            {
                var full = "_" + PropertyName;
                for (var i = 0; i < Targets.Count; i++)
                {
                    var t = Targets[i];
                    if (t.Material == null || t.Renderer == null) continue;
                    if (!t.Material.HasProperty(full)) continue;
                    // Snapshot the pre-edit value before overwriting so Reset
                    // can restore it. No-op after the first drag of this prop.
                    CaptureOriginal(t.Material, PropertyName, t.Material.GetFloat(full));
                    try { t.Material.SetFloat(full, value); }
                    catch { continue; }
                    RecordRuntimeOverride(t.Material, PropertyName, value);
                }
            }

            // True when any target material has a captured original for this
            // property, i.e. the user has moved this slider this session.
            public bool IsOverridden
            {
                get
                {
                    for (var i = 0; i < Targets.Count; i++)
                    {
                        var t = Targets[i];
                        if (t.Material != null && HasOriginal(t.Material, PropertyName)) return true;
                    }
                    return false;
                }
            }

            // Restore this one property to its pre-edit value on every target.
            public void ResetToOriginal()
            {
                for (var i = 0; i < Targets.Count; i++)
                {
                    var t = Targets[i];
                    if (t.Material != null) RestoreOriginal(t.Material, PropertyName);
                }
            }

            // Alive when at least one target's renderer and material are
            // still around. The slider draws while any of them survive.
            public bool IsAlive
            {
                get
                {
                    for (var i = 0; i < Targets.Count; i++)
                    {
                        var t = Targets[i];
                        if (t.Renderer != null && t.Material != null) return true;
                    }
                    return false;
                }
            }
        }

        public sealed class DiscoveryResult
        {
            public readonly List<Binding> Floats = new List<Binding>();
            public string ItemMeshName = "";
        }

        // Slot index -> ChaListDefine category. Maker UI uses this same
        // mapping (see CvsC_Clothes.UpdateClothesList in HS2's decompiled
        // source). Index by sex: 0=male (mo_*), 1=female (fo_*).
        private static readonly ChaListDefine.CategoryNo[] _femaleSlotCategories = {
            ChaListDefine.CategoryNo.fo_top,
            ChaListDefine.CategoryNo.fo_bot,
            ChaListDefine.CategoryNo.fo_inner_t,
            ChaListDefine.CategoryNo.fo_inner_b,
            ChaListDefine.CategoryNo.fo_gloves,
            ChaListDefine.CategoryNo.fo_panst,
            ChaListDefine.CategoryNo.fo_socks,
            ChaListDefine.CategoryNo.fo_shoes,
        };
        private static readonly ChaListDefine.CategoryNo[] _maleSlotCategories = {
            ChaListDefine.CategoryNo.mo_top,
            ChaListDefine.CategoryNo.mo_bot,
            ChaListDefine.CategoryNo.unknown,
            ChaListDefine.CategoryNo.unknown,
            ChaListDefine.CategoryNo.mo_gloves,
            ChaListDefine.CategoryNo.unknown,
            ChaListDefine.CategoryNo.unknown,
            ChaListDefine.CategoryNo.mo_shoes,
        };

        public static DiscoveryResult Discover(in SelectionTracker.Selection sel)
        {
            var result = new DiscoveryResult();
            if (sel.Character == null) return result;
            if (sel.Character.objClothes == null) return result;
            if (sel.SlotNo < 0 || sel.SlotNo >= sel.Character.objClothes.Length) return result;

            // No GDC-only gate: per GDC's request, material sliders work for
            // any item that has them. The filter button still scopes the
            // Maker clothes list to her items if the user wants, but once a
            // (non-GDC) item is selected the slider window still gives the
            // user something to tweak. The slot-to-category arrays below
            // remain in case we want to bring the gate back as a config
            // option in the future.
            var go = sel.Character.objClothes[sel.SlotNo];
            if (go == null) return result;

            // Defensive guard: MaterialEditor might not be loaded. If its
            // shader property dict is null/empty, we return an empty list
            // instead of crashing. SliderWindow shows the placeholder.
            var shaderProps = SafeGetShaderProperties();
            if (shaderProps == null || shaderProps.Count == 0) return result;

            // Group pass: walk all renderers + materials, accumulate targets
            // keyed by property name. Each unique property name ends up with
            // one Binding whose Targets list covers every material that
            // declares it. Range/default come from the first catalogued
            // shader to define the property; subsequent matches just add
            // their material to the list.
            var grouped = new Dictionary<string, BindingDraft>();

            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            foreach (var r in renderers)
            {
                if (r == null) continue;

                // renderer.materials returns the live instance array. Writing
                // through these reaches the materials the renderer actually
                // displays, avoiding the shared-vs-instance stale-reference
                // gotcha.
                var materials = r.materials;
                if (materials == null) continue;

                foreach (var material in materials)
                {
                    if (material == null || material.shader == null) continue;

                    var shaderName = StripInstanceSuffix(material.shader.name);
                    var props = ResolveShaderProps(shaderName);
                    if (props == null)
                    {
                        GDCPlugin.Logger?.LogDebug($"[material] shader '{shaderName}' not in MaterialEditor catalog (slot {sel.SlotNo}, mat '{material.name}')");
                        continue;
                    }

                    foreach (var pd in props.Values)
                    {
                        if (pd == null) continue;
                        if (pd.Type != MaterialAPI.ShaderPropertyType.Float) continue;
                        if (string.IsNullOrEmpty(pd.Name)) continue;
                        if (!material.HasProperty("_" + pd.Name)) continue;

                        // Texture-paired no-ops still filtered: if NO target
                        // material has the matched texture set, drop the
                        // property. If at least one does, we keep it and
                        // write to all (the materials without the texture
                        // ignore the value at render time).
                        var noOpHere = IsNoOpWithoutTexture(material, pd.Name);

                        if (!grouped.TryGetValue(pd.Name, out var draft))
                        {
                            float min = pd.MinValue ?? 0f;
                            float max = pd.MaxValue ?? 1f;
                            if (max <= min) max = min + 1f;
                            float def = ParseFloatOrDefault(pd.DefaultValue, min);
                            draft = new BindingDraft
                            {
                                PropertyName = pd.Name,
                                Min = min, Max = max, Default = def,
                                Targets = new List<Target>(),
                                AnyTargetIsActive = false,
                            };
                            grouped[pd.Name] = draft;
                        }

                        draft.Targets.Add(new Target { Renderer = r, Material = material });
                        if (!noOpHere) draft.AnyTargetIsActive = true;
                    }
                }
            }

            // Promote drafts to Bindings. Property is shown only if it falls in
            // a known group (curated Material whitelist, or the Snow / Rain
            // environmental families) AND at least ONE of its target materials
            // has the paired texture set (or the property needs no texture).
            foreach (var draft in grouped.Values)
            {
                if (!TryCategorize(draft.PropertyName, out var category)) continue;
                if (!draft.AnyTargetIsActive) continue;
                result.Floats.Add(new Binding(
                    label: draft.PropertyName,
                    prop:  draft.PropertyName,
                    targets: draft.Targets,
                    min:  draft.Min,
                    max:  draft.Max,
                    def:  draft.Default,
                    category: category));
            }

            return result;
        }

        // Assigns a float property to a Sliders-tab group. Snow / Rain are
        // prefix-based on the shader's _Snow* / _GDCRain* families. The shader's
        // _Wet* floats are NOT rain (per GDC: "Wet is different") so they're not
        // grouped here. The curated Material whitelist gates everything else to
        // kill slider noise. Check GDCRain before the generic paths so it never
        // falls through to the whitelist.
        private static bool TryCategorize(string propertyName, out FloatCategory category)
        {
            if (propertyName.StartsWith("GDCRain", StringComparison.OrdinalIgnoreCase))
            {
                category = FloatCategory.Rain;
                return true;
            }
            if (propertyName.StartsWith("Snow", StringComparison.OrdinalIgnoreCase))
            {
                category = FloatCategory.Snow;
                return true;
            }
            if (_exposedFloats.Contains(propertyName))
            {
                category = FloatCategory.Material;
                return true;
            }
            category = FloatCategory.Material;
            return false;
        }

        // Intermediate accumulator used during the grouping pass. Mutable so
        // we can build it up over multiple renderer/material iterations.
        private sealed class BindingDraft
        {
            public string         PropertyName = "";
            public float          Min, Max, Default;
            public List<Target>   Targets      = new List<Target>();
            public bool           AnyTargetIsActive;
        }

        // GDC's curated whitelist of float properties to surface on the
        // Materials tab. The shader catalog declares dozens of floats; only
        // these matter for her workflow, so discovery drops everything else
        // to kill slider noise. Match is case-insensitive against the
        // catalogued property name (no leading "_").
        // Spellings MUST match GDC's shader catalog exactly (the match is
        // case-insensitive but not typo-tolerant). Her shader misspells several
        // ("Carvature", "Occulusion") and orders the UV2 rotator differently
        // ("DetailUV2Rotator"); using the dictionary spelling here silently
        // dropped those sliders, since a name that isn't in the catalog never
        // produces a Binding.
        private static readonly HashSet<string> _exposedFloats =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AlphaEx",
                "AlphaMaster",
                "CarvatureStrength",
                "DetailGlossScale",      "DetailGlossScale2",
                "DetailMetallicScale",   "DetailMetallicScale2",
                "DetailNormalMapScale",  "DetailNormalMapScale2",
                "DetailOcculusionScale", "DetailOcculusionScale2",
                "DetailUVRotator",       "DetailUV2Rotator",
                "OcculusionStrength",
            };

        // Float properties that only matter when their paired texture is set
        // on the material. Hiding them cleans up the slider list to just
        // things that actually do something visible.
        private static readonly Dictionary<string, string> _floatRequiresTexture = new Dictionary<string, string>
        {
            { "BumpScale",             "BumpMap" },
            { "DetailNormalMapScale",  "DetailNormalMap" },
            { "Parallax",              "ParallaxMap" },
            { "OcculusionStrength",    "OcclusionMap" },
            { "GlossMapScale",         "MetallicGlossMap" },
        };

        private static bool IsNoOpWithoutTexture(Material material, string floatProperty)
        {
            if (!_floatRequiresTexture.TryGetValue(floatProperty, out var requiredTex)) return false;
            var texPropName = "_" + requiredTex;
            if (!material.HasProperty(texPropName)) return false;
            var tex = material.GetTexture(texPropName);
            return tex == null;
        }

        // Mirror of Material Editor's NameFormatted extension: strips the
        // Unity-runtime "(Instance)" or " Instance" suffix that gets tacked
        // onto names of materials/shaders the moment they're modified.
        private static string StripInstanceSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            return name.Replace("(Instance)", "").Replace(" Instance", "").Trim();
        }

        // MaterialEditor exposes XMLShaderProperties as a SortedDictionary,
        // not a plain Dictionary. The wrapper catches any future breaking
        // API change so discovery degrades to "no sliders" instead of
        // crashing the plugin.
        private static SortedDictionary<string, Dictionary<string, MaterialEditorPluginBase.ShaderPropertyData>>? SafeGetShaderProperties()
        {
            try { return MaterialEditorPluginBase.XMLShaderProperties; }
            catch { return null; }
        }

        // Resolves a shader's MaterialEditor property catalog by name. The
        // XMLShaderProperties dict is case-sensitive (Ordinal), but a material's
        // stored shader-reference name can differ in case from the name the
        // shader is registered under (GDC's clothing materials reference
        // "GDC_Wardrobe_Shader" while the shader mod registers
        // "GDC_Wardrobe_shader"). Try the exact key first, then fall back to a
        // case-insensitive scan so a casing mismatch doesn't silently drop every
        // slider + break preset property copy. Shared by PresetBinding and
        // TextureBinding so all three discovery paths agree.
        internal static Dictionary<string, MaterialEditorPluginBase.ShaderPropertyData>? ResolveShaderProps(string shaderName)
        {
            var dict = SafeGetShaderProperties();
            if (dict == null || string.IsNullOrEmpty(shaderName)) return null;
            if (dict.TryGetValue(shaderName, out var props)) return props;
            foreach (var kv in dict)
                if (string.Equals(kv.Key, shaderName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        // Records a layered-copy material's reconstructable state for persistence:
        // its shader name plus every catalogued Float and Color value. Used by the
        // clothing-stack layering so a copy spawned fresh from the prefab on reload
        // can be returned to the MaterialEditor-edited look (custom shader + slider
        // tweaks) it had when the layer was authored. Without this a reload reverts
        // the copy to the prefab's default shader, so ME effect shaders (Hologram /
        // Fire / Galaxy) came back unlit / unicolored and float/color edits were
        // lost. Textures are handled separately (baked to PNG). Returns false when
        // the shader isn't in the ME catalog (nothing reliable to walk).
        internal static bool SnapshotMaterial(Material m, out string shaderName,
            out int renderQueue, List<string> keywords,
            Dictionary<string, float> floats, Dictionary<string, float[]> colors,
            List<string> textureProps)
        {
            shaderName = "";
            renderQueue = -1;
            if (m == null || m.shader == null) return false;
            shaderName = StripInstanceSuffix(m.shader.name);

            // Render queue + enabled keywords are the effect-defining state for a
            // non-catalog effect shader (Flame/Hologram): reassigning the shader
            // alone left the material at the prefab's opaque queue with the
            // effect's keywords off, so it rendered inert ("didn't load"). Both
            // are runtime-enumerable on any material, unlike arbitrary floats.
            renderQueue = m.renderQueue;
            if (keywords != null && m.shaderKeywords != null)
                keywords.AddRange(m.shaderKeywords);

            // Capture the shader NAME unconditionally. An effect shader that has
            // no ME XML property catalog (Hologram/Fire/Galaxy applied for their
            // look, not for slider editing) still needs its shader reassigned on
            // reload; bailing here when the catalog was missing dropped the whole
            // state, so the copy reverted to the prefab shader (the "layering
            // resets the shaders on coordinate load" bug). With no catalog we just
            // persist the shader and skip the float/color walk.
            var props = ResolveShaderProps(shaderName);
            if (props == null)
            {
                GDCPlugin.Logger?.LogDebug($"[layer] snapshot shader '{shaderName}': no ME catalog, persisting shader name only");
                return true;
            }

            foreach (var pd in props.Values)
            {
                if (pd == null || string.IsNullOrEmpty(pd.Name)) continue;
                var full = "_" + pd.Name;
                if (!m.HasProperty(full)) continue;
                switch (pd.Type)
                {
                    case MaterialAPI.ShaderPropertyType.Float:
                        floats[pd.Name] = m.GetFloat(full);
                        break;
                    case MaterialAPI.ShaderPropertyType.Color:
                        var c = m.GetColor(full);
                        colors[pd.Name] = new[] { c.r, c.g, c.b, c.a };
                        break;
                    case MaterialAPI.ShaderPropertyType.Texture:
                        // Effect-shader texture inputs (Flame ramp/noise, Hologram
                        // pattern) are ME-assigned and not on the respawned prefab,
                        // so the look needs them baked + restored. Report the full
                        // property name; the caller bakes the non-null ones.
                        if (textureProps != null && m.GetTexture(full) != null)
                            textureProps.Add(full);
                        break;
                }
            }
            return true;
        }

        // Reapplies a snapshotted material state onto a freshly spawned layer copy:
        // reassigns the (ME custom) shader by name via Shader.Find, then writes the
        // saved float + color values. Must run BEFORE the baked textures are
        // restored, because swapping the shader resets the material's property set.
        // A null Shader.Find result (shader not loaded on this install) leaves the
        // prefab shader in place rather than blanking the material.
        internal static void ApplyMaterial(Material m, string shaderName, int renderQueue,
            List<string> keywords, Dictionary<string, float> floats, Dictionary<string, float[]> colors)
        {
            if (m == null) return;
            if (!string.IsNullOrEmpty(shaderName))
            {
                var sh = ResolveShader(shaderName, out var shaderRq);
                if (sh != null)
                {
                    if (m.shader != sh) m.shader = sh;
                    // Render queue AFTER the shader swap (assigning a shader resets
                    // the queue to the shader default). Prefer the captured live
                    // queue; fall back to the shader's ME-declared queue.
                    if (renderQueue >= 0) m.renderQueue = renderQueue;
                    else if (shaderRq.HasValue) m.renderQueue = shaderRq.Value;
                    // Re-enable the captured keyword set so effect shaders that
                    // branch on keywords render their effect.
                    if (keywords != null) m.shaderKeywords = keywords.ToArray();
                    GDCPlugin.Logger?.LogDebug($"[layer] apply shader '{shaderName}' rq={m.renderQueue} kw={(keywords?.Count ?? 0)} floats={(floats?.Count ?? 0)} colors={(colors?.Count ?? 0)}");
                }
                else GDCPlugin.Logger?.LogWarning($"[layer] shader '{shaderName}' not in MaterialEditor LoadedShaders nor Shader.Find; layer keeps its prefab shader.");
            }
            if (floats != null)
                foreach (var kv in floats)
                {
                    var full = "_" + kv.Key;
                    if (m.HasProperty(full)) m.SetFloat(full, kv.Value);
                }
            if (colors != null)
                foreach (var kv in colors)
                {
                    var full = "_" + kv.Key;
                    if (kv.Value != null && kv.Value.Length == 4 && m.HasProperty(full))
                        m.SetColor(full, new Color(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]));
                }
        }

        // MaterialEditor's registry of every shader it has loaded (stock + mod
        // bundle shaders), keyed by name. Wrapper degrades to null instead of
        // crashing if a future API change removes it.
        private static Dictionary<string, MaterialEditorPluginBase.ShaderData>? SafeGetLoadedShaders()
        {
            try { return MaterialEditorPluginBase.LoadedShaders; }
            catch { return null; }
        }

        // Resolves a shader by name. Shaders loaded from a mod asset bundle are
        // NOT in Unity's global Shader.Find table, so Shader.Find returns null for
        // them and a layer copy kept its prefab shader (the "effect shader comes
        // back unlit/unicolored on reload" bug). MaterialEditor keeps every
        // registered shader in LoadedShaders keyed by name; resolve there first
        // (exact, then case-insensitive like ResolveShaderProps), and only fall
        // back to Shader.Find for stock built-in shaders. renderQueue carries the
        // shader's ME-declared queue (null when none) so the caller can sort
        // transparent/effect shaders correctly.
        internal static Shader? ResolveShader(string shaderName, out int? renderQueue)
        {
            renderQueue = null;
            if (string.IsNullOrEmpty(shaderName)) return null;
            var loaded = SafeGetLoadedShaders();
            if (loaded != null)
            {
                if (loaded.TryGetValue(shaderName, out var sd) && sd?.Shader != null)
                {
                    renderQueue = sd.RenderQueue;
                    GDCPlugin.Logger?.LogDebug($"[layer] resolve shader '{shaderName}' -> LoadedShaders (rq={renderQueue})");
                    return sd.Shader;
                }
                foreach (var kv in loaded)
                    if (kv.Value?.Shader != null
                        && string.Equals(kv.Key, shaderName, StringComparison.OrdinalIgnoreCase))
                    {
                        renderQueue = kv.Value.RenderQueue;
                        GDCPlugin.Logger?.LogDebug($"[layer] resolve shader '{shaderName}' -> LoadedShaders ci-match '{kv.Key}' (rq={renderQueue})");
                        return kv.Value.Shader;
                    }
            }
            var found = Shader.Find(shaderName);
            GDCPlugin.Logger?.LogDebug($"[layer] resolve shader '{shaderName}' -> {(found != null ? "Shader.Find" : "NULL (not in LoadedShaders nor Find)")}");
            return found;
        }

        private static float ParseFloatOrDefault(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? v
                : fallback;
        }
    }
}
