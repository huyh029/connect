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
            // Battle effects: siege stone explosion, Quân sư summon / cast / hit (and its orb source).
            var effects = RecoilBattleEffects.Load();
            if (effects != null)
                foreach (var fx in new[] { effects.SiegeImpactPrefab, effects.AdvisorSummonPrefab, effects.AdvisorCastPrefab,
                             effects.AdvisorHitPrefab, effects.AdvisorOrbSource })
                    if (fx != null) roots.Add(fx);
            if (siegeImpactPrefab != null) roots.Add(siegeImpactPrefab);
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
                    // Mesh particles (e.g. the swirling trails of Buff 2).
                    if (renderer is ParticleSystemRenderer particles && particles.renderMode == ParticleSystemRenderMode.Mesh)
                        PreloadRenderMesh(particles.mesh, hasGraphics);
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
            if (hasGraphics) yield return RenderPreloadedMaterials(roots);
            Debug.Log($"[Render asset prewarm] Retained {_preloadedRenderAssets.Count} mesh/material/shader/texture assets before gameplay.", this);
        }

        private IEnumerator RenderPreloadedMaterials(HashSet<GameObject> roots)
        {
            var target = RenderTexture.GetTemporary(256, 256, 24, RenderTextureFormat.ARGB32);
            var command = new CommandBuffer { name = "Loading: warm model and FX rendering" };
            var quad = new Mesh { name = "Loading FX shader warmup quad" };
            quad.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
            quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            quad.RecalculateNormals(); quad.RecalculateBounds();
            var rendered = new HashSet<(Material Material, Mesh Mesh)>();
            var drawCount = 0;
            var properties = new MaterialPropertyBlock();
            try
            {
                foreach (var root in roots)
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    var mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
                    if (mesh == null) mesh = quad;
                    var bounds = mesh.bounds;
                    var scale = 1f / Mathf.Max(.01f, bounds.extents.magnitude);
                    var model = Matrix4x4.Scale(Vector3.one * scale) * Matrix4x4.Translate(-bounds.center);
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null || material.shader == null || !material.shader.isSupported || !rendered.Add((material, mesh))) continue;
                        _poolPrewarmLabel = $"Đang render trước mô hình / FX… {rendered.Count}";
                        if (material.passCount == 0) continue;
                        var passes = new List<int>();
                        var forward = material.FindPass("UniversalForward");
                        if (forward < 0) forward = material.FindPass("ForwardLit");
                        passes.Add(forward >= 0 ? forward : 0);
                        foreach (var name in new[] { "UniversalForwardOnly", "ShadowCaster", "DepthOnly", "DepthNormals", "DepthNormalsOnly", "UniversalGBuffer", "MotionVectors" })
                        {
                            var pass = material.FindPass(name);
                            if (pass >= 0 && !passes.Contains(pass)) passes.Add(pass);
                        }
                        var hero = renderer.GetComponentInParent<NhiQuoc.Tools.HeroVatPlayer>(true);
                        var clipCount = hero != null && hero.clips != null ? hero.clips.Length : 1;
                        for (var clipIndex = 0; clipIndex < clipCount; clipIndex++)
                        for (var sample = 0; sample < 3; sample++)
                        {
                            var clip = hero != null && hero.clips != null ? hero.clips[clipIndex] : null;
                            var duration = clip != null ? Mathf.Max(.01f, clip.duration) : 1f;
                            command.Clear();
                            command.SetRenderTarget(target);
                            command.SetViewport(new Rect(0, 0, 256, 256));
                            command.ClearRenderTarget(true, true, Color.clear);
                            var view = Matrix4x4.TRS(new Vector3(0, 0, -3), Quaternion.identity, new Vector3(1, 1, -1)).inverse;
                            command.SetViewProjectionMatrices(view, GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-1.5f, 1.5f, -1.5f, 1.5f, .01f, 10f), true));
                            properties.Clear();
                            renderer.GetPropertyBlock(properties);
                            properties.SetVector("_VatClip", clip != null
                                ? new Vector4(clip.startFrame, clip.frameCount, duration, clip.loop ? 1 : 0)
                                : new Vector4(0, 1, 1, 1));
                            properties.SetVector("_VatPlayback", new Vector4(Time.time - duration * (.02f + sample * .48f), 1, 0, 0));
                            foreach (var pass in passes)
                            for (var sub = 0; sub < mesh.subMeshCount; sub++)
                            {
                                command.DrawMesh(mesh, model, material, sub, pass, properties);
                                drawCount++;
                            }
                            command.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                            command.SetViewport(new Rect(0, 0, Screen.width, Screen.height));
                            var camera = Camera.main;
                            if (camera != null) command.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
                            Graphics.ExecuteCommandBuffer(command);
                            yield return null;
                        }
                    }
                }
                Debug.Log($"[Render asset prewarm] Offscreen-rendered {rendered.Count} material/mesh combinations, {drawCount} draws including VAT clips and depth/shadow passes during loading.", this);
            }
            finally
            {
                command.Release();
                RenderTexture.ReleaseTemporary(target);
                Destroy(quad);
            }
        }
        private void PreloadRenderMesh(Mesh mesh, bool hasGraphics)
        {
            if (mesh == null || !_preloadedRenderAssets.Add(mesh) || !hasGraphics) return;
            if (mesh.vertexBufferCount > 0) mesh.GetNativeVertexBufferPtr(0);
        }
    }
}
