using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using NhiQuoc.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.VFX;

[InitializeOnLoad]
public sealed class HeroAttackVfxGraphBaker : EditorWindow
{
    private const string Output = "Assets/_Game/HeroVFXGraph";
    private const int Fps = 30;
    private int hero;
    private bool includeUlti = true;
    private int frames = 180;
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static double nextCheck;
    private const string Request = "Assets/Editor/HeroVfxGraphBake.request";
    static HeroAttackVfxGraphBaker() { EditorApplication.update += CheckRequest; }
    private static void CheckRequest()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < nextCheck || VfxType("VFXAssetEditorUtility") == null) return;
        nextCheck = EditorApplication.timeSinceStartup + 3;
        if (!File.Exists(Request)) return;
        string name = File.ReadAllText(Request).Trim();
        if (!HeroVatBaker.HeroNames.Contains(name)) return;
        File.WriteAllText(Request, "");
        Bake(name, true, 180);
    }

    [MenuItem("Tools/Nhi Quoc/VFX Graph/Bake Attack FX (One Hero)")]
    public static void Open() => GetWindow<HeroAttackVfxGraphBaker>("Bake Attack VFX Graph");
    private void OnGUI()
    {
        hero = EditorGUILayout.Popup("Hero", hero, HeroVatBaker.HeroNames);
        includeUlti = EditorGUILayout.Toggle("Include Ulti", includeUlti);
        frames = EditorGUILayout.IntSlider("Capture frames (30 FPS)", frames, 30, 600);
        EditorGUILayout.HelpBox("Bakes particle animation caches and native GPU graphs, one hero at a time. Mesh particles and view-facing billboards are supported. Trails, flipbooks and custom shaders remain on the original Particle System and are listed in the report. Original FX and frame/transform presets are preserved.", MessageType.Info);
        bool ready = VfxType("VFXAssetEditorUtility") != null;
        if (ready) EditorGUILayout.HelpBox("Visual Effect Graph API ready. Select a hero and bake when Unity finishes compilation/import.", MessageType.None);
        else if (VfxType("VFXGraph") != null)
            EditorGUILayout.HelpBox("VFX Graph is loaded, but the asset creation API could not be resolved. This is an API compatibility error, not a missing package.", MessageType.Error);
        else EditorGUILayout.HelpBox("Visual Effect Graph editor assembly is not loaded yet. Wait for package import and compilation to finish.", MessageType.Warning);
        if (GUILayout.Button("Save and restart after package import (1 worker)")) RestartAfterPackageImport();
        using (new EditorGUI.DisabledScope(!ready || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("Bake VFX Graph: " + HeroVatBaker.HeroNames[hero])) Bake(HeroVatBaker.HeroNames[hero], includeUlti, frames);
    }

    [MenuItem("Tools/Nhi Quoc/VFX Graph/Save and Restart after Package Import (1 Worker)")]
    public static void RestartAfterPackageImport()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
        if (!EditorSceneManager.SaveOpenScenes()) return;
        AssetDatabase.SaveAssets();
        EditorApplication.OpenProject(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "-force-d3d11", "-desiredWorkerCount", "1");
    }

    private static Type VfxType(string name)
    {
        // Unity 17.3's asset utility lives directly in UnityEditor, whereas
        // graph nodes and resource extensions live under UnityEditor.VFX.
        string fullName = name == "VFXAssetEditorUtility" ? "UnityEditor.VisualEffectAssetEditorUtility" : "UnityEditor.VFX." + name;
        var type = Type.GetType(fullName + ", Unity.VisualEffectGraph.Editor");
        if (type != null) return type;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name != "Unity.VisualEffectGraph.Editor") continue;
            return assembly.GetType(fullName, false);
        }
        return null;
    }
    private static object Call(object target, string name, params object[] args)
    {
        var type = target as Type ?? target.GetType();
        var method = type.GetMethods(Flags).FirstOrDefault(m => m.Name == name && !m.IsGenericMethod &&
            m.GetParameters().Length >= args.Length && m.GetParameters().Skip(args.Length).All(p => p.IsOptional) &&
            m.GetParameters().Take(args.Length).Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(b => b));
        if (method == null) throw new MissingMethodException(type.FullName, name);
        var parameters = method.GetParameters();
        var values = new object[parameters.Length];
        for (int i = 0; i < values.Length; i++) values[i] = i < args.Length ? args[i] : parameters[i].DefaultValue;
        return method.Invoke(target is Type ? null : target, values);
    }
    private static object Get(object target, string name)
    {
        var property = target.GetType().GetProperty(name, Flags);
        return property != null ? property.GetValue(target) : target.GetType().GetField(name, Flags).GetValue(target);
    }
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Flags).SetValue(target, value);
    private static ScriptableObject Node(string type) => ScriptableObject.CreateInstance(VfxType(type) ?? throw new InvalidOperationException("Missing VFX node: " + type));
    private static void Setting(object target, string name, object value)
    {
        if (value is string text)
        {
            var setting = Call(target, "GetSetting", name);
            object current = Get(setting, "value");
            if (current != null && current.GetType().IsEnum) value = Enum.Parse(current.GetType(), text);
        }
        Call(target, "SetSettingValue", name, value);
    }
    private static void Input(object node, string name, object value)
    {
        var slots = ((IEnumerable)Get(node, "inputSlots")).Cast<object>().ToArray();
        // CustomHLSL.CreateProperty prefixes function arguments with "_".
        // Other VFX nodes expose their property names unchanged.
        foreach (string candidate in new[] { name, "_" + name })
            foreach (var slot in slots)
                if ((string)Get(slot, "name") == candidate) { Set(slot, "value", value); return; }
        throw new InvalidOperationException(node.GetType().Name + " has no input " + name +
            ". Available inputs: " + string.Join(", ", slots.Select(slot => (string)Get(slot, "name"))));
    }

    private static void SetHlsl(object node, string source)
    {
        // sourceCode is the package's editing entrypoint; it invalidates parsed
        // function properties. Resync after the block has a parent graph.
        Set(node, "sourceCode", source);
        Call(node, "ResyncSlots", true);
    }
    private static T Save<T>(T asset, string path) where T : UnityEngine.Object
    {
        asset.name = Path.GetFileNameWithoutExtension(path);
        var previous = AssetDatabase.LoadAssetAtPath<T>(path);
        if (!previous) { AssetDatabase.CreateAsset(asset, path); return asset; }
        EditorUtility.CopySerialized(asset, previous); EditorUtility.SetDirty(previous);
        DestroyImmediate(asset); return previous;
    }

    private static void Bake(string name, bool ulti, int captureFrames)
    {
        string directory = Output + "/" + name;
        Directory.CreateDirectory(directory); AssetDatabase.Refresh();
        var report = new List<string> { name + ": particle cache -> Visual Effect Graph, 30 FPS, " + captureFrames + " frames.",
            "Native graph outputs use unlit alpha/additive shading. Particle motion/color/size are captured; arbitrary original shader logic is not recreated.",
            "Unsupported renderers remain Particle Systems; this may produce a hybrid effect, not a full conversion." };
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject vat = null;
        try
        {
            var source = HeroAnimationDisplaySetup.CreateSingleBakeSource(name, scene);
            var configurations = source.attackEffects.Concat(source.alternateAttackEffects).Concat(ulti ? source.ultiEffects : Array.Empty<HeroAnimationDisplay.AttackEffect>()).ToArray();
            var prepared = new Dictionary<GameObject, GameObject>();
            var converted = new Dictionary<GameObject, GameObject>();
            foreach (var fx in configurations)
            {
                if (!fx.prefab || converted.ContainsKey(fx.prefab)) continue;
                var clean = HeroVatFxRepair.Prepare(fx.prefab, prepared);
                converted[fx.prefab] = BakeEffect(clean, fx.prefab.name, directory, captureFrames, report);
            }
            // Only replace FX references. Meshes, VAT textures, transforms, frames
            // and baked socket animation data are not regenerated.
            string vatPath = HeroVatBaker.Output + "/" + name + "/" + name + " VAT.prefab";
            if (File.Exists(vatPath))
            {
                vat = PrefabUtility.LoadPrefabContents(vatPath);
                var player = vat.GetComponent<HeroVatPlayer>();
                foreach (int state in new[] { 2, 5, 4 })
                {
                    if (state >= player.clips.Length || state == 4 && !ulti) continue;
                    var originals = state == 2 ? source.attackEffects : state == 5 ? source.alternateAttackEffects : source.ultiEffects;
                    for (int i = 0; i < player.clips[state].effects.Length && i < originals.Length; i++)
                        if (converted.TryGetValue(originals[i].prefab, out var replacement)) player.clips[state].effects[i].prefab = replacement;
                }
                PrefabUtility.SaveAsPrefabAsset(vat, vatPath);
                report.Add("Updated baked hero FX references. No VAT rebake required.");
            }
            report.Add("COMPLETED. Review fallback entries and compare appearance in VAT preview.");
        }
        catch (Exception exception) { report.Add("FAILED: " + exception); Debug.LogException(exception); }
        finally
        {
            if (vat) PrefabUtility.UnloadPrefabContents(vat);
            EditorSceneManager.ClosePreviewScene(scene);
            EditorUtility.ClearProgressBar();
            File.WriteAllLines(directory + "/ConversionReport.txt", report);
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            Debug.Log("VFX Graph bake report: " + directory + "/ConversionReport.txt\n" + string.Join("\n", report));
        }
    }

    private struct Sample
    {
        public int frame;
        public uint id;
        public Vector3 position, size, angle;
        public Color color;
    }

    private static GameObject BakeEffect(GameObject source, string label, string directory, int frameCount, List<string> report)
    {
        string id = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
        string folder = directory + "/" + label.Replace('/', '_') + "_" + id.Substring(0, 8);
        Directory.CreateDirectory(folder); AssetDatabase.Refresh();
        var clone = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(source));
        try
        {
            clone.transform.position = Vector3.zero; clone.transform.rotation = Quaternion.identity; clone.transform.localScale = Vector3.one;
            foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true)) if (script) script.enabled = false;
            var systems = clone.GetComponentsInChildren<ParticleSystem>(true);
            var eligible = new List<ParticleSystem>();
            foreach (var system in systems)
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                string reason = Unsupported(system, renderer);
                if (reason != null) { report.Add("FALLBACK " + label + "/" + system.name + ": " + reason); continue; }
                eligible.Add(system);
            }
            var buffers = eligible.ToDictionary(s => s, s => new ParticleSystem.Particle[Mathf.Max(1, s.main.maxParticles)]);
            var samples = eligible.ToDictionary(s => s, s => new List<Sample>());
            var ids = eligible.ToDictionary(s => s, s => new Dictionary<uint, int>());
            foreach (var system in systems)
            {
                system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                system.useAutoRandomSeed = false; system.randomSeed = (uint)(Array.IndexOf(systems, system) + 1337);
                var main = system.main; main.loop = false; main.stopAction = ParticleSystemStopAction.None;
            }
            var roots = systems.Where(s => !s.transform.parent || !s.transform.parent.GetComponentsInParent<ParticleSystem>(true).Any()).ToArray();
            foreach (var root in roots) root.Simulate(0, true, true, false);
            for (int frame = 0; frame < frameCount; frame++)
            {
                if (frame % 10 == 0 && EditorUtility.DisplayCancelableProgressBar("Bake ATK to VFX Graph", label + " / frame " + frame, frame / (float)frameCount)) throw new OperationCanceledException();
                if (frame > 0) foreach (var root in roots) root.Simulate(1f / Fps, true, false, false);
                foreach (var system in eligible)
                {
                    var main = system.main;
                    var space = main.simulationSpace == ParticleSystemSimulationSpace.World ? null :
                        main.simulationSpace == ParticleSystemSimulationSpace.Custom ? main.customSimulationSpace : system.transform;
                    int count = system.GetParticles(buffers[system]);
                    var renderer = system.GetComponent<ParticleSystemRenderer>();
                    var material = renderer.sharedMaterial;
                    Color tint = material.HasProperty("_TintColor") ? material.GetColor("_TintColor") : material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
                    for (int i = 0; i < count; i++)
                    {
                        var particle = buffers[system][i];
                        if (!ids[system].ContainsKey(particle.randomSeed)) ids[system].Add(particle.randomSeed, ids[system].Count);
                        var position = space ? space.TransformPoint(particle.position) : particle.position;
                        var size = particle.GetCurrentSize3D(system);
                        size = Vector3.Scale(size, system.transform.lossyScale);
                        var rotation = particle.rotation3D;
                        if (renderer.renderMode == ParticleSystemRenderMode.Mesh) rotation = ((space ? space.rotation : Quaternion.identity) * Quaternion.Euler(rotation)).eulerAngles;
                        samples[system].Add(new Sample { frame = frame, id = particle.randomSeed, position = clone.transform.InverseTransformPoint(position),
                            size = size, angle = rotation * Mathf.Deg2Rad, color = particle.GetCurrentColor(system) * tint });
                    }
                }
            }
            int converted = 0;
            foreach (var system in eligible)
            {
                if (ids[system].Count == 0) { report.Add("FALLBACK " + label + "/" + system.name + ": no particles during capture."); continue; }
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                int width = Mathf.Min(4096, Mathf.NextPowerOfTwo(ids[system].Count));
                int rows = Mathf.CeilToInt(ids[system].Count / (float)width);
                int height = rows * frameCount;
                if (height > SystemInfo.maxTextureSize) throw new InvalidOperationException("Particle cache exceeds maximum texture dimensions: " + system.name);
                var position = new Color[width * height]; var size = new Color[position.Length]; var rotation = new Color[position.Length]; var color = new Color[position.Length];
                var bounds = new Bounds(Vector3.zero, Vector3.one);
                foreach (var sample in samples[system])
                {
                    int pixel = sample.frame * rows * width + ids[system][sample.id];
                    position[pixel] = new Color(sample.position.x, sample.position.y, sample.position.z, 1);
                    size[pixel] = new Color(sample.size.x, sample.size.y, sample.size.z, 1);
                    rotation[pixel] = new Color(sample.angle.x, sample.angle.y, sample.angle.z, 0);
                    color[pixel] = sample.color;
                    bounds.Encapsulate(new Bounds(sample.position, sample.size * 2));
                }
                string prefix = folder + "/system" + converted;
                Texture2D Texture(Color[] pixels, string suffix)
                {
                    var texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                    texture.SetPixels(pixels); texture.Apply(false, true);
                    return Save(texture, prefix + suffix + ".asset");
                }
                var textures = new[] { Texture(position, "_position"), Texture(size, "_size"), Texture(rotation, "_rotation"), Texture(color, "_color") };
                var asset = MakeGraph(prefix + ".vfx", renderer, textures, ids[system].Count, width, rows, frameCount, bounds);
                var output = new GameObject("GPU " + system.name);
                output.transform.SetParent(clone.transform, false);
                output.AddComponent<VisualEffect>().visualEffectAsset = asset;
                renderer.enabled = false;
                report.Add("GPU " + label + "/" + system.name + ": " + ids[system].Count + " particles, " + frameCount + " frames.");
                converted++;
            }
            if (systems.Any(s => s.IsAlive(false))) report.Add("WARNING " + label + ": particles still alive at capture end. Increase Capture frames if the tail is cut off.");
            // A fully converted hierarchy no longer needs its original CPU particle components.
            bool fullyConverted = converted == systems.Length;
            if (fullyConverted)
            {
                foreach (var renderer in systems.Select(s => s.GetComponent<ParticleSystemRenderer>())) DestroyImmediate(renderer);
                foreach (var system in systems) DestroyImmediate(system);
            }
            else foreach (var system in eligible)
                if (system.GetComponent<ParticleSystemRenderer>().enabled == false)
                { var emission = system.emission; emission.enabled = false; }
            report.Add(label + ": " + converted + "/" + systems.Length + " GPU systems; " + (fullyConverted ? "fully GPU" : "hybrid, original CPU fallback retained"));
            if (converted == 0) return source;
            return PrefabUtility.SaveAsPrefabAsset(clone, folder + "/" + label + " VFX.prefab");
        }
        finally { PrefabUtility.UnloadPrefabContents(clone); }
    }

    private static string Unsupported(ParticleSystem system, ParticleSystemRenderer renderer)
    {
        if (!renderer || !renderer.enabled || !system.gameObject.activeInHierarchy) return "inactive or hidden source";
        if (!renderer.sharedMaterial) return "missing material";
        if (system.trails.enabled) return "trail rendering requires a strip graph";
        if (system.textureSheetAnimation.enabled) return "flipbook requires a flipbook graph";
        if (renderer.renderMode != ParticleSystemRenderMode.Mesh && renderer.renderMode != ParticleSystemRenderMode.Billboard) return "unsupported renderer mode " + renderer.renderMode;
        if (renderer.renderMode == ParticleSystemRenderMode.Mesh && (renderer.meshCount != 1 || !renderer.mesh)) return "multi-mesh particles";
        if (renderer.renderMode == ParticleSystemRenderMode.Billboard && renderer.alignment != ParticleSystemRenderSpace.View) return "non-camera-facing billboard";
        if (system.lights.enabled) return "particle lights";
        string shader = renderer.sharedMaterial.shader ? renderer.sharedMaterial.shader.name.ToLowerInvariant() : "";
        if (new[] { "distort", "dissolv", "refract", "lightning", "noise" }.Any(shader.Contains)) return "custom shader " + shader;
        var material = renderer.sharedMaterial;
        string map = new[] { "_BaseMap", "_MainTex" }.FirstOrDefault(p => material.HasProperty(p) && material.GetTexture(p));
        if (map != null && (material.GetTextureScale(map) != Vector2.one || material.GetTextureOffset(map) != Vector2.zero)) return "custom UV tiling/offset";
        return null;
    }

    private static VisualEffectAsset MakeGraph(string path, ParticleSystemRenderer renderer, Texture2D[] textures, int count, int width, int rows, int frames, Bounds bounds)
    {
        var asset = (VisualEffectAsset)Call(VfxType("VFXAssetEditorUtility"), "CreateNewAsset", path);
        var resource = Call(VfxType("VisualEffectObjectExtensions"), "GetResource", asset);
        var graph = Call(VfxType("VisualEffectResourceExtensions"), "GetOrCreateGraph", resource);
        var spawn = Node("VFXBasicSpawner"); var init = Node("VFXBasicInitialize"); var update = Node("VFXBasicUpdate");
        bool mesh = renderer.renderMode == ParticleSystemRenderMode.Mesh;
        var output = Node(mesh ? "VFXMeshOutput" : "VFXPlanarPrimitiveOutput");
        int x = 0;
        foreach (var node in new[] { spawn, init, update, output })
        { Call(graph, "AddChild", node); Set(node, "position", new Vector2(x, 100)); x += 350; }
        Call(spawn, "LinkTo", init); Call(init, "LinkTo", update); Call(update, "LinkTo", output);
        var data = Call(init, "GetData");
        Setting(data, "capacity", (uint)count);
        Set(init, "space", Enum.Parse(Get(init, "space").GetType(), "Local"));
        var burst = Node("VFXSpawnerBurst"); Call(spawn, "AddChild", burst); Input(burst, "Count", (float)count);
        var initial = Node("Block.CustomHLSL");
        string duration = (frames / (float)Fps + 1).ToString("R", CultureInfo.InvariantCulture);
        Call(init, "AddChild", initial);
        SetHlsl(initial, "void StartCache(inout VFXAttributes attributes) { attributes.lifetime = " + duration + "; attributes.size = 0; attributes.alpha = 0; }");
        Setting(update, "integration", "None"); Setting(update, "angularIntegration", "None");
        var playback = Node("Block.CustomHLSL");
        string hlsl = "void PlayCache(inout VFXAttributes attributes, in VFXSampler2D positionCache, in VFXSampler2D sizeCache, in VFXSampler2D rotationCache, in VFXSampler2D colorCache)\n{\n" +
            "uint id = attributes.particleId % " + count + "u; uint frame = min((uint)(attributes.age * 30.0), " + (frames - 1) + "u);\n" +
            "int3 uv = int3(id % " + width + "u, id / " + width + "u + frame * " + rows + "u, 0);\n" +
            "float4 p = positionCache.t.Load(uv); float4 s = sizeCache.t.Load(uv); float4 r = rotationCache.t.Load(uv); float4 c = colorCache.t.Load(uv);\n" +
            "attributes.position = p.xyz; attributes.size = 1; attributes.scaleX = s.x; attributes.scaleY = s.y; attributes.scaleZ = s.z;\n" +
            "attributes.angleX = r.x; attributes.angleY = r.y; attributes.angleZ = r.z; attributes.color = c.rgb; attributes.alpha = attributes.age < " + (frames / (float)Fps).ToString("R", CultureInfo.InvariantCulture) + " ? c.a * p.w : 0;\n}";
        Call(update, "AddChild", playback); SetHlsl(playback, hlsl);
        for (int i = 0; i < 4; i++) Input(playback, new[] { "positionCache", "sizeCache", "rotationCache", "colorCache" }[i], textures[i]);
        if (!mesh) Call(output, "AddChild", Node("Block.Orient"));
        if (mesh) Input(output, "mesh", renderer.mesh);
        var material = renderer.sharedMaterial;
        string map = new[] { "_BaseMap", "_MainTex" }.FirstOrDefault(p => material.HasProperty(p) && material.GetTexture(p));
        if (map != null) Input(output, "mainTexture", material.GetTexture(map));
        Setting(output, "blendMode", material.shader.name.IndexOf("add", StringComparison.OrdinalIgnoreCase) >= 0 ? "Additive" : "Alpha");
        // Match a conservative manual bound; no GPU readback is needed for culling.
        var boundsSlot = ((IEnumerable)Get(init, "inputSlots")).Cast<object>().FirstOrDefault(s => (string)Get(s, "name") == "bounds");
        if (boundsSlot != null)
        {
            var box = Activator.CreateInstance(Get(boundsSlot, "value").GetType());
            box.GetType().GetField("center").SetValue(box, bounds.center);
            box.GetType().GetField("size").SetValue(box, bounds.size + Vector3.one);
            Set(boundsSlot, "value", box);
        }
        Call(VfxType("VisualEffectResourceExtensions"), "WriteAssetWithSubAssets", resource);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(path);
    }
}
