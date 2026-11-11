using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Every viewer's name is drawn in their side's colour everywhere it shows (summon intros, ranking, top donors,
    /// hero name tags, endgame cards): a shade of blue for the blue side, of red for the red side. The shade is picked
    /// by a hash of the name, so names on one side stay apart and the same name always gets the same shade.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        // Bright enough to read on dark plates and on the battlefield.
        private static readonly Color[] BlueNamePalette =
        {
            new(.35f, .72f, 1f),   // azure
            new(.5f, .85f, 1f),    // sky
            new(.42f, .6f, 1f),    // royal
            new(.3f, .9f, 1f),     // cyan
            new(.62f, .74f, 1f),   // periwinkle
            new(.55f, .95f, 1f)    // ice
        };

        private static readonly Color[] RedNamePalette =
        {
            new(1f, .35f, .32f),   // red
            new(1f, .5f, .42f),    // coral
            new(1f, .42f, .55f),   // rose
            new(1f, .58f, .5f),    // salmon
            new(.95f, .3f, .42f),  // crimson
            new(1f, .66f, .6f)     // blush
        };

        internal static Color UserNameColor(string name, bool blue)
        {
            var palette = blue ? BlueNamePalette : RedNamePalette;
            if (string.IsNullOrWhiteSpace(name)) return palette[0];
            unchecked
            {
                uint hash = 2166136261; // FNV-1a: stable across runs, unlike string.GetHashCode
                foreach (var c in name.Trim()) hash = (hash ^ c) * 16777619;
                return palette[hash % (uint)palette.Length];
            }
        }
    }
}
