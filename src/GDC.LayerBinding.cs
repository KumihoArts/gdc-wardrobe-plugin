using System;
using System.Collections.Generic;
using System.Reflection;
using AIChara;
using HarmonyLib;
using UnityEngine;

namespace GDCplugin
{
    // Runtime "layer a clothing item as a body-skinned accessory" helper.
    //
    // HS2 ties each clothing slot to a category list, but the load primitive
    // ChaControl.LoadCharaFbxData(category, id, ..., copyWeights:1, objTop)
    // loads any list item's prefab and rebinds its skinned mesh to the live
    // body skeleton by bone name (AssignedAnotherWeights.AssignedWeightsAndSetBounds,
    // copyWeights==1). That is the exact path ordinary clothing uses, and the
    // mechanism GDC described: a skinned accessory ignores its attach bone and
    // rigs to the body weightpaint. So spawning a second copy of an equipped
    // item with copyWeights:1, held OUTSIDE objClothes, yields a body-skinned
    // layer that deforms with the body and leaves the original slot free for
    // another piece. See ACCESSORY_LAYERING_PLAN.md.
    internal static class LayerBinding
    {
        // LoadCharaFbxData is private on ChaControl. Signature confirmed from
        // the decompile: (int category, int id, string createName, bool
        // copyDynamicBone, byte copyWeights, Transform trfParent, int defaultId,
        // bool worldPositionStays). The sync overload runs the load coroutine
        // with AsyncFlags:false, which has no real yields, so the returned
        // GameObject is populated before the call returns.
        private static MethodInfo _loadFbx;
        private static MethodInfo LoadFbx =>
            _loadFbx ??= AccessTools.Method(typeof(ChaControl), "LoadCharaFbxData",
                new[] { typeof(int), typeof(int), typeof(string), typeof(bool),
                        typeof(byte), typeof(Transform), typeof(int), typeof(bool) });

        // Reads the (category, id) and display name of whatever clothing item
        // occupies the slot, via the ListInfoComponent every loaded clothing
        // GameObject carries. False when the slot is empty or carries no list
        // info.
        public static bool TryGetSlotItem(ChaControl cha, int slot, out int category, out int id, out string name)
        {
            category = -1;
            id       = -1;
            name     = "";
            if (cha?.objClothes == null || slot < 0 || slot >= cha.objClothes.Length) return false;
            var go = cha.objClothes[slot];
            if (go == null) return false;
            var lic = go.GetComponent<ListInfoComponent>();
            if (lic == null || lic.data == null) return false;
            category = lic.data.Category;
            id       = lic.data.Id;
            try { name = lic.data.GetInfo(ChaListDefine.KeyType.Name) ?? ""; } catch { name = ""; }
            return category >= 0 && id >= 0;
        }

        // Display name for a clothing slot index, matching HS2's kind order.
        private static readonly string[] SlotNames =
            { "Top", "Bottom", "Bra", "Panties", "Gloves", "Pantyhose", "Socks", "Shoes" };

        public static string SlotDisplayName(int slot)
            => slot >= 0 && slot < SlotNames.Length ? SlotNames[slot] : $"Slot {slot}";

        // Spawns a body-skinned copy of (category, id) parented under objTop and
        // returns it. The GameObject is NOT registered in objClothes, so the
        // clothing slot stays free. Null on failure (item not in list, missing
        // loader, etc.). defaultId:-1 makes a missing id return null rather than
        // silently loading the wrong default item.
        public static GameObject Spawn(ChaControl cha, int category, int id, string name)
        {
            if (cha == null) return null;
            var objTop = cha.objTop;
            if (objTop == null) return null;

            var loader = LoadFbx;
            if (loader == null)
            {
                GDCPlugin.Logger?.LogError("[layer] LoadCharaFbxData not found via reflection; HS2 build may have changed.");
                return null;
            }

            try
            {
                return loader.Invoke(cha, new object[]
                {
                    category, id, name,
                    /* copyDynamicBone   */ true,
                    /* copyWeights       */ (byte)1,
                    /* trfParent         */ objTop.transform,
                    /* defaultId         */ -1,
                    /* worldPositionStays*/ false,
                }) as GameObject;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogError($"[layer] Spawn failed for cat={category} id={id}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // One snapshotted composited texture: where it goes on the copy
        // (renderer index, material index, shader property) plus the runtime
        // Texture2D and its PNG bytes for persistence. Live is owned by the
        // layer entry and destroyed with it.
        internal sealed class BakedTex
        {
            public int       Ri;
            public int       Mi;
            public string    Prop = "";
            public byte[]    Png;    // PNG for card persistence
            public Texture2D Live;   // runtime texture, assigned to the material
        }

        // Texture properties HS2 paints with the per-slot composited render
        // texture (ChangeCustomClothes -> RebuildTextureAndSetMaterial). These
        // carry the region colors / patterns, so the layer must own a standalone
        // copy: otherwise a later slot swap recycles the slot's render texture
        // and the copy changes with it. Snapshotted to PNG so the exact colored
        // look survives save/load with no re-composite.
        private static readonly string[] CompositedProps = { "_MainTex", "_DetailMainTex" };

        // Standard garment texture slots: the respawned prefab already carries
        // these, so baking them into the card is pure bloat (they were the ~9 MB
        // of 2048 maps in tester v11). The material-state bake skips them and only
        // keeps an effect shader's own custom inputs.
        private static readonly HashSet<string> _prefabProvidedTextureSlots =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "_MainTex", "_MainTex2", "_DetailMainTex",
                "_BumpMap", "_BumpMap2", "_NormalMap",
                "_DetailNormalMap", "_DetailNormalMap2",
                "_DetailGlossMap", "_DetailGlossMap2",
                "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap",
                "_ParallaxMap", "_EmissionMap", "_DetailMask",
                "_ColorMask", "_LineMask", "_liquidmask", "_AlphaMask",
            };

        // Cap on a baked layer texture's dimension. Effect inputs (ramps, noise,
        // gradients) are small; anything larger is a garment map the prefab
        // provides, so it's skipped to keep cards small.
        private const int MaxBakedTextureSize = 1024;

        // Copies the fully-composited material state from the item currently in
        // `slot` onto `copy`, so the layered copy matches what the user sees on
        // the original (region colors, patterns, gloss). Renderer + material
        // order line up because both are clones of the same prefab. Composited
        // textures are snapshotted into standalone readable Texture2Ds (recorded
        // in `baked`, with PNG bytes) and assigned to the copy. Only valid while
        // the source item is still equipped in the slot.
        public static void CopyColorFromSlot(ChaControl cha, int slot, GameObject copy, List<BakedTex> baked)
        {
            if (cha?.objClothes == null || slot < 0 || slot >= cha.objClothes.Length) return;
            var src = cha.objClothes[slot];
            if (src == null || copy == null) return;

            var srcRends = src.GetComponentsInChildren<Renderer>(includeInactive: true);
            var dstRends = copy.GetComponentsInChildren<Renderer>(includeInactive: true);
            var rn = Mathf.Min(srcRends.Length, dstRends.Length);

            // Dedup identical composited textures (a dress often shares one
            // composite across renderers) so we read back + encode each once.
            var seen = new Dictionary<Texture, BakedTex>();

            for (var i = 0; i < rn; i++)
            {
                if (srcRends[i] == null || dstRends[i] == null) continue;
                var sm = srcRends[i].materials;   // live instances
                var dm = dstRends[i].materials;
                var mn = Mathf.Min(sm.Length, dm.Length);
                for (var j = 0; j < mn; j++)
                {
                    if (sm[j] == null || dm[j] == null) continue;
                    try
                    {
                        if (dm[j].shader != sm[j].shader) dm[j].shader = sm[j].shader;
                        dm[j].CopyPropertiesFromMaterial(sm[j]);
                        foreach (var prop in CompositedProps)
                        {
                            var b = Bake(dm[j], prop, i, j, seen);
                            if (b != null) baked.Add(b);
                        }
                    }
                    catch (Exception ex)
                    {
                        GDCPlugin.Logger?.LogDebug($"[layer] CopyColor mat {i}/{j} failed: {ex.Message}");
                    }
                }
                dstRends[i].materials = dm;
            }
        }

        // Snapshots one material texture property into a standalone readable
        // Texture2D + PNG and assigns it to the material. Returns the BakedTex
        // record, or null when the property is absent/unset. Reuses an earlier
        // snapshot of the same source texture (shared Live + Png) to avoid
        // duplicate GPU readbacks.
        private static BakedTex Bake(Material mat, string prop, int ri, int mi, Dictionary<Texture, BakedTex> seen)
        {
            if (mat == null || !mat.HasProperty(prop)) return null;
            var tex = mat.GetTexture(prop);
            if (tex == null) return null;
            // A 0-size texture (some effect-shader slots carry a placeholder)
            // makes RenderTexture.GetTemporary fail with "width & height must be
            // larger than 0". Skip it instead of spamming Unity errors.
            if (tex.width <= 0 || tex.height <= 0) return null;

            if (seen.TryGetValue(tex, out var prior) && prior.Live != null)
            {
                mat.SetTexture(prop, prior.Live);
                return new BakedTex { Ri = ri, Mi = mi, Prop = prop, Png = prior.Png, Live = prior.Live };
            }

            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0);
            var prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var snap = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, mipChain: true)
                {
                    name = (string.IsNullOrEmpty(tex.name) ? prop : tex.name) + "_layered",
                };
                snap.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                snap.Apply(updateMipmaps: true);
                mat.SetTexture(prop, snap);

                var png = ImageConversion.EncodeToPNG(snap);
                var b   = new BakedTex { Ri = ri, Mi = mi, Prop = prop, Png = png, Live = snap };
                seen[tex] = b;
                return b;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[layer] Bake {prop} failed: {ex.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // One layer-copy material's reconstructable state: where it sits (renderer
        // + material index) plus its shader name and every catalogued float/color
        // value. Captured at AddLayer from the live (ME-edited) copy, reapplied on
        // reload onto the fresh prefab spawn so custom ME shaders + slider edits
        // survive save/load. Pure data; nothing to dispose.
        internal sealed class MatState
        {
            public int    Ri;
            public int    Mi;
            public string Shader = "";
            public int    RenderQueue = -1;            // -1 = leave at shader default
            public List<string> Keywords = new List<string>();
            public Dictionary<string, float>   Floats = new Dictionary<string, float>();
            public Dictionary<string, float[]> Colors = new Dictionary<string, float[]>();
        }

        // Walks the copy's renderers/materials and records each material's shader +
        // catalogued float/color values into `states`. Run AFTER CopyColorFromSlot,
        // so it reads the fully ME-edited live state the user sees. Materials whose
        // shader isn't in the ME catalog are skipped (nothing reliable to record).
        public static void SnapshotMaterialState(GameObject copy, List<MatState> states, List<BakedTex> baked)
        {
            if (copy == null || states == null) return;
            // Keys already baked by CopyColorFromSlot (composited MainTex/DetailMainTex)
            // so the texture pass below doesn't re-encode them.
            var alreadyBaked = new HashSet<string>();
            if (baked != null)
                foreach (var b in baked)
                    if (b != null) alreadyBaked.Add($"{b.Ri}|{b.Mi}|{b.Prop}");
            var seen = new Dictionary<Texture, BakedTex>();

            var rends = copy.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (var i = 0; i < rends.Length; i++)
            {
                if (rends[i] == null) continue;
                var mats = rends[i].materials;   // live instances
                for (var j = 0; j < mats.Length; j++)
                {
                    if (mats[j] == null) continue;
                    var floats   = new Dictionary<string, float>();
                    var colors   = new Dictionary<string, float[]>();
                    var keywords = new List<string>();
                    var texProps = new List<string>();
                    if (!MaterialBinding.SnapshotMaterial(mats[j], out var shader, out var rq, keywords, floats, colors, texProps)) continue;
                    if (string.IsNullOrEmpty(shader) && floats.Count == 0 && colors.Count == 0) continue;
                    states.Add(new MatState { Ri = i, Mi = j, Shader = shader, RenderQueue = rq, Keywords = keywords, Floats = floats, Colors = colors });

                    // Bake the effect's own texture inputs (ramp/noise/pattern) so
                    // they survive save/load. Skip: already-baked composited maps;
                    // the standard garment slots the respawned prefab already
                    // carries (baking the 2048 dress maps bloated cards to ~9 MB);
                    // and textures above the cap (effect inputs are small, big maps
                    // are prefab assets). This keeps the bake to the small custom
                    // inputs only.
                    if (baked != null)
                        foreach (var prop in texProps)
                        {
                            if (alreadyBaked.Contains($"{i}|{j}|{prop}")) continue;
                            if (_prefabProvidedTextureSlots.Contains(prop)) continue;
                            var t = mats[j].GetTexture(prop);
                            if (t == null || t.width <= 0 || t.height <= 0) continue;
                            if (t.width > MaxBakedTextureSize || t.height > MaxBakedTextureSize) continue;
                            var b = Bake(mats[j], prop, i, j, seen);
                            if (b != null) { baked.Add(b); alreadyBaked.Add($"{i}|{j}|{prop}"); }
                        }
                }
            }
        }

        // Reapplies persisted material states onto a freshly spawned copy. Must run
        // BEFORE ApplyBaked (the shader swap inside resets the property set, so the
        // baked MainTex/DetailMainTex have to be re-set afterward).
        public static void ApplyMaterialState(GameObject copy, List<MatState> states)
        {
            if (copy == null || states == null || states.Count == 0)
            {
                GDCPlugin.Logger?.LogDebug($"[layer] ApplyMaterialState '{(copy != null ? copy.name : "null")}': {(states?.Count ?? 0)} state(s), nothing to apply");
                return;
            }
            GDCPlugin.Logger?.LogDebug($"[layer] ApplyMaterialState '{copy.name}': applying {states.Count} material state(s)");
            var rends = copy.GetComponentsInChildren<Renderer>(includeInactive: true);
            foreach (var st in states)
            {
                if (st == null || st.Ri < 0 || st.Ri >= rends.Length || rends[st.Ri] == null) continue;
                var mats = rends[st.Ri].materials;
                if (st.Mi < 0 || st.Mi >= mats.Length || mats[st.Mi] == null) continue;
                MaterialBinding.ApplyMaterial(mats[st.Mi], st.Shader, st.RenderQueue, st.Keywords, st.Floats, st.Colors);
                rends[st.Ri].materials = mats;
            }
        }

        // Clothing items ship every state in the prefab (full = objTopDef, half
        // = objTopHalf, ...) and HS2 shows only the active one via GameObject
        // SetActive. A fresh clone has them all active, so the copy renders full
        // AND half at once. Mirroring each transform's activeSelf from the source
        // slot replicates the current state. Returns the active mask in
        // depth-first order for persistence.
        public static List<bool> MirrorStateFromSlot(ChaControl cha, int slot, GameObject copy)
        {
            var mask = new List<bool>();
            if (cha?.objClothes == null || slot < 0 || slot >= cha.objClothes.Length) return mask;
            var src = cha.objClothes[slot];
            if (src == null || copy == null) return mask;

            var srcT = new List<Transform>(); CollectDepthFirst(src.transform, srcT);
            var dstT = new List<Transform>(); CollectDepthFirst(copy.transform, dstT);
            var n = Mathf.Min(srcT.Count, dstT.Count);
            for (var i = 0; i < n; i++)
            {
                var active = srcT[i].gameObject.activeSelf;
                dstT[i].gameObject.SetActive(active);
                mask.Add(active);
            }
            return mask;
        }

        // Applies a persisted active mask (depth-first order) onto a fresh copy.
        // Used on load, where there is no source slot to mirror from.
        public static void ApplyState(GameObject copy, List<bool> mask)
        {
            if (copy == null || mask == null || mask.Count == 0) return;
            var dstT = new List<Transform>(); CollectDepthFirst(copy.transform, dstT);
            var n = Mathf.Min(mask.Count, dstT.Count);
            for (var i = 0; i < n; i++)
                dstT[i].gameObject.SetActive(mask[i]);
        }

        private static void CollectDepthFirst(Transform t, List<Transform> outList)
        {
            outList.Add(t);
            for (var i = 0; i < t.childCount; i++)
                CollectDepthFirst(t.GetChild(i), outList);
        }

        // Reapplies persisted baked textures (PNG bytes -> Texture2D) onto a
        // freshly spawned copy. Used on load, where there is no live source to
        // copy from. Builds each Live texture once from its PNG and assigns it
        // to the matching renderer/material/property.
        public static void ApplyBaked(GameObject copy, List<BakedTex> baked)
        {
            if (copy == null || baked == null || baked.Count == 0) return;
            var rends = copy.GetComponentsInChildren<Renderer>(includeInactive: true);

            foreach (var b in baked)
            {
                if (b == null) continue;
                if (b.Live == null)
                {
                    if (b.Png == null || b.Png.Length == 0) continue;
                    var t = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true) { name = b.Prop + "_layered" };
                    if (!ImageConversion.LoadImage(t, b.Png)) { UnityEngine.Object.Destroy(t); continue; }
                    b.Live = t;
                }
                if (b.Ri < 0 || b.Ri >= rends.Length || rends[b.Ri] == null) continue;
                var mats = rends[b.Ri].materials;
                if (b.Mi < 0 || b.Mi >= mats.Length || mats[b.Mi] == null) continue;
                if (mats[b.Mi].HasProperty(b.Prop)) mats[b.Mi].SetTexture(b.Prop, b.Live);
                rends[b.Ri].materials = mats;
            }
        }

        // Body regions the clipping push can be scoped to. "All" is the original
        // uniform whole-mesh push; the rest target a body area so only the
        // clipping part of the garment moves, not the entire piece. Matched
        // against the live body skeleton bone names the copy is weighted to
        // (the load path remaps the copy's bones to the body), so it needs no
        // authored data. Order is the cycle order in the UI; index is persisted.
        public enum InflateRegion { All, Chest, Torso, Hips, Legs, Arms }

        public static readonly string[] RegionNames =
            { "All", "Chest", "Torso", "Hips", "Legs", "Arms" };

        public static InflateRegion NextRegion(InflateRegion r)
            => (InflateRegion)(((int)r + 1) % RegionNames.Length);

        // True when a body bone name belongs to the region. HS2 body bones are
        // cf_J_* (Mune=bust, Spine, Kosi=hips, Siri=glutes, Leg/Foot, Arm/
        // Shoulder/Hand). Substring match, lowercased, so minor name variants
        // across builds still hit.
        internal static bool RegionMatchesBone(InflateRegion region, string boneName)
        {
            if (region == InflateRegion.All) return true;
            if (string.IsNullOrEmpty(boneName)) return false;
            var n = boneName.ToLowerInvariant();
            switch (region)
            {
                case InflateRegion.Chest: return n.Contains("mune") || n.Contains("spine");
                case InflateRegion.Torso: return n.Contains("spine") || n.Contains("kosi") || n.Contains("mune");
                case InflateRegion.Hips:  return n.Contains("kosi") || n.Contains("siri");
                case InflateRegion.Legs:  return n.Contains("leg")  || n.Contains("foot") || n.Contains("siri");
                case InflateRegion.Arms:  return n.Contains("arm")  || n.Contains("shoulder") || n.Contains("hand");
                default: return true;
            }
        }

        // Sets vertex-normal inflation on a layered copy: the clipping fix.
        // A body-weightpainted skinned mesh ignores its object transform (Unity
        // skins from bone transforms), so moving/scaling the GameObject does
        // nothing on screen. Pushing the mesh verts outward along their normals
        // DOES show, and stops the layer poking through an inner garment. The
        // push is scaled per vertex by how much that vertex is weighted to the
        // chosen body region, so a region other than All moves only that area.
        // Adds the LayerInflater component on first nonzero use; a zero amount
        // with no inflater present is a no-op, so a layer that never needs
        // adjusting never clones its meshes.
        public static void SetInflate(GameObject copy, float inflate, InflateRegion region)
        {
            if (copy == null)
            {
                // Diagnostic at Info so it shows with default (Debug-off) tester
                // configs: a null copy means the layered GameObject is gone, so
                // the slider can never move anything.
                GDCPlugin.Logger?.LogInfo("[layer] SetInflate: copy is null (layer GameObject missing); push has no target.");
                return;
            }
            var inf = copy.GetComponent<LayerInflater>();
            var fresh = inf == null;
            if (inf == null)
            {
                if (Mathf.Approximately(inflate, 0f)) return;
                inf = copy.AddComponent<LayerInflater>();
            }
            inf.SetInflate(inflate, region);
            // Debug-level: the push path works; these were the investigation
            // scaffolding for the scale + re-skin fixes. Kept (at Debug) so a
            // future regression can be traced without re-instrumenting.
            if (fresh)
                GDCPlugin.Logger?.LogDebug(
                    $"[layer] push first-apply on '{copy.name}' amount={inflate:F3} region={region} -> {inf.TargetCount} skinned target(s), maxVertDelta={inf.LastMaxDelta:F4}");
        }
    }

    // Pushes a layered copy's skinned-mesh vertices out along their normals to
    // resolve clipping with an inner piece. Each SkinnedMeshRenderer gets a
    // private clone of its mesh: mutating the shared bundle mesh would deform
    // the item on every character wearing it (same footgun as sharedMaterials).
    // Offsets are always recomputed from a cached pristine copy of the verts so
    // the slider moves both ways. Lives on the copy GameObject and frees its
    // cloned meshes on destroy.
    internal sealed class LayerInflater : MonoBehaviour
    {
        // Class (not struct) so per-target Weights can be recomputed in place
        // when the region changes.
        private sealed class Target
        {
            public SkinnedMeshRenderer Smr;
            public Mesh      Working;
            public Vector3[] BaseVerts;
            public Vector3[] BaseNormals;
            public float[]   Weights;   // per-vertex push scale for the current region
            // Mesh size reference (local-space half-diagonal). The push amount is
            // a FRACTION of this, so the slider means the same visible inflation
            // regardless of the mesh's authoring scale. GDC's meshes export at a
            // ~6-12 unit local scale; a fixed absolute push (the old behavior) was
            // ~0.3% of the mesh and invisible. Captured per target so different
            // meshes in one copy each scale by their own size.
            public float     RefSize;
        }

        private readonly List<Target> _targets = new List<Target>();
        private bool _init;
        private float _amount;
        private LayerBinding.InflateRegion _region = LayerBinding.InflateRegion.All;
        private bool _weightsValid;

        // Diagnostics surfaced to the layer log: how many skinned meshes the push
        // actually moves, and the largest per-vertex displacement the last Apply
        // produced. A target count of 0 means no usable skinned mesh was found
        // (push can't work); a tiny maxDelta on a working target points at the
        // push range being too small to be visible rather than a binding fault.
        public int   TargetCount  => _targets.Count;
        public float LastMaxDelta { get; private set; }

        private void Init()
        {
            if (_init) return;
            _init = true;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                var src     = smr.sharedMesh;
                var verts   = src.vertices;
                var normals = src.normals;
                // Skip meshes without per-vertex normals; nothing to push along.
                if (verts == null || normals == null || normals.Length != verts.Length) continue;

                var working = Instantiate(src);   // private clone, owned + destroyed here
                working.name = src.name + "_inflate";
                smr.sharedMesh = working;
                // Force the renderer to re-skin every frame. Editing mesh.vertices
                // does NOT reliably make a SkinnedMeshRenderer re-bake: in Maker the
                // body is often near-static, so it skips skinning and our vertex push
                // stays invisible until the idle pose happens to update. That is the
                // "sometimes works, sometimes doesn't" intermittency. This makes the
                // push always show. Cost is one extra skinning pass per layer mesh.
                smr.forceMatrixRecalculationPerRender = true;
                // Local-space half-diagonal: the push is a fraction of this so the
                // slider scales with the mesh, not an absolute unit (see Target.RefSize).
                var refSize = Mathf.Max(src.bounds.extents.magnitude, 1e-4f);
                _targets.Add(new Target { Smr = smr, Working = working, BaseVerts = verts, BaseNormals = normals, RefSize = refSize });
            }
            if (_targets.Count == 0)
                GDCPlugin.Logger?.LogInfo(
                    $"[layer] inflate init on '{name}': found NO usable skinned mesh (non-readable verts or missing normals); push will do nothing.");
        }

        public void SetInflate(float amount, LayerBinding.InflateRegion region)
        {
            Init();
            if (!_weightsValid || region != _region)
            {
                _region = region;
                RecomputeWeights();
            }
            _amount = amount;
            Apply();
        }

        // Per-vertex push scale for the active region: 1 where the vertex is
        // fully weighted to the region's bones, 0 where it isn't, blended in
        // between. "All" is uniform 1. Falls back to uniform if the mesh has no
        // bone weights (so the push still works, just not regionally).
        private void RecomputeWeights()
        {
            foreach (var t in _targets)
            {
                var n = t.BaseVerts.Length;
                var w = new float[n];

                if (_region == LayerBinding.InflateRegion.All)
                {
                    for (var i = 0; i < n; i++) w[i] = 1f;
                    t.Weights = w;
                    continue;
                }

                var bones = t.Smr != null ? t.Smr.bones : null;
                var bw    = t.Working != null ? t.Working.boneWeights : null;
                if (bones == null || bw == null || bw.Length != n)
                {
                    for (var i = 0; i < n; i++) w[i] = 1f;   // uniform fallback
                    t.Weights = w;
                    continue;
                }

                var match = new bool[bones.Length];
                for (var b = 0; b < bones.Length; b++)
                    match[b] = bones[b] != null && LayerBinding.RegionMatchesBone(_region, bones[b].name);

                for (var i = 0; i < n; i++)
                {
                    var x = bw[i];
                    var s = 0f;
                    if (x.boneIndex0 >= 0 && x.boneIndex0 < match.Length && match[x.boneIndex0]) s += x.weight0;
                    if (x.boneIndex1 >= 0 && x.boneIndex1 < match.Length && match[x.boneIndex1]) s += x.weight1;
                    if (x.boneIndex2 >= 0 && x.boneIndex2 < match.Length && match[x.boneIndex2]) s += x.weight2;
                    if (x.boneIndex3 >= 0 && x.boneIndex3 < match.Length && match[x.boneIndex3]) s += x.weight3;
                    w[i] = Mathf.Clamp01(s);
                }
                t.Weights = w;
            }
            _weightsValid = true;
        }

        private void Apply()
        {
            var maxDelta = 0f;
            foreach (var t in _targets)
            {
                if (t.Working == null) continue;
                var w     = t.Weights;
                var verts = new Vector3[t.BaseVerts.Length];
                for (var i = 0; i < verts.Length; i++)
                {
                    var scale = w != null && i < w.Length ? w[i] : 1f;
                    // _amount is a fraction of the mesh size, so multiply by RefSize
                    // to get the local-space displacement. Scale-independent push.
                    var off   = _amount * scale * t.RefSize;
                    var delta = t.BaseNormals[i] * off;
                    verts[i] = t.BaseVerts[i] + delta;
                    // Measure ACTUAL displacement magnitude (normal length included)
                    // so the diagnostic reveals degenerate/zero normals instead of
                    // assuming unit length.
                    var dm = delta.magnitude;
                    if (dm > maxDelta) maxDelta = dm;
                }
                t.Working.vertices = verts;
                t.Working.RecalculateBounds();
                // Reassign the mesh so the SkinnedMeshRenderer re-reads the edited
                // vertex buffer this change, not whenever it next decides to re-bake.
                if (t.Smr != null) t.Smr.sharedMesh = t.Working;
            }
            LastMaxDelta = maxDelta;
        }

        private void OnDestroy()
        {
            foreach (var t in _targets)
                if (t.Working != null) Destroy(t.Working);
            _targets.Clear();
        }
    }
}
