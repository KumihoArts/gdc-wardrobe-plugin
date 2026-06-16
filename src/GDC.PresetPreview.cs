using System;
using System.Collections.Generic;
using Kumiho.UI;
using UnityEngine;

namespace GDCplugin
{
    // Preview art for the Textures-tab preset buttons. The preset set is
    // fixed ("Leather", "Knit", "Latex", "Denim"), so each gets a premade orb
    // texture painted into the UI bundle rather than rendered at runtime.
    // Rendering HS2's clothing shaders on a bare sphere was unreliable (the
    // shaders expect the character's lighting/colormask rig); a baked image
    // is both reliable and exactly what Sly painted.
    //
    // Bundle naming convention: "preset-<lowercased preset name>", e.g.
    // "preset-leather", "preset-transparent". Missing assets fall back to an
    // empty frame, so a preset works before its orb art exists.
    internal static class PresetPreview
    {
        // Keyed by lowercased preset name. Caches misses (null) too, but Get
        // re-loads when a cached entry has gone null so a scene-reload bundle
        // refresh doesn't leave permanently-blank tiles.
        private static readonly Dictionary<string, Texture2D> _cache
            = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public static Texture Get(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return null;

            if (_cache.TryGetValue(presetName, out var cached) && cached != null)
                return cached;

            var asset  = "preset-" + presetName.ToLowerInvariant();
            var loaded = KumihoUI.LoadBundleTexture(asset);
            _cache[presetName] = loaded;
            return loaded;
        }
    }
}
