using System;
using System.Collections.Generic;
using AIChara;      // ChaFileCoordinate for the coordinate save hooks
using ExtensibleSaveFormat;
using KKAPI;        // GameMode enum lives here
using KKAPI.Chara;
using MessagePack;
using UnityEngine;

namespace GDCplugin
{
    // Per-character controller that persists blendshape overrides to the
    // character card via KKAPI's ExtendedSave system. The runtime override
    // dict in BlendshapeBinding is global and ephemeral; this controller
    // owns the durable, per-character storage.
    //
    // Save path: snapshot the runtime overrides whose renderers belong to
    // this character, serialise as a flat string->float map keyed by
    // "rendererName|shapeIndex".
    //
    // Load path: deserialise, walk this character's SkinnedMeshRenderers,
    // re-establish runtime overrides so LateUpdate keeps pushing them.
    public class GDCharaController : CharaCustomFunctionController
    {
        private const string ExtDataKey         = "BlendshapeOverrides";
        private const string MaterialExtDataKey = "MaterialOverrides";
        private const string PresetExtDataKey   = "PresetOverrides";
        private const string TextureExtDataKey  = "TextureOverrides";
        private const string LayerExtDataKey      = "LayerOverrides";
        private const string LayerTexExtDataKey   = "LayerTextures";
        private const string LayerStateExtDataKey = "LayerStates";
        private const string LayerMatExtDataKey   = "LayerMaterials";

        // A clothing item layered as a body-skinned accessory copy. Holds the
        // source identity (category + sideloader GUID/origId for cross-machine
        // re-resolve, plus the same-machine resolved id as a fast path) and the
        // live spawned GameObject. Persisted as "category|guid|origId|resolvedId"
        // strings. Live is rebuilt on load via LayerBinding.Spawn; it is never
        // serialized. See [[hs2-loadcharafbxdata-weightbind]] and the plan doc.
        internal class LayerEntry
        {
            public int        Category;
            public int        ResolvedId;   // runtime id at save time (same-machine fast path)
            public string     Guid = "";    // sideloader GUID, "" for stock items
            public int        OrigId;       // pre-resolve slot id, for cross-machine re-resolve
            public GameObject Live;          // spawned object, null until DeferredApply re-mounts it
            // Composited textures snapshotted from the source slot (region colors
            // baked in), with PNG bytes for persistence. Owned by this entry;
            // Live textures destroyed when the layer is removed or reloaded.
            public List<LayerBinding.BakedTex> Baked = new List<LayerBinding.BakedTex>();
            // Active-state mask (depth-first) mirrored from the source slot, so
            // the copy shows only the worn state (full vs half) not every state
            // the prefab ships. Persisted as a 0/1 byte array.
            public List<bool> StateMask = new List<bool>();
            // Per-material shader + float/color snapshot, so a copy respawned
            // from the prefab on reload gets its ME-edited shader (Hologram /
            // Fire / Galaxy) and slider tweaks back instead of the prefab default.
            public List<LayerBinding.MatState> Materials = new List<LayerBinding.MatState>();
            // Show/hide toggle for the whole layered piece. Applied last (after
            // the state mask) so hiding overrides the worn-state activeSelf.
            public bool Visible = true;
            // The clothing slot this layer was created from (0=Top, 1=Bottom...).
            // Drives the subcategory grouping in the UI. -1 if unknown.
            public int SourceSlot = -1;
            // User-editable label shown in the list. Defaults to the garment name.
            public string Name = "";
            // Vertex-normal inflation (body units) applied to the copy's skinned
            // meshes to stop it clipping through an inner garment. 0 = untouched.
            // Reapplied after each (re)spawn via LayerBinding.SetInflate.
            public float Inflate = 0f;
            // Body region the inflate push is scoped to (All = whole mesh, the
            // original behavior). Persisted as an int index.
            public LayerBinding.InflateRegion Region = LayerBinding.InflateRegion.All;
            // Set once when this layer's source mod can't be resolved on the
            // current install (card made elsewhere), so the reapply loop warns
            // once instead of every frame. Not serialized.
            public bool ResolveWarned = false;
        }

        // Live + persistent layering state for this character. This list IS the
        // runtime state (unlike the other override stores, which mirror a global
        // binding dict), so it is not cleared on save.
        internal List<LayerEntry> Layers = new List<LayerEntry>();

        // Persistent storage for material float overrides. Key format is
        // "slot|materialName|propertyName" so we can find the right slot's
        // clothing GameObject at apply time and locate the material by name
        // among its renderers. Material instances change references between
        // save and load (Unity clones, Sideloader rebuilds), so name-based
        // lookup is the only stable bridge.
        internal Dictionary<string, float> PersistentMaterials = new Dictionary<string, float>();

        // Active material preset per clothing slot. Key is the slot index as
        // a string, value is the preset name ("Leather", ...). On reload the
        // preset is re-applied from the item's own bundles, restoring its
        // detail textures / shader without storing any texture bytes.
        internal Dictionary<string, string> PersistentPresets = new Dictionary<string, string>();

        // Texture swaps from the Textures tab. Key is
        // "slot|materialName|propertyName", value is the swapped texture's
        // asset name. On reload the name is resolved back to a Texture by
        // re-running TextureBinding discovery for the slot (same source the
        // UI grid pulls from), so only the lightweight name travels in the
        // card rather than pixel data.
        internal Dictionary<string, string> PersistentTextures = new Dictionary<string, string>();

        // Per-load cache of name -> Texture maps, keyed by slot index. The
        // texture reapply-loop runs every frame for the deferred window and
        // would otherwise re-run TextureBinding.Discover (which can force-load
        // sibling bundles) every frame; caching the slot's variant map keeps
        // it to a single discovery once the bundle is in memory. Cleared at
        // the start of every load so a different card can't reuse stale maps.
        private readonly Dictionary<int, Dictionary<string, Texture>> _texVariantCache
            = new Dictionary<int, Dictionary<string, Texture>>();

        // Parsed cards captured by Plugin's ExtendedSave.CardBeingLoaded hook,
        // awaiting their matching OnReload. The chaFile ESF parses ExtData into
        // can differ from the one OnReload eventually sees (HS2 maker copies the
        // parsed data into the existing ChaControl's chaFile, leaving the source
        // to be GC'd), so OnReload can't just read ChaControl.chaFile.
        //
        // This is a LIST, not a single slot: a batch load of several characters
        // (a Studio scene, "load all") fires every CardBeingLoaded before the
        // first OnReload, so a single static would collapse to the last card and
        // bleed it onto every character. OnReload claims the entry matching its
        // own ChaFile when it can (precise, order-independent), else the oldest
        // pending entry (FIFO == single-character maker load, unchanged).
        internal sealed class PendingCard
        {
            public ChaFile     File = null!;
            public PluginData? Data;
        }
        private static readonly List<PendingCard> _pendingCards = new List<PendingCard>();

        // Called from Plugin's ExtendedSave.CardBeingLoaded hook for every card
        // that finishes parsing. Data may be null (card has no GDC data); the
        // entry still matters so OnReload knows this card genuinely had nothing
        // and won't fall back to the possibly-stale ChaControl.chaFile.
        internal static void StashLoadedCard(ChaFile file, PluginData? data)
        {
            if (file == null) return;
            _pendingCards.Add(new PendingCard { File = file, Data = data });
            // Safety cap: a CardBeingLoaded with no matching OnReload (aborted
            // load) would otherwise leak. Drop the oldest beyond a sane batch.
            if (_pendingCards.Count > 32) _pendingCards.RemoveAt(0);
        }

        // Lifecycle confirmation. If these don't appear in the log when a
        // character loads, the controller isn't being attached at all and
        // the issue is in registration/dependency-order, not in the
        // serialisation path.
        protected override void Awake()
        {
            GDCPlugin.Logger?.LogDebug($"[chara] Awake attached to '{ChaControl?.fileParam?.fullname ?? "(unknown)"}'");
            base.Awake();
        }

        // Persistent storage. Public so future plugins / debug tooling can
        // peek at what's saved without having to deserialize the card.
        internal Dictionary<string, float> Persistent = new Dictionary<string, float>();

        protected override void OnCardBeingSaved(GameMode currentGameMode)
        {
            // Unconditional entry log so we can confirm the hook fires even
            // when there's nothing to save.
            GDCPlugin.Logger?.LogDebug($"[chara] OnCardBeingSaved fired (mode={currentGameMode}, chara='{ChaControl?.fileParam?.fullname}')");

            // Refresh from the runtime overrides dict, filtering to only
            // renderers that belong to this character.
            Persistent.Clear();

            foreach (var kv in BlendshapeBinding.IterateOverrides())
            {
                var renderer = kv.Key;
                if (renderer == null) continue;
                if (ChaControl == null) continue;
                if (!renderer.transform.IsChildOf(ChaControl.transform)) continue;
                // Don't persist overrides that landed on a layered copy: its
                // renderer name collides with the worn garment's, so saving it
                // would clobber the real item's entry (last-write-wins).
                if (IsLayerCopyRenderer(renderer.transform)) continue;

                foreach (var inner in kv.Value)
                {
                    Persistent[$"{renderer.name}|{inner.Key}"] = inner.Value;
                }
            }

            // Material overrides snapshot — same idea but keys by
            // "slot|materialName|propertyName" since materials live under
            // specific objClothes slots, not directly under the character.
            SnapshotMaterialOverrides();

            // Active presets and texture swaps, both keyed by slot so they
            // re-resolve against whatever item occupies the slot on reload.
            SnapshotPresetOverrides();
            SnapshotTextureOverrides();

            // No overrides at all for this character: clear any stale data
            // (all keys) so the card doesn't grow stale entries over time.
            if (!HasAnyOverrides())
            {
                SetExtendedData(null);
                return;
            }

            var data = BuildPluginData();
            if (data != null) SetExtendedData(data);

            // Sideloader reloads the card immediately after save, which
            // rebuilds the clothing GameObjects. That tears down the
            // renderer references in BlendshapeBinding's runtime override
            // dict, so the new clothing meshes never receive the overrides
            // and item shapes appear to "reset" to zero.
            // DeferredApply re-resolves Persistent onto whatever renderers
            // exist after the rebuild completes.
            RestartDeferredApply();
        }

        protected override void OnReload(GameMode currentGameMode, bool maintainState)
        {
            GDCPlugin.Logger?.LogDebug($"[chara] OnReload fired (mode={currentGameMode}, maintainState={maintainState}, chara='{ChaControl?.fileParam?.fullname}')");

            Persistent.Clear();
            PersistentMaterials.Clear();
            PersistentPresets.Clear();
            PersistentTextures.Clear();
            _texVariantCache.Clear();
            DestroyAllLive();
            Layers.Clear();

            // Claim this reload's parsed card from the pending list UP FRONT, so
            // the entry is used at most once and can't bleed to another
            // character. Prefer the entry whose ChaFile IS this character's
            // (precise, survives out-of-order batch loads); otherwise take the
            // oldest pending entry (FIFO == the single-character maker case).
            PluginData? data = null;
            var seen = false;
            if (_pendingCards.Count > 0)
            {
                var myFile = ChaControl?.chaFile;
                var idx = -1;
                if (myFile != null)
                {
                    for (var i = 0; i < _pendingCards.Count; i++)
                        if (ReferenceEquals(_pendingCards[i].File, myFile)) { idx = i; break; }
                }
                if (idx < 0) idx = 0; // oldest pending
                seen = true;
                data = _pendingCards[idx].Data;
                _pendingCards.RemoveAt(idx);
            }

            if (seen)
            {
                // A card just loaded: its parsed data is authoritative, even when
                // null (= this card genuinely has no GDC data). We must NOT read
                // ChaControl.chaFile here: it can still point at the PREVIOUS
                // card's instance (documented KKAPI/ESF instance mismatch), so a
                // lookup would return the prior character's data and bleed its
                // overrides/layers onto this one. This is the cross-character
                // carry-over bug.
                GDCPlugin.Logger?.LogDebug($"[chara] OnReload: using CardBeingLoaded data ({(data == null ? "none" : "present")})");
            }
            else
            {
                // No card-load signal (e.g. a maintainState re-apply): the
                // chaFile is settled, so read it directly.
                data = GetExtendedData();
                if (data == null && ChaControl?.chaFile != null)
                {
                    var direct = ExtendedSave.GetExtendedDataById(ChaControl.chaFile, GDCPlugin.GUID);
                    if (direct != null)
                    {
                        GDCPlugin.Logger?.LogDebug($"[chara] OnReload: no card signal; direct lookup found data");
                        data = direct;
                    }
                }
            }

            // Diagnostic dump of ESF's internal state, kept compiled out
            // because persistence is working via the CardBeingLoaded stash.
            // Flip the #if to true if you need to debug ESF state again.
            #if false
            try
            {
                var chaFile = ChaControl?.chaFile;
                if (chaFile != null)
                {
                    var hashId = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chaFile);
                    var fileName = chaFile.charaFileName;
                    GDCPlugin.Logger?.LogDebug($"[chara] OnReload: chaFile type={chaFile.GetType().Name} hash={hashId} fileName='{fileName}'");

                    // ALSO enumerate every chaFile currently in ESF's
                    // WeakKeyDictionary. If my data is in some OTHER chaFile
                    // (not ChaControl.chaFile), we have an instance mismatch.
                    var esType = typeof(ExtendedSave);
                    foreach (var f in esType.GetFields(System.Reflection.BindingFlags.Static
                                                       | System.Reflection.BindingFlags.NonPublic
                                                       | System.Reflection.BindingFlags.Public))
                    {
                        if (f.Name == "internalCharaDictionary")
                        {
                            var wkd = f.GetValue(null);
                            if (wkd is System.Collections.IEnumerable enumerable)
                            {
                                var entries = 0;
                                foreach (var kvp in enumerable)
                                {
                                    entries++;
                                    // kvp is KeyValuePair<ChaFile, Dictionary<string, PluginData>>
                                    var kvpType = kvp.GetType();
                                    var keyProp = kvpType.GetProperty("Key");
                                    var valProp = kvpType.GetProperty("Value");
                                    var key = keyProp?.GetValue(kvp);
                                    var val = valProp?.GetValue(kvp) as System.Collections.IDictionary;
                                    var keyHash = key != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key) : 0;
                                    var valKeys = val != null ? string.Join(",", System.Linq.Enumerable.Cast<object>(val.Keys)) : "(null val)";
                                    GDCPlugin.Logger?.LogDebug($"[chara] OnReload:   ESF entry #{entries}: chaFile hash={keyHash} guids=[{valKeys}]");
                                }
                                GDCPlugin.Logger?.LogDebug($"[chara] OnReload: ESF has {entries} chaFile entries total");
                            }
                            break;
                        }
                    }

                    var all = ExtendedSave.GetAllExtendedData(chaFile);
                    if (all == null)
                    {
                        GDCPlugin.Logger?.LogDebug("[chara] OnReload: GetAllExtendedData returned null (chaFile not in ESF's map)");
                    }
                    else
                    {
                        GDCPlugin.Logger?.LogDebug($"[chara] OnReload: GetAllExtendedData has {all.Count} entries: [{string.Join(", ", all.Keys)}]");
                    }
                }
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"[chara] OnReload: GetAllExtendedData probe failed: {ex.Message}");
            }
            #endif

            if (data == null)
            {
                GDCPlugin.Logger?.LogDebug("[chara] OnReload: no ExtData on card (neither KKAPI helper nor direct ExtendedSave lookup)");
            }
            else if (data.data == null)
            {
                GDCPlugin.Logger?.LogDebug("[chara] OnReload: ExtData found but inner data dict is null");
            }
            else
            {
                GDCPlugin.Logger?.LogDebug($"[chara] OnReload: ExtData has keys [{string.Join(", ", data.data.Keys)}]");
                DeserializeBlendshapeEntries(data);
                DeserializeMaterialEntries(data);
                DeserializePresetEntries(data);
                DeserializeTextureEntries(data);
                DeserializeLayerEntries(data);
                DeserializeLayerTextures(data);
                DeserializeLayerStates(data);
                DeserializeLayerMaterials(data);
            }

            if (Persistent.Count > 0)
                GDCPlugin.Logger?.LogDebug($"[chara] Loaded {Persistent.Count} blendshape overrides for '{ChaControl?.fileParam?.fullname}', deferring apply");
            if (PersistentMaterials.Count > 0)
                GDCPlugin.Logger?.LogDebug($"[chara] Loaded {PersistentMaterials.Count} material overrides for '{ChaControl?.fileParam?.fullname}', deferring apply");
            if (PersistentPresets.Count > 0)
                GDCPlugin.Logger?.LogDebug($"[chara] Loaded {PersistentPresets.Count} preset overrides for '{ChaControl?.fileParam?.fullname}', deferring apply");
            if (PersistentTextures.Count > 0)
                GDCPlugin.Logger?.LogDebug($"[chara] Loaded {PersistentTextures.Count} texture overrides for '{ChaControl?.fileParam?.fullname}', deferring apply");

            // Coroutine-driven apply because item renderers usually haven't
            // been instantiated yet at this hook. Body / face renderers DO
            // exist now, but clothing meshes land a few frames later.
            RestartDeferredApply();

            // Keep layers mounted past the DeferredApply window so an in-session
            // clothing swap can't strand them.
            if (Layers.Count > 0) EnsureLayerMaintenance();
        }

        // Apply ALL persistent overrides to whatever renderers currently
        // exist on the character. Returns true when every key found a
        // matching renderer, false when some are still missing (typically
        // clothing renderers that HS2 hasn't instantiated yet).
        private bool ApplyPersistentToRenderers()
        {
            if (ChaControl == null) return false;

            var renderers = ChaControl.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            var allFound  = true;

            foreach (var kv in Persistent)
            {
                if (!TryParseKey(kv.Key, out var rendererName, out var shapeIdx)) continue;

                var matched = false;
                foreach (var r in renderers)
                {
                    if (r == null || r.sharedMesh == null) continue;
                    if (r.name != rendererName) continue;
                    // Skip layered-accessory copies: a GDCLayer_* clone carries
                    // the same renderer names as the worn garment, so matching it
                    // here migrates the override onto the copy and the real item's
                    // shape reverts. Blendshape keys are name-only (not slot-keyed)
                    // so this scope guard is the discriminator. See IsLayerCopyRenderer.
                    if (IsLayerCopyRenderer(r.transform)) continue;
                    if (shapeIdx < 0 || shapeIdx >= r.sharedMesh.blendShapeCount) continue;

                    r.SetBlendShapeWeight(shapeIdx, kv.Value);
                    BlendshapeBinding.RecordRuntimeOverride(r, shapeIdx, kv.Value);
                    matched = true;
                    break;
                }

                if (!matched) allFound = false;
            }

            return allFound;
        }

        // Coroutine that applies persistent overrides every frame for a
        // sustained window. Important: I do NOT stop on first success.
        // After my initial apply, other plugins (Sideloader, MaterialEditor,
        // Mod Bone Implantor, ColliderController) continue their own deferred
        // loads that frequently rebuild clothing renderers, destroying the
        // ones my overrides referenced. By re-applying every frame for the
        // window I catch those rebuilds and re-bind to the fresh renderers.
        // SetBlendShapeWeight is idempotent so repeats cost nothing.
        // Handle to the running DeferredApply so a new save/load can cancel the
        // prior window before starting its own. Without this, save-then-reload
        // (or two reloads in quick succession) stacks several 120-frame loops;
        // two concurrent passes can both see a layer's Live == null on the same
        // frame and each Spawn it, producing duplicate GDCLayer_* objects, and
        // an older pass keeps applying after OnReload has swapped in a new card's
        // data. Unity auto-stops the coroutine when this behaviour is destroyed,
        // so no OnDestroy is needed.
        private Coroutine? _deferredApply;

        private void RestartDeferredApply()
        {
            if (_deferredApply != null) StopCoroutine(_deferredApply);
            _deferredApply = StartCoroutine(DeferredApply());
        }

        // Steady layer re-mount pass, separate from the time-boxed DeferredApply.
        // A layered copy is parented under the clothing container, so HS2 destroys
        // it whenever the underlying slot's item is swapped (an in-session change,
        // not just a card load). DeferredApply only respawns during its ~2s
        // post-save/load window, so without this a mid-session swap leaves the
        // layer gone and its Push slider a no-op (e.Live is destroyed, so the
        // slider updates the number but can't touch any mesh). This loop re-mounts
        // any missing layer as soon as HS2 finishes its rebuild. It is cheap when
        // every layer is alive (per-entry null check + early continue) and stops
        // itself once the character has no layers left.
        private Coroutine? _layerMaintain;

        private void EnsureLayerMaintenance()
        {
            if (_layerMaintain != null) return;
            _layerMaintain = StartCoroutine(LayerMaintenance());
        }

        private System.Collections.IEnumerator LayerMaintenance()
        {
            while (Layers.Count > 0)
            {
                ApplyLayersToCharacter();
                yield return null;
            }
            _layerMaintain = null;
        }

        private System.Collections.IEnumerator DeferredApply()
        {
            const int MaxFrames = 120; // ~2 seconds; covers the slowest plugin load chain
            var loggedSuccess = false;
            for (var i = 0; i < MaxFrames; i++)
            {
                GDCPlugin.DeferredFrames++;   // perf diagnostic: count active reapply frames
                // All apply paths run every frame. The AND means we count
                // success only once everything (blendshapes, materials,
                // presets, textures) has caught up, not the moment one
                // lands. Presets run before textures so a MainTex swap sits
                // on top of whatever the preset brought in.
                var blendshapesOk = ApplyPersistentToRenderers();
                var materialsOk   = ApplyMaterialPersistentToRenderers();
                var presetsOk     = ApplyPresetPersistentToRenderers();
                var texturesOk    = ApplyTexturePersistentToRenderers();
                var layersOk      = ApplyLayersToCharacter();
                if (blendshapesOk && materialsOk && presetsOk && texturesOk && layersOk && !loggedSuccess)
                {
                    GDCPlugin.Logger?.LogDebug($"[chara] DeferredApply: {Persistent.Count}+{PersistentMaterials.Count}+{PersistentPresets.Count}+{PersistentTextures.Count} overrides matched after {i + 1} frame(s); will keep re-applying for window");
                    loggedSuccess = true;
                }
                yield return null;
            }
            if (!loggedSuccess)
                GDCPlugin.Logger?.LogWarning($"[chara] DeferredApply: never matched all overrides in {MaxFrames} frames (blendshape={Persistent.Count}, material={PersistentMaterials.Count}, preset={PersistentPresets.Count}, texture={PersistentTextures.Count})");
            else
                GDCPlugin.Logger?.LogDebug($"[chara] DeferredApply: window closed after {MaxFrames} frames");
        }

        // Parses "rendererName|shapeIndex" back into its parts. Returns
        // false (and skip the entry) when the key shape is wrong, which
        // can happen if a future plugin version changes the key format.
        private static bool TryParseKey(string key, out string rendererName, out int shapeIdx)
        {
            rendererName = "";
            shapeIdx     = 0;
            var pipeIdx  = key.IndexOf('|');
            if (pipeIdx <= 0 || pipeIdx >= key.Length - 1) return false;

            rendererName = key.Substring(0, pipeIdx);
            return int.TryParse(key.Substring(pipeIdx + 1), out shapeIdx);
        }

        // True when a renderer lives inside a layered-accessory copy (a
        // GDCLayer_* GameObject spawned under objTop), as opposed to the real
        // worn clothing under objClothes. The copy is a clone of the garment so
        // its SkinnedMeshRenderers share the original's names; blendshape
        // persistence keys only by renderer name, so without this guard a saved
        // shape can bind to the copy instead of the worn item (and stop being
        // pushed to the real one). Material/texture/preset stores are slot-keyed
        // and never reach the copies, so they don't need this.
        private static bool IsLayerCopyRenderer(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name.StartsWith("GDCLayer_", StringComparison.Ordinal)) return true;
            return false;
        }

        // ---- Coordinate save/load path ------------------------------------
        //
        // HS2's Maker has two save paths: the full character card AND the
        // coordinate card ("Costume Card > Save / Delete"). KKAPI exposes
        // them as separate hooks. Most of the time the user uses coordinate
        // save (it's the more prominent button in the UI), so without these
        // overrides our persistence never fires for the common flow.
        //
        // I write the same payload to both targets so the overrides survive
        // either save path. A slight overshoot for full-card saves but it
        // means there's never a "I saved but it didn't persist" footgun.

        protected override void OnCoordinateBeingSaved(ChaFileCoordinate coordinate)
        {
            GDCPlugin.Logger?.LogDebug($"[chara] OnCoordinateBeingSaved fired (chara='{ChaControl?.fileParam?.fullname}')");

            // Reuse the same snapshot logic the card save uses, blendshapes
            // and materials both.
            Persistent.Clear();
            foreach (var kv in BlendshapeBinding.IterateOverrides())
            {
                var renderer = kv.Key;
                if (renderer == null) continue;
                if (ChaControl == null) continue;
                if (!renderer.transform.IsChildOf(ChaControl.transform)) continue;
                if (IsLayerCopyRenderer(renderer.transform)) continue;

                foreach (var inner in kv.Value)
                {
                    Persistent[$"{renderer.name}|{inner.Key}"] = inner.Value;
                }
            }
            SnapshotMaterialOverrides();
            SnapshotPresetOverrides();
            SnapshotTextureOverrides();

            if (!HasAnyOverrides())
            {
                SetCoordinateExtendedData(coordinate, null);
                return;
            }

            var data = BuildPluginData();
            if (data != null)
            {
                SetCoordinateExtendedData(coordinate, data);
                GDCPlugin.Logger?.LogDebug($"[chara] Saved {Persistent.Count} blendshape + {PersistentMaterials.Count} material + {PersistentPresets.Count} preset + {PersistentTextures.Count} texture overrides to coordinate '{coordinate?.coordinateName}'");
            }

            // Same post-save reapply as the card save path. Coordinate save
            // also triggers a clothing reload that breaks renderer references.
            RestartDeferredApply();
        }

        protected override void OnCoordinateBeingLoaded(ChaFileCoordinate coordinate, bool maintainState)
        {
            GDCPlugin.Logger?.LogDebug($"[chara] OnCoordinateBeingLoaded fired (maintainState={maintainState})");

            Persistent.Clear();
            PersistentMaterials.Clear();
            PersistentPresets.Clear();
            PersistentTextures.Clear();
            _texVariantCache.Clear();
            DestroyAllLive();
            Layers.Clear();

            var data = GetCoordinateExtendedData(coordinate);
            if (data?.data != null)
            {
                DeserializeBlendshapeEntries(data);
                DeserializeMaterialEntries(data);
                DeserializePresetEntries(data);
                DeserializeTextureEntries(data);
                DeserializeLayerEntries(data);
                DeserializeLayerTextures(data);
                DeserializeLayerStates(data);
                DeserializeLayerMaterials(data);
            }

            if (Persistent.Count > 0 || PersistentMaterials.Count > 0
                || PersistentPresets.Count > 0 || PersistentTextures.Count > 0)
                GDCPlugin.Logger?.LogDebug($"[chara] Loaded {Persistent.Count} blendshape + {PersistentMaterials.Count} material + {PersistentPresets.Count} preset + {PersistentTextures.Count} texture overrides from coordinate");

            RestartDeferredApply();
            if (Layers.Count > 0) EnsureLayerMaintenance();
        }

        // ---- Helpers ------------------------------------------------------

        // Walks the runtime material overrides, locates which clothing slot
        // each Material lives under, and builds the persistent dict keyed by
        // "slot|materialName|propertyName". Material instance references are
        // unstable across save/load (Unity clones, Sideloader rebuilds), so
        // the persistent form uses stable name strings instead.
        private void SnapshotMaterialOverrides()
        {
            PersistentMaterials.Clear();
            if (ChaControl?.objClothes == null) return;

            foreach (var matEntry in MaterialBinding.IterateOverrides())
            {
                var material = matEntry.Key;
                if (material == null) continue;

                var slot = FindSlotForMaterial(material);
                if (slot < 0) continue;

                var matName = StripInstanceSuffix(material.name);
                foreach (var propEntry in matEntry.Value)
                {
                    var key = $"{slot}|{matName}|{propEntry.Key}";
                    PersistentMaterials[key] = propEntry.Value;
                }
            }
        }

        // Returns the objClothes slot index the material belongs to, or -1
        // if it isn't attached to any of this character's clothing items
        // (e.g. it lives on a non-clothing renderer; skip those, they're
        // out of scope for the GDC plugin).
        private int FindSlotForMaterial(Material target)
        {
            if (ChaControl?.objClothes == null) return -1;
            for (var slot = 0; slot < ChaControl.objClothes.Length; slot++)
            {
                var go = ChaControl.objClothes[slot];
                if (go == null) continue;
                var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;
                    foreach (var mat in mats)
                    {
                        if (ReferenceEquals(mat, target)) return slot;
                    }
                    // Also check the live instances since materials may have
                    // been cloned by other plugins after our discovery pass.
                    mats = r.materials;
                    foreach (var mat in mats)
                    {
                        if (ReferenceEquals(mat, target)) return slot;
                    }
                }
            }
            return -1;
        }

        private static string StripInstanceSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? "";
            return name.Replace("(Instance)", "").Replace(" Instance", "").Trim();
        }

        // Centralised deserializers, used by both the card and coordinate
        // load paths. Each one safely no-ops when its key is absent.
        private void DeserializeBlendshapeEntries(PluginData data)
        {
            if (!data.data.TryGetValue(ExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var loaded = MessagePackSerializer.Deserialize<Dictionary<string, float>>(bytes);
                if (loaded != null) Persistent = loaded;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize blendshape overrides: {ex.Message}");
            }
        }

        private void DeserializeMaterialEntries(PluginData data)
        {
            if (!data.data.TryGetValue(MaterialExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var loaded = MessagePackSerializer.Deserialize<Dictionary<string, float>>(bytes);
                if (loaded != null) PersistentMaterials = loaded;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize material overrides: {ex.Message}");
            }
        }

        // True when any of the four override stores hold data for this
        // character. Gate for whether the card needs ExtData written at all.
        private bool HasAnyOverrides()
            => Persistent.Count > 0 || PersistentMaterials.Count > 0
               || PersistentPresets.Count > 0 || PersistentTextures.Count > 0
               || Layers.Count > 0;

        // Builds the PluginData payload from whichever stores are non-empty.
        // Shared by the card and coordinate save paths. Returns null on a
        // serialization failure (logged) so the caller skips the write.
        private PluginData BuildPluginData()
        {
            var data = new PluginData { version = 1 };
            try
            {
                if (Persistent.Count > 0)
                {
                    var bytes = MessagePackSerializer.Serialize(Persistent);
                    data.data[ExtDataKey] = bytes;
                    GDCPlugin.Logger?.LogDebug($"[chara] Saved {Persistent.Count} blendshape overrides ({bytes.Length} bytes)");
                }
                if (PersistentMaterials.Count > 0)
                {
                    var mbytes = MessagePackSerializer.Serialize(PersistentMaterials);
                    data.data[MaterialExtDataKey] = mbytes;
                    GDCPlugin.Logger?.LogDebug($"[chara] Saved {PersistentMaterials.Count} material overrides ({mbytes.Length} bytes)");
                }
                if (PersistentPresets.Count > 0)
                {
                    var pbytes = MessagePackSerializer.Serialize(PersistentPresets);
                    data.data[PresetExtDataKey] = pbytes;
                    GDCPlugin.Logger?.LogDebug($"[chara] Saved {PersistentPresets.Count} preset overrides ({pbytes.Length} bytes)");
                }
                if (PersistentTextures.Count > 0)
                {
                    var tbytes = MessagePackSerializer.Serialize(PersistentTextures);
                    data.data[TextureExtDataKey] = tbytes;
                    GDCPlugin.Logger?.LogDebug($"[chara] Saved {PersistentTextures.Count} texture overrides ({tbytes.Length} bytes)");
                }
                if (Layers.Count > 0)
                {
                    var list = new List<string>(Layers.Count);
                    foreach (var e in Layers)
                    {
                        // name at fixed index 6 (stripped of '|' so it can't
                        // break parsing); inflate appended after it. Invariant
                        // culture so a locale decimal-comma can't corrupt the float.
                        var safeName   = (e.Name ?? "").Replace('|', ' ');
                        var inflateStr = e.Inflate.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        // Region (index 8) appended after inflate; backward-compatible.
                        list.Add($"{e.Category}|{e.Guid}|{e.OrigId}|{e.ResolvedId}|{(e.Visible ? 1 : 0)}|{e.SourceSlot}|{safeName}|{inflateStr}|{(int)e.Region}");
                    }
                    var laybytes = MessagePackSerializer.Serialize(list);
                    data.data[LayerExtDataKey] = laybytes;
                    GDCPlugin.Logger?.LogDebug($"[chara] Saved {Layers.Count} layer overrides ({laybytes.Length} bytes)");

                    // Baked composited textures (region colors), keyed by
                    // "layerIndex|rendererIndex|materialIndex|property" -> PNG.
                    var texDict = new Dictionary<string, byte[]>();
                    for (var li = 0; li < Layers.Count; li++)
                    {
                        foreach (var b in Layers[li].Baked)
                        {
                            if (b?.Png == null || b.Png.Length == 0) continue;
                            texDict[$"{li}|{b.Ri}|{b.Mi}|{b.Prop}"] = b.Png;
                        }
                    }
                    if (texDict.Count > 0)
                    {
                        var tb = MessagePackSerializer.Serialize(texDict);
                        data.data[LayerTexExtDataKey] = tb;
                        GDCPlugin.Logger?.LogDebug($"[chara] Saved {texDict.Count} layer textures ({tb.Length} bytes)");
                    }

                    // Worn-state masks, keyed by layer index -> 0/1 bytes.
                    var stateDict = new Dictionary<string, byte[]>();
                    for (var li = 0; li < Layers.Count; li++)
                    {
                        var mask = Layers[li].StateMask;
                        if (mask == null || mask.Count == 0) continue;
                        var packed = new byte[mask.Count];
                        for (var k = 0; k < mask.Count; k++) packed[k] = (byte)(mask[k] ? 1 : 0);
                        stateDict[li.ToString()] = packed;
                    }
                    if (stateDict.Count > 0)
                    {
                        var sb = MessagePackSerializer.Serialize(stateDict);
                        data.data[LayerStateExtDataKey] = sb;
                        GDCPlugin.Logger?.LogDebug($"[chara] Saved {stateDict.Count} layer state masks ({sb.Length} bytes)");
                    }

                    // Per-material shader + float/color snapshots. One string per
                    // material: "layer|ri|mi|shader|floats|colors", floats as
                    // "name=val,..." and colors as "name=r/g/b/a,...". Invariant
                    // culture so a locale decimal-comma can't corrupt the values.
                    var inv     = System.Globalization.CultureInfo.InvariantCulture;
                    var matList = new List<string>();
                    for (var li = 0; li < Layers.Count; li++)
                    {
                        foreach (var st in Layers[li].Materials)
                        {
                            if (st == null) continue;
                            var fb = new System.Text.StringBuilder();
                            foreach (var kv in st.Floats)
                            {
                                if (fb.Length > 0) fb.Append(',');
                                fb.Append(kv.Key).Append('=').Append(kv.Value.ToString("R", inv));
                            }
                            var cb = new System.Text.StringBuilder();
                            foreach (var kv in st.Colors)
                            {
                                if (kv.Value == null || kv.Value.Length != 4) continue;
                                if (cb.Length > 0) cb.Append(',');
                                cb.Append(kv.Key).Append('=')
                                  .Append(kv.Value[0].ToString("R", inv)).Append('/')
                                  .Append(kv.Value[1].ToString("R", inv)).Append('/')
                                  .Append(kv.Value[2].ToString("R", inv)).Append('/')
                                  .Append(kv.Value[3].ToString("R", inv));
                            }
                            var kb = new System.Text.StringBuilder();
                            if (st.Keywords != null)
                                foreach (var kw in st.Keywords)
                                {
                                    if (string.IsNullOrEmpty(kw)) continue;
                                    if (kb.Length > 0) kb.Append(',');
                                    kb.Append(kw.Replace('|', ' ').Replace(',', ' '));
                                }
                            var safeShader = (st.Shader ?? "").Replace('|', ' ');
                            // Fields: li|ri|mi|shader|floats|colors|renderQueue|keywords
                            matList.Add($"{li}|{st.Ri}|{st.Mi}|{safeShader}|{fb}|{cb}|{st.RenderQueue}|{kb}");
                        }
                    }
                    if (matList.Count > 0)
                    {
                        var mb = MessagePackSerializer.Serialize(matList);
                        data.data[LayerMatExtDataKey] = mb;
                        GDCPlugin.Logger?.LogDebug($"[chara] Saved {matList.Count} layer material states ({mb.Length} bytes)");
                    }
                }
                return data;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogError($"[chara] Save serialization failed: {ex.Message}");
                return null;
            }
        }

        // Snapshots the active material preset on each of this character's
        // slots into PersistentPresets, keyed by slot index. PresetBinding
        // tracks active presets globally per (character, slot); we filter to
        // this character and store the slot -> name pairs.
        private void SnapshotPresetOverrides()
        {
            PersistentPresets.Clear();
            if (ChaControl == null) return;

            foreach (var kv in PresetBinding.GetActivePresetsForCharacter(ChaControl))
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                PersistentPresets[kv.Key.ToString()] = kv.Value;
            }
        }

        // Snapshots the runtime texture swaps into PersistentTextures keyed by
        // "slot|materialName|propertyName" -> textureAssetName. Mirrors
        // SnapshotMaterialOverrides; only the texture's stable asset name is
        // stored, re-resolved to a Texture on reload via discovery.
        private void SnapshotTextureOverrides()
        {
            PersistentTextures.Clear();
            if (ChaControl?.objClothes == null) return;

            foreach (var texEntry in TextureBinding.IterateOverrides())
            {
                var material = texEntry.Key;
                if (material == null) continue;

                var slot = FindSlotForMaterial(material);
                if (slot < 0) continue;

                var matName = StripInstanceSuffix(material.name);
                foreach (var propEntry in texEntry.Value)
                {
                    var tex = propEntry.Value;
                    if (tex == null || string.IsNullOrEmpty(tex.name)) continue;
                    var key = $"{slot}|{matName}|{propEntry.Key}";
                    PersistentTextures[key] = tex.name;
                }
            }
        }

        private void DeserializePresetEntries(PluginData data)
        {
            if (!data.data.TryGetValue(PresetExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var loaded = MessagePackSerializer.Deserialize<Dictionary<string, string>>(bytes);
                if (loaded != null) PersistentPresets = loaded;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize preset overrides: {ex.Message}");
            }
        }

        private void DeserializeTextureEntries(PluginData data)
        {
            if (!data.data.TryGetValue(TextureExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var loaded = MessagePackSerializer.Deserialize<Dictionary<string, string>>(bytes);
                if (loaded != null) PersistentTextures = loaded;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize texture overrides: {ex.Message}");
            }
        }

        // Per-frame preset reapply. For each persisted slot -> preset, build a
        // Selection for the slot and ask PresetBinding to apply it. Apply is
        // idempotent and cheap once the item's preset Materials are cached, so
        // running it every frame for the window catches Sideloader's clothing
        // rebuilds (which revert the live materials to the bundle defaults).
        private bool ApplyPresetPersistentToRenderers()
        {
            if (PersistentPresets.Count == 0) return true;
            if (ChaControl?.objClothes == null) return false;

            var allFound = true;
            foreach (var kv in PersistentPresets)
            {
                if (!int.TryParse(kv.Key, out var slot)) continue;
                if (slot < 0 || slot >= ChaControl.objClothes.Length) continue;
                var go = ChaControl.objClothes[slot];
                if (go == null) { allFound = false; continue; }

                var sel = new SelectionTracker.Selection(ChaControl, slot, go);
                if (!PresetBinding.Apply(sel, kv.Value)) allFound = false;
            }
            return allFound;
        }

        // Per-frame texture reapply. Resolves each persisted texture name back
        // to a Texture via the slot's variant map (cached) and writes it onto
        // every matching material, re-recording the runtime override so
        // Plugin.LateUpdate keeps pushing it past shader refreshes.
        private bool ApplyTexturePersistentToRenderers()
        {
            if (PersistentTextures.Count == 0) return true;
            if (ChaControl?.objClothes == null) return false;

            var allFound = true;
            foreach (var kv in PersistentTextures)
            {
                if (!TryParseMaterialKey(kv.Key, out var slot, out var matName, out var propName))
                    continue;
                if (slot < 0 || slot >= ChaControl.objClothes.Length) continue;
                var go = ChaControl.objClothes[slot];
                if (go == null) { allFound = false; continue; }

                var byName = GetVariantMapCached(slot, go);
                if (byName == null || byName.Count == 0) { allFound = false; continue; }
                if (!byName.TryGetValue(kv.Value, out var tex) || tex == null) { allFound = false; continue; }

                var matched = false;
                var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var mats = r.materials;
                    if (mats == null) continue;
                    foreach (var material in mats)
                    {
                        if (material == null) continue;
                        if (StripInstanceSuffix(material.name) != matName) continue;
                        var full = "_" + propName;
                        if (!material.HasProperty(full)) continue;

                        try
                        {
                            material.SetTexture(full, tex);
                            TextureBinding.RecordRuntimeOverride(material, propName, tex);
                            matched = true;
                        }
                        catch { /* destroyed mid-frame */ }
                    }
                }

                if (!matched) allFound = false;
            }
            return allFound;
        }

        // Returns a name -> Texture map for the slot's swap variants, cached
        // per load. Only caches once discovery yields a non-empty result (the
        // bundle is in memory), so early empty frames retry instead of pinning
        // a blank map.
        private Dictionary<string, Texture> GetVariantMapCached(int slot, GameObject go)
        {
            if (_texVariantCache.TryGetValue(slot, out var cached) && cached != null && cached.Count > 0)
                return cached;

            var sel  = new SelectionTracker.Selection(ChaControl, slot, go);
            var disc = TextureBinding.Discover(sel);
            var map  = new Dictionary<string, Texture>();
            foreach (var v in disc.Variants)
            {
                if (v.Texture == null || string.IsNullOrEmpty(v.Name)) continue;
                map[v.Name] = v.Texture;
            }
            if (map.Count > 0) _texVariantCache[slot] = map;
            return map;
        }

        // Per-frame apply for material persistents, called from DeferredApply
        // alongside the blendshape one. Returns true when every persistent
        // entry found a matching material to write into.
        private bool ApplyMaterialPersistentToRenderers()
        {
            if (PersistentMaterials.Count == 0) return true;
            if (ChaControl?.objClothes == null) return false;

            var allFound = true;
            foreach (var kv in PersistentMaterials)
            {
                if (!TryParseMaterialKey(kv.Key, out var slot, out var matName, out var propName))
                    continue;
                if (slot < 0 || slot >= ChaControl.objClothes.Length) continue;

                var go = ChaControl.objClothes[slot];
                if (go == null) { allFound = false; continue; }

                var matched = false;
                var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    // Use materials (instance) so writes reach the live
                    // material the renderer actually displays.
                    var mats = r.materials;
                    if (mats == null) continue;
                    foreach (var material in mats)
                    {
                        if (material == null) continue;
                        if (StripInstanceSuffix(material.name) != matName) continue;
                        var full = "_" + propName;
                        if (!material.HasProperty(full)) continue;

                        try
                        {
                            material.SetFloat(full, kv.Value);
                            MaterialBinding.RecordRuntimeOverride(material, propName, kv.Value);
                            matched = true;
                        }
                        catch { /* destroyed mid-frame */ }
                    }
                }

                if (!matched) allFound = false;
            }
            return allFound;
        }

        private static bool TryParseMaterialKey(string key, out int slot, out string matName, out string propName)
        {
            slot     = -1;
            matName  = "";
            propName = "";
            if (string.IsNullOrEmpty(key)) return false;
            var firstPipe = key.IndexOf('|');
            if (firstPipe <= 0) return false;
            var lastPipe = key.LastIndexOf('|');
            if (lastPipe <= firstPipe) return false;
            if (!int.TryParse(key.Substring(0, firstPipe), out slot)) return false;
            matName  = key.Substring(firstPipe + 1, lastPipe - firstPipe - 1);
            propName = key.Substring(lastPipe + 1);
            return !string.IsNullOrEmpty(matName) && !string.IsNullOrEmpty(propName);
        }

        // ---- Layering (clothing item -> body-skinned accessory copy) ------

        // Layers the item currently in `slot` as a body-skinned copy held
        // outside objClothes, so the slot is free for another piece. The copy
        // follows the body weightpaint (LayerBinding.Spawn, copyWeights:1).
        // Returns false when the slot is empty or the spawn fails.
        public bool AddLayerForSlot(int slot)
        {
            if (ChaControl == null) return false;
            if (!LayerBinding.TryGetSlotItem(ChaControl, slot, out var cat, out var id, out var itemName)) return false;

            var entry = new LayerEntry
            {
                Category   = cat,
                ResolvedId = id,
                SourceSlot = slot,
                Name       = string.IsNullOrEmpty(itemName) ? LayerBinding.SlotDisplayName(slot) + " layer" : itemName,
            };
            SideloaderBridge.TryGetSource(cat, id, out entry.Guid, out entry.OrigId);

            entry.Live = LayerBinding.Spawn(ChaControl, cat, id, LayerObjName(Layers.Count));
            if (entry.Live == null) return false;

            // Copy the composited (correctly colored) material state from the
            // source slot while the item is still equipped there. This also bakes
            // the composited textures to PNG for save/load.
            LayerBinding.CopyColorFromSlot(ChaControl, slot, entry.Live, entry.Baked);

            // Snapshot the (now ME-edited) shader + float/color values so a fresh
            // respawn on reload can be returned to this exact look. Runs after the
            // color copy so it reads the final live state.
            LayerBinding.SnapshotMaterialState(entry.Live, entry.Materials, entry.Baked);

            // Mirror the worn state (full vs half) so the copy doesn't show all
            // states at once.
            entry.StateMask = LayerBinding.MirrorStateFromSlot(ChaControl, slot, entry.Live);

            Layers.Add(entry);
            EnsureLayerMaintenance();
            return true;
        }

        // Removes and destroys a layered piece by index.
        public void RemoveLayer(int index)
        {
            if (index < 0 || index >= Layers.Count) return;
            var e = Layers[index];
            DestroyLive(e);
            Layers.RemoveAt(index);
        }

        // Destroys the spawned object + owned snapshot textures for one entry.
        // Dedup means several Baked entries can share a Live texture, so guard
        // against double-destroy by null-checking each.
        private static void DestroyLive(LayerEntry e)
        {
            if (e.Live != null) UnityEngine.Object.Destroy(e.Live);
            e.Live = null;
            foreach (var b in e.Baked)
            {
                if (b?.Live != null) UnityEngine.Object.Destroy(b.Live);
                if (b != null) b.Live = null;
            }
        }

        private static string LayerObjName(int n) => $"GDCLayer_{n}";

        // Destroys every live layer GameObject we currently hold. Called before
        // a reload repopulates the list so the previous load's copies can't leak
        // or duplicate if HS2 didn't tear them down with the character rebuild.
        private void DestroyAllLive()
        {
            foreach (var e in Layers) DestroyLive(e);
        }

        // Per-frame layer reapply, called from DeferredApply. Spawns any entry
        // whose live object is missing (fresh load) or was destroyed by a
        // clothing rebuild (Unity makes the destroyed ref compare == null).
        // Idempotent: an entry with a live object is skipped, so no duplicates.
        private bool ApplyLayersToCharacter()
        {
            if (Layers.Count == 0) return true;
            if (ChaControl == null) return false;

            var allFound = true;
            for (var i = 0; i < Layers.Count; i++)
            {
                var e = Layers[i];
                if (e.Live != null) continue;

                var id = SideloaderBridge.ResolveCurrentId(e.Category, e.ResolvedId, e.Guid, e.OrigId);
                if (id == SideloaderBridge.Unresolved)
                {
                    // Mod not installed on this machine. Skip permanently (no
                    // retry, no wrong-garment fallback) and warn once.
                    if (!e.ResolveWarned)
                    {
                        GDCPlugin.Logger?.LogWarning($"[layer] Skipping layer '{e.Name}' (guid='{e.Guid}', origId={e.OrigId}): source mod not installed on this machine.");
                        e.ResolveWarned = true;
                    }
                    continue;
                }
                var go = LayerBinding.Spawn(ChaControl, e.Category, id, LayerObjName(i));
                if (go == null) { allFound = false; continue; }
                e.Live = go;

                // Restore the ME shader + float/color edits FIRST: swapping the
                // shader resets the property set, so this has to precede the baked
                // texture restore below.
                LayerBinding.ApplyMaterialState(go, e.Materials);

                // Restore the baked (colored) textures onto the fresh copy. On a
                // card load these come from PNG bytes; in-session respawns reuse
                // the already-built Live textures.
                LayerBinding.ApplyBaked(go, e.Baked);

                // Restore the worn state so only the right state renders.
                LayerBinding.ApplyState(go, e.StateMask);

                // Visibility last so a hidden layer overrides the state mask's
                // activeSelf on the root.
                go.SetActive(e.Visible);

                // Reapply the clipping push onto the fresh copy (skips the mesh
                // clone entirely when no inflation was set).
                if (!Mathf.Approximately(e.Inflate, 0f))
                    LayerBinding.SetInflate(go, e.Inflate, e.Region);
            }
            return allFound;
        }

        private void DeserializeLayerEntries(PluginData data)
        {
            if (!data.data.TryGetValue(LayerExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var list = MessagePackSerializer.Deserialize<List<string>>(bytes);
                if (list == null) return;
                foreach (var s in list)
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    var parts = s.Split('|');
                    if (parts.Length < 4) continue;
                    if (!int.TryParse(parts[0], out var cat)) continue;
                    // Bail on bad id fields rather than defaulting to 0: a 0
                    // would later resolve to stock item 0 of the category and
                    // mount the wrong garment as a layer.
                    if (!int.TryParse(parts[2], out var origId)) continue;
                    if (!int.TryParse(parts[3], out var resId)) continue;
                    // Fields added over time; older records omit the trailing ones.
                    var visible = parts.Length < 5 || parts[4] != "0";
                    var srcSlot = parts.Length >= 6 && int.TryParse(parts[5], out var ss) ? ss : -1;
                    var name    = parts.Length >= 7 ? parts[6] : "";
                    var inflate = 0f;
                    if (parts.Length >= 8)
                        float.TryParse(parts[7], System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out inflate);
                    var region = LayerBinding.InflateRegion.All;
                    if (parts.Length >= 9 && int.TryParse(parts[8], out var ri)
                        && ri >= 0 && ri < LayerBinding.RegionNames.Length)
                        region = (LayerBinding.InflateRegion)ri;
                    Layers.Add(new LayerEntry
                    {
                        Category   = cat,
                        Guid       = parts[1] ?? "",
                        OrigId     = origId,
                        ResolvedId = resId,
                        Visible    = visible,
                        SourceSlot = srcSlot,
                        Name       = name,
                        Inflate    = inflate,
                        Region     = region,
                    });
                }
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize layer overrides: {ex.Message}");
            }
        }

        // Must run after DeserializeLayerEntries: attaches baked textures to the
        // layer entry at the index encoded in each key.
        private void DeserializeLayerTextures(PluginData data)
        {
            if (!data.data.TryGetValue(LayerTexExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var dict = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(bytes);
                if (dict == null) return;
                foreach (var kv in dict)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    var parts = kv.Key.Split('|');
                    if (parts.Length < 4) continue;
                    if (!int.TryParse(parts[0], out var li)) continue;
                    if (li < 0 || li >= Layers.Count) continue;
                    int.TryParse(parts[1], out var ri);
                    int.TryParse(parts[2], out var mi);
                    Layers[li].Baked.Add(new LayerBinding.BakedTex
                    {
                        Ri = ri, Mi = mi, Prop = parts[3] ?? "", Png = kv.Value, Live = null,
                    });
                }
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize layer textures: {ex.Message}");
            }
        }

        // Must run after DeserializeLayerEntries: attaches worn-state masks to
        // the layer entry at the index encoded in each key.
        private void DeserializeLayerStates(PluginData data)
        {
            if (!data.data.TryGetValue(LayerStateExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var dict = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(bytes);
                if (dict == null) return;
                foreach (var kv in dict)
                {
                    if (!int.TryParse(kv.Key, out var li)) continue;
                    if (li < 0 || li >= Layers.Count || kv.Value == null) continue;
                    var mask = new List<bool>(kv.Value.Length);
                    foreach (var b in kv.Value) mask.Add(b != 0);
                    Layers[li].StateMask = mask;
                }
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize layer states: {ex.Message}");
            }
        }

        // Must run after DeserializeLayerEntries: attaches per-material shader +
        // float/color snapshots to the layer at the index encoded in each string.
        private void DeserializeLayerMaterials(PluginData data)
        {
            if (!data.data.TryGetValue(LayerMatExtDataKey, out var raw)) return;
            if (!(raw is byte[] bytes)) return;
            try
            {
                var list = MessagePackSerializer.Deserialize<List<string>>(bytes);
                if (list == null) return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var added = 0;
                foreach (var s in list)
                {
                    if (string.IsNullOrEmpty(s)) continue;
                    // 8 fields: layer|ri|mi|shader|floats|colors|renderQueue|keywords.
                    // Split with a cap so a '/' or '=' inside a value can't spawn
                    // extra fields. Trailing fields are backward-compatible (older
                    // cards have 6).
                    var parts = s.Split(new[] { '|' }, 8);
                    if (parts.Length < 4) continue;
                    if (!int.TryParse(parts[0], out var li) || li < 0 || li >= Layers.Count) continue;
                    int.TryParse(parts[1], out var ri);
                    int.TryParse(parts[2], out var mi);

                    var st = new LayerBinding.MatState { Ri = ri, Mi = mi, Shader = parts[3] ?? "" };
                    if (parts.Length >= 7 && int.TryParse(parts[6], out var rq)) st.RenderQueue = rq;
                    if (parts.Length >= 8 && !string.IsNullOrEmpty(parts[7]))
                        foreach (var kw in parts[7].Split(','))
                            if (!string.IsNullOrEmpty(kw)) st.Keywords.Add(kw);

                    if (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]))
                        foreach (var item in parts[4].Split(','))
                        {
                            var eq = item.IndexOf('=');
                            if (eq <= 0) continue;
                            var name = item.Substring(0, eq);
                            if (float.TryParse(item.Substring(eq + 1), System.Globalization.NumberStyles.Float, inv, out var v))
                                st.Floats[name] = v;
                        }

                    if (parts.Length >= 6 && !string.IsNullOrEmpty(parts[5]))
                        foreach (var item in parts[5].Split(','))
                        {
                            var eq = item.IndexOf('=');
                            if (eq <= 0) continue;
                            var name = item.Substring(0, eq);
                            var comp = item.Substring(eq + 1).Split('/');
                            if (comp.Length != 4) continue;
                            var rgba = new float[4];
                            var ok = true;
                            for (var k = 0; k < 4; k++)
                                ok &= float.TryParse(comp[k], System.Globalization.NumberStyles.Float, inv, out rgba[k]);
                            if (ok) st.Colors[name] = rgba;
                        }

                    Layers[li].Materials.Add(st);
                    added++;
                }
                GDCPlugin.Logger?.LogDebug($"[chara] Deserialized {added} layer material state(s) onto {Layers.Count} layer(s)");
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogWarning($"[chara] Could not deserialize layer materials: {ex.Message}");
            }
        }
    }
}
