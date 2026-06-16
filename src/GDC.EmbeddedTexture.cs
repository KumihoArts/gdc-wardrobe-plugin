using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace GDCplugin
{
    // Loads PNGs embedded in the plugin assembly into Texture2D, cached by
    // resource name. Used for raw textures the KumihoUI bundle doesn't carry
    // (the window logo, the maker tab icon). Resource names follow the
    // "<RootNamespace>.<Folder>.<File>" convention, e.g.
    // "GDCplugin.Resources.GDCLogo.png".
    internal static class EmbeddedTexture
    {
        private static readonly Dictionary<string, Texture2D?> _cache
            = new Dictionary<string, Texture2D?>();

        public static Texture2D? Load(string resourceName)
        {
            if (_cache.TryGetValue(resourceName, out var cached)) return cached;

            Texture2D? tex = null;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        GDCPlugin.Logger?.LogWarning($"[texload] Embedded resource '{resourceName}' not found.");
                    }
                    else
                    {
                        var buf = new byte[stream.Length];
                        stream.Read(buf, 0, buf.Length);

                        var t = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                        {
                            hideFlags  = HideFlags.HideAndDontSave,
                            wrapMode   = TextureWrapMode.Clamp,
                            filterMode = FilterMode.Bilinear,
                        };
                        if (t.LoadImage(buf))
                            tex = t;
                        else
                            GDCPlugin.Logger?.LogWarning($"[texload] LoadImage failed for '{resourceName}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogError($"[texload] '{resourceName}' load failed: {ex.Message}");
            }

            // Cache even null so a missing resource doesn't re-hit the stream
            // every draw.
            _cache[resourceName] = tex;
            return tex;
        }
    }
}
