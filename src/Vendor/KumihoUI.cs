using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Kumiho.UI;

/// <summary>
/// Drop-in IMGUI skin for HS2/AIS/KKS BepInEx plugins. Loads the embedded
/// kumiho_ui.unity3d, caches every Texture2D and Font, and exposes ready-built
/// GUIStyle objects keyed by control type. Call Initialize once from Awake,
/// then EnsureReady at the top of each OnGUI to survive scene reloads.
/// </summary>
public static class KumihoUI
{
    // -------------------------------------------------------------------
    // Palette  (mirror of kumiho_gen.py PALETTE; keep these two in sync)
    // -------------------------------------------------------------------
    public static class Colors
    {
        public static readonly Color Bg          = Hex("#0E0E11");
        public static readonly Color SurfaceLow  = Hex("#16161C");
        public static readonly Color Surface     = Hex("#202028");
        public static readonly Color SurfaceHi   = Hex("#2C2C38");
        public static readonly Color SurfacePr   = Hex("#121218");
        public static readonly Color Border      = Hex("#3A3A48");
        public static readonly Color Text        = Hex("#E8E8F0");
        public static readonly Color TextDim     = Hex("#8C8C9A");

        // Accents are intentionally mutable so a consuming plugin can swap them
        // at runtime to reskin the kit. Set them before Initialize via
        // KumihoUI.OverrideAccent / OverrideActive (or assign directly) and
        // BuildStyles will pick the new values up.

        // Brand / structural accent: used wherever a control is in a default/idle
        // state and still wants a colored cue (handles, top-edge lines on idle
        // buttons, section headers, etc.).
        public static Color Accent      = Hex("#1DC198");
        public static Color AccentHi    = Hex("#3FDCB6");
        public static Color AccentPr    = Hex("#16906F");

        // Active / on-state accent: used wherever a control is engaged, toggled
        // on, or selected. Magenta provides clear semantic contrast against the
        // teal brand accent so off vs on reads at a glance.
        public static Color Active      = Hex("#D946A6");
        public static Color ActiveHi    = Hex("#F36CC0");
        public static Color ActivePr    = Hex("#A8327E");

        public static readonly Color Warning     = Hex("#F2A53B");
    }

    /// <summary>
    /// Optional hook invoked once per bundle texture at load time. The consumer
    /// can return a recolored copy to reskin the kit without rebuilding the
    /// bundle. Return the source unchanged for textures it doesn't care about,
    /// or null/source if no transformation is desired.
    /// Set this BEFORE calling Initialize so the filter runs during the first
    /// BuildStyles pass.
    /// </summary>
    public static System.Func<string, Texture2D, Texture2D> TextureFilter { get; set; }

    /// <summary>
    /// Override the structural accent colors (used for section headers, idle
    /// cues, etc.). Pass new values then call Initialize or EnsureReady.
    /// </summary>
    public static void OverrideAccent(Color idle, Color hover, Color pressed)
    {
        Colors.Accent   = idle;
        Colors.AccentHi = hover;
        Colors.AccentPr = pressed;
    }

    /// <summary>
    /// Override the on-state accent colors (toggled controls, selected tabs,
    /// drag thumbs). Pass new values then call Initialize or EnsureReady.
    /// </summary>
    public static void OverrideActive(Color idle, Color hover, Color pressed)
    {
        Colors.Active   = idle;
        Colors.ActiveHi = hover;
        Colors.ActivePr = pressed;
    }

    // -------------------------------------------------------------------
    // Fonts
    // -------------------------------------------------------------------
    public static Font Regular { get; private set; }
    public static Font Bold    { get; private set; }
    public static Font Mono    { get; private set; }

    // -------------------------------------------------------------------
    // Branding
    // -------------------------------------------------------------------
    /// <summary>
    /// Kumiho logo texture (transparent background). Use <see cref="DrawLogo(Rect)"/>
    /// or <see cref="DrawLogo(Rect, Color)"/> for drawing, or read this directly
    /// to bake it into a custom GUIStyle.
    /// </summary>
    public static Texture2D Logo { get; private set; }

    // -------------------------------------------------------------------
    // Styles (one per control type, all six states baked in where relevant)
    // -------------------------------------------------------------------
    public static GUIStyle Window        { get; private set; }
    public static GUIStyle Box           { get; private set; }
    public static GUIStyle Box2          { get; private set; }
    public static GUIStyle Panel         { get; private set; }
    public static GUIStyle WarningBox    { get; private set; }
    public static GUIStyle PreviewTile   { get; private set; }

    public static GUIStyle Button        { get; private set; }
    public static GUIStyle ToolbarButton { get; private set; }
    public static GUIStyle Toggle        { get; private set; }
    public static GUIStyle Switch        { get; private set; }
    public static GUIStyle CloseButton   { get; private set; }

    // Background-only styles for animated wrappers (see KumihoDraw.cs).
    // Used to crossfade between states with explicit alpha.
    public static GUIStyle BtnBgIdle      { get; private set; }
    public static GUIStyle BtnBgHover     { get; private set; }
    public static GUIStyle BtnBgAct       { get; private set; }
    public static GUIStyle BtnBgOnIdle    { get; private set; }
    public static GUIStyle BtnBgOnHover   { get; private set; }
    public static GUIStyle BtnBgOnAct     { get; private set; }

    public static GUIStyle ToggleBgIdle    { get; private set; }
    public static GUIStyle ToggleBgHover   { get; private set; }
    public static GUIStyle ToggleBgAct     { get; private set; }
    public static GUIStyle ToggleBgOnIdle  { get; private set; }
    public static GUIStyle ToggleBgOnHover { get; private set; }
    public static GUIStyle ToggleBgOnAct   { get; private set; }

    public static GUIStyle SwitchBgIdle    { get; private set; }
    public static GUIStyle SwitchBgHover   { get; private set; }
    public static GUIStyle SwitchBgAct     { get; private set; }
    public static GUIStyle SwitchBgOnIdle  { get; private set; }
    public static GUIStyle SwitchBgOnHover { get; private set; }
    public static GUIStyle SwitchBgOnAct   { get; private set; }

    // Path B switch: separate track (3 state textures) and handle (1 white
    // texture, tinted teal/magenta at runtime). Used by KumihoDraw.AnimatedSwitch
    // for the true sliding-handle animation. The handle texture is exposed as
    // a Texture2D rather than a GUIStyle so we can apply GUI.color tinting
    // for the off/on color transition.
    public static GUIStyle  SwitchTrackBg      { get; private set; }
    public static GUIStyle  SwitchTrackHoverBg { get; private set; }
    public static GUIStyle  SwitchTrackActBg   { get; private set; }
    public static Texture2D SwitchHandleTex    { get; private set; }

    // Label-only styles (no background) for drawing text on top of animated backgrounds.
    public static GUIStyle ButtonText    { get; private set; }
    public static GUIStyle ToggleText    { get; private set; }

    public static GUIStyle TabHeader     { get; private set; }
    public static GUIStyle TabSmall      { get; private set; }
    public static GUIStyle TabContent    { get; private set; }

    public static GUIStyle TextField     { get; private set; }

    public static GUIStyle SliderH       { get; private set; }
    public static GUIStyle SliderV       { get; private set; }
    public static GUIStyle SliderThumb   { get; private set; }
    public static GUIStyle SliderHFill   { get; private set; }   // value fill overlay (drawn manually)
    public static GUIStyle SliderVFill   { get; private set; }   // V version of the fill overlay

    public static GUIStyle ScrollH       { get; private set; }
    public static GUIStyle ScrollV       { get; private set; }
    public static GUIStyle ScrollHThumb  { get; private set; }
    public static GUIStyle ScrollVThumb  { get; private set; }

    public static GUIStyle Label         { get; private set; }
    public static GUIStyle LabelBold     { get; private set; }
    public static GUIStyle LabelMuted    { get; private set; }
    public static GUIStyle LabelSection  { get; private set; }   // section title text style (use SectionHeader method for full collapsible header)
    public static GUIStyle LabelWarning  { get; private set; }

    // -------------------------------------------------------------------
    // Icons (16x16 white sprites, tinted at runtime via GUI.color)
    // -------------------------------------------------------------------
    /// <summary>
    /// Registry of all icon textures keyed by short name (e.g. "plus", "gear",
    /// "chevron-down"). Populated from the bundle in BuildStyles. Use
    /// <see cref="DrawIcon(Rect, string, Color)"/> or <see cref="IconButton"/>
    /// for the common cases; index this dictionary directly if you need raw
    /// access to the texture.
    /// </summary>
    public static readonly System.Collections.Generic.Dictionary<string, Texture2D> Icons
        = new System.Collections.Generic.Dictionary<string, Texture2D>();

    // -------------------------------------------------------------------
    // Color picker textures (composited manually by KumihoColorPicker.Show)
    // -------------------------------------------------------------------
    public static Texture2D PickerHueTex   { get; private set; }   // vertical rainbow strip
    public static Texture2D PickerSatTex   { get; private set; }   // 128x16 white-to-transparent
    public static Texture2D PickerValTex   { get; private set; }   // 16x128 transparent-to-black
    public static Texture2D PickerHueThumb { get; private set; }   // horizontal marker for hue strip
    public static Texture2D PickerSvThumb  { get; private set; }   // double-ring marker for SV box

    // -------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------
    private static AssetBundle _bundle;
    private static bool _stylesBuilt;
    private static readonly object _initLock = new object();

    // sentinel texture used to detect scene-reload null-out
    private static Texture2D _sentinel;

    /// <summary>
    /// Load the embedded bundle and build every style. Safe to call multiple
    /// times; subsequent calls are no-ops if the bundle is already loaded.
    /// </summary>
    /// <param name="hostAssembly">
    /// The assembly that embeds kumiho_ui.unity3d as a resource. Pass
    /// Assembly.GetExecutingAssembly() from your plugin entry point.
    /// </param>
    /// <param name="resourceName">
    /// Fully qualified resource name. Format: &lt;DefaultNamespace&gt;.&lt;folder&gt;.kumiho_ui.unity3d
    /// </param>
    public static void Initialize(Assembly hostAssembly, string resourceName)
    {
        lock (_initLock)
        {
            if (_bundle == null)
                _bundle = ResolveOrLoadBundle(hostAssembly, resourceName);

            if (!_stylesBuilt)
                BuildStyles();
        }
    }

    /// <summary>
    /// Call at the top of OnGUI. Detects scene-reload texture loss and
    /// rebuilds styles transparently.
    /// </summary>
    public static void EnsureReady()
    {
        if (_stylesBuilt && (_sentinel == null))
            _stylesBuilt = false;
        if (!_stylesBuilt)
            BuildStyles();
    }

    /// <summary>
    /// Release the bundle and all loaded assets. Call from OnDestroy.
    /// </summary>
    public static void Dispose()
    {
        if (_bundle != null)
        {
            _bundle.Unload(true);
            _bundle = null;
        }
        _stylesBuilt = false;
    }

    // -------------------------------------------------------------------
    // Bundle resolution
    // -------------------------------------------------------------------
    private static AssetBundle ResolveOrLoadBundle(Assembly hostAssembly, string resourceName)
    {
        // reuse if another instance of the plugin already loaded it
        foreach (var b in AssetBundle.GetAllLoadedAssetBundles())
        {
            if (b.name == "kumiho_ui" || b.name == "kumiho_ui.unity3d")
                return b;
        }

        if (hostAssembly == null)
            throw new ArgumentNullException(nameof(hostAssembly),
                "Pass Assembly.GetExecutingAssembly() from your plugin.");

        using (var stream = hostAssembly.GetManifestResourceStream(resourceName))
        {
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"Embedded resource '{resourceName}' not found in {hostAssembly.GetName().Name}. " +
                    "Check the resource name (it's <Namespace>.<Folder>.<File>, dots for separators).");
            }
            var buf = new byte[stream.Length];
            stream.Read(buf, 0, buf.Length);
            return AssetBundle.LoadFromMemory(buf);
        }
    }

    // -------------------------------------------------------------------
    // Style construction
    // -------------------------------------------------------------------
    private static Texture2D Tex(string name)
    {
        var t = _bundle.LoadAsset<Texture2D>(name);
        // Pipe through the optional filter so consumers can reskin without touching
        // the bundle. Original bundle texture stays untouched; filter is expected
        // to return a copy when it transforms.
        if (t != null && TextureFilter != null)
            t = TextureFilter(name, t) ?? t;
        return t;
    }
    private static Font FontAsset(string name) => _bundle.LoadAsset<Font>(name);

    /// <summary>
    /// Load a Texture2D from the kit's asset bundle by name. Bypasses the
    /// TextureFilter recolor hook so the returned texture is exactly as
    /// painted (used for premade preview art that must not be reskinned).
    /// Returns null if the bundle isn't loaded or the asset is missing.
    /// </summary>
    public static Texture2D LoadBundleTexture(string name)
    {
        if (_bundle == null || string.IsNullOrEmpty(name)) return null;
        try { return _bundle.LoadAsset<Texture2D>(name); }
        catch { return null; }
    }

    private static void BuildStyles()
    {
        if (_bundle == null)
            throw new InvalidOperationException("KumihoUI not initialized. Call Initialize() first.");

        // Fonts (any of these can be null if you didn't include them in the bundle).
        // Noto Sans is the default; Inter is kept as a fallback so older bundles still load.
        Regular = FontAsset("NotoSans-Regular")
               ?? FontAsset("Inter-Regular-slnt=0")
               ?? FontAsset("Inter-Regular");
        Bold    = FontAsset("NotoSans-Bold")
               ?? FontAsset("Inter-Bold-slnt=0")
               ?? FontAsset("Inter-Bold");
        Mono    = FontAsset("JetBrainsMonoNL-Regular");

        // brand asset (optional; missing logo just skips DrawLogo silently)
        Logo    = Tex("KumihoVectorTrans");

        // color picker assets (optional; KumihoColorPicker.Show no-ops gracefully if missing)
        PickerHueTex   = Tex("picker-hue");
        PickerSatTex   = Tex("picker-sat");
        PickerValTex   = Tex("picker-val");
        PickerHueThumb = Tex("picker-hue-thumb");
        PickerSvThumb  = Tex("picker-sv-thumb");

        // Icons. Each is a 16x16 white sprite on transparent; the registry
        // lookup returns null for missing names so callers can safely no-op.
        Icons.Clear();
        string[] iconNames =
        {
            // Geometric basics
            "plus", "minus", "close", "check",
            "chevron-up", "chevron-down", "chevron-left", "chevron-right",
            // Actions
            "gear", "refresh", "reset", "save", "folder", "eye", "search", "trash",
            // Status
            "info", "warning", "star", "question",
        };
        foreach (var name in iconNames)
        {
            var tex = Tex("icon-" + name);
            if (tex != null) Icons[name] = tex;
        }

        // Textures
        var tWin       = Tex("window");
        var tBox       = Tex("box");
        var tBox2      = Tex("box2");
        var tPanel     = Tex("panel");
        var tWarn      = Tex("warningbox");
        var tPrev      = Tex("preview_norm");
        var tPrevHov   = Tex("preview_hover");

        var tBtn       = Tex("btn");
        var tBtnHov    = Tex("btn-hover");
        var tBtnAct    = Tex("btn-act");
        var tBtnOn     = Tex("btn-on");
        var tBtnOnHov  = Tex("btn-on-hover");
        var tBtnOnAct  = Tex("btn-on-act");

        var tTb        = Tex("toolbar-button");
        var tTbHov     = Tex("toolbar-button-hov");
        var tTbAct     = Tex("toolbar-button-act");
        var tTbFoc     = Tex("toolbar-button-foc");
        var tTbOn      = Tex("toolbar-button-on");
        var tTbOnAct   = Tex("toolbar-button-act-on");

        var tTog       = Tex("toggle");
        var tTogHov    = Tex("toggle-hover");
        var tTogAct    = Tex("toggle-act");
        var tTogOn     = Tex("toggle-on");
        var tTogOnHov  = Tex("toggle-on-hover");
        var tTogOnAct  = Tex("toggle-on-act");

        var tSw        = Tex("switch");
        var tSwHov     = Tex("switch-hover");
        var tSwAct     = Tex("switch-act");
        var tSwOn      = Tex("switch-on");
        var tSwOnHov   = Tex("switch-on-hover");
        var tSwOnAct   = Tex("switch-on-act");

        // Path B switch sprites: separate track + handle. The old 6 sprites
        // above stay loaded for backward compat with anything using the
        // monolithic Switch style. New plugins should use KumihoDraw.AnimatedSwitch.
        var tSwTrack    = Tex("switch-track");
        var tSwTrackHov = Tex("switch-track-hover");
        var tSwTrackAct = Tex("switch-track-act");
        var tSwHandle   = Tex("switch-handle");

        var tTabH      = Tex("tabheader");
        var tTabHHov   = Tex("tabheader-hover");
        var tTabHOn    = Tex("tabheader-on");
        var tTabSm     = Tex("tabsmall");
        var tTabSmHov  = Tex("tabsmall-hover");
        var tTabSmOn   = Tex("tabsmall-on");
        var tTabBody   = Tex("tabcontent");

        var tTf        = Tex("TextField");
        var tTfFoc     = Tex("TextField-focused");

        var tSlH       = Tex("slider-horiz");
        var tSlV       = Tex("slider-vert");
        var tSlThumb   = Tex("slider-thumb");
        var tSlThumbHv = Tex("slider-thumb-hover");
        var tSlThumbAc = Tex("slider-thumb-act");

        var tScH        = Tex("scroll-horiz");
        var tScV        = Tex("scroll-vert");
        var tScHThm     = Tex("scroll-horiz-thumb");
        var tScHThmHov  = Tex("scroll-horiz-thumb-hover");
        var tScHThmAct  = Tex("scroll-horiz-thumb-act");
        var tScVThm     = Tex("scroll-vert-thumb");
        var tScVThmHov  = Tex("scroll-vert-thumb-hover");
        var tScVThmAct  = Tex("scroll-vert-thumb-act");

        var tClose     = Tex("btn-close");
        var tCloseHov  = Tex("btn-close-hover");
        var tCloseAct  = Tex("btn-close-act");

        // pick any texture for the rebuild sentinel
        _sentinel = tBtn;

        // ============ panels ============
        Window = NewStyle(tWin, border: 5, padding: 8, font: Regular, fontSize: 12);
        Box    = NewStyle(tBox, border: 5, padding: 6);
        Box2   = NewStyle(tBox2, border: 5, padding: 6);
        Panel  = NewStyle(tPanel, border: 5, padding: 4);
        WarningBox = NewStyle(tWarn, border: 5, padding: 8);
        PreviewTile = new GUIStyle
        {
            normal = { background = tPrev },
            hover  = { background = tPrevHov },
            border = new RectOffset(5, 5, 5, 5),
            padding = new RectOffset(4, 4, 4, 4),
        };

        // ============ buttons ============
        // 72x32 slanted parallelogram, 20-degree H skew, no handle. Top edge
        // and left edge each carry an accent (teal for off, magenta for on);
        // the left has a double-bar L-bracket detail. Asymmetric 9-slice:
        // left border 18 covers the slant + both left-accent bars; right
        // border 12 covers just the right slant; top 4 covers the outline
        // plus the top accent; bottom 1 covers the outline.
        Button = new GUIStyle
        {
            normal   = { background = tBtn,       textColor = Colors.Text },
            hover    = { background = tBtnHov,    textColor = Colors.Text },
            active   = { background = tBtnAct,    textColor = Colors.Text },
            onNormal = { background = tBtnOn,     textColor = Colors.Text },
            onHover  = { background = tBtnOnHov,  textColor = Colors.Text },
            onActive = { background = tBtnOnAct,  textColor = Colors.Text },
            border   = new RectOffset(18, 12, 4, 1),
            padding  = new RectOffset(22, 16, 8, 6),
            margin   = new RectOffset(2, 2, 2, 2),
            alignment = TextAnchor.MiddleCenter,
            font     = Regular,
            fontSize = 12,
            fixedHeight = 32,
        };

        // 28x22 slanted parallelogram, 20deg skew, 1px outline + 1px top accent
        // and a 1x3 px left-edge dot. Smaller and denser than the main Button,
        // meant for icon strips and quick-action rows. Asymmetric 9-slice:
        // left 7 covers the slant + the left dot, right 5 covers just the
        // slant, top 3 covers outline + accent + buffer, bottom 1 covers the
        // outline.
        ToolbarButton = new GUIStyle
        {
            normal   = { background = tTb,       textColor = Colors.TextDim },
            hover    = { background = tTbHov,    textColor = Colors.Text },
            active   = { background = tTbAct,    textColor = Colors.Text },
            focused  = { background = tTbFoc,    textColor = Colors.Text },
            onNormal = { background = tTbOn,     textColor = Colors.Text },
            onActive = { background = tTbOnAct,  textColor = Colors.Text },
            // Bottom border 2 (not 1) so the white bottom outline row sits in
            // the 1:1 bottom slice, not the stretched middle. Right border 8
            // (not 5): the slanted right outline travels from x=31 at the top
            // to x=24 at the bottom of the 32px sprite, so a 5px right border
            // left the lower part of that diagonal in the horizontally-stretched
            // middle slice, smearing it into a white wash on wide buttons (the
            // Reset button is drawn ~70px). 8 pulls the whole diagonal into the
            // 1:1 right slice. Height is native (16) so there's no vertical
            // stretch to smear it the other way.
            // Left border 10 (not 7): the sprite's yellow left-accent stripe is
            // diagonal (follows the parallelogram's left slant), sitting at x=8
            // at the top and x=3 at the bottom of the 28px sprite. A 7px left
            // border left the stripe's upper portion (x>=7) in the horizontally
            // stretched middle slice, smearing it into a yellow wash on wide
            // buttons. 10 pulls the whole diagonal stripe into the 1:1 left cap.
            // Right border 8 already contains the right slant. (Sprite is 28x22,
            // not the 32x16 an older comment claimed.)
            border   = new RectOffset(10, 8, 3, 2),
            padding  = new RectOffset(12, 8, 3, 2),
            margin   = new RectOffset(1, 1, 1, 1),
            alignment = TextAnchor.MiddleCenter,
            font     = Bold ?? Regular,
            fontSize = 11,
            // Native sprite is 32x16. Draw at native height so the 9-slice is
            // 1:1 vertical; the old 22 stretched the 16px texture 1.375x and
            // smeared the white outline into a wash.
            fixedHeight = 16,
        };

        // 24x24 slanted parallelogram, 20deg skew. Off-states are dark body with
        // dim teal top accent; on-states are full magenta body (no accent — the
        // solid magenta IS the "checked" indicator). Fixed size, no 9-slice.
        Toggle = new GUIStyle
        {
            normal   = { background = tTog,       textColor = Colors.Text },
            hover    = { background = tTogHov,    textColor = Colors.Text },
            active   = { background = tTogAct,    textColor = Colors.Text },
            onNormal = { background = tTogOn,     textColor = Colors.Text },
            onHover  = { background = tTogOnHov,  textColor = Colors.Text },
            onActive = { background = tTogOnAct,  textColor = Colors.Text },
            border   = new RectOffset(0, 0, 0, 0),
            padding  = new RectOffset(0, 0, 0, 0),
            margin   = new RectOffset(2, 4, 2, 2),
            fixedWidth = 24,
            fixedHeight = 24,
        };

        Switch = new GUIStyle
        {
            normal   = { background = tSw,       textColor = Colors.Text },
            hover    = { background = tSwHov,    textColor = Colors.Text },
            active   = { background = tSwAct,    textColor = Colors.Text },
            onNormal = { background = tSwOn,     textColor = Colors.Text },
            onHover  = { background = tSwOnHov,  textColor = Colors.Text },
            onActive = { background = tSwOnAct,  textColor = Colors.Text },
            // 72x32 slanted parallelogram with magenta-or-teal handle. Not 9-sliced;
            // the handle position is baked into each PSD (left for off, right for on).
            border = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(2, 4, 2, 2),
            fixedWidth = 72,
            fixedHeight = 32,
        };

        CloseButton = new GUIStyle
        {
            normal = { background = tClose,    textColor = Colors.TextDim },
            hover  = { background = tCloseHov, textColor = Colors.Text },
            active = { background = tCloseAct, textColor = Colors.Text },
            border = new RectOffset(0, 0, 0, 0),
            fixedWidth = 16,
            fixedHeight = 16,
        };

        // ============ tabs ============
        // 80x30 slanted parallelogram with top accent. Inactive uses the dim
        // SurfaceLow body and dim teal accent; active matches the content
        // panel fill (Surface) with a magenta accent. Asymmetric 9-slice:
        // left/right covers the slant, top covers outline+accent, bottom 1px.
        TabHeader = new GUIStyle
        {
            normal   = { background = tTabH,    textColor = Colors.TextDim },
            hover    = { background = tTabHHov, textColor = Colors.Text },
            onNormal = { background = tTabHOn,  textColor = Colors.Text },
            onHover  = { background = tTabHOn,  textColor = Colors.Text },
            // Left border 11 covers slant (~6 px) + left accent (2 px) + buffer.
            border   = new RectOffset(11, 8, 4, 1),
            padding  = new RectOffset(15, 12, 6, 4),
            margin   = new RectOffset(1, 1, 0, 0),
            alignment = TextAnchor.MiddleCenter,
            font     = Bold ?? Regular,
            fontSize = 12,
            fixedHeight = 30,
        };
        TabSmall = new GUIStyle(TabHeader)
        {
            normal   = { background = tTabSm,    textColor = Colors.TextDim },
            hover    = { background = tTabSmHov, textColor = Colors.Text },
            onNormal = { background = tTabSmOn,  textColor = Colors.Text },
            onHover  = { background = tTabSmOn,  textColor = Colors.Text },
            border   = new RectOffset(9, 6, 4, 1),
            padding  = new RectOffset(13, 10, 4, 3),
            fontSize = 11,
            fixedHeight = 22,
        };
        TabContent = NewStyle(tTabBody, border: 3, padding: 6);

        // ============ text field ============
        // 14x14 slanted parallelogram, 20deg skew, Border outline (Active outline
        // when focused). Asymmetric 9-slice for the slants on both sides.
        TextField = new GUIStyle
        {
            normal  = { background = tTf,    textColor = Colors.Text },
            focused = { background = tTfFoc, textColor = Colors.Text },
            hover   = { background = tTf,    textColor = Colors.Text },
            border  = new RectOffset(6, 6, 1, 1),
            padding = new RectOffset(8, 8, 2, 2),
            margin  = new RectOffset(2, 2, 2, 2),
            font    = Mono ?? Regular,
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter,
        };

        // ============ sliders ============
        SliderH = new GUIStyle
        {
            normal = { background = tSlH },
            border = new RectOffset(3, 3, 3, 3),
            margin = new RectOffset(4, 4, 8, 8),
            fixedHeight = 7,
        };

        // Fill overlay drawn manually by HorizontalSliderWithFill below.
        var tSlHFill = Tex("slider-horiz-fill");
        SliderHFill = new GUIStyle
        {
            normal = { background = tSlHFill },
            border = new RectOffset(3, 3, 3, 3),
            fixedHeight = 7,
        };
        SliderV = new GUIStyle
        {
            normal = { background = tSlV },
            border = new RectOffset(3, 3, 3, 3),
            margin = new RectOffset(8, 8, 4, 4),
            fixedWidth = 7,
        };
        // Fill overlay for the vertical slider, drawn manually by
        // VerticalSliderWithFill below. Same source as the H fill since the
        // 7x7 pill is rotationally symmetric.
        var tSlVFill = Tex("slider-vert-fill");
        SliderVFill = new GUIStyle
        {
            normal = { background = tSlVFill },
            border = new RectOffset(3, 3, 3, 3),
            fixedWidth = 7,
        };
        SliderThumb = new GUIStyle
        {
            normal  = { background = tSlThumb },
            hover   = { background = tSlThumbHv },
            active  = { background = tSlThumbAc },
            border  = new RectOffset(0, 0, 0, 0),
            fixedWidth = 14,
            fixedHeight = 14,
        };

        // ============ scrollbars ============
        // Tracks: flat pill channels, native 16x8 (H) / 8x16 (V), 1px outline.
        // Drawn at native thickness (8) so the 9-slice doesn't squash the white
        // outline and bilinear-blur it into a wash. Middle stretches along the
        // long axis as the parent control resizes.
        ScrollH = new GUIStyle
        {
            normal = { background = tScH },
            border = new RectOffset(1, 1, 1, 1),
            fixedHeight = 8,
        };
        ScrollV = new GUIStyle
        {
            normal = { background = tScV },
            border = new RectOffset(1, 1, 1, 1),
            fixedWidth = 8,
        };
        // Thumbs: native 16x8 (H) / 8x16 (V), 1px white outline. Idle is dim
        // teal, hover is full teal, drag is magenta. Drawn at native thickness
        // so the outline stays crisp.
        ScrollHThumb = new GUIStyle
        {
            normal = { background = tScHThm },
            hover  = { background = tScHThmHov },
            active = { background = tScHThmAct },
            border = new RectOffset(3, 3, 1, 1),
            fixedHeight = 8,
        };
        ScrollVThumb = new GUIStyle
        {
            normal = { background = tScVThm },
            hover  = { background = tScVThmHov },
            active = { background = tScVThmAct },
            border = new RectOffset(1, 1, 3, 3),
            fixedWidth = 8,
        };

        // ============ labels ============
        Label = new GUIStyle
        {
            normal = { textColor = Colors.Text },
            font = Regular,
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(0, 0, 2, 2),
        };
        LabelBold = new GUIStyle(Label) { font = Bold ?? Regular };
        LabelMuted = new GUIStyle(Label) { normal = { textColor = Colors.TextDim } };
        LabelSection = new GUIStyle(Label)
        {
            font = Bold ?? Regular,
            fontSize = 15,
            normal = { textColor = Colors.Accent },
            padding = new RectOffset(0, 0, 6, 4),
        };
        LabelWarning = new GUIStyle(Label) { normal = { textColor = Colors.Warning } };

        // background-only styles (no padding/font) for animated wrappers
        // Asymmetric 9-slice: left 18 covers the slant + double-bar left
        // accent; right 12 covers the right slant.
        BtnBgIdle      = MakeBgAsym(tBtn,       18, 12, 4, 1);
        BtnBgHover     = MakeBgAsym(tBtnHov,    18, 12, 4, 1);
        BtnBgAct       = MakeBgAsym(tBtnAct,    18, 12, 4, 1);
        BtnBgOnIdle    = MakeBgAsym(tBtnOn,     18, 12, 4, 1);
        BtnBgOnHover   = MakeBgAsym(tBtnOnHov,  18, 12, 4, 1);
        BtnBgOnAct     = MakeBgAsym(tBtnOnAct,  18, 12, 4, 1);

        ToggleBgIdle    = MakeBg(tTog,      4);
        ToggleBgHover   = MakeBg(tTogHov,   4);
        ToggleBgAct     = MakeBg(tTogAct,   4);
        ToggleBgOnIdle  = MakeBg(tTogOn,    4);
        ToggleBgOnHover = MakeBg(tTogOnHov, 4);
        ToggleBgOnAct   = MakeBg(tTogOnAct, 4);

        // Switch background-only styles. No 9-slice border since the switch
        // sprites are fixed-size 72x32 with the handle position baked in
        // (left for off, right for on). Used by KumihoDraw.AnimatedSwitch
        // for the crossfade between off and on stacks during state changes.
        SwitchBgIdle    = MakeBg(tSw,       0);
        SwitchBgHover   = MakeBg(tSwHov,    0);
        SwitchBgAct     = MakeBg(tSwAct,    0);
        SwitchBgOnIdle  = MakeBg(tSwOn,     0);
        SwitchBgOnHover = MakeBg(tSwOnHov,  0);
        SwitchBgOnAct   = MakeBg(tSwOnAct,  0);

        // Path B: track + handle. Track has 3 state textures crossfaded on
        // hover/press; handle is one white texture tinted teal->magenta on
        // state change, drawn at a sliding X position.
        SwitchTrackBg      = MakeBg(tSwTrack,    0);
        SwitchTrackHoverBg = MakeBg(tSwTrackHov, 0);
        SwitchTrackActBg   = MakeBg(tSwTrackAct, 0);
        SwitchHandleTex    = tSwHandle;

        ButtonText = new GUIStyle
        {
            font = Regular,
            fontSize = 12,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Colors.Text },
            // label sits inside the body, clear of the double-bar left accent and the right slant
            padding = new RectOffset(22, 16, 8, 6),
        };
        ToggleText = new GUIStyle
        {
            font = Regular,
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Colors.Text },
            padding = new RectOffset(28, 4, 4, 4),  // leaves room for the 24px toggle box on the left
        };

        _stylesBuilt = true;
    }

    private static GUIStyle MakeBg(Texture2D tex, int border)
    {
        return new GUIStyle
        {
            normal = { background = tex },
            border = new RectOffset(border, border, border, border),
        };
    }

    private static GUIStyle MakeBgAsym(Texture2D tex, int left, int right, int top, int bottom)
    {
        return new GUIStyle
        {
            normal = { background = tex },
            border = new RectOffset(left, right, top, bottom),
        };
    }

    // -------------------------------------------------------------------
    // Collapsible section header
    // -------------------------------------------------------------------
    /// <summary>
    /// Basic collapsible section header. Whole row is clickable to toggle
    /// the expand/collapse state. Chevron icon flips between right (collapsed)
    /// and down (expanded). Returns the new expanded state so callers can
    /// short-circuit rendering the section body when collapsed.
    ///
    /// Typical use:
    /// <code>
    /// if (KumihoUI.SectionHeader("Bloom", ref _bloomExpanded))
    /// {
    ///     // render Bloom controls here
    /// }
    /// </code>
    /// </summary>
    public static bool SectionHeader(string title, ref bool expanded)
    {
        var rect = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));

        var chevRect  = new Rect(rect.x + 4, rect.y + 4, 16, 16);
        var titleRect = new Rect(rect.x + 24, rect.y, rect.width - 24, rect.height);

        if (Event.current.type == EventType.Repaint)
        {
            DrawIcon(chevRect, expanded ? "chevron-down" : "chevron-right", Colors.TextDim);
            GUI.Label(titleRect, title, LabelBold);
        }

        if (Event.current.type == EventType.MouseDown
            && rect.Contains(Event.current.mousePosition)
            && Event.current.button == 0)
        {
            expanded = !expanded;
            Event.current.Use();
        }

        return expanded;
    }

    /// <summary>
    /// Section header with an inline enable/disable toggle plus an optional
    /// reset button. The whole row except the toggle and reset hit areas is
    /// clickable to expand/collapse. Title and chevron dim when the toggle
    /// is off, signaling the section is inactive.
    ///
    /// This is the canonical FX-section pattern from KumihoFX (Bloom, AO,
    /// DoF, etc) where each effect can be toggled on/off and reset to
    /// defaults independently of being expanded for editing.
    /// </summary>
    public static bool SectionHeader(string title, ref bool expanded,
        ref bool enabled, System.Action onReset = null)
    {
        var rect = GUILayoutUtility.GetRect(0, 28, GUILayout.ExpandWidth(true));

        const float chevW = 16;
        const float toggleW = 24;
        const float resetW = 16;
        const float pad = 4;

        var chevRect = new Rect(rect.x + pad, rect.y + 6, chevW, chevW);

        float rightX = rect.xMax - pad;
        Rect resetRect = default;
        if (onReset != null)
        {
            rightX -= resetW;
            resetRect = new Rect(rightX, rect.y + 6, resetW, resetW);
            rightX -= pad;
        }

        var toggleRect = new Rect(rightX - toggleW, rect.y + 2, toggleW, toggleW);

        float titleX = rect.x + chevW + 2 * pad;
        var titleRect = new Rect(titleX, rect.y, toggleRect.x - titleX - pad, rect.height);

        // Expand/collapse click area excludes the toggle and reset hit boxes.
        var clickRect = new Rect(rect.x, rect.y, toggleRect.x - rect.x, rect.height);

        if (Event.current.type == EventType.Repaint)
        {
            var iconTint = enabled ? Colors.Text : Colors.TextDim;
            DrawIcon(chevRect, expanded ? "chevron-down" : "chevron-right", iconTint);

            var savedColor = GUI.color;
            GUI.color = enabled ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
            GUI.Label(titleRect, title, LabelBold);
            GUI.color = savedColor;
        }

        // Inline enable/disable toggle (uses the kit's standard Toggle style).
        enabled = GUI.Toggle(toggleRect, enabled, GUIContent.none, Toggle);

        // Optional reset button.
        if (onReset != null)
        {
            if (IconButton(resetRect, "reset"))
                onReset();
        }

        // Expand/collapse click (runs after toggle/reset so consumed events
        // don't fire this).
        if (Event.current.type == EventType.MouseDown
            && clickRect.Contains(Event.current.mousePosition)
            && Event.current.button == 0)
        {
            expanded = !expanded;
            Event.current.Use();
        }

        return expanded;
    }

    // -------------------------------------------------------------------
    // Toggle rows
    // -------------------------------------------------------------------
    /// <summary>
    /// Inline toggle + text label, vertically aligned. The toggle box renders
    /// at its fixed size with the label drawn beside it. Returns the new value.
    /// </summary>
    public static bool ToggleRow(string label, bool value, float labelWidth = 200f)
    {
        GUILayout.BeginHorizontal();
        value = GUILayout.Toggle(value, GUIContent.none, Toggle);
        GUILayout.Space(8);
        GUILayout.Label(label, Label,
            GUILayout.Width(labelWidth),
            GUILayout.Height(Toggle.fixedHeight));
        GUILayout.EndHorizontal();
        return value;
    }

    // -------------------------------------------------------------------
    // Game input blocking
    // -------------------------------------------------------------------
    /// <summary>
    /// True if the OS mouse cursor is currently over the given screen rect.
    /// Handles the Y-flip between Input.mousePosition (bottom-left origin)
    /// and IMGUI rect coords (top-left origin).
    /// </summary>
    public static bool IsCursorOver(Rect windowRect)
    {
        var pos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        return windowRect.Contains(pos);
    }

    /// <summary>
    /// Call from your plugin's Update() to stop game input (camera rotation,
    /// object selection, etc.) from firing when the cursor is over your
    /// window. Pass every window rect your plugin draws.
    /// </summary>
    public static void BlockGameInput(params Rect[] windowRects)
    {
        var pos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        for (int i = 0; i < windowRects.Length; i++)
        {
            if (windowRects[i].Contains(pos))
            {
                Input.ResetInputAxes();
                return;
            }
        }
    }

    // -------------------------------------------------------------------
    // ScrollView wrappers
    // -------------------------------------------------------------------
    // Saved skin slots so we can restore IMGUI's defaults after a Kumiho
    // ScrollView block. Modifying GUI.skin globally would leak our styles
    // into other plugins' UI, so we always swap in/out.
    private static GUIStyle _savedHThumb;
    private static GUIStyle _savedVThumb;
    private static GUIStyle _savedHBar;
    private static GUIStyle _savedVBar;
    private static GUIStyle _savedScrollView;
    private static GUIStyle _savedHLeftBtn;
    private static GUIStyle _savedHRightBtn;
    private static GUIStyle _savedVUpBtn;
    private static GUIStyle _savedVDownBtn;
    private static bool _scrollSkinPushed;

    // Transparent style used for the four arrow-button slots at each end of
    // every scrollbar. Default IMGUI skin renders those as small light-gray
    // squares which show up as white stubs at the ends of our bars; force
    // them blank to remove them.
    private static GUIStyle _scrollViewBlank;

    // Dark-fill style for the scrollView container. Default IMGUI scrollView
    // has a light-gray background that bleeds through everywhere there's
    // transparency, most visibly at the corner where the H and V scrollbars
    // meet. We paint that corner (and the rest of the scrollview's bg) with
    // a dark 1px texture so the corner stays in-aesthetic.
    private static GUIStyle _scrollViewDark;
    private static Texture2D _darkPx;

    // "Drag" variants of the scrollbar thumbs. Same as the regular thumb
    // styles but with the act (magenta) background moved into the normal
    // slot so the thumb shows magenta regardless of cursor position once a
    // drag has started.
    private static GUIStyle _hThumbDrag;
    private static GUIStyle _vThumbDrag;

    // Cached scrollbar rects from last frame's EndScrollView, used to detect
    // mouse-down inside the H/V bar regions. Window-relative coords.
    private static Rect _hScrollRect;
    private static Rect _vScrollRect;
    private static bool _hScrollDragging;
    private static bool _vScrollDragging;

    /// <summary>
    /// BeginScrollView with Kumiho-styled scrollbars. Pair every call with
    /// EndScrollView() (the Kumiho version, not GUILayout's). Safe to use
    /// inside any IMGUI block; the host game's scrollbar skin is restored
    /// when EndScrollView returns.
    /// </summary>
    public static Vector2 BeginScrollView(Vector2 scrollPosition, params GUILayoutOption[] options)
    {
        if (_scrollViewBlank == null)
            _scrollViewBlank = new GUIStyle();   // empty style = no bg

        // Lazily build the dark 1px fill used for the scrollView corner.
        if (_darkPx == null)
        {
            _darkPx = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _darkPx.SetPixel(0, 0, Colors.Bg);
            _darkPx.filterMode = FilterMode.Point;
            _darkPx.wrapMode   = TextureWrapMode.Clamp;
            _darkPx.Apply();
        }
        if (_scrollViewDark == null)
        {
            _scrollViewDark = new GUIStyle();
            _scrollViewDark.normal.background = _darkPx;
        }

        // Build the drag variants once. The act texture moves into the
        // normal slot so the thumb stays magenta even when the cursor leaves
        // the thumb's bounding box during a drag.
        if (_hThumbDrag == null && ScrollHThumb != null)
        {
            _hThumbDrag = new GUIStyle(ScrollHThumb);
            _hThumbDrag.normal.background = ScrollHThumb.active.background;
            _hThumbDrag.hover.background  = ScrollHThumb.active.background;
        }
        if (_vThumbDrag == null && ScrollVThumb != null)
        {
            _vThumbDrag = new GUIStyle(ScrollVThumb);
            _vThumbDrag.normal.background = ScrollVThumb.active.background;
            _vThumbDrag.hover.background  = ScrollVThumb.active.background;
        }

        // Drag detection. MouseDown inside the cached H or V bar rect (from
        // the previous frame's EndScrollView) flips the drag flag on; any
        // MouseUp anywhere flips it off. Cached rects are window-relative,
        // matching Event.current.mousePosition inside a GUI.Window callback.
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (_hScrollRect.width > 0 && _hScrollRect.Contains(e.mousePosition))
                _hScrollDragging = true;
            if (_vScrollRect.width > 0 && _vScrollRect.Contains(e.mousePosition))
                _vScrollDragging = true;
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            _hScrollDragging = false;
            _vScrollDragging = false;
        }

        _savedHThumb     = GUI.skin.horizontalScrollbarThumb;
        _savedVThumb     = GUI.skin.verticalScrollbarThumb;
        _savedHBar       = GUI.skin.horizontalScrollbar;
        _savedVBar       = GUI.skin.verticalScrollbar;
        _savedScrollView = GUI.skin.scrollView;
        _savedHLeftBtn   = GUI.skin.horizontalScrollbarLeftButton;
        _savedHRightBtn  = GUI.skin.horizontalScrollbarRightButton;
        _savedVUpBtn     = GUI.skin.verticalScrollbarUpButton;
        _savedVDownBtn   = GUI.skin.verticalScrollbarDownButton;
        GUI.skin.horizontalScrollbarThumb       = _hScrollDragging && _hThumbDrag != null ? _hThumbDrag : ScrollHThumb;
        GUI.skin.verticalScrollbarThumb         = _vScrollDragging && _vThumbDrag != null ? _vThumbDrag : ScrollVThumb;
        GUI.skin.horizontalScrollbar            = ScrollH;
        GUI.skin.verticalScrollbar              = ScrollV;
        GUI.skin.scrollView                     = _scrollViewBlank;  // GDC: window owns the bg, no nested dark fill
        GUI.skin.horizontalScrollbarLeftButton  = _scrollViewBlank;
        GUI.skin.horizontalScrollbarRightButton = _scrollViewBlank;
        GUI.skin.verticalScrollbarUpButton      = _scrollViewBlank;
        GUI.skin.verticalScrollbarDownButton    = _scrollViewBlank;
        _scrollSkinPushed = true;
        return GUILayout.BeginScrollView(scrollPosition, ScrollH, ScrollV, options);
    }

    /// <summary>
    /// Pair with KumihoUI.BeginScrollView. Restores the host game's scrollbar
    /// skin so other plugins draw normally after this block.
    /// </summary>
    public static void EndScrollView()
    {
        GUILayout.EndScrollView();
        if (_scrollSkinPushed)
        {
            GUI.skin.horizontalScrollbarThumb       = _savedHThumb;
            GUI.skin.verticalScrollbarThumb         = _savedVThumb;
            GUI.skin.horizontalScrollbar            = _savedHBar;
            GUI.skin.verticalScrollbar              = _savedVBar;
            GUI.skin.scrollView                     = _savedScrollView;
            GUI.skin.horizontalScrollbarLeftButton  = _savedHLeftBtn;
            GUI.skin.horizontalScrollbarRightButton = _savedHRightBtn;
            GUI.skin.verticalScrollbarUpButton      = _savedVUpBtn;
            GUI.skin.verticalScrollbarDownButton    = _savedVDownBtn;
            _scrollSkinPushed = false;
        }

        // Cache the scrollview's outer rect so next frame's BeginScrollView
        // can spot mouse-down inside the H/V bar regions. The bars sit at the
        // bottom 7 px and right 7 px of the scrollview rect respectively;
        // the corner is excluded from each so a click in the corner doesn't
        // count as either.
        if (Event.current.type == EventType.Repaint)
        {
            Rect sv = GUILayoutUtility.GetLastRect();
            _hScrollRect = new Rect(sv.x,        sv.yMax - 7, sv.width  - 7, 7);
            _vScrollRect = new Rect(sv.xMax - 7, sv.y,        7,            sv.height - 7);
        }
    }

    // -------------------------------------------------------------------
    // File / folder picker (path display + browse button)
    // -------------------------------------------------------------------
    /// <summary>
    /// Path display row: a TextField showing the current path + a small
    /// "Browse..." button on the right. Returns the path (possibly modified
    /// if the user typed into the field). When the button is clicked, the
    /// caller's <paramref name="onBrowse"/> action runs — typically the host
    /// plugin opens a native folder/file picker dialog and writes the
    /// chosen path back.
    ///
    /// Kumiho UI doesn't open OS dialogs itself to stay free of platform
    /// dependencies; the caller is responsible for that. A common pattern
    /// on Windows is to use System.Windows.Forms.FolderBrowserDialog from
    /// inside the onBrowse action.
    /// </summary>
    public static string PathField(string path, string id, System.Action onBrowse,
        string browseLabel = "Browse...", float browseButtonWidth = 80f)
    {
        GUILayout.BeginHorizontal();

        // Path text field (editable)
        string ctrlName = "kumiho_path_" + id;
        GUI.SetNextControlName(ctrlName);
        path = GUILayout.TextField(path ?? "", TextField,
            GUILayout.ExpandWidth(true), GUILayout.Height(24));

        GUILayout.Space(4);

        // Browse button with a folder icon
        if (GUILayout.Button(new GUIContent("  " + browseLabel, Icons.TryGetValue("folder", out var f) ? f : null),
                Button,
                GUILayout.Width(browseButtonWidth), GUILayout.Height(24)))
        {
            onBrowse?.Invoke();
        }

        GUILayout.EndHorizontal();
        return path;
    }

    // -------------------------------------------------------------------
    // Histogram display
    // -------------------------------------------------------------------
    /// <summary>
    /// Render a histogram from pre-computed bin counts. Caller is responsible
    /// for sampling the source image (e.g. a RenderTexture from the current
    /// camera) and computing the per-channel bin counts.
    ///
    /// Pass either a single int[] for a luma histogram, or three arrays (r/g/b)
    /// for an RGB overlay. Bin counts don't need to be normalized — the
    /// rendering auto-scales by the max value across all channels.
    ///
    /// For a stylized look matching the kit, the histogram draws on a Box
    /// background with bars in the brand accent colors.
    /// </summary>
    public static void Histogram(int[] luma, params GUILayoutOption[] options)
    {
        Histogram(luma, null, null, options);
    }

    public static void Histogram(int[] r, int[] g, int[] b, params GUILayoutOption[] options)
    {
        var rect = GUILayoutUtility.GetRect(120, 80, options);

        if (Event.current.type != EventType.Repaint) return;
        if ((r == null || r.Length == 0) && (g == null || g.Length == 0) && (b == null || b.Length == 0))
            return;

        // Background panel
        Box.Draw(rect, false, false, false, false);
        var innerRect = new Rect(rect.x + 4, rect.y + 4, rect.width - 8, rect.height - 8);

        // Find max bin count across all channels for normalization
        int max = 1;
        if (r != null) foreach (var v in r) if (v > max) max = v;
        if (g != null) foreach (var v in g) if (v > max) max = v;
        if (b != null) foreach (var v in b) if (v > max) max = v;

        int bins = (r != null ? r.Length : (g != null ? g.Length : b.Length));
        float binW = innerRect.width / bins;

        // Draw each channel as a vertical bar at its bin position, semi-transparent
        // so overlapping channels mix visually.
        void DrawChannel(int[] data, Color col)
        {
            if (data == null) return;
            var prev = GUI.color;
            GUI.color = new Color(col.r, col.g, col.b, 0.55f);
            for (int i = 0; i < data.Length; i++)
            {
                float h = (data[i] / (float)max) * innerRect.height;
                var barRect = new Rect(
                    innerRect.x + i * binW,
                    innerRect.yMax - h,
                    Mathf.Max(1, binW),
                    h);
                GUI.DrawTexture(barRect, Texture2D.whiteTexture);
            }
            GUI.color = prev;
        }

        // Single-channel (luma) variant
        if (r != null && g == null && b == null)
        {
            DrawChannel(r, Colors.Text);
            return;
        }

        // RGB overlay variant
        DrawChannel(r, new Color(1f, 0.3f, 0.3f));
        DrawChannel(g, new Color(0.3f, 1f, 0.3f));
        DrawChannel(b, new Color(0.3f, 0.5f, 1f));
    }

    // -------------------------------------------------------------------
    // A/B split toggle
    // -------------------------------------------------------------------
    /// <summary>
    /// Side-by-side A/B button pair. The selected side renders in the
    /// on-state (magenta accent) while the unselected side renders in the
    /// off-state (teal accent). Returns the new active index: 0 for A,
    /// 1 for B. Useful for before/after preview toggles, comparison views,
    /// or any binary mode switch where both labels need to stay visible.
    /// </summary>
    public static int ABToggle(int active, string labelA = "A", string labelB = "B",
        float buttonWidth = 60f, float buttonHeight = 28f)
    {
        GUILayout.BeginHorizontal();
        // GUI.Toggle with the button style draws the on-state when active is true.
        bool aOn = GUILayout.Toggle(active == 0, labelA, Button,
            GUILayout.Width(buttonWidth), GUILayout.Height(buttonHeight));
        bool bOn = GUILayout.Toggle(active == 1, labelB, Button,
            GUILayout.Width(buttonWidth), GUILayout.Height(buttonHeight));
        GUILayout.EndHorizontal();

        // Resolve to the new active index. If the user clicked the inactive
        // one, switch. If they clicked the active one, keep it (no toggle off).
        if (aOn && active != 0) return 0;
        if (bOn && active != 1) return 1;
        return active;
    }

    // -------------------------------------------------------------------
    // Confirm modal
    // -------------------------------------------------------------------
    private static string _modalActiveId;
    private static string _modalTitle;
    private static string _modalMessage;
    private static string _modalConfirmLabel;
    private static string _modalCancelLabel;
    private static System.Action _modalOnConfirm;
    private static bool _modalIsDangerous;

    /// <summary>
    /// Show a confirm/cancel modal dialog. Only one modal can be active at
    /// a time. Call <see cref="DrawPendingModal"/> at the end of your OnGUI
    /// to render any active modal on top of all other UI.
    /// </summary>
    public static void ShowModal(string id, string title, string message,
        System.Action onConfirm,
        string confirmLabel = "OK", string cancelLabel = "Cancel",
        bool dangerous = false)
    {
        _modalActiveId = id;
        _modalTitle = title;
        _modalMessage = message;
        _modalConfirmLabel = confirmLabel;
        _modalCancelLabel = cancelLabel;
        _modalOnConfirm = onConfirm;
        _modalIsDangerous = dangerous;
    }

    /// <summary>True while a modal is awaiting confirmation.</summary>
    public static bool IsModalOpen => !string.IsNullOrEmpty(_modalActiveId);

    /// <summary>
    /// Render the active modal (if any). Call at the very end of OnGUI,
    /// outside any GUI.Window block, so the modal overlays everything.
    /// </summary>
    public static void DrawPendingModal()
    {
        if (string.IsNullOrEmpty(_modalActiveId)) return;

        // Full-screen dim overlay
        var screenRect = new Rect(0, 0, Screen.width, Screen.height);
        if (Event.current.type == EventType.Repaint)
        {
            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(screenRect, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        // Centered panel
        const float panelW = 360f;
        const float panelH = 140f;
        var panelRect = new Rect(
            (Screen.width  - panelW) * 0.5f,
            (Screen.height - panelH) * 0.5f,
            panelW, panelH);

        if (Event.current.type == EventType.Repaint)
        {
            Window.Draw(panelRect, false, false, false, false);
        }

        // Title
        var titleRect = new Rect(panelRect.x + 16, panelRect.y + 12, panelRect.width - 32, 20);
        GUI.Label(titleRect, _modalTitle, LabelBold);

        // Message
        var msgRect = new Rect(panelRect.x + 16, panelRect.y + 40, panelRect.width - 32, 50);
        var msgStyle = new GUIStyle(Label) { wordWrap = true };
        GUI.Label(msgRect, _modalMessage, msgStyle);

        // Buttons (right-aligned at bottom)
        const float btnW = 96f;
        const float btnH = 28f;
        const float btnGap = 8f;
        var confirmBtnRect = new Rect(panelRect.xMax - 16 - btnW, panelRect.yMax - 16 - btnH, btnW, btnH);
        var cancelBtnRect = new Rect(confirmBtnRect.x - btnGap - btnW, confirmBtnRect.y, btnW, btnH);

        if (GUI.Button(cancelBtnRect, _modalCancelLabel, Button))
        {
            _modalActiveId = null;
            _modalOnConfirm = null;
            Event.current.Use();
        }

        if (GUI.Button(confirmBtnRect, _modalConfirmLabel, Button))
        {
            var cb = _modalOnConfirm;
            _modalActiveId = null;
            _modalOnConfirm = null;
            cb?.Invoke();
            Event.current.Use();
        }

        // Consume all clicks outside the panel so they don't pass through
        if (Event.current.isMouse && !panelRect.Contains(Event.current.mousePosition))
        {
            Event.current.Use();
        }
    }

    // -------------------------------------------------------------------
    // Tooltip
    // -------------------------------------------------------------------
    // Tooltips render deferred so they appear on top of any subsequent UI
    // drawn in the same OnGUI pass. The trigger is recorded during normal
    // widget draw; the actual rendering happens via DrawPendingTooltip()
    // which the host plugin calls at the very end of OnGUI.
    private static string _pendingTooltipText;
    private static Vector2 _pendingTooltipPos;

    /// <summary>
    /// Register a tooltip to draw at the end of the OnGUI pass. Typically
    /// called inside a hover check on any widget — e.g.:
    /// <code>
    /// if (rect.Contains(Event.current.mousePosition))
    ///     KumihoUI.Tooltip("Sample count for the AO pass");
    /// </code>
    /// Position defaults to just below+right of the cursor; pass a custom
    /// position for fixed-tooltip placement.
    /// </summary>
    public static void Tooltip(string text, Vector2? position = null)
    {
        if (Event.current.type != EventType.Repaint) return;
        if (string.IsNullOrEmpty(text)) return;
        _pendingTooltipText = text;
        _pendingTooltipPos = position ?? (Event.current.mousePosition + new Vector2(12, 16));
    }

    /// <summary>
    /// Render any tooltip registered this frame. Call once at the very end
    /// of your plugin's OnGUI (after DrawWindow returns / outside the window
    /// block ideally) so the tooltip layers on top of everything else.
    /// </summary>
    public static void DrawPendingTooltip()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (string.IsNullOrEmpty(_pendingTooltipText)) return;

        // Measure the text
        var style = new GUIStyle(Label) { padding = new RectOffset(6, 6, 4, 4) };
        var content = new GUIContent(_pendingTooltipText);
        var size = style.CalcSize(content);
        // Cap width
        const float maxW = 280f;
        float w = Mathf.Min(size.x, maxW);
        float h = size.x > maxW ? style.CalcHeight(content, maxW) : size.y;

        var rect = new Rect(_pendingTooltipPos.x, _pendingTooltipPos.y, w + 4, h + 2);

        // Clamp to screen so the tooltip doesn't go off-edge
        if (rect.xMax > Screen.width)  rect.x = Screen.width  - rect.width - 4;
        if (rect.yMax > Screen.height) rect.y = Screen.height - rect.height - 4;

        // Background using Box style
        Box.Draw(rect, false, false, false, false);
        // Text on top
        GUI.Label(new Rect(rect.x + 2, rect.y + 1, rect.width - 4, rect.height - 2),
            _pendingTooltipText, style);

        // Clear so it doesn't carry over to the next frame
        _pendingTooltipText = null;
    }

    // -------------------------------------------------------------------
    // Progress bar
    // -------------------------------------------------------------------
    /// <summary>
    /// Non-interactive progress bar. Reuses the slider track + fill sprites
    /// so it visually matches sliders elsewhere in the kit. Pass a 0..1
    /// progress value; optionally pass a label drawn over the bar (e.g. a
    /// percentage or status string).
    /// </summary>
    public static void ProgressBar(float progress, string label = null,
        params GUILayoutOption[] options)
    {
        float trackH = SliderH != null ? SliderH.fixedHeight : 7f;
        var rect = GUILayoutUtility.GetRect(120, trackH, options);

        if (Event.current.type == EventType.Repaint)
        {
            // Track
            SliderH.Draw(rect, false, false, false, false);

            // Fill (clamped to minimum pill width so the rounded ends always
            // render cleanly at low progress values).
            float t = Mathf.Clamp01(progress);
            float fillW = rect.width * t;
            if (t > 0.001f && SliderHFill != null && SliderHFill.normal.background != null)
            {
                const float MinFillW = 7f;
                float drawW = Mathf.Max(fillW, MinFillW);
                SliderHFill.Draw(new Rect(rect.x, rect.y, drawW, rect.height),
                    false, false, false, false);
            }

            // Optional label overlay (centered above the bar).
            if (!string.IsNullOrEmpty(label))
            {
                var labelRect = new Rect(rect.x, rect.y - 16, rect.width, 14);
                var centered = new GUIStyle(LabelMuted) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(labelRect, label, centered);
            }
        }
    }

    // -------------------------------------------------------------------
    // Vector2 / Vector3 fields
    // -------------------------------------------------------------------
    private static readonly System.Collections.Generic.Dictionary<string, string> _vecEditingText
        = new System.Collections.Generic.Dictionary<string, string>();

    /// <summary>
    /// Single float component with an axis label (X/Y/Z/W). Used internally
    /// by Vector2Field/Vector3Field; exposed publicly for one-off use.
    /// </summary>
    public static float AxisField(string axis, float value, string id, float componentWidth = 50f)
    {
        const float labelW = 14f;
        GUILayout.BeginHorizontal();
        var prevColor = GUI.contentColor;
        // Tint the axis label by convention: X=red-ish, Y=green-ish, Z=blue-ish, others=accent
        Color labelColor;
        switch (axis.ToUpperInvariant())
        {
            case "X": labelColor = new Color(0.94f, 0.36f, 0.36f); break;
            case "Y": labelColor = new Color(0.42f, 0.85f, 0.36f); break;
            case "Z": labelColor = new Color(0.36f, 0.55f, 0.95f); break;
            default:  labelColor = Colors.Accent; break;
        }
        var axisStyle = new GUIStyle(LabelBold)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = labelColor },
        };
        GUILayout.Label(axis, axisStyle, GUILayout.Width(labelW), GUILayout.Height(20));
        GUI.contentColor = prevColor;

        // Focus-tracked text field
        string ctrlName = "kumiho_axis_" + id;
        GUI.SetNextControlName(ctrlName);
        bool focused = GUI.GetNameOfFocusedControl() == ctrlName;
        string shown;
        if (focused && _vecEditingText.TryGetValue(ctrlName, out var cached))
            shown = cached;
        else
        {
            shown = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            _vecEditingText.Remove(ctrlName);
        }

        string typed = GUILayout.TextField(shown, TextField,
            GUILayout.Width(componentWidth), GUILayout.Height(20));

        if (focused)
        {
            _vecEditingText[ctrlName] = typed;
            if (float.TryParse(typed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed))
            {
                value = parsed;
            }
        }
        GUILayout.EndHorizontal();
        return value;
    }

    /// <summary>2-component vector field (X, Y) inline on a single row.</summary>
    public static Vector2 Vector2Field(Vector2 value, string id, float componentWidth = 50f)
    {
        GUILayout.BeginHorizontal();
        value.x = AxisField("X", value.x, id + "_x", componentWidth);
        GUILayout.Space(4);
        value.y = AxisField("Y", value.y, id + "_y", componentWidth);
        GUILayout.EndHorizontal();
        return value;
    }

    /// <summary>3-component vector field (X, Y, Z) inline on a single row.</summary>
    public static Vector3 Vector3Field(Vector3 value, string id, float componentWidth = 50f)
    {
        GUILayout.BeginHorizontal();
        value.x = AxisField("X", value.x, id + "_x", componentWidth);
        GUILayout.Space(4);
        value.y = AxisField("Y", value.y, id + "_y", componentWidth);
        GUILayout.Space(4);
        value.z = AxisField("Z", value.z, id + "_z", componentWidth);
        GUILayout.EndHorizontal();
        return value;
    }

    // -------------------------------------------------------------------
    // Layer mask grid
    // -------------------------------------------------------------------
    /// <summary>
    /// 32-toggle grid representing Unity layer mask bits. Each cell is a
    /// small clickable chip showing the layer index (0-31). The layer's
    /// configured name from Unity (via LayerMask.LayerToName) is shown as a
    /// hover tooltip if available. Returns the new mask value.
    ///
    /// Layout: 8 columns x 4 rows by default. Each chip is 28x18 px.
    /// </summary>
    public static int LayerMaskGrid(int mask, params GUILayoutOption[] options)
    {
        const int cols = 8;
        const int rows = 4;
        const float chipW = 28f;
        const float chipH = 18f;
        const float gap = 2f;

        float totalW = cols * chipW + (cols - 1) * gap;
        float totalH = rows * chipH + (rows - 1) * gap;
        var rect = GUILayoutUtility.GetRect(totalW, totalH, options);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                int idx = row * cols + col;
                int bit = 1 << idx;
                bool on = (mask & bit) != 0;

                var chipRect = new Rect(
                    rect.x + col * (chipW + gap),
                    rect.y + row * (chipH + gap),
                    chipW, chipH);

                // Hover/press tracking for visual feedback
                var e = Event.current;
                bool hovered = chipRect.Contains(e.mousePosition);

                if (e.type == EventType.Repaint)
                {
                    // Background: SurfaceHi if on, SurfaceLow if off, slight teal on hover
                    Color bg;
                    if (on) bg = hovered ? Colors.AccentHi : Colors.Accent;
                    else    bg = hovered ? Colors.SurfaceHi : Colors.Surface;
                    var prev = GUI.color;
                    GUI.color = bg;
                    GUI.DrawTexture(chipRect, Texture2D.whiteTexture);
                    GUI.color = prev;

                    // Layer index label
                    var labelStyle = new GUIStyle(Label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 10,
                        normal = { textColor = on ? Color.white : Colors.TextDim },
                    };
                    GUI.Label(chipRect, idx.ToString(), labelStyle);
                }

                if (e.type == EventType.MouseDown && hovered && e.button == 0)
                {
                    mask ^= bit;
                    e.Use();
                    GUI.changed = true;
                }
            }
        }

        return mask;
    }

    // -------------------------------------------------------------------
    // Search / filter field
    // -------------------------------------------------------------------
    /// <summary>
    /// Text field with a magnifier icon inset on the left and a clear-X
    /// button inset on the right (only shown when the field has text).
    /// Common pattern for filtering lists. Returns the new text value.
    /// </summary>
    public static string SearchField(string text, string id,
        string placeholder = "Search...", params GUILayoutOption[] options)
    {
        var rect = GUILayoutUtility.GetRect(120, 24, options);

        // Background uses the same TextField style as the body.
        var bodyRect = rect;
        // Inset for the magnifier icon on the left.
        var magRect = new Rect(rect.x + 4, rect.y + 4, 16, 16);
        // Inset for the clear button on the right (only if text not empty).
        bool hasText = !string.IsNullOrEmpty(text);
        var clearRect = new Rect(rect.xMax - 20, rect.y + 4, 16, 16);

        // Text field rect inset between icons.
        float fieldXMax = hasText ? clearRect.x - 4 : rect.xMax - 4;
        var fieldRect = new Rect(magRect.xMax + 4, rect.y, fieldXMax - (magRect.xMax + 4), rect.height);

        // Draw background
        if (Event.current.type == EventType.Repaint)
        {
            TextField.Draw(bodyRect, false, false, false,
                GUI.GetNameOfFocusedControl() == "kumiho_search_" + id);
            DrawIcon(magRect, "search", Colors.TextDim);
        }

        // Text input
        string ctrlName = "kumiho_search_" + id;
        GUI.SetNextControlName(ctrlName);
        // Show placeholder text dimly if empty AND not focused
        bool focused = GUI.GetNameOfFocusedControl() == ctrlName;
        if (!hasText && !focused && Event.current.type == EventType.Repaint)
        {
            var prevColor = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.4f);
            GUI.Label(fieldRect, placeholder, Label);
            GUI.color = prevColor;
        }
        // Use a transparent style so we don't double-draw the TextField bg.
        var transparentField = new GUIStyle(Label) { padding = new RectOffset(0, 0, 4, 0) };
        text = GUI.TextField(fieldRect, text ?? "", transparentField);

        // Clear button (only when there's text to clear)
        if (hasText)
        {
            if (IconButton(clearRect, "close", Colors.TextDim, Colors.Text, Colors.Warning))
            {
                text = "";
                GUI.FocusControl(null);
            }
        }

        return text ?? "";
    }

    // -------------------------------------------------------------------
    // Int stepper (- value +)
    // -------------------------------------------------------------------
    private static string _intStepperEditingId;
    private static string _intStepperEditingText;

    /// <summary>
    /// Integer value control with minus and plus buttons flanking an
    /// editable numeric field. Click the buttons to step by <paramref name="step"/>,
    /// or click the field and type a value directly. The value is clamped to
    /// [min, max] on every change.
    ///
    /// Replaces the common KumihoFX pattern of a float slider feeding
    /// Mathf.RoundToInt — cleaner intent and no off-by-one float artifacts.
    /// </summary>
    public static int IntStepper(int value, int min, int max, string id,
        int step = 1, float fieldWidth = 50f)
    {
        const float btnSize = 18f;

        GUILayout.BeginHorizontal();

        // Minus button
        var minusRect = GUILayoutUtility.GetRect(btnSize, btnSize,
            GUILayout.Width(btnSize), GUILayout.Height(btnSize));
        if (IconButton(minusRect, "minus"))
            value = Mathf.Max(min, value - step);

        GUILayout.Space(2);

        // Editable value field (focus-tracked like EditableSliderRow so the
        // user's literal text isn't clobbered while they're typing).
        string ctrlName = "kumiho_intstepper_" + id;
        GUI.SetNextControlName(ctrlName);
        bool isFocused = GUI.GetNameOfFocusedControl() == ctrlName;

        string shown;
        if (isFocused && _intStepperEditingId == ctrlName)
        {
            shown = _intStepperEditingText;
        }
        else
        {
            shown = value.ToString();
            if (_intStepperEditingId == ctrlName)
            {
                _intStepperEditingId = null;
                _intStepperEditingText = null;
            }
        }

        string typed = GUILayout.TextField(shown, TextField,
            GUILayout.Width(fieldWidth), GUILayout.Height(btnSize));

        if (isFocused)
        {
            _intStepperEditingId = ctrlName;
            _intStepperEditingText = typed;
            if (int.TryParse(typed, out int parsed))
                value = Mathf.Clamp(parsed, min, max);
        }

        GUILayout.Space(2);

        // Plus button
        var plusRect = GUILayoutUtility.GetRect(btnSize, btnSize,
            GUILayout.Width(btnSize), GUILayout.Height(btnSize));
        if (IconButton(plusRect, "plus"))
            value = Mathf.Min(max, value + step);

        GUILayout.EndHorizontal();

        return value;
    }

    /// <summary>
    /// IntStepper with a leading label. Common pattern for plugin settings:
    /// "Samples: [- 16 +]".
    /// </summary>
    public static int IntStepperRow(string label, int value, int min, int max, string id,
        int step = 1, float labelWidth = 100f, float fieldWidth = 50f)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, Label, GUILayout.Width(labelWidth), GUILayout.Height(18));
        value = IntStepper(value, min, max, id, step, fieldWidth);
        GUILayout.EndHorizontal();
        return value;
    }

    // -------------------------------------------------------------------
    // Dropdown / combo box
    // -------------------------------------------------------------------
    // Per-id open state. Only one dropdown can be open at a time within a
    // process; opening a second auto-closes the first.
    private static string _openDropdownId;

    /// <summary>
    /// Single-select dropdown. Returns the selected index (which may be the
    /// same as the input if the user didn't click an option this frame).
    ///
    /// Layout: when closed, renders as a rectangular field with the current
    /// selection text and a chevron-down on the right. When open, expands a
    /// list panel below the field with one row per option; click a row to
    /// select and auto-close. The list pushes subsequent layout down.
    ///
    /// Pass a unique <paramref name="id"/> per dropdown so multiple
    /// dropdowns in the same window don't share open state.
    /// </summary>
    public static int Dropdown(int selectedIndex, string[] options, string id,
        params GUILayoutOption[] layoutOpts)
    {
        if (options == null || options.Length == 0) return selectedIndex;
        selectedIndex = Mathf.Clamp(selectedIndex, 0, options.Length - 1);

        // Closed field rect.
        var fieldRect = GUILayoutUtility.GetRect(120, 24, layoutOpts);
        bool isOpen = _openDropdownId == id;

        // Draw the field.
        if (Event.current.type == EventType.Repaint)
        {
            Box.Draw(fieldRect, false, false, false, false);

            var textRect = new Rect(fieldRect.x + 8, fieldRect.y, fieldRect.width - 28, fieldRect.height);
            GUI.Label(textRect, options[selectedIndex], Label);

            var chevRect = new Rect(fieldRect.xMax - 20, fieldRect.y + 4, 16, 16);
            DrawIcon(chevRect, isOpen ? "chevron-up" : "chevron-down",
                isOpen ? Colors.Accent : Colors.TextDim);
        }

        // Click the field to toggle open/closed.
        if (Event.current.type == EventType.MouseDown
            && fieldRect.Contains(Event.current.mousePosition)
            && Event.current.button == 0)
        {
            _openDropdownId = isOpen ? null : id;
            Event.current.Use();
        }

        // If open, draw the list below.
        if (isOpen)
        {
            const float rowH = 22f;
            const float padTop = 2f;
            var listRect = GUILayoutUtility.GetRect(fieldRect.width, options.Length * rowH + padTop * 2);

            if (Event.current.type == EventType.Repaint)
            {
                Panel.Draw(listRect, false, false, false, false);
            }

            for (int i = 0; i < options.Length; i++)
            {
                var rowRect = new Rect(
                    listRect.x + 2,
                    listRect.y + padTop + i * rowH,
                    listRect.width - 4,
                    rowH);
                bool hovered = rowRect.Contains(Event.current.mousePosition);

                if (Event.current.type == EventType.Repaint)
                {
                    if (hovered)
                    {
                        var prev = GUI.color;
                        GUI.color = new Color(Colors.Accent.r, Colors.Accent.g, Colors.Accent.b, 0.15f);
                        GUI.DrawTexture(rowRect, Texture2D.whiteTexture);
                        GUI.color = prev;
                    }
                    var textRect = new Rect(rowRect.x + 8, rowRect.y, rowRect.width - 8, rowRect.height);
                    var style = (i == selectedIndex) ? LabelBold : Label;
                    GUI.Label(textRect, options[i], style);
                }

                if (Event.current.type == EventType.MouseDown
                    && rowRect.Contains(Event.current.mousePosition)
                    && Event.current.button == 0)
                {
                    selectedIndex = i;
                    _openDropdownId = null;
                    Event.current.Use();
                }
            }

            // Click outside the field or the list closes the dropdown.
            if (Event.current.type == EventType.MouseDown
                && !fieldRect.Contains(Event.current.mousePosition)
                && !listRect.Contains(Event.current.mousePosition))
            {
                _openDropdownId = null;
            }
        }

        return selectedIndex;
    }

    // -------------------------------------------------------------------
    // Slider with fill overlay
    // -------------------------------------------------------------------
    /// <summary>
    /// Like GUILayout.HorizontalSlider but overlays the SliderHFill texture
    /// between the track's left edge and the thumb's current position, so
    /// the slider visually reads "this much is filled / selected."
    /// </summary>
    public static float HorizontalSliderWithFill(float value, float min, float max,
        params GUILayoutOption[] options)
    {
        // Reserve a layout rect tall enough to hold the thumb (14), not just
        // the track (7). The thumb then centers vertically inside the rect
        // rather than hanging below the track.
        float thumbH = SliderThumb.fixedHeight;
        float trackH = SliderH.fixedHeight;
        var layoutRect = GUILayoutUtility.GetRect(0f, thumbH, options);

        // Track sits vertically centered within the layout rect.
        float trackY = layoutRect.y + (layoutRect.height - trackH) * 0.5f;
        var trackRect = new Rect(layoutRect.x, trackY, layoutRect.width, trackH);

        if (Event.current.type == EventType.Repaint)
        {
            // Draw the track manually (we hand GUIStyle.none to GUI.HorizontalSlider
            // below so Unity doesn't draw it again on top of ours).
            SliderH.Draw(trackRect, false, false, false, false);

            // Fill overlay from the track's left edge to the current value position.
            // Below ~7px wide the 9-slice can't render both rounded corners cleanly,
            // so we clamp the draw width to a minimum 7px pill cap once value > 0.
            // Result: at value 0 no fill shows, at value > 0 a rounded cap appears
            // and grows with the value, never going square or popping.
            float t = Mathf.InverseLerp(min, max, value);
            float fillW = trackRect.width * t;
            if (t > 0.001f && SliderHFill != null && SliderHFill.normal.background != null)
            {
                const float MinFillW = 7f;
                float drawW = Mathf.Max(fillW, MinFillW);
                var fillRect = new Rect(trackRect.x, trackY, drawW, trackH);
                SliderHFill.Draw(fillRect, false, false, false, false);
            }
        }

        // Hand layoutRect (thumb-height tall) to Unity's slider so the thumb
        // centers on the track. GUIStyle.none for the track since we already drew it.
        return GUI.HorizontalSlider(layoutRect, value, min, max, GUIStyle.none, SliderThumb);
    }

    /// <summary>
    /// Like GUILayout.VerticalSlider but overlays the SliderVFill texture
    /// between the track's bottom edge and the thumb's current position. Min
    /// sits at the BOTTOM, max at the top, matching Unity's convention for
    /// vertical sliders (positive values grow upward).
    ///
    /// Uses manual drag tracking (hot control + button-held) so the thumb
    /// keeps following the cursor's Y position even when the cursor drifts
    /// off the narrow track. Same approach as the Kumiho scrollbar wrapper.
    /// </summary>
    public static float VerticalSliderWithFill(float value, float min, float max,
        params GUILayoutOption[] options)
    {
        // Reserve a layout rect wide enough to hold the thumb (14), not just
        // the track (7). The thumb then centers horizontally inside the rect.
        float thumbW = SliderThumb.fixedWidth;
        float thumbH = SliderThumb.fixedHeight;
        float trackW = SliderV.fixedWidth;
        var layoutRect = GUILayoutUtility.GetRect(thumbW, 100f, options);

        // Track sits horizontally centered within the layout rect.
        float trackX = layoutRect.x + (layoutRect.width - trackW) * 0.5f;
        var trackRect = new Rect(trackX, layoutRect.y, trackW, layoutRect.height);

        // Allocate a stable control id so we can track drag state across
        // event passes via GUIUtility.hotControl.
        int controlId = GUIUtility.GetControlID(FocusType.Passive);

        // Map a Y position (in window-relative coords) to a clamped value.
        float YToValue(float y)
        {
            // Account for thumb height: top usable position has cursor at
            // trackRect.y + thumbH/2, bottom at trackRect.yMax - thumbH/2.
            float usableTop = trackRect.y + thumbH * 0.5f;
            float usableBot = trackRect.yMax - thumbH * 0.5f;
            float clamped = Mathf.Clamp(y, usableTop, usableBot);
            float t = (clamped - usableTop) / (usableBot - usableTop);
            return Mathf.Lerp(max, min, t);   // top = max, bottom = min
        }

        var e = Event.current;
        switch (e.type)
        {
            case EventType.MouseDown:
                if (e.button == 0 && layoutRect.Contains(e.mousePosition))
                {
                    GUIUtility.hotControl = controlId;
                    value = YToValue(e.mousePosition.y);
                    e.Use();
                    GUI.changed = true;
                }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == controlId)
                {
                    value = YToValue(e.mousePosition.y);
                    e.Use();
                    GUI.changed = true;
                }
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == controlId)
                {
                    GUIUtility.hotControl = 0;
                    e.Use();
                }
                break;
        }

        if (Event.current.type == EventType.Repaint)
        {
            // Draw the track manually so we can overlay the fill below.
            SliderV.Draw(trackRect, false, false, false, false);

            // Fill grows upward from the track's bottom edge to the value
            // position. Same minimum-cap clamp as the H version so the pill
            // ends always render cleanly.
            float t = Mathf.InverseLerp(min, max, value);
            float fillH = trackRect.height * t;
            if (t > 0.001f && SliderVFill != null && SliderVFill.normal.background != null)
            {
                const float MinFillH = 7f;
                float drawH = Mathf.Max(fillH, MinFillH);
                var fillRect = new Rect(trackX, trackRect.yMax - drawH, trackW, drawH);
                SliderVFill.Draw(fillRect, false, false, false, false);
            }

            // Thumb at the current value position. State is hot (active) when
            // dragging, hover when cursor is in the layout rect, otherwise
            // normal. Active overrides hover so the magenta sticks during the
            // entire drag regardless of cursor position.
            float tt = Mathf.InverseLerp(min, max, value);
            float usableTop = trackRect.y + thumbH * 0.5f;
            float usableBot = trackRect.yMax - thumbH * 0.5f;
            float thumbCenterY = Mathf.Lerp(usableBot, usableTop, tt);
            float thumbX = layoutRect.x + (layoutRect.width - thumbW) * 0.5f;
            float thumbY = thumbCenterY - thumbH * 0.5f;
            var thumbRect = new Rect(thumbX, thumbY, thumbW, thumbH);

            bool isHot   = GUIUtility.hotControl == controlId;
            bool isHover = layoutRect.Contains(Event.current.mousePosition);
            Texture2D bg = isHot ? SliderThumb.active.background
                         : isHover ? SliderThumb.hover.background
                         : SliderThumb.normal.background;
            if (bg != null) GUI.DrawTexture(thumbRect, bg);
        }

        return value;
    }

    /// <summary>
    /// Combined slider + numeric value display in a single horizontal row,
    /// vertically aligned. Returns the new slider value. Common pattern for
    /// plugin settings: drag the thumb to set a value, see the exact number
    /// next to it.
    /// </summary>
    public static float SliderRow(float value, float min, float max,
        string format = "0.00",
        float sliderWidth = 220f,
        float valueWidth = 50f)
    {
        GUILayout.BeginHorizontal();
        value = HorizontalSliderWithFill(value, min, max, GUILayout.Width(sliderWidth));
        GUILayout.Space(8);
        // Match the slider's layout height (thumb height) so the value text
        // vertically centers with the slider thumb instead of drifting.
        GUILayout.Label(value.ToString(format), Label,
            GUILayout.Width(valueWidth),
            GUILayout.Height(SliderThumb.fixedHeight));
        GUILayout.EndHorizontal();
        return value;
    }

    /// <summary>
    /// Slider row with a leading text label on the left. Pattern:
    /// "Strength  [---o--------]  0.42"
    /// </summary>
    public static float LabeledSliderRow(string label, float value, float min, float max,
        string format = "0.00",
        float labelWidth = 100f,
        float sliderWidth = 220f,
        float valueWidth = 50f)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, Label,
            GUILayout.Width(labelWidth),
            GUILayout.Height(SliderThumb.fixedHeight));
        value = HorizontalSliderWithFill(value, min, max, GUILayout.Width(sliderWidth));
        GUILayout.Space(8);
        GUILayout.Label(value.ToString(format), Label,
            GUILayout.Width(valueWidth),
            GUILayout.Height(SliderThumb.fixedHeight));
        GUILayout.EndHorizontal();
        return value;
    }

    // Tracks which text field is currently being edited and what the user has
    // typed. Lets EditableSliderRow keep the user's literal text while focused
    // (so "0." mid-typing doesn't get clobbered by reformatting to "0.00").
    private static string _editingFieldName;
    private static string _editingFieldText;

    /// <summary>
    /// Slider with an editable numeric text field next to it. The user can
    /// either drag the slider or type a value directly. Optional leading
    /// label. Each call needs a stable id string (any unique name works) so
    /// the focus-tracking can identify which field is active.
    /// </summary>
    public static float EditableSliderRow(string id, float value, float min, float max,
        string label = null,
        string format = "0.00",
        float labelWidth = 100f,
        float sliderWidth = 200f,
        float fieldWidth = 60f)
    {
        GUILayout.BeginHorizontal();

        if (!string.IsNullOrEmpty(label))
        {
            GUILayout.Label(label, Label,
                GUILayout.Width(labelWidth),
                GUILayout.Height(SliderThumb.fixedHeight));
        }

        value = HorizontalSliderWithFill(value, min, max, GUILayout.Width(sliderWidth));
        GUILayout.Space(8);

        // Decide what to show in the text field. If this field is focused and
        // we have remembered text, show that; otherwise show the formatted value.
        GUI.SetNextControlName(id);
        bool isFocused = GUI.GetNameOfFocusedControl() == id;

        string textToShow;
        if (isFocused && _editingFieldName == id)
        {
            textToShow = _editingFieldText;
        }
        else
        {
            textToShow = value.ToString(format,
                System.Globalization.CultureInfo.InvariantCulture);
            // If we just lost focus on this field, drop the cached text.
            if (_editingFieldName == id)
            {
                _editingFieldName = null;
                _editingFieldText = null;
            }
        }

        string newText = GUILayout.TextField(textToShow, TextField,
            GUILayout.Width(fieldWidth),
            GUILayout.Height(SliderThumb.fixedHeight));

        if (isFocused)
        {
            // Remember what the user has typed so we don't reformat over them.
            _editingFieldName = id;
            _editingFieldText = newText;

            // If it parses to a real number, update the value. Otherwise the
            // value stays where it was and the user keeps typing.
            if (float.TryParse(newText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float parsed))
            {
                value = Mathf.Clamp(parsed, min, max);
            }
        }

        GUILayout.EndHorizontal();
        return value;
    }

    // -------------------------------------------------------------------
    // Icon helpers
    // -------------------------------------------------------------------
    /// <summary>
    /// Draw an icon by name, tinted to the given color. No-op if the icon
    /// isn't registered (so calling code with a typo just skips silently
    /// rather than throwing). Icon names are short tokens like "plus",
    /// "gear", "chevron-down" — see KumihoUI.Icons for the full list.
    /// </summary>
    public static void DrawIcon(Rect rect, string name, Color tint)
    {
        if (!Icons.TryGetValue(name, out var tex) || tex == null) return;
        var prev = GUI.color;
        GUI.color = tint;
        GUI.DrawTexture(rect, tex);
        GUI.color = prev;
    }

    /// <summary>
    /// Draw an icon at its default Text color tint. Useful for inline icon
    /// placement that should match label text.
    /// </summary>
    public static void DrawIcon(Rect rect, string name)
    {
        DrawIcon(rect, name, Colors.Text);
    }

    /// <summary>
    /// Clickable icon button. Returns true on the frame the mouse releases
    /// inside the rect. The icon is tinted with TextDim when idle, Text on
    /// hover, and Accent (teal) while pressed. Override these via the
    /// optional color parameters for custom styling (e.g. Warning for a
    /// destructive icon-only delete button).
    /// </summary>
    public static bool IconButton(Rect rect, string name,
        Color? idleTint = null,
        Color? hoverTint = null,
        Color? pressTint = null)
    {
        var idle  = idleTint  ?? Colors.TextDim;
        var hover = hoverTint ?? Colors.Text;
        var press = pressTint ?? Colors.Accent;

        var e = Event.current;
        bool hovered = rect.Contains(e.mousePosition);
        bool pressed = hovered && Input.GetMouseButton(0);
        bool clicked = false;
        if (e.type == EventType.MouseUp && e.button == 0 && hovered)
        {
            clicked = true;
            e.Use();
        }

        Color tint = pressed ? press : (hovered ? hover : idle);
        if (e.type == EventType.Repaint)
            DrawIcon(rect, name, tint);
        return clicked;
    }

    // -------------------------------------------------------------------
    // Branding helpers
    // -------------------------------------------------------------------
    /// <summary>
    /// Draw the Kumiho logo at the given rect, preserving aspect ratio.
    /// No-op if the logo wasn't included in the bundle.
    /// </summary>
    public static void DrawLogo(Rect rect)
    {
        if (Logo == null) return;
        GUI.DrawTexture(rect, Logo, ScaleMode.ScaleToFit, true);
    }

    /// <summary>
    /// Draw the Kumiho logo tinted by the given color. Use a low-alpha
    /// color for watermark effects, or <see cref="Colors.AccentHi"/> for an
    /// accent-colored variant.
    /// </summary>
    public static void DrawLogo(Rect rect, Color tint)
    {
        if (Logo == null) return;
        var prev = GUI.color;
        GUI.color = tint;
        GUI.DrawTexture(rect, Logo, ScaleMode.ScaleToFit, true);
        GUI.color = prev;
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------
    private static GUIStyle NewStyle(Texture2D bg, int border, int padding, Font font = null, int fontSize = 12)
    {
        return new GUIStyle
        {
            normal = { background = bg, textColor = Colors.Text },
            border = new RectOffset(border, border, border, border),
            padding = new RectOffset(padding, padding, padding, padding),
            font = font,
            fontSize = fontSize,
        };
    }

    private static Color Hex(string s)
    {
        if (ColorUtility.TryParseHtmlString(s, out var c))
            return c;
        return Color.magenta; // visibly broken so missing colors get noticed
    }
}
