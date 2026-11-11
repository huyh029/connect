using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NhiQuoc.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class HeroFxParticleClone
{
    public const string Folder = "Assets/_Game/HeroFXClones";
    private const string Request = "Assets/Editor/HeroFxParticleClone.request";
    private static double nextCheck;
    private static float CurvePeak(ParticleSystem.MinMaxCurve curve)
    {
        float peak = 0;
        for (int i = 0; i <= 32; i++)
            peak = Mathf.Max(peak, curve.Evaluate(i / 32f, 0), curve.Evaluate(i / 32f, 1));
        return peak;
    }

    private static bool IsSmallEmber(ParticleSystem system, Transform root)
    {
        var renderer = system.GetComponent<ParticleSystemRenderer>();
        if (!renderer || renderer.renderMode != ParticleSystemRenderMode.Billboard || system.trails.enabled) return false;
        string name = system.name.ToLowerInvariant();
        if (Regex.IsMatch(name, "big|slash|lightning|shock|wave|explosion|core|decal|impact")) return false;
        var main = system.main;
        float size = main.startSize3D ? Mathf.Max(CurvePeak(main.startSizeX), CurvePeak(main.startSizeY), CurvePeak(main.startSizeZ)) : CurvePeak(main.startSize);
        var lifetimeSize = system.sizeOverLifetime;
        if (lifetimeSize.enabled) size *= lifetimeSize.separateAxes ? Mathf.Max(CurvePeak(lifetimeSize.x), CurvePeak(lifetimeSize.y), CurvePeak(lifetimeSize.z)) : CurvePeak(lifetimeSize.size);
        var relative = root.worldToLocalMatrix * system.transform.localToWorldMatrix;
        size *= Mathf.Max(relative.MultiplyVector(Vector3.right).magnitude, relative.MultiplyVector(Vector3.up).magnitude, relative.MultiplyVector(Vector3.forward).magnitude);
        var emission = system.emission;
        float rate = Mathf.Max(CurvePeak(emission.rateOverTime), CurvePeak(emission.rateOverDistance));
        float burst = 0;
        for (int i = 0; i < emission.burstCount; i++) burst = Mathf.Max(burst, CurvePeak(emission.GetBurst(i).count));
        bool emberName = Regex.IsMatch(name, "spark|ember|firefl|glitter|lightparticle|smallparticle|dustmote");
        return emberName ? size <= .4f && rate <= 60 && burst <= 80 : size <= .12f && rate > 0 && rate <= 12 && burst <= 12;
    }
    static HeroFxParticleClone() { EditorApplication.update += Check; }
    private static void Check()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < nextCheck) return;
        nextCheck = EditorApplication.timeSinceStartup + 2;
        if (!File.Exists(Request)) return;
        var names = File.ReadAllLines(Request).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (names.Length == 0) return;
        // Explicit queue processes one hero at a time, avoiding loading all rigs.
        File.WriteAllLines(Request, names.Skip(1));
        try { ApplyHero(names[0].Trim()); }
        catch (Exception exception) { Debug.LogException(exception); }
    }

    public static GameObject Prepare(GameObject source, Dictionary<GameObject, GameObject> cache)
    {
        if (!source) return null;
        if (cache.TryGetValue(source, out var cached)) return cached;
        var original = source;
        var visited = new HashSet<string>();
        while (original)
        {
            string path = AssetDatabase.GetAssetPath(original);
            if (!visited.Add(path)) throw new InvalidOperationException("Cyclic FX source: " + path);
            string sourcePath;
            if (path.StartsWith(Folder + "/", StringComparison.Ordinal))
            {
                string report = Path.ChangeExtension(path, ".report.txt");
                if (!File.Exists(report)) throw new InvalidOperationException("Missing FX source report: " + path);
                sourcePath = File.ReadLines(report).First(line => line.StartsWith("Source: ")).Substring(8).Trim();
            }
            else if (path.StartsWith("Assets/_Game/HeroVAT/PreparedFX/", StringComparison.Ordinal))
                sourcePath = AssetDatabase.GUIDToAssetPath(Path.GetFileNameWithoutExtension(path));
            else break;
            original = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (!original) throw new InvalidOperationException("Missing original FX: " + sourcePath);
        }
        cache[source] = original;
        return original;
    }
    public static void ApplyHero(string name)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject vat = null;
        try
        {
            var hero = HeroAnimationDisplaySetup.CreateSingleBakeSource(name, scene);
            var cache = new Dictionary<GameObject, GameObject>();
            var attack = hero.attackEffects.Select(fx => Prepare(fx.prefab, cache)).ToArray();
            var alternate = hero.alternateAttackEffects.Select(fx => Prepare(fx.prefab, cache)).ToArray();
            var ulti = hero.ultiEffects.Select(fx => Prepare(fx.prefab, cache)).ToArray();
            string path = HeroVatBaker.Output + "/" + name + "/" + name + " VAT.prefab";
            if (!File.Exists(path)) return;
            vat = PrefabUtility.LoadPrefabContents(path);
            var player = vat.GetComponent<HeroVatPlayer>();
            foreach (int state in new[] { 2, 5, 4 })
            {
                if (state >= player.clips.Length) continue;
                var sources = state == 2 ? attack : state == 5 ? alternate : ulti;
                if (sources.Length != player.clips[state].effects.Length) throw new InvalidOperationException("FX slot mismatch: " + name + "/" + state);
                for (int i = 0; i < sources.Length; i++) player.clips[state].effects[i].prefab = sources[i];
            }
            PrefabUtility.SaveAsPrefabAsset(vat, path);
            Debug.Log(name + ": VAT uses original FX prefabs for Attack 1/2 and Ulti. Transform, trigger frames and socket tracks preserved.");
        }
        finally
        {
            if (vat) PrefabUtility.UnloadPrefabContents(vat);
            EditorSceneManager.ClosePreviewScene(scene);
            AssetDatabase.SaveAssets();
            EditorUtility.UnloadUnusedAssetsImmediate();
        }
    }
}
