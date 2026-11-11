using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NhiQuoc.Tools;
using RecoilPathfindingEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class HeroVatBaker
{
    public const string Output = "Assets/_Game/HeroVAT";
    private const string Request = "Assets/Editor/HeroVatBake.request";
    private const float Height = 6f;
    private const int Fps = 30;
    private static bool running;
    private static double nextCheck;
    static HeroVatBaker() { EditorApplication.update += Check; }
    private static void Check()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess() || running || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (EditorApplication.timeSinceStartup < nextCheck) return;
        nextCheck = EditorApplication.timeSinceStartup + 3;
        if (!File.Exists(Request) || string.IsNullOrWhiteSpace(File.ReadAllText(Request))) return;
        string request = File.ReadAllText(Request).Trim();
        File.WriteAllText(Request, "");
        if (request.StartsWith("hero:", StringComparison.Ordinal)) BakeOne(request.Substring(5).Trim());
        else Debug.LogWarning("Ignored old bulk hero VAT request. Select one hero in Tools/Nhi Quoc/VAT/Bake One Hero.");
    }
    // Retained for script callers; the normal UI intentionally bakes one hero only.
    public static void BakeAll()
    {
        if (running || EditorApplication.isPlayingOrWillChangePlaymode) return;
        var display = UnityEngine.Object.FindFirstObjectByType<HeroAnimationDisplay>();
        if (!display) { HeroAnimationDisplaySetup.Create(); display = UnityEngine.Object.FindFirstObjectByType<HeroAnimationDisplay>(); }
        if (!display || display.heroes.Length != 8) throw new InvalidOperationException("Create the 8-hero Final animation display first.");
        HeroEffectPresetStore.Save(display, true);
        running = true;
        var report = new List<string> { "Hero VAT, body height 6, 30 FPS. Idle/Run/Attack1/Death/Ulti/Attack2.", "FX remain particle prefabs; animated attachment transforms are baked alongside VAT." };
        Directory.CreateDirectory(Output); AssetDatabase.Refresh();
        try
        {
            foreach (var hero in display.heroes)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Bake hero VAT", hero.name, Array.IndexOf(display.heroes, hero) / 8f))
                    throw new OperationCanceledException("Bake cancelled; completed hero assets retained.");
                Bake(hero, report);
            }
            CreatePreview(display.heroes.Select(h => h.name).ToArray());
            report.Add("SUCCESS: 8/8 heroes baked.");
            Debug.Log("Hero VAT complete: " + Output);
        }
        catch (Exception error) { report.Add("FAILED: " + error); Debug.LogException(error); }
        finally
        {
            EditorUtility.ClearProgressBar();
            File.WriteAllLines(Output + "/BakeReport.txt", report);
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); running = false;
        }
    }

    public static readonly string[] HeroNames = { "Chu Du", "Gia Cat Luong", "Ha Hau Don", "Lu Bo", "Luu Bi", "Quan Vu", "Tao Thao", "Trieu Van" };

    public static void BakeOne(string name, float targetHeight = Height)
    {
        if (running || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!HeroNames.Contains(name)) throw new ArgumentException("Unknown hero: " + name);
        running = true;
        var report = new List<string> { name + ": single hero VAT; target body height " + targetHeight + "; 30 FPS." };
        var sourceScene = EditorSceneManager.NewPreviewScene();
        try
        {
            Directory.CreateDirectory(Output); AssetDatabase.Refresh();
            var display = UnityEngine.Object.FindFirstObjectByType<HeroAnimationDisplay>();
            var hero = display ? display.heroes.FirstOrDefault(h => h.name == name) : null;
            if (hero == null) hero = HeroAnimationDisplaySetup.CreateSingleBakeSource(name, sourceScene);
            else HeroEffectPresetStore.Save(display, true);
            Bake(hero, report, targetHeight);
            report.Add("SUCCESS: " + name + ", Idle body height " + targetHeight + ".");
            Debug.Log("VAT baked: " + name + " -> " + Output + "/" + name);
        }
        catch (Exception error) { report.Add("FAILED: " + error); Debug.LogException(error); }
        finally
        {
            EditorSceneManager.ClosePreviewScene(sourceScene);
            EditorUtility.ClearProgressBar();
            Directory.CreateDirectory(Output + "/" + name);
            File.WriteAllLines(Output + "/" + name + "/BakeReport.txt", report);
            AssetDatabase.SaveAssets();
            // Free transient texture pixel buffers before another manual bake.
            GC.Collect();
            EditorUtility.UnloadUnusedAssetsImmediate();
            running = false;
        }
    }

    [MenuItem("Tools/Nhi Quoc/VAT/Create VAT Preview Scene")]
    public static void CreatePreviewFromAssets()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        var names = HeroNames.Where(name => AssetDatabase.LoadAssetAtPath<GameObject>(Output + "/" + name + "/" + name + " VAT.prefab")).ToArray();
        if (names.Length == 0) throw new InvalidOperationException("Bake at least one hero first.");
        CreatePreview(names);
        OpenPreview(Output + "/Hero VAT Preview.unity", false);
    }

    public static void PreviewOne(string name)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        HeroVatMaterialRepair.RepairHero(name);
        string path = Output + "/" + name + "/" + name + " VAT Preview.unity";
        CreatePreview(new[] { name }, path);
        OpenPreview(path, true);
    }

    private static void OpenPreview(string path, bool play)
    {
        // Preserve the user's current scene before switching away from it.
        if (!EditorSceneManager.SaveOpenScenes()) return;
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        Debug.Log("Opened VAT preview: " + path + ". Keys 1-5: Idle/Run/Attack/Death/Ulti; D: rotate.");
        if (play) EditorApplication.isPlaying = true;
    }

    private static void CreatePreview(string[] names, string scenePath = null)
    {
        var prefabs = names.Select(name => AssetDatabase.LoadAssetAtPath<GameObject>(Output + "/" + name + "/" + name + " VAT.prefab")).ToArray();
        if (prefabs.Any(p => !p)) throw new InvalidOperationException("The selected hero has not been baked yet.");
        var original = SceneManager.GetActiveScene();
        string path = scenePath ?? Output + "/Hero VAT Preview.unity";
        if (SceneManager.GetSceneByPath(path).isLoaded) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            for (int i = 0; i < prefabs.Length; i++)
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[i], scene);
                instance.transform.position = new Vector3((i - (prefabs.Length - 1) * .5f) * 12, 0, 0);
                instance.transform.rotation = Quaternion.Euler(0, 180, 0);
                instance.GetComponent<HeroVatPlayer>().keyboardPreview = true;
            }
            var camera = new GameObject("VAT Preview Camera", typeof(Camera), typeof(AudioListener));
            SceneManager.MoveGameObjectToScene(camera, scene);
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 10, -30);
            camera.transform.LookAt(new Vector3(0, 1.5f, 0));
            var view = camera.GetComponent<Camera>();
            view.orthographic = true;
            view.orthographicSize = Mathf.Max(3f, ((prefabs.Length - 1) * 12f + 8f) / (2f * (16f / 9f)) * 1.15f);
            view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = new Color(.12f, .14f, .18f);
            var light = new GameObject("Light", typeof(Light));
            SceneManager.MoveGameObjectToScene(light, scene);
            light.GetComponent<Light>().type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(45, -30, 0);
            EditorSceneManager.SaveScene(scene, path);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
            if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
        }
    }
    private static T Save<T>(T value, string path) where T : UnityEngine.Object
    {
        value.name = Path.GetFileNameWithoutExtension(path);
        var old = AssetDatabase.LoadAssetAtPath<T>(path);
        if (!old) { AssetDatabase.CreateAsset(value, path); return value; }
        EditorUtility.CopySerialized(value, old); EditorUtility.SetDirty(old);
        UnityEngine.Object.DestroyImmediate(value); return old;
    }

    public static void FixAssetNames(string hero)
    {
        string folder = Output + "/" + hero;
        if (!AssetDatabase.IsValidFolder(folder)) return;
        int count = 0;
        foreach (string guid in AssetDatabase.FindAssets("", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            string expected = Path.GetFileNameWithoutExtension(path);
            if (asset && asset.name != expected)
            {
                asset.name = expected;
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
                count++;
            }
        }
        EditorUtility.UnloadUnusedAssetsImmediate();
        Debug.Log(hero + ": fixed " + count + " VAT asset names. No animation rebake required.");
    }
    private static void Bake(HeroAnimationDisplay.Hero hero, List<string> report, float targetHeight = Height)
    {
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject clone = null, result = null;
        try
        {
            if (!hero.displayRoot || !hero.animator) throw new InvalidOperationException("Missing Final rig: " + hero.name);
            clone = UnityEngine.Object.Instantiate(hero.displayRoot.gameObject);
            SceneManager.MoveGameObjectToScene(clone, scene);
            clone.transform.position = Vector3.zero; clone.transform.rotation = Quaternion.identity;
            foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true)) if (script) script.enabled = false;
            foreach (var animator in clone.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
            foreach (var particle in clone.GetComponentsInChildren<ParticleSystem>(true)) particle.gameObject.SetActive(false);
            foreach (var fx in hero.attackEffects.Concat(hero.alternateAttackEffects).Concat(hero.ultiEffects))
                foreach (var embedded in fx.embeddedInstances)
                    if (embedded)
                    {
                        var target = clone.transform.Find(AnimationUtility.CalculateTransformPath(embedded.transform, hero.displayRoot));
                        if (target) target.gameObject.SetActive(false);
                    }
            string rigPath = AnimationUtility.CalculateTransformPath(hero.animator.transform, hero.displayRoot);
            var rig = string.IsNullOrEmpty(rigPath) ? clone.transform : clone.transform.Find(rigPath);
            var transforms = clone.GetComponentsInChildren<Transform>(true);
            var positions = transforms.Select(t => t.localPosition).ToArray();
            var rotations = transforms.Select(t => t.localRotation).ToArray();
            var scales = transforms.Select(t => t.localScale).ToArray();
            void Sample(AnimationClip clip, float time)
            {
                for (int t = 0; t < transforms.Length; t++)
                { transforms[t].localPosition = positions[t]; transforms[t].localRotation = rotations[t]; transforms[t].localScale = scales[t]; }
                clip.SampleAnimation(rig.gameObject, time);
                int owner = Array.IndexOf(transforms, rig);
                rig.localPosition = positions[owner]; rig.localRotation = rotations[owner]; rig.localScale = scales[owner];
            }
            var clips = hero.clips.ToList();
            if (hero.alternateAttack) clips.Add(hero.alternateAttack);
            if (clips.Count < 5 || clips.Any(c => !c || c.length <= 0)) throw new InvalidOperationException("Missing animation: " + hero.name);
            Sample(clips[0], 0);
            var excluded = new HashSet<Renderer>();
            foreach (var lod in clone.GetComponentsInChildren<LODGroup>(true))
            {
                var levels = lod.GetLODs();
                foreach (var level in levels.Skip(1)) foreach (var r in level.renderers) if (r) excluded.Add(r);
                if (levels.Length > 0) foreach (var r in levels[0].renderers) excluded.Remove(r);
            }
            var renderers = clone.GetComponentsInChildren<Renderer>(true).Where(r => !excluded.Contains(r) && r.enabled && r.gameObject.activeInHierarchy &&
                (r is SkinnedMeshRenderer || r is MeshRenderer && r.GetComponent<MeshFilter>()))
                .GroupBy(r => r.gameObject).Select(g => g.FirstOrDefault(r => r is SkinnedMeshRenderer) ?? g.First()).ToArray();
            // Some Final FBXs contain every body LOD without a configured LODGroup.
            // In that case Unity reports all of them active. Keep the most detailed
            // mesh for each explicitly named LOD family, while preserving weapons.
            string MeshName(Renderer r) => (r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>().sharedMesh)?.name ?? r.name;
            string Family(Renderer r) => Regex.Replace(MeshName(r), "_LOD[0-9]+$", "", RegexOptions.IgnoreCase);
            var retained = new List<Renderer>();
            foreach (var family in renderers.GroupBy(Family))
            {
                var members = family.ToArray();
                bool hasNamedLod = members.Any(r => Regex.IsMatch(MeshName(r), "_LOD[0-9]+$", RegexOptions.IgnoreCase));
                if (!hasNamedLod || members.Length == 1) { retained.AddRange(members); continue; }
                int Level(Renderer r)
                {
                    var match = Regex.Match(MeshName(r), "_LOD([0-9]+)$", RegexOptions.IgnoreCase);
                    return match.Success ? int.Parse(match.Groups[1].Value) : -1;
                }
                var highest = members.OrderBy(Level).First();
                retained.Add(highest);
                report.Add(hero.name + ": LOD family " + family.Key + ", using " + MeshName(highest) + "; skipped " + (members.Length - 1) + " lower/duplicate levels.");
            }
            renderers = retained.ToArray();
            var meshes = renderers.Select(r => r is SkinnedMeshRenderer s ? s.sharedMesh : r.GetComponent<MeshFilter>().sharedMesh).ToArray();
            if (renderers.Length == 0 || meshes.Any(m => !m)) throw new InvalidOperationException("Missing body mesh: " + hero.name);
            var skins = renderers.Select(r => r is SkinnedMeshRenderer s ? new RecoilVatBaker.SkinSource(s) : null).ToArray();
            Vector3[] Vertices(int p) => skins[p] != null ? skins[p].Evaluate(clone.transform) : meshes[p].vertices.Select((clone.transform.worldToLocalMatrix * renderers[p].transform.localToWorldMatrix).MultiplyPoint3x4).ToArray();
            // Imported character names vary (body/face/hair). The main character
            // surface is the largest skinned mesh, not the union with weapons/FX.
            int bodyIndex = Enumerable.Range(0, meshes.Length).Where(p => skins[p] != null)
                .OrderByDescending(p => meshes[p].vertexCount).DefaultIfEmpty(0).First();
            var measuredBody = HeroVatSizeRepair.MeasureBody(Vertices(bodyIndex));
            Bounds idle = measuredBody.bounds;
            if (idle.size.y < .00001f) throw new InvalidOperationException("Invalid Idle height: " + hero.name);
            float scale = targetHeight / idle.size.y;
            Vector3 pivot = measuredBody.pivot;
            var metadata = new HeroVatPlayer.Clip[clips.Count]; int totalFrames = 0;
            var preparedFx = new Dictionary<GameObject, GameObject>();
            var names = new[] { "Idle", "Run", "Attack1", "Death", "Ulti", "Attack2" };
            var effects = new List<HeroAnimationDisplay.AttackEffect[]>();
            for (int c = 0; c < clips.Count; c++)
            {
                var configs = c == 2 ? hero.attackEffects : c == 4 ? hero.ultiEffects : c == 5 ? hero.alternateAttackEffects : Array.Empty<HeroAnimationDisplay.AttackEffect>();
                effects.Add(configs);
                metadata[c] = new HeroVatPlayer.Clip { name = names[c], startFrame = totalFrames,
                    frameCount = Mathf.Max(2, Mathf.CeilToInt(clips[c].length * Fps) + (c < 2 ? 0 : 1)), duration = clips[c].length,
                    sourceFps = clips[c].frameRate, loop = c < 2,
                    casterCentered = c == 4 && (hero.name == "Chu Du" || hero.name == "Ha Hau Don" || hero.name == "Lu Bo" || hero.name == "Luu Bi" || hero.name == "Quan Vu" || hero.name == "Tao Thao" || hero.name == "Trieu Van"),
                    targetCentered = c == 2 && hero.name == "Gia Cat Luong",
                    straightProjectile = c == 5 && hero.name == "Gia Cat Luong" };
                totalFrames += metadata[c].frameCount;
                metadata[c].effects = configs.Select(fx => new HeroVatPlayer.Effect { prefab = HeroFxParticleClone.Prepare(fx.prefab, preparedFx), triggerFrame = fx.triggerFrame,
                    position = c == 2 && hero.name == "Gia Cat Luong" ? new Vector3(fx.position.x, 0, fx.position.z) : fx.position,
                    rotation = fx.eulerAngles, scale = fx.scale,
                    socketPositions = new Vector3[metadata[c].frameCount], socketRotations = new Quaternion[metadata[c].frameCount], socketScales = new Vector3[metadata[c].frameCount] }).ToArray();
            }
            var shader = Shader.Find("Recoil/VAT Preview");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("Recoil VAT shader unavailable.");
            string folder = Output + "/" + hero.name;
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            result = new GameObject(hero.name + " VAT"); SceneManager.MoveGameObjectToScene(result, scene);
            result.transform.localScale = Vector3.one * scale;
            var player = result.AddComponent<HeroVatPlayer>(); player.clips = metadata;
            player.referenceBodyHeight = idle.size.y; player.targetBodyHeight = targetHeight;
            var parts = new List<Renderer>();
            for (int p = 0; p < renderers.Length; p++)
            {
                int width = Mathf.Min(4096, Mathf.NextPowerOfTwo(meshes[p].vertexCount));
                int rows = Mathf.CeilToInt(meshes[p].vertexCount / (float)width);
                int textureHeight = rows * totalFrames;
                if (textureHeight > SystemInfo.maxTextureSize || width > SystemInfo.maxTextureSize) throw new InvalidOperationException("VAT texture too large: " + hero.name + "/" + renderers[p].name);
                // Half floats halve both the staging buffer and persistent VAT texture.
                var pixels = new ushort[checked(width * textureHeight * 4)];
                var outputMesh = UnityEngine.Object.Instantiate(meshes[p]);
                Bounds animatedBounds = default; bool initialized = false;
                for (int c = 0; c < clips.Count; c++) for (int f = 0; f < metadata[c].frameCount; f++)
                {
                    if (f % 15 == 0 && EditorUtility.DisplayCancelableProgressBar("Bake " + hero.name, renderers[p].name + " / " + names[c] + " / frame " + f,
                        (p + (metadata[c].startFrame + f) / (float)totalFrames) / renderers.Length))
                        throw new OperationCanceledException("Single hero VAT bake cancelled.");
                    float time = metadata[c].duration * f / (metadata[c].loop ? metadata[c].frameCount : metadata[c].frameCount - 1);
                    Sample(clips[c], time);
                    var vertices = Vertices(p);
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        var point = vertices[v] - pivot;
                        int pixel = ((metadata[c].startFrame + f) * rows * width + v) * 4;
                        pixels[pixel] = Mathf.FloatToHalf(point.x);
                        pixels[pixel + 1] = Mathf.FloatToHalf(point.y);
                        pixels[pixel + 2] = Mathf.FloatToHalf(point.z);
                        pixels[pixel + 3] = Mathf.FloatToHalf(1);
                        vertices[v] = point;
                        if (!initialized) { animatedBounds = new Bounds(point, Vector3.zero); initialized = true; } else animatedBounds.Encapsulate(point);
                    }
                    if (c == 0 && f == 0) outputMesh.vertices = vertices;
                    if (p == 0) for (int e = 0; e < effects[c].Length; e++)
                    {
                        var fx = effects[c][e];
                        string path = AnimationUtility.CalculateTransformPath(fx.attachment, hero.displayRoot);
                        var socket = string.IsNullOrEmpty(path) ? clone.transform : clone.transform.Find(path);
                        if (!socket) throw new InvalidOperationException("Missing FX socket: " + path);
                        var baked = metadata[c].effects[e];
                        baked.socketPositions[f] = clone.transform.InverseTransformPoint(socket.position) - pivot;
                        baked.socketRotations[f] = Quaternion.Inverse(clone.transform.rotation) * socket.rotation;
                        var rootScale = clone.transform.lossyScale; var socketScale = socket.lossyScale;
                        baked.socketScales[f] = new Vector3(socketScale.x / rootScale.x, socketScale.y / rootScale.y, socketScale.z / rootScale.z);
                    }
                }
                animatedBounds.Expand(.02f); outputMesh.bounds = animatedBounds;
                var texture = new Texture2D(width, textureHeight, TextureFormat.RGBAHalf, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                texture.SetPixelData(pixels, 0); texture.Apply(false, true);
                texture = Save(texture, folder + "/part" + p + "_positions.asset");
                outputMesh = Save(outputMesh, folder + "/part" + p + "_mesh.asset");
                var child = new GameObject(renderers[p].name); child.transform.SetParent(result.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = outputMesh;
                var renderer = child.AddComponent<MeshRenderer>();
                var materials = new Material[outputMesh.subMeshCount];
                for (int m = 0; m < materials.Length; m++)
                {
                    var material = new Material(shader) { enableInstancing = true };
                    var original = renderers[p].sharedMaterials.ElementAtOrDefault(m);
                    if (original)
                    {
                        string map = original.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                        if (original.HasProperty(map)) { material.SetTexture("_BaseMap", original.GetTexture(map)); material.SetTextureScale("_BaseMap", original.GetTextureScale(map)); material.SetTextureOffset("_BaseMap", original.GetTextureOffset(map)); }
                        if (original.HasProperty("_BaseColor")) material.SetColor("_BaseColor", original.GetColor("_BaseColor"));
                        if (original.HasProperty("_Cutoff") && original.IsKeywordEnabled("_ALPHATEST_ON")) material.SetFloat("_Cutoff", original.GetFloat("_Cutoff"));
                    }
                    HeroVatMaterialRepair.CopySurface(renderers[p], clone.GetComponentsInChildren<Renderer>(true), m, material, hero.name);
                    material.SetTexture("_VatPositions", texture); material.SetVector("_VatSize", new Vector4(width, rows, 0, 0));
                    material.SetVector("_VatClip", new Vector4(0, metadata[0].frameCount, metadata[0].duration, 1));
                    materials[m] = Save(material, folder + "/part" + p + "_material" + m + ".mat");
                }
                renderer.sharedMaterials = materials; parts.Add(renderer);
                report.Add(hero.name + "/" + renderer.name + ": " + meshes[p].vertexCount + " vertices; " + width + "x" + textureHeight + " RGBAHalf; " + ((long)width * textureHeight * 8 / (1024f * 1024f)).ToString("F1") + " MiB");
            }
            player.parts = parts.ToArray();
            PrefabUtility.SaveAsPrefabAsset(result, folder + "/" + hero.name + " VAT.prefab");
            report.Add(hero.name + ": height=" + (idle.size.y * scale) + "; scale=" + scale + "; " + clips.Count + " clips; " + totalFrames + " frames; FX=" + metadata.Sum(c => c.effects.Length));
        }
        finally
        {
            if (result) UnityEngine.Object.DestroyImmediate(result);
            if (clone) UnityEngine.Object.DestroyImmediate(clone);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
