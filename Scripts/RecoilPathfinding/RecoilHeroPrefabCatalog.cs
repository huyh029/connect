using System;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed class RecoilHeroPrefabCatalog : ScriptableObject
    {
        [Serializable] public struct Entry { public int Id; public GameObject Prefab; }
        public Entry[] Heroes;
        public RecoilHeroSkillLibrary Skills;
        public GameObject Find(int id)
        {
            if (Heroes != null) foreach (var entry in Heroes) if (entry.Id == id) return entry.Prefab;
            return null;
        }
    }
}
