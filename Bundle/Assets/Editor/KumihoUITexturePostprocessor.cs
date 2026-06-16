using UnityEditor;
using UnityEngine;

/// <summary>
/// Auto-applies the canonical Kumiho UI import settings to any texture
/// dropped into Assets/Textures/. Runs every time Unity imports or
/// re-imports an asset in that folder. Textures elsewhere are left alone.
/// </summary>
public class KumihoUITexturePostprocessor : AssetPostprocessor
{
    // Folder filter. Anything outside this path is ignored.
    private const string TargetFolder = "Assets/Textures/";

    private void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(TargetFolder, System.StringComparison.Ordinal))
            return;

        var importer = (TextureImporter)assetImporter;
        if (importer == null) return;

        // Environmental preview sprite sheets ("env-*") are a special case: huge
        // (up to 4096), and their cells are shown minified on screen, so they get
        // mipmaps + trilinear for a correct, non-shimmery downscale. Everything
        // else is small UI chrome drawn near 1:1, so plain bilinear + no mips +
        // a 256 cap (which also catches oversized art) is right.
        var fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
        var isEnv    = fileName.StartsWith("env-", System.StringComparison.OrdinalIgnoreCase);

        // Sprite type and shape
        importer.textureType        = TextureImporterType.Default;
        importer.textureShape       = TextureImporterShape.Texture2D;

        // Color handling
        importer.sRGBTexture        = true;
        importer.alphaSource        = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;

        // Sampling. env sheets: mipmaps + trilinear (clean minification of the
        // oversized cells, no bilinear blur, no point-filter shimmer on the fine
        // rain/snow lines). UI chrome: no mips, bilinear.
        importer.mipmapEnabled      = isEnv;
        importer.wrapMode           = TextureWrapMode.Clamp;
        importer.filterMode         = isEnv ? FilterMode.Trilinear : FilterMode.Bilinear;
        importer.anisoLevel         = 0;

        // Runtime cost
        importer.isReadable         = false;

        // Default-platform: no compression. 256 max size for chrome; env sheets
        // opt into 4096 so they're not downscaled into mush.
        var platform = importer.GetDefaultPlatformTextureSettings();
        platform.textureCompression = TextureImporterCompression.Uncompressed;
        platform.maxTextureSize     = isEnv ? 4096 : 256;
        importer.SetPlatformTextureSettings(platform);
    }
}
