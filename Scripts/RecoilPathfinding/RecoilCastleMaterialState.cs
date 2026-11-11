using System;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed class RecoilCastleMaterialState : MonoBehaviour
    {
        [Serializable]
        public struct RendererMaterials
        {
            public Renderer Renderer;
            public Material[] Intact;
            public Material[] Burning;
        }

        [SerializeField] private RendererMaterials[] renderers = Array.Empty<RendererMaterials>();
        [SerializeField] private bool isBurning;

        public bool IsBurning => isBurning;

        private void Start()
        {
            ApplyMaterials();
        }

        public void SetMaterialStates(RendererMaterials[] states)
        {
            renderers = states ?? Array.Empty<RendererMaterials>();
            isBurning = false;
            ApplyMaterials();
        }

        public void SetBurning(bool value)
        {
            if (isBurning == value) return;
            isBurning = value;
            ApplyMaterials();
        }

        private void ApplyMaterials()
        {
            if (renderers == null) return;
            foreach (var state in renderers)
            {
                if (state.Renderer == null) continue;
                var materials = isBurning ? state.Burning : state.Intact;
                if (materials == null || materials.Length != state.Renderer.sharedMaterials.Length)
                {
                    Debug.LogError($"Invalid {(isBurning ? "burning" : "intact")} materials for castle renderer " +
                        $"{state.Renderer.name}. Reconfigure it with the Castle Material States tool.", this);
                    continue;
                }
                state.Renderer.sharedMaterials = materials;
            }
        }
    }
}
