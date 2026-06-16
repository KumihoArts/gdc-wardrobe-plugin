using System;
using System.Collections.Generic;

namespace GDCplugin
{
    // Central parsing for GDC's mod convention: the Part x Material matrix
    // described in MOD_CONVENTION.md. Both PresetBinding (material prefix +
    // part suffix) and the texture grid (def_tex_<part> scoping) key off the
    // same Part tokens here so the two stay in sync.
    //
    // Everything is typo-tolerant and case-insensitive. The Part set is closed
    // (the eight clothing slots); preset names are open and only normalized for
    // a couple of known authoring typos.
    internal static class ModConvention
    {
        // Canonical Part tokens, indexed by the HS2 objClothes / CvsC_Clothes.SNo
        // slot order. Same order for both sexes; male just lacks materials for
        // some rows. Index out of range returns null.
        private static readonly string[] _slotPart =
        {
            "Top",      // 0
            "Bottom",   // 1
            "Intop",    // 2
            "Inbottom", // 3
            "Gloves",   // 4
            "Panst",    // 5
            "Socks",    // 6
            "Shoes",    // 7
        };

        public static string PartForSlot(int slot)
            => slot >= 0 && slot < _slotPart.Length ? _slotPart[slot] : null;

        // Display order + premade-orb keys for the well-known presets. Discovery
        // is not limited to these; unknown names still surface (appended after).
        public static readonly string[] KnownPresetOrder = { "Leather", "Knit", "Latex", "Denim" };

        // Folder / material suffix spelling -> canonical Part token. Returns null
        // when the raw token is not a recognized Part.
        private static readonly Dictionary<string, string> _partAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "top", "Top" },
                { "bottom", "Bottom" }, { "bot", "Bottom" },
                { "intop", "Intop" }, { "inner_t", "Intop" }, { "innert", "Intop" }, { "innertop", "Intop" },
                { "inbottom", "Inbottom" }, { "inner_b", "Inbottom" }, { "innerb", "Inbottom" }, { "innerbottom", "Inbottom" },
                { "panst", "Panst" }, { "pasnt", "Panst" }, { "pantyhose", "Panst" }, { "panstockings", "Panst" },
                { "gloves", "Gloves" }, { "glove", "Gloves" },
                { "socks", "Socks" }, { "sock", "Socks" },
                { "shoes", "Shoes" }, { "shoe", "Shoes" },
            };

        public static string NormalizePart(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            return _partAliases.TryGetValue(raw.Trim(), out var canon) ? canon : null;
        }

        // Known preset-name typo fixes. Anything else is taken as authored and
        // title-cased for display.
        private static readonly Dictionary<string, string> _presetAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "demin", "Denim" },
            };

        public static string NormalizePreset(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var t = raw.Trim();
            if (_presetAliases.TryGetValue(t, out var canon)) return canon;
            return char.ToUpperInvariant(t[0]) + (t.Length > 1 ? t.Substring(1) : "");
        }

        // Splits a material name "<Preset>_<Part>" into its normalized preset
        // prefix and Part token. Returns false when the suffix after the last
        // underscore is not a recognized Part (e.g. "Jacketmainmaterial").
        public static bool TrySplitMaterial(string matName, out string preset, out string part)
        {
            preset = null; part = null;
            if (string.IsNullOrEmpty(matName)) return false;
            var us = matName.LastIndexOf('_');
            if (us <= 0 || us >= matName.Length - 1) return false;
            var canonPart = NormalizePart(matName.Substring(us + 1));
            if (canonPart == null) return false;
            preset = NormalizePreset(matName.Substring(0, us));
            part   = canonPart;
            return !string.IsNullOrEmpty(preset);
        }

        // A bare single-token material name (no underscore) that is one of the
        // known presets. Old testmods shipped "Leather"/"Transparent" with no
        // part suffix; those are usable on any slot.
        public static bool IsBareKnownPreset(string matName, out string preset)
        {
            preset = null;
            if (string.IsNullOrEmpty(matName)) return false;
            if (matName.IndexOf('_') >= 0) return false;
            foreach (var k in KnownPresetOrder)
                if (string.Equals(k, matName, StringComparison.OrdinalIgnoreCase))
                { preset = k; return true; }
            return false;
        }

        // True when a bundle asset path sits in the def_tex folder for the given
        // Part: "/def_tex_<part>/" or "/deftex_<part>/". A bare "/def_tex/" (no
        // suffix) matches any Part as a legacy single-part fallback. The Part is
        // matched through the alias table so "/def_tex_pasnt/" still resolves.
        public static bool PathInDefTexForPart(string path, string part)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lower = path.ToLowerInvariant();
            // Legacy bare folder, any part.
            if (lower.IndexOf("/def_tex/", StringComparison.Ordinal) >= 0
                || lower.IndexOf("/deftex/", StringComparison.Ordinal) >= 0)
                return true;
            if (string.IsNullOrEmpty(part)) return false;

            foreach (var seg in EnumerateSegments(lower))
            {
                string suffix = null;
                if (seg.StartsWith("def_tex_", StringComparison.Ordinal)) suffix = seg.Substring(8);
                else if (seg.StartsWith("deftex_", StringComparison.Ordinal)) suffix = seg.Substring(7);
                if (suffix == null) continue;
                if (string.Equals(NormalizePart(suffix), part, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // True when a path has any def_tex / deftex / extra folder segment. Used
        // by the unscoped variant harvest and the force-load sibling picker.
        public static bool PathHasSwapFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var seg in EnumerateSegments(path))
            {
                if (seg.StartsWith("extra", StringComparison.OrdinalIgnoreCase)) return true;
                if (seg.StartsWith("def_tex", StringComparison.OrdinalIgnoreCase)) return true;
                if (seg.StartsWith("deftex", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static IEnumerable<string> EnumerateSegments(string path)
        {
            var idx = 0;
            while (idx < path.Length)
            {
                var slash = path.IndexOf('/', idx);
                if (slash < 0) yield break;
                if (slash > idx) yield return path.Substring(idx, slash - idx);
                idx = slash + 1;
            }
        }
    }
}
