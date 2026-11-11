using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NhiQuoc.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class HeroAnimationDisplaySetup
{
    private const string Root = "Assets/_Game/ALL TUONG NHI QUOC/";
    private const string Group = "Nhi Quoc - Animation Display";
    // Clear space between character bodies, with extra room for wide ultimate FX.
    private const float MinimumHeroGap = 12f;
    private const float GapInBodyHeights = 4f;
    private sealed class Spec
    {
        public string name, model;
        public string alternateAttackSource, alternateAttackName;
        public string[] sources, clips;
        public Spec(string name, string model, string source, params string[] clips)
        { this.name = name; this.model = model; sources = Enumerable.Repeat(source, 5).ToArray(); this.clips = clips; }
    }

    private static Spec[] Specs()
    {
        var liu = new Spec("Luu Bi", "LUU BI/FInal + fx/lb_lod.prefab", "", "Scene", "rig|luubi_walk", "Scene", "rig|luubi_death", "rig|luubi_until");
        liu.sources = new[] { "LUU BI/Animation/lb_lod idle.fbx", "LUU BI/Animation/LB_ WALK_ULTI_DEATH.fbx", "LUU BI/Animation/lb_lod atk.fbx", "LUU BI/Animation/LB_ WALK_ULTI_DEATH.fbx", "LUU BI/Animation/LB_ WALK_ULTI_DEATH.fbx" };
        var specs = new[]
        {
            new Spec("Chu Du", "CHU DU/Final +fx/test.prefab", "CHU DU/Animation/CD_lod 1.fbx", "Armature|Armature|IDLE", "Armature|Armature|RUN", "Armature|Armature|ATTACK_01", "Armature|Armature|DEATH", "Armature|Armature|ULTI"),
            new Spec("Gia Cat Luong", "GIA CAT LUONG/FINAL + fx/gia cat luong LOD 2.prefab", "GIA CAT LUONG/Animation/gia cat luong LOD 1.fbx", "rig|rig|giacatluong_idle", "rig|rig|giacatluong_run", "rig|rig|giacatluong_attack", "rig|rig|giacatluong_die", "rig|rig|giacatluong_ulti"),
            new Spec("Ha Hau Don", "Ha hau don/Final nv + fx/hhd_lod 1.prefab", "Ha hau don/ANIMATION/hhd_lod 1.fbx", "rig|rig|hahaudon_idle", "rig|rig|hahaudon_run", "rig|rig|hahaudon_attack_02", "rig|rig|hahaudon_death", "rig|rig|hahaudon_Ulti"),
            new Spec("Lu Bo", "LU BO/FInal + Fx/LU BO LOD 1.prefab", "LU BO/Animation/LU BO LOD 2.fbx", "rig|rig|lubu_idle.002", "rig|rig|lubu_run.002", "rig|rig|lubu_attack_01", "rig|rig|lubu_death", "rig|rig|lubu_Ulti"),
            liu,
            new Spec("Quan Vu", "QUAN VU RIEL/Final + gx/QV_lod.prefab", "QUAN VU RIEL/Animation/QV_lod.fbx", "Armature|Armature|IDLE", "Armature|Armature|RUN", "Armature|Armature|ATK_1", "Armature|Armature|DEATH", "Armature|Armature|ULTI"),
            new Spec("Tao Thao", "TAO THAO/FULL + fx/tt_lod.prefab", "TAO THAO/Animation/tt_lod 1.fbx", "Armature|Armature|IDLE", "Armature|Armature|RUN", "Armature|Armature|atk 1", "Armature|Armature|DEATH", "Armature|Armature|ULT"),
            new Spec("Trieu Van", "TRIEU VAN/Body + fx/TRIEU VAN sLOD.prefab", "TRIEU VAN/Animation/trieu van some .fbx", "Armature|IDLE", "Armature|RUN", "Armature|ATK_01", "Armature|DEATH", "Armature|ULTI")
        };
        specs[1].alternateAttackSource = specs[1].sources[2];
        specs[1].alternateAttackName = "rig|rig|giacatluong_attack_02";
        specs[3].alternateAttackSource = specs[3].sources[2];
        specs[3].alternateAttackName = "rig|rig|lubu_attack_02";
        specs[4].alternateAttackSource = "LUU BI/Animation/lb_lod ATK 2.fbx";
        specs[4].alternateAttackName = "Scene";
        specs[6].alternateAttackSource = specs[6].sources[2];
        specs[6].alternateAttackName = "Armature|Armature|atk 2";
        return specs;
    }

    private static string Normalize(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");

    public static HeroAnimationDisplay.Hero CreateSingleBakeSource(string name, Scene scene)
    {
        var specs = Specs();
        int index = Array.FindIndex(specs, s => s.name == name);
        if (index < 0) throw new ArgumentException("Unknown hero: " + name);
        var spec = specs[index];
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + spec.model);
        if (!prefab) throw new InvalidOperationException("Missing Final prefab: " + spec.model);
        var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var clips = Enumerable.Range(0, 5).Select(i => ResolveClip(name, spec.sources[i], spec.clips[i])).ToArray();
        var alternate = string.IsNullOrEmpty(spec.alternateAttackName) ? null : ResolveClip(name, spec.alternateAttackSource, spec.alternateAttackName);
        var rig = FindAnimationRoot(model, alternate ? clips.Concat(new[] { alternate }).ToArray() : clips);
        var animator = rig.GetComponent<Animator>();
        if (!animator) animator = rig.gameObject.AddComponent<Animator>();
        var hero = new HeroAnimationDisplay.Hero { name = name, displayRoot = model.transform, animator = animator, clips = clips, alternateAttack = alternate };
        var fx = AttackFxPaths()[index].Select((path, i) => BindEffect(model, path, index == 1 && i % 2 == 1)).ToArray();
        hero.attackEffects = index == 1 ? fx.Take(2).ToArray() : fx.Take(1).ToArray();
        hero.alternateAttackEffects = index == 1 ? fx.Skip(2).ToArray() : fx.Skip(1).ToArray();
        hero.ultiEffects = new[] { BindEffect(model, UltiFxPaths()[index], false, true) };
        var holder = new GameObject("Single hero FX preset holder");
        SceneManager.MoveGameObjectToScene(holder, scene);
        var display = holder.AddComponent<HeroAnimationDisplay>();
        display.heroes = new[] { hero };
        if (System.IO.File.Exists(HeroEffectPresetStore.Latest)) HeroEffectPresetStore.Load(display, HeroEffectPresetStore.Latest);
        return hero;
    }

    private static string[][] AttackFxPaths() => new[]
    {
        new[] { "CHU DU/FX ATK/GameObject.prefab" },
        new[] { "GIA CAT LUONG/FX ATK/Projectile 2 electro.prefab", "GIA CAT LUONG/FX ATK/Magic hit (1).prefab", "GIA CAT LUONG/FX ATK/Projectile 2 electro (1).prefab", "GIA CAT LUONG/FX ATK/Projectile 25 orange explosion.prefab" },
        new[] { "Ha hau don/FX ATK/GameObject (2).prefab" },
        new[] { "LU BO/ATK FX/ATK 1.prefab", "LU BO/ATK FX/ATK 2.prefab" },
        new[] { "LUU BI/ATK/ATK 1.prefab", "LUU BI/ATK/ATK 2.prefab" },
        new[] { "QUAN VU RIEL/fx atk/GameObject.prefab" },
        new[] { "TAO THAO/FX ATK/ATK 1.prefab", "TAO THAO/FX ATK/ATK 2.prefab" },
        new[] { "TRIEU VAN/FX ATK/GameObject (1).prefab" }
    };

    private static string[] UltiFxPaths() => new[]
    {
        "CHU DU/FX ULTI/GameObject (1).prefab",
        "GIA CAT LUONG/FX ULTI/GameObject (2).prefab",
        "Ha hau don/FX ULTI/GameObject (3).prefab",
        "LU BO/ULTI FX/Effect28.prefab",
        "LUU BI/ULTI/GameObject (4).prefab",
        "QUAN VU RIEL/FX ULTI/ULTI QUAN VU.prefab",
        "TAO THAO/FX ULTI/GameObject (3).prefab",
        "TRIEU VAN/FX ULTI/Effect5.prefab"
    };

    private static HeroAnimationDisplay.AttackEffect BindEffect(GameObject model, string path, bool impact, bool ulti = false)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + path);
        var existing = model.GetComponentsInChildren<Transform>(true).Where(t => t != model.transform &&
            (PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) == prefab ||
             PrefabUtility.GetCorrespondingObjectFromSourceAtPath(t.gameObject, Root + path) == prefab)).ToArray();
        var pose = existing.Length > 0 ? existing[0] : prefab.transform;
        var effect = new HeroAnimationDisplay.AttackEffect
        {
            prefab = prefab,
            attachment = existing.Length > 0 ? pose.parent : model.transform,
            position = pose.localPosition,
            eulerAngles = pose.localEulerAngles,
            scale = pose.localScale,
            // Preserve the FX's authored particle start delays. The previous extra
            // 25%/65% clip delay shifted that entire authored sequence late.
            normalizedDelay = 0f,
            embeddedInstances = existing.Select(t => t.gameObject).ToArray()
        };
        // A standalone projectile's saved scene coordinates are not an attachment offset.
        if (!ulti && existing.Length == 0 && path.Contains("GIA CAT LUONG"))
            effect.position = new Vector3(0, 1, impact ? 2 : 0.5f);
        foreach (var instance in existing) instance.gameObject.SetActive(false);
        return effect;
    }

    private static AnimationClip ResolveClip(string hero, string source, string expected)
    {
        var available = AssetDatabase.LoadAllAssetsAtPath(Root + source).OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__")).ToArray();
        var matches = available.Where(c => Normalize(c.name) == Normalize(expected)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(hero + ": expected " + expected + " in " + source + ". Found: " + string.Join(", ", available.Select(c => c.name)));
        return matches[0];
    }

    private static void RestoreEffectEdits(HeroAnimationDisplay.AttackEffect[] source, HeroAnimationDisplay.AttackEffect[] target)
    {
        if (source == null || target == null) return;
        foreach (var effect in target)
        {
            var saved = source.FirstOrDefault(item => item.prefab == effect.prefab);
            if (saved == null) continue;
            effect.position = saved.position;
            effect.eulerAngles = saved.eulerAngles;
            effect.scale = saved.scale;
            effect.triggerFrame = saved.triggerFrame;
        }
    }

    private static Transform FindAnimationRoot(GameObject prefab, AnimationClip[] clips)
    {
        // Final prefabs may contain FX Animators, or no character Animator at all.
        // Match the actual animation binding paths against the model hierarchy.
        var paths = clips.SelectMany(AnimationUtility.GetCurveBindings)
            .Select(binding => binding.path).Where(path => !string.IsNullOrEmpty(path)).Distinct().ToArray();
        var transforms = prefab.GetComponentsInChildren<Transform>(true);
        var ranked = transforms.Select(t => new
        {
            transform = t,
            matches = paths.Count(path => t.Find(path) != null),
            animator = t.GetComponent<Animator>() != null
        }).OrderByDescending(entry => entry.matches).ThenByDescending(entry => entry.animator).ToArray();
        if (ranked.Length > 0 && ranked[0].matches > 0)
        {
            var best = ranked.Where(entry => entry.matches == ranked[0].matches).ToArray();
            if (best.Length == 1) return best[0].transform;
            var animated = best.Where(entry => entry.animator).ToArray();
            if (animated.Length == 1) return animated[0].transform;
            throw new InvalidOperationException("Ambiguous animation binding roots in " + prefab.name + ": " + string.Join(", ", best.Select(entry => AnimationUtility.CalculateTransformPath(entry.transform, prefab.transform))));
        }
        // Some imported clips do not expose transform curves. Locate the FBX root,
        // rather than selecting an Animator belonging to a particle effect.
        var modelRoots = transforms.Where(t =>
        {
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(t.gameObject);
            return source && source.transform.parent == null &&
                AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) is ModelImporter;
        }).ToArray();
        if (modelRoots.Length == 1) return modelRoots[0];
        throw new InvalidOperationException("Cannot identify the character animation root in " + prefab.name);
    }

    [MenuItem("Tools/Nhi Quoc/Create Animation Display in Current Scene")]
    public static void Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid() || !scene.isLoaded) throw new InvalidOperationException("Open a scene first.");
        var specs = Specs();
        var fxPaths = AttackFxPaths();
        var ultiPaths = UltiFxPaths();
        foreach (var path in fxPaths.SelectMany(paths => paths).Concat(ultiPaths))
            if (!AssetDatabase.LoadAssetAtPath<GameObject>(Root + path))
                throw new InvalidOperationException("Missing attack FX: " + path);
        var resolved = new List<AnimationClip[]>();
        var alternateAttacks = new List<AnimationClip>();
        // Resolve every mapping before making any scene changes. Never choose the first clip silently.
        foreach (var spec in specs)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + spec.model);
            if (!prefab) throw new InvalidOperationException("Missing final prefab: " + spec.model);
            if (prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
                throw new InvalidOperationException("Missing character mesh in " + spec.model);
            var clips = new AnimationClip[5];
            for (int i = 0; i < 5; i++)
            {
                clips[i] = ResolveClip(spec.name, spec.sources[i], spec.clips[i]);
            }
            var alternate = string.IsNullOrEmpty(spec.alternateAttackName) ? null :
                ResolveClip(spec.name, spec.alternateAttackSource, spec.alternateAttackName);
            FindAnimationRoot(prefab, alternate ? clips.Concat(new[] { alternate }).ToArray() : clips);
            resolved.Add(clips);
            alternateAttacks.Add(alternate);
        }
        Undo.IncrementCurrentGroup();
        int undo = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Create hero animation display");
        var previous = scene.GetRootGameObjects().Where(g => g.name == Group)
            .Select(g => g.GetComponent<HeroAnimationDisplay>()).Where(d => d != null)
            .SelectMany(d => d.heroes).GroupBy(h => h.name).ToDictionary(g => g.Key, g => g.First());
        foreach (var old in scene.GetRootGameObjects().Where(g => g.name == Group)) Undo.DestroyObjectImmediate(old);
        var root = new GameObject(Group);
        SceneManager.MoveGameObjectToScene(root, scene);
        Undo.RegisterCreatedObjectUndo(root, "Create display");
        var display = root.AddComponent<HeroAnimationDisplay>();
        display.heroes = new HeroAnimationDisplay.Hero[specs.Length];
        float cursor = 0;
        float previousGap = MinimumHeroGap;
        float largestGap = MinimumHeroGap;
        Bounds combined = default;
        for (int i = 0; i < specs.Length; i++)
        {
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + specs[i].model), root.transform);
            model.name = specs[i].name;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0, 180, 0);
            var animationRoot = FindAnimationRoot(model, alternateAttacks[i] ? resolved[i].Concat(new[] { alternateAttacks[i] }).ToArray() : resolved[i]);
            var animator = animationRoot.GetComponent<Animator>();
            if (!animator) animator = animationRoot.gameObject.AddComponent<Animator>();
            for (var parent = animationRoot; parent != root.transform; parent = parent.parent)
                parent.gameObject.SetActive(true);
            animator.enabled = true;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            // FX bounds can be much larger than the character; space the row by its body.
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("No renderers: " + specs[i].name);
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            float gap = Mathf.Max(MinimumHeroGap, bounds.size.y * GapInBodyHeights, bounds.size.x * 2f);
            if (i > 0) cursor += Mathf.Max(previousGap, gap);
            model.transform.position += new Vector3(cursor - bounds.min.x, -bounds.min.y, -bounds.center.z);
            bounds.center += model.transform.position;
            if (i == 0) combined = bounds; else combined.Encapsulate(bounds);
            cursor = bounds.max.x;
            previousGap = gap;
            largestGap = Mathf.Max(largestGap, gap);
            display.heroes[i] = new HeroAnimationDisplay.Hero { name = specs[i].name, animator = animator, displayRoot = model.transform, clips = resolved[i], alternateAttack = alternateAttacks[i] };
            var fx = fxPaths[i].Select((path, index) => BindEffect(model, path, i == 1 && index % 2 == 1)).ToArray();
            display.heroes[i].attackEffects = i == 1 ? fx.Take(2).ToArray() : fx.Take(1).ToArray();
            display.heroes[i].alternateAttackEffects = i == 1 ? fx.Skip(2).ToArray() : fx.Skip(1).ToArray();
            display.heroes[i].ultiEffects = new[] { BindEffect(model, ultiPaths[i], false, true) };
            if (previous.TryGetValue(specs[i].name, out var savedHero))
            {
                RestoreEffectEdits(savedHero.attackEffects, display.heroes[i].attackEffects);
                RestoreEffectEdits(savedHero.alternateAttackEffects, display.heroes[i].alternateAttackEffects);
                RestoreEffectEdits(savedHero.ultiEffects, display.heroes[i].ultiEffects);
            }
            Debug.Log(specs[i].name + " [Idle, Run, Attack, Death, Ulti]: " + string.Join(", ", resolved[i].Select(c => c.name)), model);
            if (alternateAttacks[i]) Debug.Log(specs[i].name + " Attack 2: " + alternateAttacks[i].name, model);
        }
        // Include room around both end characters so their ultimate effects remain visible.
        combined.Expand(new Vector3(largestGap * 2f, largestGap, largestGap * 2f));
        var cameraObject = new GameObject("Animation Display Camera", typeof(Camera));
        cameraObject.transform.SetParent(root.transform);
        var camera = cameraObject.GetComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = Mathf.Max(combined.size.y * 0.8f, combined.size.x / (2f * (16f / 9f)) * 1.15f);
        camera.depth = 100;
        camera.farClipPlane = Mathf.Max(1000, combined.size.magnitude * 10);
        camera.transform.position = combined.center + new Vector3(0, combined.size.y * 0.3f, -Mathf.Max(20, combined.size.magnitude));
        camera.transform.LookAt(combined.center);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.14f, 0.18f);
        if (!Camera.main) cameraObject.tag = "MainCamera";
        var light = new GameObject("Display Light", typeof(Light));
        light.transform.SetParent(root.transform);
        light.transform.rotation = Quaternion.Euler(45, -30, 0);
        light.GetComponent<Light>().type = LightType.Directional;
        light.GetComponent<Light>().intensity = 1.2f;
        if (previous.Count == 0 && System.IO.File.Exists(HeroEffectPresetStore.Latest))
            HeroEffectPresetStore.Load(display, HeroEffectPresetStore.Latest);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = root;
        Undo.CollapseUndoOperations(undo);
        Debug.Log("Animation display ready. Enter Play mode, focus Game view; keys 1-5 and D. Save the scene to keep it.", root);
    }

    [InitializeOnLoadMethod]
    private static void InstallOnce()
    {
        EditorApplication.delayCall += () =>
        {
            const string key = "NhiQuoc.AnimationDisplay.SynchronizedEffects.v7";
            if (SessionState.GetBool(key, false) || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SceneManager.GetActiveScene().path != "Assets/Data/Characters/Scenes/Countdown Test.unity") return;
            SessionState.SetBool(key, true);
            try { Create(); }
            catch (Exception exception) { Debug.LogException(exception); }
        };
    }
}
