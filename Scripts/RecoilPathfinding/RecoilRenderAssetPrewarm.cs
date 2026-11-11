using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        // Retain render dependencies for the match, including inactive attack/ultimate FX.
        private readonly HashSet<Object> _preloadedRenderAssets = new();

        private IEnumerator PreloadPoolRenderAssets(List<PrewarmPool> pools)
        {
            var roots = new HashSet<GameObject>();
            foreach (var pool in pools)
            {
                var root = pool.Pool.Count > 0 ? pool.Pool.Peek() : ResolvePresentationTemplate(pool.Template);
                if (root != null) roots.Add(root);
                var hero = pool.Template.GetComponent<NhiQuoc.Tools.HeroVatPlayer>();
                if (hero == null || hero.clips == null) continue;
                foreach (var clip in hero.clips)
                    if (clip != null && clip.effects != null)
                        foreach (var fx in clip.effects)
                            if (fx != null && fx.prefab != null) roots.Add(fx.prefab);
            }
            if (endgameMeteorPrefab != null) roots.Add(endgameMeteorPrefab);
            var ready = 0;
            var hasGraphics = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            foreach (var root in roots)
            {
                _poolPrewarmLabel = $"Đang chuẩn bị hình ảnh… {++ready}/{roots.Count}";
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    PreloadRenderMesh(filter.sharedMesh, hasGraphics);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer is SkinnedMeshRenderer skin) PreloadRenderMesh(skin.sharedMesh, hasGraphics);
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null || !_preloadedRenderAssets.Add(material)) continue;
                        if (material.shader != null) _preloadedRenderAssets.Add(material.shader);
                        foreach (var property in material.GetTexturePropertyNameIDs())
                        {
                            var texture = material.GetTexture(property);
                            if (texture != null && _preloadedRenderAssets.Add(texture) && hasGraphics)
                                texture.GetNativeTexturePtr();
                        }
                        // Native resource synchronization belongs in loading, never the attack loop.
                        yield return null;
                    }
                }
                yield return null;
            }
            Debug.Log($"[Render asset prewarm] Retained {_preloadedRenderAssets.Count} mesh/material/shader/texture assets before gameplay.", this);
        }

        private void PreloadRenderMesh(Mesh mesh, bool hasGraphics)
        {
            if (mesh == null || !_preloadedRenderAssets.Add(mesh) || !hasGraphics) return;
            if (mesh.vertexBufferCount > 0) mesh.GetNativeVertexBufferPtr(0);
        }
    }
}
