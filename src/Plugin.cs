using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KKAPI;
using KKAPI.Chara;
using Kumiho.UI;
using UnityEngine;

namespace GDCplugin
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("HoneySelect2")]
    [BepInProcess("HoneySelect2VR")]
    // Studio is a separate process. Without this the plugin never loads there,
    // so saved layering (and the other ExtData overrides) never re-mount when a
    // card is loaded into Studio.
    [BepInProcess("StudioNEOV2")]
    [BepInDependency("com.bepis.bepinex.sideloader", BepInDependency.DependencyFlags.HardDependency)]
    // KKAPI provides CharaCustomFunctionController + ExtendedSave dispatch.
    // Without this hard-dep BepInEx may load this plugin before KKAPI, in
    // which case RegisterExtraBehaviour returns but no save/load events
    // ever reach the controller.
    [BepInDependency(KoikatuAPI.GUID, KoikatuAPI.VersionConst)]
    // Soft dep on MaterialEditor: if it's installed I use its
    // XMLShaderProperties for auto-discovery. If not, the material section
    // gracefully shows "no editable properties" and the rest of the plugin
    // still works.
    [BepInDependency("com.deathweasel.bepinex.materialeditor", BepInDependency.DependencyFlags.SoftDependency)]
    public class GDCPlugin : BaseUnityPlugin
    {
        public const string GUID       = "com.kumiho.bepinex.hs2.gdcplugin";
        public const string PluginName = "GDC Wardrobe Plugin";
        public const string Version    = "1.0.0";

        // Author string GDC uses in her zipmod manifests. Pulled out so it's
        // easy to change later, or extend to a list if she ever signs mods
        // under more than one name.
        public const string GDCAuthorTag = "GDC";

        // Resource path the embedded KumihoUI bundle lives at. Format is
        // <RootNamespace>.<FolderWithDotsForSlashes>.<File>.
        private const string UIBundleResource = "GDCplugin.Resources.gdc_ui.unity3d";

        internal static new ManualLogSource Logger   = null!;
        internal static GDCPlugin           Instance = null!;
        internal static Harmony             HarmonyInstance = null!;

        // -- Filter toggle --------------------------------------------------
        // FilterEnabled is the live state — flipped at runtime by the hotkey.
        // It's persisted between sessions (BepInEx writes it back). Default
        // false so a fresh user doesn't see a confusingly-empty clothes list.
        // FilterOnByDefault is the user preference: when true, FilterEnabled
        // resets to true on every plugin Awake. Lets GDC/Sly opt into the
        // filter being the default behaviour without giving up the hotkey.
        internal static ConfigEntry<bool>             FilterEnabled     = null!;
        internal static ConfigEntry<bool>             FilterOnByDefault = null!;
        internal static ConfigEntry<KeyboardShortcut> FilterHotkey      = null!;

        // Second, independent filter: keeps only items whose zipmod manifest
        // carries the <gdcPlugin compatible="true"/> marker (mods built for
        // this plugin: presets / def_tex). Same live-state + on-by-default +
        // hotkey shape as the GDC-only filter above.
        internal static ConfigEntry<bool>             CompatFilterEnabled     = null!;
        internal static ConfigEntry<bool>             CompatFilterOnByDefault = null!;
        internal static ConfigEntry<KeyboardShortcut> CompatFilterHotkey      = null!;

        // -- Maker ----------------------------------------------------------
        // Toggles the GDC logo button injected into the maker's top category
        // tab strip. MakerTab watches this and adds/removes the button live.
        internal static ConfigEntry<bool>             ShowMenuButton     = null!;

        // -- Slider window --------------------------------------------------
        internal static ConfigEntry<bool>             SliderWindowOpen   = null!;
        internal static ConfigEntry<KeyboardShortcut> SliderWindowHotkey = null!;
        internal static ConfigEntry<float>            SliderWindowX = null!;
        internal static ConfigEntry<float>            SliderWindowY = null!;
        internal static ConfigEntry<float>            SliderWindowW = null!;
        internal static ConfigEntry<float>            SliderWindowH = null!;

        // -- Diagnostics ----------------------------------------------------
        // When on, Update logs one [perf] line per second: frame rate, worst
        // frame time, GC gen0 collections that second, whether the window is
        // open, and plugin activity counters. For pinning down the periodic
        // stutter without guessing. Off by default; never ships on.
        internal static ConfigEntry<bool>             PerfDiag = null!;

        // Activity counters, summed per second and logged + reset by PerfTick.
        // DiscoverTicks: heavy TextureBinding.Discover runs (bundle scan /
        // force-load). DeferredFrames: DeferredApply coroutine iterations
        // (GetComponentsInChildren + per-frame reapply). A high steady value in
        // either is the stutter source; near-zero means it's elsewhere (GC).
        internal static int DiscoverTicks;
        internal static int DeferredFrames;

        private float _perfAccum;
        private int   _perfFrames;
        private float _perfWorstMs;
        private int   _perfGen0Last;
        private long  _perfMemLast;     // managed heap last frame
        private long  _perfAllocAccum;  // summed positive heap growth this second

        private void Awake()
        {
            Logger   = base.Logger;
            Instance = this;

            // HS2's "Catch Unity Event Exceptions" plugin swallows Awake
            // exceptions before they reach the Unity log, so a failed init looks
            // completely silent (the plugin loads but does nothing). I log through
            // our own logger so the real cause stays visible. This earned its keep
            // diagnosing the obfuscated beta build; leaving it in permanently.
            try
            {
            BindConfig();

            // KumihoUI accent overrides cover the runtime-applied colors:
            // header text, accent lines, some hover/press tints. They must
            // be set before Initialize so the first BuildStyles pass picks
            // them up.
            //
            // GDC's two-color brand split: yellow for idle / off states,
            // teal for engaged / on states. Hand-painted PSDs supply the
            // texture colors directly. These overrides only affect runtime
            // accent lines, header text, and hover/press lerps.
            KumihoUI.OverrideAccent(
                idle:    Hex("#FFCC00"),  // brand yellow (off / idle)
                hover:   Hex("#FFD93D"),
                pressed: Hex("#E0B100"));

            KumihoUI.OverrideActive(
                idle:    Hex("#5DF5DD"),  // brand teal (on / engaged)
                hover:   Hex("#7DFAE0"),
                pressed: Hex("#3FD9C0"));

            // GDC hand-painted the brand colors into the PSDs directly, so
            // no runtime tinting is needed. The hand-painted source is
            // authoritative. MultiplyTintToBrand and ReadGpuTexture are
            // kept below in case a future plugin in the same family wants
            // the cheap programmatic recolor path again.
            // KumihoUI.TextureFilter = MultiplyTintToBrand;

            // Clamp every UI sprite's edges. The bundle textures import with
            // the Unity default wrapMode = Repeat; combined with bilinear
            // filtering and the 9-slice stretch IMGUI applies to buttons,
            // toolbars and scrollbars, the bright accent edge on one side
            // bleeds across and samples the opposite edge, painting a white
            // gradient at the stretched ends. Clamp stops the wrap-around so
            // edges stay crisp. Runs before Initialize so the first
            // BuildStyles pass picks up the clamped textures.
            KumihoUI.TextureFilter = ClampEdges;

            KumihoUI.Initialize(Assembly.GetExecutingAssembly(), UIBundleResource);

            // Apply JetBrains Mono everywhere. KumihoUI builds its styles
            // with the bundled Regular by default; swapping each style's
            // .font to Mono after init is cheaper than rebuilding them.
            // Reflection walks every public static GUIStyle property so we
            // don't have to maintain a list as KumihoUI evolves.
            ApplyMonoFont();

            HarmonyInstance = Harmony.CreateAndPatchAll(typeof(FilterHooks), GUID);

            // Per-character controller that persists blendshape overrides
            // to character cards via KKAPI's ExtendedSave. KKAPI handles
            // instance lifecycle: one controller is attached to every
            // ChaControl that loads, and KKAPI fires OnReload/OnCardBeingSaved
            // at the right moments.
            CharacterApi.RegisterExtraBehaviour<GDCharaController>(GUID);

            // Adds the GDC logo button to the maker's top category tab strip.
            // Subscribes to KKAPI's MakerFinishedLoading and injects on each
            // maker entry (the button dies with the scene, so re-add it).
            MakerTab.Initialize();

            // Diagnostic: hook ESF's CardBeingLoaded event so we can see
            // exactly which ChaFile instance gets the parsed ExtData, and
            // whether it matches the one our OnReload eventually sees.
            ExtensibleSaveFormat.ExtendedSave.CardBeingLoaded += chaFile =>
            {
                if (chaFile == null) return;
                var hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chaFile);
                var all  = ExtensibleSaveFormat.ExtendedSave.GetAllExtendedData(chaFile);
                var allKeys = all == null ? "(null dict)" : string.Join(",", all.Keys);
                var data = ExtensibleSaveFormat.ExtendedSave.GetExtendedDataById(chaFile, GUID);
                var dataHasOurKey = data?.data != null && data.data.ContainsKey("BlendshapeOverrides");
                Logger.LogDebug($"[hook] CardBeingLoaded: hash={hash}, fileName='{chaFile.charaFileName}', hasOurData={dataHasOurKey}, allGuids=[{allKeys}]");

                // Stash so the matching OnReload can pick it up. Recorded even
                // when data is null, so OnReload knows THIS card genuinely had no
                // GDC data and won't fall back to the (possibly stale)
                // ChaControl.chaFile and inherit the previous character's
                // overrides. Keyed by the chaFile instance so a batch load of
                // several characters doesn't collapse onto the last card.
                GDCharaController.StashLoadedCard(chaFile, data);
            };

            Logger.LogInfo($"{PluginName} v{Version} loaded. Filter={(FilterEnabled.Value ? "on" : "off")}.");
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Awake failed: {ex}");
                throw;
            }
        }

        private void OnDestroy() => KumihoUI.Dispose();

        private void BindConfig()
        {
            // Local helper: builds a ConfigDescription carrying a
            // ConfigurationManagerAttributes so the F1 menu orders entries
            // top-to-bottom by descending Order and tucks rarely-touched ones
            // behind the Advanced toggle.
            ConfigDescription Desc(string text, int order, bool advanced = false, AcceptableValueBase? range = null)
                => new ConfigDescription(text, range,
                    new ConfigurationManagerAttributes { Order = order, IsAdvanced = advanced });

            FilterEnabled = Config.Bind(
                "Filter", "Enabled", false,
                Desc("Live state of the GDC-only clothes filter. Toggled by the hotkey.", 30));

            FilterOnByDefault = Config.Bind(
                "Filter", "On by default", false,
                Desc("When enabled, the GDC-only filter starts ON each time HS2 launches. Hotkey still works to flip it off mid-session.", 20));

            // All three hotkeys live under one "Hotkeys" section so the F1
            // ConfigurationManager groups them together instead of scattering
            // them across Filter / Window. Order: window, GDC filter, compatible.
            FilterHotkey = Config.Bind(
                "Hotkeys", "GDC filter toggle", new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl),
                Desc("Shortcut that flips the GDC-only filter on and off.", 20));

            // The live state always resets to match the user's "on by default"
            // preference at startup. Previously the persistent FilterEnabled
            // would override and survive between sessions, ignoring the
            // preference flip. Hotkey toggles still work mid-session, they
            // just don't persist beyond the current launch.
            FilterEnabled.Value = FilterOnByDefault.Value;

            CompatFilterEnabled = Config.Bind(
                "Filter", "Compatible only enabled", false,
                Desc("Live state of the plugin-compatible clothes filter. Toggled by the hotkey.", 9));

            CompatFilterOnByDefault = Config.Bind(
                "Filter", "Compatible only on by default", false,
                Desc("When enabled, the compatible-only filter starts ON each time HS2 launches. Hotkey still works to flip it off mid-session.", 8));

            CompatFilterHotkey = Config.Bind(
                "Hotkeys", "Compatible filter toggle", new KeyboardShortcut(KeyCode.H, KeyCode.LeftControl),
                Desc("Shortcut that flips the compatible-only filter on and off.", 10));

            CompatFilterEnabled.Value = CompatFilterOnByDefault.Value;

            // -- Maker -----------------------------------------------------
            ShowMenuButton = Config.Bind(
                "Maker", "Show menu button", false,
                Desc("Show the GDC logo button in the maker's top category tab strip. Click it to open the GDC window.", 10));

            // -- Window ----------------------------------------------------
            SliderWindowHotkey = Config.Bind(
                "Hotkeys", "Window toggle", new KeyboardShortcut(KeyCode.G, KeyCode.LeftControl, KeyCode.LeftShift),
                Desc("Shortcut that shows or hides the GDC window.", 30));

            SliderWindowOpen = Config.Bind(
                "Window", "Open on start", false,
                Desc("Reopen the GDC window automatically when Maker loads.", 30));

            // -1 sentinels: SliderWindow centres itself the first time it's
            // shown. Persisted layout state, not knobs to fiddle, so they sit
            // under Advanced.
            SliderWindowX = Config.Bind("Window", "X", -1f, Desc("Window X. -1 centres on first open.", 4, advanced: true));
            SliderWindowY = Config.Bind("Window", "Y", -1f, Desc("Window Y. -1 centres on first open.", 3, advanced: true));
            SliderWindowW = Config.Bind("Window", "Width",  420f, Desc("Window width.",  2, advanced: true, new AcceptableValueRange<float>(280f, 1200f)));
            SliderWindowH = Config.Bind("Window", "Height", 540f, Desc("Window height.", 1, advanced: true, new AcceptableValueRange<float>(240f, 1600f)));

            PerfDiag = Config.Bind("Diagnostics", "Perf logging", false,
                Desc("Logs frame rate, worst frame time, GC collections and plugin activity once per second to the BepInEx log. For diagnosing stutter. Off by default.", 1, advanced: true));
        }

        private void Update()
        {
            if (FilterHotkey.Value.IsDown())
            {
                FilterEnabled.Value = !FilterEnabled.Value;
                Logger.LogInfo($"GDC filter {(FilterEnabled.Value ? "enabled" : "disabled")}.");
                FilterHooks.RequestListRefresh();
            }

            if (CompatFilterHotkey.Value.IsDown())
            {
                CompatFilterEnabled.Value = !CompatFilterEnabled.Value;
                Logger.LogInfo($"Compatible-only filter {(CompatFilterEnabled.Value ? "enabled" : "disabled")}.");
                FilterHooks.RequestListRefresh();
            }

            if (SliderWindowHotkey.Value.IsDown())
            {
                SliderWindow.Toggle();
            }

            if (PerfDiag.Value) PerfTick();
        }

        // Once-per-second perf snapshot. unscaledDeltaTime so a paused/slow-mo
        // scene doesn't skew it. gen0 GC delta confirms whether the stutter is
        // the Mono collector; window state + activity counters say whether our
        // draw loop or a discovery/deferred-apply loop is feeding it.
        private void PerfTick()
        {
            _perfFrames++;
            var ms = Time.unscaledDeltaTime * 1000f;
            if (ms > _perfWorstMs) _perfWorstMs = ms;

            // Approx allocation rate: sum the frame-to-frame managed heap growth.
            // A drop means a collection ran; skip those so we count only bytes
            // allocated, not freed. Comparing this with the window open vs closed
            // isolates how much of the allocation (-> GC frequency) is ours.
            var mem = System.GC.GetTotalMemory(false);
            if (mem > _perfMemLast) _perfAllocAccum += mem - _perfMemLast;
            _perfMemLast = mem;

            _perfAccum += Time.unscaledDeltaTime;
            if (_perfAccum < 1f) return;

            var gen0 = System.GC.CollectionCount(0);
            Logger.LogInfo($"[perf] {_perfFrames}fps worst={_perfWorstMs:F0}ms gen0GC+{gen0 - _perfGen0Last} " +
                           $"alloc={_perfAllocAccum / 1024}kb/s window={(SliderWindow.IsOpen ? "open" : "closed")} " +
                           $"discover={DiscoverTicks} deferFrames={DeferredFrames}");
            _perfGen0Last = gen0;
            _perfAccum = 0f; _perfFrames = 0; _perfWorstMs = 0f;
            _perfAllocAccum = 0;
            DiscoverTicks = 0; DeferredFrames = 0;
        }

        private void OnGUI()
        {
            // EnsureReady survives scene reloads. No-op when already loaded.
            KumihoUI.EnsureReady();
            SliderWindow.Draw();
        }

        // LateUpdate runs after Unity's animation/face controllers and the
        // game's per-frame shader updates have run, so this is where I push
        // user slider values back to win the override race. Without this,
        // HS2's facial animation overwrites blendshape weights and
        // Sideloader/MaterialEditor refresh-passes wipe material floats.
        private void LateUpdate()
        {
            BlendshapeBinding.PushOverrides();
            MaterialBinding.PushOverrides();
            TextureBinding.PushOverrides();
        }

        // Textures that should pass through untouched: warning art keeps its
        // own colour, the logo is drawn at full-colour, preview tiles render
        // user content, icons are tinted at draw time via GUI.color in
        // KumihoUI, color-picker swatches need their full gradient intact.
        private static bool IsTintCandidate(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant();
            if (n.Contains("warning"))          return false;
            if (n.StartsWith("icon-"))          return false;
            if (n.StartsWith("channel-icon-"))  return false;
            if (n.StartsWith("kumihovector"))   return false;
            if (n.Contains("preview"))          return false;
            if (n.StartsWith("picker-"))        return false;
            return true;
        }

        // On-state textures get the Active accent (brighter amber) so a
        // toggled/pressed control reads warmer than its idle counterpart.
        // Match the bundle's naming: btn-on, btn-on-hover, btn-on-act,
        // toggle-on, switch-on, tabheader-on, toolbar-button-on, etc.
        private static bool IsOnState(string name)
        {
            var n = name.ToLowerInvariant();
            return n.EndsWith("-on")
                || n.Contains("-on-")
                || n.Contains("-on.");
        }

        // TextureFilter callback. KumihoUI invokes this once per loaded
        // texture during Initialize. Sets the sampler to clamp at the edges
        // so 9-slice stretching can't wrap-sample the opposite edge and
        // bleed the accent into a white gradient at the ends. Mutates the
        // bundle texture in place (we own this bundle) and returns it
        // unchanged otherwise — no recolor, no copy. (The "stretch wash" was a
        // separate issue: tabs used the short TabSmall texture stretched tall;
        // fixed by switching the tab strip to TabHeader. filterMode is left at
        // the bundle default — Bilinear — to match KumihoUITestPlugin.)
        private static Texture2D ClampEdges(string name, Texture2D src)
        {
            if (src != null) src.wrapMode = TextureWrapMode.Clamp;
            return src!;
        }

        // TextureFilter callback. KumihoUI invokes this once per loaded
        // texture during Initialize. Returns a colorized copy where each
        // pixel = source * brand-color (Photoshop "Multiply" blend), or
        // the source unchanged for textures on the skip list.
        private static Texture2D MultiplyTintToBrand(string name, Texture2D src)
        {
            if (src == null) return src!;
            if (!IsTintCandidate(name)) return src;

            var tint = IsOnState(name) ? Kumiho.UI.KumihoUI.Colors.Active
                                       : Kumiho.UI.KumihoUI.Colors.Accent;

            // Try the cheap path first: textures imported with Read/Write
            // Enabled in Unity let us call GetPixels directly. Bundles built
            // without that flag throw UnityException; in that case I blit
            // through a temporary RenderTexture to extract the pixels GPU
            // side, which works regardless of import settings.
            Color[] pixels;
            Texture2D? scratch = null;
            try
            {
                pixels = src.GetPixels();
            }
            catch (UnityException)
            {
                scratch = ReadGpuTexture(src);
                if (scratch == null)
                {
                    Logger?.LogWarning($"[tint] Could not read pixels for '{name}', skipping. UI element will render untinted.");
                    return src;
                }
                pixels = scratch.GetPixels();
            }

            // Per-texture log silenced in v0.3+. The blit-readback path makes
            // tinting work regardless of bundle import flags, so the spam
            // isn't useful day-to-day. Re-enable temporarily with LogDebug
            // if a future texture comes back untinted.

            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.a < 0.01f) continue;

                // Multiply blend: dark stays dark, white becomes tint,
                // mid-grey becomes a muted version of tint. Alpha preserved.
                pixels[i] = new Color(
                    p.r * tint.r,
                    p.g * tint.g,
                    p.b * tint.b,
                    p.a);
            }

            var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false)
            {
                hideFlags  = HideFlags.HideAndDontSave,
                filterMode = src.filterMode,
                wrapMode   = src.wrapMode,
            };
            copy.SetPixels(pixels);
            copy.Apply(updateMipmaps: false);

            // Done with the GPU-readback scratch buffer.
            if (scratch != null) UnityEngine.Object.Destroy(scratch);

            return copy;
        }

        // Read a non-readable GPU texture back to CPU by blitting through a
        // temporary RenderTexture. Returns a new readable Texture2D the
        // caller is responsible for destroying. Null on hard failure.
        // Uses sRGB read/write to match how the bundle textures are sampled
        // at draw time, so the multiply result roundtrips cleanly.
        private static Texture2D? ReadGpuTexture(Texture2D src)
        {
            RenderTexture? rt = null;
            var prevActive = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(
                    src.width, src.height, 0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                readable.Apply(updateMipmaps: false);
                return readable;
            }
            catch (System.Exception ex)
            {
                Logger?.LogWarning($"[tint] GPU readback failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Reverse of Hex(), used for debug logging.
        private static string ColorHex(Color c)
            => $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";

        // Walks every public static GUIStyle on KumihoUI and points its
        // .font at the bundled JetBrains Mono. Cheap one-shot done after
        // Initialize so every subsequent draw uses Mono without us touching
        // each call site.
        private static void ApplyMonoFont()
        {
            var mono = KumihoUI.Mono;
            if (mono == null)
            {
                Logger?.LogWarning("[font] KumihoUI.Mono was null after Initialize; bundle may be missing the Mono TTF.");
                return;
            }

            var count = 0;
            foreach (var prop in typeof(KumihoUI).GetProperties(
                         System.Reflection.BindingFlags.Static
                         | System.Reflection.BindingFlags.Public))
            {
                if (prop.PropertyType != typeof(UnityEngine.GUIStyle)) continue;
                if (!(prop.GetValue(null) is UnityEngine.GUIStyle style)) continue;
                style.font = mono;
                count++;
            }
            Logger?.LogDebug($"[font] Applied JetBrains Mono to {count} KumihoUI style(s).");
        }

        // Tiny hex helper so the accent definitions read cleanly above. Format
        // is #RRGGBB or #RRGGBBAA. Returns Color.magenta on malformed input
        // so a typo is loud rather than silent.
        private static Color Hex(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#') return Color.magenta;
            try
            {
                int Read(int i) => System.Convert.ToInt32(s.Substring(i, 2), 16);
                var r = Read(1) / 255f;
                var g = Read(3) / 255f;
                var b = Read(5) / 255f;
                var a = s.Length >= 9 ? Read(7) / 255f : 1f;
                return new Color(r, g, b, a);
            }
            catch { return Color.magenta; }
        }
    }
}
