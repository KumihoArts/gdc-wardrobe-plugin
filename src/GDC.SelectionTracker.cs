using AIChara;
using CharaCustom;
using KKAPI.Studio;
using UnityEngine;

namespace GDCplugin
{
    // Lightweight "what's the user currently editing?" probe. Static method
    // pattern instead of an event subscription because the maker UI is the
    // only place we care about, and a per-frame poll from SliderWindow.Draw
    // is cheap enough that adding event plumbing would be overkill.
    //
    // Returns null when maker isn't open, when no character is loaded, or
    // when the clothes tab isn't the active sub-scene. The slider window
    // treats null as "no selection, render empty section."
    internal static class SelectionTracker
    {
        public readonly struct Selection
        {
            public readonly ChaControl Character;
            public readonly int        SlotNo;
            public readonly GameObject? ItemObject;   // objClothes[slot], captures item-swap identity

            public Selection(ChaControl character, int slot, GameObject? item)
            {
                Character  = character;
                SlotNo     = slot;
                ItemObject = item;
            }

            // Equality must include ItemObject so that swapping to a different
            // item inside the same slot still triggers a refresh. Without
            // this, the slider window stayed stuck on whichever item was
            // loaded when the slot was first opened.
            // Use Unity's overloaded == (not ReferenceEquals) so a destroyed
            // ChaControl / item GameObject (Unity fake-null) compares unequal
            // to a live one. With ReferenceEquals a slot whose item was
            // destroyed and rebuilt under the same managed reference would
            // report "unchanged" and discovery would keep stale bindings.
            public bool Matches(in Selection other)
                => Character == other.Character
                   && SlotNo == other.SlotNo
                   && ItemObject == other.ItemObject;
        }

        // Find the ACTIVE CvsC_Clothes in the scene. HS2 maker spawns eight of
        // these (one per clothing slot, each with a fixed SNo) and switches
        // tabs by toggling a CanvasGroup's alpha/interactable, NOT by
        // SetActive. So every instance stays activeInHierarchy and a plain
        // FindObjectOfType<CvsC_Clothes>() always returns the same one (top),
        // which is why the plugin used to stay stuck on the top slot.
        //
        // The selected tab is the instance whose controlling CanvasGroup is
        // interactable (and fully faded in). Score every instance by that and
        // pick the winner. Cached per frame because SliderWindow asks several
        // times per draw pass.
        private static CvsC_Clothes? _cachedCvs;
        private static int           _cachedFrame = -1;

        private static CvsC_Clothes? GetCvs()
        {
            if (_cachedFrame == Time.frameCount && _cachedCvs != null) return _cachedCvs;
            _cachedFrame = Time.frameCount;
            _cachedCvs   = FindActiveCvs();
            return _cachedCvs;
        }

        private static CvsC_Clothes? FindActiveCvs()
        {
            CvsC_Clothes? best = null;
            var bestScore = float.NegativeInfinity;
            var candidates = 0;

            // FindObjectsOfTypeAll catches every instance regardless of canvas
            // fade state; filter to real, initialized scene instances so prefab
            // assets and not-yet-started copies (chaCtrl still null) never win.
            foreach (var cvs in Resources.FindObjectsOfTypeAll<CvsC_Clothes>())
            {
                if (cvs == null) continue;
                if (!cvs.gameObject.scene.IsValid()) continue;

                // Must be a usable selection: live character, in-range slot,
                // clothes array present. Skips half-initialized instances that
                // would otherwise return a null/garbage Selection.
                var cha  = cvs.chaCtrl;
                var slot = cvs.SNo;
                if (cha == null) continue;
                if (slot < 0 || slot >= 8) continue;
                if (cha.objClothes == null) continue;
                candidates++;

                // Nearest CanvasGroup up the hierarchy gates this tab's
                // visibility. Interactable dominates; alpha and blocksRaycasts
                // break ties so a mid-fade transition still resolves to the
                // incoming tab. activeInHierarchy is a final tiebreak.
                var cg = cvs.GetComponentInParent<CanvasGroup>();
                var score = 0f;
                if (cg != null)
                {
                    if (cg.interactable)   score += 1000f;
                    if (cg.blocksRaycasts) score += 100f;
                    score += cg.alpha;
                }
                if (cvs.gameObject.activeInHierarchy) score += 0.001f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best      = cvs;
                }
            }

            // Log only when the picked slot changes; this runs every frame the
            // window draws, so logging unconditionally floods the file.
            if (best != null && best.SNo != _lastLoggedSNo)
            {
                _lastLoggedSNo = best.SNo;
                GDCPlugin.Logger?.LogDebug($"[select] active cvs SNo={best.SNo} score={bestScore} of {candidates} candidate(s)");
            }
            return best;
        }

        private static int _lastLoggedSNo = -999;

        // The clothing slot the Studio UI is editing. Studio has no per-slot
        // canvas tabs like maker, so the window's slot dropdown writes the
        // user's pick here and the Studio selection path below reads it. Maker
        // ignores this entirely (it gets the slot from the active CvsC_Clothes).
        public static int StudioSlot;

        // Last character that was actually selected in Studio. Studio's
        // selection can land on a non-character (light, folder, camera); when
        // it does we keep editing the last real character instead of going
        // blank, which matches how a user expects the window to behave while
        // they fiddle with other scene objects.
        private static ChaControl? _lastStudioChar;

        // Per-frame cache of the resolved selection. OnGUI fires several times
        // per frame (Layout + Repaint + one per input event) and the window
        // asks for Current on each pass; without this cache the Studio path
        // re-ran StudioAPI.GetSelectedCharacters() (which allocates a list)
        // every pass, and that steady per-frame garbage triggered a gen0 GC
        // roughly once a second, stalling all threads for a frame or two. The
        // maker path was already frame-cached inside GetCvs; this extends the
        // same guarantee to the whole getter.
        private static int        _curFrame = -1;
        private static Selection? _curCached;

        public static Selection? Current
        {
            get
            {
                if (_curFrame == Time.frameCount) return _curCached;
                _curFrame  = Time.frameCount;
                _curCached = Compute();
                return _curCached;
            }
        }

        private static Selection? Compute()
        {
            // Studio has no maker clothes canvas, so drive selection from
            // the workspace-selected character + the dropdown-picked slot.
            if (StudioAPI.InsideStudio) return StudioCurrent();

            var cvs = GetCvs();
            if (cvs == null) return null;

            // chaCtrl is the character being edited; SNo is the active
            // clothing slot (0=top, 1=bottom, 2=bra, 3=panties, 4=gloves,
            // 5=pantyhose, 6=socks, 7=shoes).
            var cha  = cvs.chaCtrl;
            var slot = cvs.SNo;

            if (cha == null || slot < 0 || slot >= 8) return null;

            // Capture the GameObject so item swaps within the same slot
            // are detected. Null when the slot is empty.
            var item = cha.objClothes != null && slot < cha.objClothes.Length
                ? cha.objClothes[slot]
                : null;

            return new Selection(cha, slot, item);
        }

        // Studio selection: the currently-selected character (or the last real
        // one) plus the dropdown-picked clothing slot. Returns null only when
        // no character has ever been selected this session.
        private static Selection? StudioCurrent()
        {
            ChaControl? cha = null;
            foreach (var oci in StudioAPI.GetSelectedCharacters())
            {
                if (oci != null && oci.charInfo != null) { cha = oci.charInfo; break; }
            }

            // Keep the last real character when the user selects a non-character.
            if (cha != null) _lastStudioChar = cha;
            else cha = _lastStudioChar;

            // Unity-null check covers a destroyed last-character too. Guard the
            // empty array explicitly: Mathf.Clamp(x, 0, -1) returns -1, which
            // would then index objClothes[-1] and throw.
            if (cha == null || cha.objClothes == null || cha.objClothes.Length == 0) return null;

            var slot = Mathf.Clamp(StudioSlot, 0, cha.objClothes.Length - 1);
            var item = cha.objClothes[slot];
            return new Selection(cha, slot, item);
        }
    }
}
