using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Every viewer's name keeps its own colour everywhere it shows (summon intros, ranking, top donors, hero name
    /// tags, endgame cards): picked from a palette of bright, readable colours by a hash of the name, so the same
    /// name always gets the same colour.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        private static readonly Color[] UserNamePalette =
        {
            new(1f, .84f, .3f),   // gold
            new(.45f, .85f, 1f),  // sky
            new(1f, .52f, .45f),  // coral
            new(.55f, 1f, .55f),  // green
            new(.85f, .62f, 1f),  // violet
            new(1f, .66f, .22f),  // orange
            new(.42f, 1f, .9f),   // aqua
            new(1f, .56f, .8f),   // pink
            new(.78f, .95f, .36f), // lime
            new(.62f, .76f, 1f),  // periwinkle
            new(1f, .95f, .55f),  // lemon
            new(.96f, .76f, .58f) // peach
        };

        internal static Color UserNameColor(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Color.white;
            unchecked
            {
                uint hash = 2166136261; // FNV-1a: stable across runs, unlike string.GetHashCode
                foreach (var c in name.Trim()) hash = (hash ^ c) * 16777619;
                return UserNamePalette[hash % (uint)UserNamePalette.Length];
            }
        }
    }
}
