using System;
using System.Collections.Generic;
using CharaCustom;
using HarmonyLib;

namespace GDCplugin
{
    // Same hook FavorHide uses: prefix CustomSelectScrollController.CreateList,
    // mutate the incoming list before HS2 builds cells. Different predicate.
    internal static class FilterHooks
    {
        // Last seen controller, kept so a hotkey-driven toggle can ask HS2
        // to rebuild its list without the user clicking around to retrigger it.
        private static CustomSelectScrollController? _lastClothesController;
        private static List<CustomSelectInfo>?       _lastUnfilteredList;

        private static bool IsClothesController(CustomSelectScrollController ctrl)
            => ctrl != null && ctrl.GetComponentInParent<CvsC_Clothes>() != null;

        [HarmonyPrefix, HarmonyPatch(typeof(CustomSelectScrollController), nameof(CustomSelectScrollController.CreateList))]
        private static void OnListCreating(CustomSelectScrollController __instance, ref List<CustomSelectInfo> _lst)
        {
            try
            {
                if (_lst == null || !IsClothesController(__instance)) return;

                // Always stash the unfiltered list so a later toggle-off can
                // restore the full set without requiring a maker reload.
                _lastClothesController = __instance;
                _lastUnfilteredList    = new List<CustomSelectInfo>(_lst);

                // Two independent filters. GDC-only keeps GDC-authored items;
                // Compatible-only keeps items whose manifest carries the
                // plugin marker. Both on = the intersection (compatible GDC
                // items), since each pass narrows the list further.
                // Per-item try/catch inside the predicate: a single bad manifest
                // throwing must not abort RemoveAll mid-pass and leave the Maker
                // list half-filtered. On error keep the item (return false).
                if (GDCPlugin.FilterEnabled.Value)
                    _lst.RemoveAll(info => info != null && KeepFails(() => !SideloaderBridge.IsGDC(info)));

                if (GDCPlugin.CompatFilterEnabled.Value)
                    _lst.RemoveAll(info => info != null && KeepFails(() => !SideloaderBridge.IsPluginCompatible(info)));
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogError($"FilterHooks prefix error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Evaluates a remove-predicate, swallowing any exception as "keep the
        // item" (false) so one bad manifest can't half-filter the whole list.
        private static bool KeepFails(Func<bool> predicate)
        {
            try { return predicate(); }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"FilterHooks: predicate threw, keeping item. {ex.Message}");
                return false;
            }
        }

        // Called by Plugin.Update when the user toggles the hotkey.
        // I try to call the controller's own CreateList again with the
        // stashed unfiltered list. If that fails (controller torn down,
        // signature changed), I fall back to a no-op and the next time
        // HS2 rebuilds the list it'll pick up the new filter state.
        public static void RequestListRefresh()
        {
            SideloaderBridge.ClearCache();

            if (_lastClothesController == null || _lastUnfilteredList == null) return;

            try
            {
                _lastClothesController.CreateList(new List<CustomSelectInfo>(_lastUnfilteredList));
            }
            catch (Exception ex)
            {
                GDCPlugin.Logger?.LogDebug($"RequestListRefresh: live refresh failed, will pick up on next rebuild. {ex.Message}");
            }
        }
    }
}
