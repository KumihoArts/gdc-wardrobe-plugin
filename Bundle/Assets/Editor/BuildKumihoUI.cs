using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class BuildKumihoUI
{
    [MenuItem("Tools/Build Kumiho UI Bundle")]
    public static void Build()
    {
        string outputDir = "Assets/StreamingAssets";
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Sweep every PNG in Textures/ and every TTF in Fonts/ so the bundle
        // tracks whatever the generator emits without manual list maintenance.
        var textures = Directory
            .GetFiles("Assets/Textures", "*.png", SearchOption.TopDirectoryOnly)
            .Select(p => p.Replace('\\', '/'));

        var fonts = Directory.Exists("Assets/Fonts")
            ? Directory
                .GetFiles("Assets/Fonts", "*.ttf", SearchOption.TopDirectoryOnly)
                .Select(p => p.Replace('\\', '/'))
            : System.Linq.Enumerable.Empty<string>();

        var assetNames = textures.Concat(fonts).ToArray();
        if (assetNames.Length == 0)
        {
            Debug.LogError("[KumihoUI] No assets found under Assets/Textures or Assets/Fonts. Run the generator first.");
            return;
        }

        var builds = new[]
        {
            new AssetBundleBuild
            {
                assetBundleName = "kumiho_ui.unity3d",
                assetNames = assetNames,
            }
        };

        BuildPipeline.BuildAssetBundles(
            outputDir,
            builds,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);

        Debug.Log($"[KumihoUI] Built bundle with {assetNames.Length} assets at {Path.GetFullPath(outputDir)}");
        AssetDatabase.Refresh();
    }
}
