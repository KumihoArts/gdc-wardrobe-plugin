using System;
using System.Collections.Generic;
using System.Xml.Linq;
using AIChara;          // ChaListDefine.CategoryNo lives here on HS2
using CharaCustom;
using Sideloader;
using Sideloader.AutoResolver;

namespace GDCplugin
{
    // Maps a Maker list entry (CustomSelectInfo) to the zipmod that supplied
    // it, then reads the author string out of the manifest. FavorHide showed
    // me the right way to do this: Sideloader exposes Manifests and
    // UniversalAutoResolver directly, no reflection needed.
    internal static class SideloaderBridge
    {
        // Cache resolution per (category, id) so the filter prefix doesn't
        // walk the sideloader index on every list rebuild. Cleared whenever
        // the user toggles the filter so a hot-reload picks up changes.
        private static readonly Dictionary<long, bool> _isGdcCache = new Dictionary<long, bool>();

        // Same idea for the plugin-compatible filter: an item is "compatible"
        // when its zipmod manifest carries the <gdcPlugin compatible="true"/>
        // marker (the mod ships presets / def_tex the plugin can drive).
        private static readonly Dictionary<long, bool> _isCompatCache = new Dictionary<long, bool>();

        public static void ClearCache()
        {
            _isGdcCache.Clear();
            _isCompatCache.Clear();
        }

        // True when this entry came from a zipmod whose author == GDC.
        // Stock-game items (id below the sideloader base slot range) and
        // anything I can't resolve return false rather than throwing, so a
        // single bad entry can't break the filter for the whole list.
        public static bool IsGDC(CustomSelectInfo info)
        {
            if (info == null) return false;

            var key = CacheKey(info);
            if (_isGdcCache.TryGetValue(key, out var cached)) return cached;

            var result = ComputeIsGDC(info);
            _isGdcCache[key] = result;
            return result;
        }

        // Overload that takes category + id directly, for code paths
        // (like MaterialBinding) that don't have a CustomSelectInfo
        // handy. Same caching, same lookup logic.
        public static bool IsGDC(int category, int id)
        {
            var key = ((long)category << 32) ^ (uint)id;
            if (_isGdcCache.TryGetValue(key, out var cached)) return cached;

            var result = ComputeIsGDCByIds(category, id);
            _isGdcCache[key] = result;
            return result;
        }

        private static bool ComputeIsGDCByIds(int category, int id)
        {
            try
            {
                if (id < UniversalAutoResolver.BaseSlotID) return false;

                var resolve = UniversalAutoResolver.TryGetResolutionInfo(
                    (ChaListDefine.CategoryNo)category, id);
                if (resolve == null || string.IsNullOrEmpty(resolve.GUID)) return false;

                if (!Sideloader.Sideloader.Manifests.TryGetValue(resolve.GUID, out var manifest)
                    || manifest == null) return false;

                var author = manifest.Author;
                return !string.IsNullOrEmpty(author)
                       && string.Equals(author, GDCPlugin.GDCAuthorTag, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"SideloaderBridge.IsGDC(ids) failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool ComputeIsGDC(CustomSelectInfo info)
        {
            try
            {
                // Stock items live below the sideloader base slot range. They
                // have no manifest, can't be GDC, no point asking.
                if (info.id < UniversalAutoResolver.BaseSlotID) return false;

                var resolve = UniversalAutoResolver.TryGetResolutionInfo(
                    (ChaListDefine.CategoryNo)info.category, info.id);
                if (resolve == null || string.IsNullOrEmpty(resolve.GUID)) return false;

                if (!Sideloader.Sideloader.Manifests.TryGetValue(resolve.GUID, out var manifest)
                    || manifest == null) return false;

                var author = manifest.Author;
                return !string.IsNullOrEmpty(author)
                       && string.Equals(author, GDCPlugin.GDCAuthorTag, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"SideloaderBridge.IsGDC failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // True when the entry's zipmod manifest carries the marker
        //   <gdcPlugin compatible="true" />
        // i.e. the mod was built for this plugin (ships presets / def_tex).
        // Drives the second, independent "Compatible only" maker filter. Same
        // resolve-then-manifest path as IsGDC, same per-(category,id) cache, so
        // it never touches the bundle (no force-load during list build).
        public static bool IsPluginCompatible(CustomSelectInfo info)
        {
            if (info == null) return false;
            return IsPluginCompatible(info.category, info.id);
        }

        public static bool IsPluginCompatible(int category, int id)
        {
            var key = ((long)category << 32) ^ (uint)id;
            if (_isCompatCache.TryGetValue(key, out var cached)) return cached;

            var result = ComputeIsCompatible(category, id);
            _isCompatCache[key] = result;
            return result;
        }

        private static bool ComputeIsCompatible(int category, int id)
        {
            try
            {
                if (id < UniversalAutoResolver.BaseSlotID) return false; // stock item, no manifest

                var resolve = UniversalAutoResolver.TryGetResolutionInfo(
                    (ChaListDefine.CategoryNo)category, id);
                if (resolve == null || string.IsNullOrEmpty(resolve.GUID)) return false;

                if (!Sideloader.Sideloader.Manifests.TryGetValue(resolve.GUID, out var manifest)
                    || manifest == null) return false;

                var doc = manifest.ManifestDocument;
                var marker = doc?.Root?.Element("gdcPlugin");
                if (marker == null) return false;

                // Presence of the element means compatible; an explicit
                // compatible="false" opts back out.
                var flag = marker.Attribute("compatible")?.Value;
                return string.IsNullOrEmpty(flag)
                       || string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"SideloaderBridge.IsPluginCompatible failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static long CacheKey(CustomSelectInfo info)
        {
            // category and id together are unique within a session.
            return ((long)info.category << 32) ^ (uint)info.id;
        }

        // Resolves a live (category, runtime id) back to its stable source: the
        // zipmod GUID and the mod's own (pre-resolve) slot. The layering feature
        // stores these so a card made on one machine re-resolves to the right
        // item on another, where Sideloader's AutoResolver hands the same mod a
        // different runtime id. False for stock items (no GUID) or unresolved.
        public static bool TryGetSource(int category, int id, out string guid, out int origId)
        {
            guid   = "";
            origId = id;
            try
            {
                if (id < UniversalAutoResolver.BaseSlotID) return false; // stock item
                var ri = UniversalAutoResolver.TryGetResolutionInfo((ChaListDefine.CategoryNo)category, id);
                if (ri == null || string.IsNullOrEmpty(ri.GUID)) return false;
                guid   = ri.GUID;
                origId = ri.Slot;
                return true;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"SideloaderBridge.TryGetSource failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // Sentinel returned by ResolveCurrentId when a GUID-backed item can't be
        // resolved this session (mod not installed). Callers must treat it as
        // "skip", never as a real id.
        public const int Unresolved = -1;

        // Inverse of TryGetSource: given the stored (category, guid, origId),
        // returns the current runtime id for this session.
        //
        // For a stock item (no GUID) the stored resolvedId is machine-stable, so
        // return it directly. For a GUID-backed (sideloader) item the runtime id
        // is assigned per session and differs across machines, so it MUST be
        // re-resolved through the GUID. If that fails, the mod isn't installed
        // here: return Unresolved rather than the stored resolvedId, which is an
        // authoring-machine runtime id that on another install points at an
        // unrelated item (would spawn the wrong garment as a layer).
        public static int ResolveCurrentId(int category, int resolvedId, string guid, int origId)
        {
            if (string.IsNullOrEmpty(guid)) return resolvedId;
            try
            {
                var ri = UniversalAutoResolver.TryGetResolutionInfo(origId, (ChaListDefine.CategoryNo)category, guid);
                if (ri != null) return ri.LocalSlot;
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"SideloaderBridge.ResolveCurrentId failed: {ex.GetType().Name}: {ex.Message}");
            }
            return Unresolved;
        }
    }
}
