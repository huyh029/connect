using System;
using System.Linq;
using NhiQuoc.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public sealed class HeroEffectPreviewWindow : EditorWindow
{
    private HeroAnimationDisplay display;
    private int heroIndex, action, effectIndex, frame;
    private bool playing;
    private double lastTick, fractionalFrame;
    private PreviewRenderUtility preview;
    private GameObject model;
    private GameObject[] effects;
    private PlayableGraph graph;
    private AnimationClipPlayable playable;
    private AnimationClip clip;
    private Vector2 orbit = new Vector2(160, 12);
    private float zoom = 1;
    private Bounds bounds;
    private Vector2 scroll;
    private int sampledFrame = -1;
    private int transformTool;
    private bool localAxes = true;
    private bool handleDragging;
    private int handleUndoGroup;
    private HeroAnimationDisplay pendingBackup;
    private double backupAt;

    [MenuItem("Tools/Nhi Quoc/Edit Attack and Ulti FX (Frame Preview)")]
    public static void Open() => GetWindow<HeroEffectPreviewWindow>("Hero FX Preview");

    private HeroAnimationDisplay.Hero Hero => display && display.heroes.Length > 0 ? display.heroes[Mathf.Clamp(heroIndex, 0, display.heroes.Length - 1)] : null;
    private HeroAnimationDisplay.AttackEffect[] Configs => Hero == null ? Array.Empty<HeroAnimationDisplay.AttackEffect>() :
        (action == 2 ? Hero.ultiEffects : action == 1 ? Hero.alternateAttackEffects : Hero.attackEffects) ?? Array.Empty<HeroAnimationDisplay.AttackEffect>();

    private void OnEnable()
    {
        EditorApplication.update += Tick;
        Undo.undoRedoPerformed += AfterUndo;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        display = FindFirstObjectByType<HeroAnimationDisplay>();
    }

    private void OnDisable()
    {
        FlushBackup();
        EditorApplication.update -= Tick;
        Undo.undoRedoPerformed -= AfterUndo;
        EditorApplication.playModeStateChanged -= PlayModeChanged;
        Cleanup();
    }

    private void PlayModeChanged(PlayModeStateChange state) { playing = false; Cleanup(); Repaint(); }

    private void AfterUndo()
    {
        Rebuild();
        if (display && !EditorApplication.isPlayingOrWillChangePlaymode) MarkDirty();
    }

    private void Cleanup()
    {
        if (graph.IsValid()) graph.Destroy();
        if (preview != null) preview.Cleanup();
        preview = null; model = null; effects = null; clip = null;
        sampledFrame = -1;
    }

    private void OnGUI()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        { EditorGUILayout.HelpBox("Stop Play mode to edit and save FX settings.", MessageType.Info); return; }
        EditorGUI.BeginChangeCheck();
        display = (HeroAnimationDisplay)EditorGUILayout.ObjectField("Display in scene", display, typeof(HeroAnimationDisplay), true);
        if (EditorGUI.EndChangeCheck()) { heroIndex = 0; frame = 0; Rebuild(); }
        if (!display || Hero == null)
        { EditorGUILayout.HelpBox("Create the animation display in your scene first, then select it here.", MessageType.Info); return; }
        EditorGUI.BeginChangeCheck();
        heroIndex = EditorGUILayout.Popup("Hero", heroIndex, display.heroes.Select(h => h.name).ToArray());
        action = EditorGUILayout.Popup("Action", action, new[] { "Attack 1", "Attack 2", "Ulti" });
        if (EditorGUI.EndChangeCheck()) { frame = 0; effectIndex = 0; Rebuild(); }
        if (preview == null) Rebuild();
        if (!clip) { EditorGUILayout.HelpBox("This hero has no clip for this action.", MessageType.Info); return; }
        int endFrame = Mathf.CeilToInt(clip.length * clip.frameRate);
        EditorGUILayout.LabelField(clip.name + " | " + clip.frameRate.ToString("0.##") + " FPS | " + endFrame + " frames");
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Width(65)))
        { playing = !playing; lastTick = EditorApplication.timeSinceStartup; fractionalFrame = frame; if (frame >= endFrame) { frame = 0; fractionalFrame = 0; } }
        if (GUILayout.Button("|<", GUILayout.Width(35))) { playing = false; frame = 0; Sample(); }
        if (GUILayout.Button("-1", GUILayout.Width(35))) { playing = false; frame = Mathf.Max(0, frame - 1); Sample(); }
        if (GUILayout.Button("+1", GUILayout.Width(35))) { playing = false; frame = Mathf.Min(endFrame, frame + 1); Sample(); }
        EditorGUI.BeginChangeCheck();
        frame = EditorGUILayout.IntSlider("Frame", frame, 0, endFrame);
        if (EditorGUI.EndChangeCheck()) { playing = false; Sample(); }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        transformTool = GUILayout.Toolbar(transformTool, new[] { "Move (W)", "Rotate (E)", "Scale (R)" });
        localAxes = GUILayout.Toggle(localAxes, "Local axes", GUILayout.Width(90));
        EditorGUILayout.EndHorizontal();
        Rect view = GUILayoutUtility.GetRect(100, Mathf.Max(180, position.height * 0.42f), GUILayout.ExpandWidth(true));
        DrawPreview(view);
        EditorGUILayout.LabelField("Drag gizmo: edit FX | Alt + drag / right drag: orbit | Wheel: zoom", EditorStyles.miniLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        var configs = Configs;
        if (configs.Length > 0)
        {
            effectIndex = EditorGUILayout.Popup("FX", Mathf.Clamp(effectIndex, 0, configs.Length - 1), configs.Select((c, i) => i + ": " + (c.prefab ? c.prefab.name : "Missing prefab")).ToArray());
            var config = configs[effectIndex];
            EditorGUI.BeginChangeCheck();
            int start = EditorGUILayout.IntSlider("FX trigger frame", config.triggerFrame, Mathf.Min(-endFrame, config.triggerFrame), Mathf.Max(endFrame, config.triggerFrame));
            Vector3 pos = EditorGUILayout.Vector3Field("Local position", config.position);
            Vector3 rot = EditorGUILayout.Vector3Field("Local rotation", config.eulerAngles);
            Vector3 scale = EditorGUILayout.Vector3Field("Local scale", config.scale);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(display, "Edit hero FX transform and frame");
                config.triggerFrame = start; config.position = pos; config.eulerAngles = rot; config.scale = scale;
                MarkDirty(); Sample();
            }
            if (GUILayout.Button("Set trigger to current frame"))
            { Undo.RecordObject(display, "Set FX trigger frame"); config.triggerFrame = frame; MarkDirty(); Sample(); }
            EditorGUILayout.LabelField("Animation frame: " + frame + "  |  FX trigger: " + config.triggerFrame +
                (frame < config.triggerFrame ? "  (waiting)" : "  |  FX age: " + (frame - config.triggerFrame) + " frames"));
            EditorGUILayout.HelpBox("0 = FX starts with animation. Positive = later. Negative = earlier, compensating particle delay. Example: -10 prewarms FX by 10 frames. Play from frame 0 after adjusting. Authored particle delays remain included.", MessageType.Info);
        }
        else EditorGUILayout.HelpBox("No FX configured for this action.", MessageType.Info);
        EditorGUILayout.LabelField("Backup: " + HeroEffectPresetStore.Latest, EditorStyles.miniLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Save scene + preset snapshot"))
        {
            HeroEffectPresetStore.Save(display, true);
            pendingBackup = null;
            EditorSceneManager.SaveScene(display.gameObject.scene);
        }
        if (GUILayout.Button("Restore latest preset"))
        {
            pendingBackup = null;
            HeroEffectPresetStore.Load(display, HeroEffectPresetStore.Latest);
            Rebuild();
        }
        if (GUILayout.Button("Load preset..."))
        {
            string path = EditorUtility.OpenFilePanel("Load FX preset", HeroEffectPresetStore.Folder, "json");
            if (!string.IsNullOrEmpty(path)) { pendingBackup = null; HeroEffectPresetStore.Load(display, path); Rebuild(); }
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndScrollView();
    }

    private void MarkDirty()
    {
        EditorUtility.SetDirty(display);
        PrefabUtility.RecordPrefabInstancePropertyModifications(display);
        EditorSceneManager.MarkSceneDirty(display.gameObject.scene);
        pendingBackup = display;
        backupAt = EditorApplication.timeSinceStartup + 1;
    }

    private void FlushBackup()
    {
        if (!pendingBackup) return;
        try { HeroEffectPresetStore.Save(pendingBackup, false); pendingBackup = null; }
        catch (Exception exception) { Debug.LogException(exception); backupAt = EditorApplication.timeSinceStartup + 10; }
    }

    private void Rebuild()
    {
        Cleanup();
        if (EditorApplication.isPlayingOrWillChangePlaymode || Hero == null || !Hero.displayRoot || !Hero.animator) return;
        clip = action == 1 ? Hero.alternateAttack : Hero.clips[action == 2 ? 4 : 2];
        if (!clip) return;
        preview = new PreviewRenderUtility();
        model = Instantiate(Hero.displayRoot.gameObject);
        model.hideFlags = HideFlags.HideAndDontSave;
        // Disable gameplay behaviours on the temporary copy.
        foreach (var script in model.GetComponentsInChildren<MonoBehaviour>(true)) if (script) script.enabled = false;
        preview.AddSingleGO(model);
        model.transform.position = Vector3.zero;
        model.transform.rotation = Quaternion.identity;
        foreach (var particle in model.GetComponentsInChildren<ParticleSystem>(true))
        { var main = particle.main; main.playOnAwake = false; particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); }
        foreach (var renderer in model.GetComponentsInChildren<ParticleSystemRenderer>(true)) renderer.enabled = false;
        foreach (var config in Hero.attackEffects.Concat(Hero.alternateAttackEffects).Concat(Hero.ultiEffects))
            foreach (var embedded in config.embeddedInstances)
            {
                if (!embedded) continue;
                string embeddedPath = AnimationUtility.CalculateTransformPath(embedded.transform, Hero.displayRoot);
                var copy = model.transform.Find(embeddedPath);
                if (copy) copy.gameObject.SetActive(false);
            }
        foreach (var animator in model.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
        string animatorPath = AnimationUtility.CalculateTransformPath(Hero.animator.transform, Hero.displayRoot);
        var target = string.IsNullOrEmpty(animatorPath) ? model.transform : model.transform.Find(animatorPath);
        var animation = target.GetComponent<Animator>();
        animation.enabled = true; animation.runtimeAnimatorController = null; animation.fireEvents = false; animation.applyRootMotion = false;
        graph = PlayableGraph.Create("Hero FX editor preview");
        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
        playable = AnimationClipPlayable.Create(graph, clip);
        playable.SetApplyFootIK(false);
        AnimationPlayableOutput.Create(graph, "Pose", animation).SetSourcePlayable(playable);
        graph.Play();
        effects = new GameObject[Configs.Length];
        for (int i = 0; i < Configs.Length; i++)
        {
            var config = Configs[i];
            if (!config.prefab || !config.attachment) continue;
            string path = AnimationUtility.CalculateTransformPath(config.attachment, Hero.displayRoot);
            var attachment = string.IsNullOrEmpty(path) ? model.transform : model.transform.Find(path);
            if (!attachment) continue;
            var instance = Instantiate(config.prefab, attachment);
            foreach (var script in instance.GetComponentsInChildren<MonoBehaviour>(true)) if (script) script.enabled = false;
            foreach (var particle in instance.GetComponentsInChildren<ParticleSystem>(true))
            { var main = particle.main; main.playOnAwake = false; main.loop = false; particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear); }
            effects[i] = instance;
        }
        var bodies = model.GetComponentsInChildren<SkinnedMeshRenderer>();
        bounds = new Bounds(Vector3.up, Vector3.one * 2);
        if (bodies.Length > 0) { bounds = bodies[0].bounds; foreach (var body in bodies) bounds.Encapsulate(body.bounds); }
        Sample(); Repaint();
    }

    private void Sample(bool restart = true)
    {
        if (!graph.IsValid() || !clip) return;
        bool reset = restart || frame < sampledFrame;
        if (reset) sampledFrame = Mathf.Min(0, Configs.Length > 0 ? Configs.Min(c => c.triggerFrame) : 0) - 1;
        for (int i = 0; i < effects.Length; i++)
        {
            var instance = effects[i];
            if (!instance) continue;
            var config = Configs[i];
            instance.transform.localPosition = config.position;
            instance.transform.localRotation = Quaternion.Euler(config.eulerAngles);
            instance.transform.localScale = config.scale;
            if (reset)
            {
                foreach (var particle in instance.GetComponentsInChildren<ParticleSystem>(true))
                    particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                instance.SetActive(false);
            }
        }
        // Replay every crossed frame so world-space particles follow the animated
        // weapon path when scrubbing, instead of emitting at the final pose only.
        for (int step = sampledFrame + 1; step <= frame; step++)
        {
            playable.SetTime(Math.Min(Math.Max(0, step / clip.frameRate), Math.Max(0, clip.length - 0.0001f)));
            graph.Evaluate(0);
            for (int i = 0; i < effects.Length; i++)
            {
                var instance = effects[i];
                if (!instance || step < Configs[i].triggerFrame) continue;
                bool first = step == Configs[i].triggerFrame;
                instance.SetActive(true);
                foreach (var particle in instance.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var parent = particle.transform.parent;
                    bool nested = false;
                    while (parent && parent != instance.transform.parent)
                    { if (parent.GetComponent<ParticleSystem>()) { nested = true; break; } parent = parent.parent; }
                    if (!nested)
                    {
                        particle.Simulate(first ? 0 : 1f / clip.frameRate, true, first, false);
                        particle.Pause(true);
                    }
                }
            }
        }
        sampledFrame = frame;
        Repaint();
    }

    private void Tick()
    {
        if (pendingBackup && EditorApplication.timeSinceStartup >= backupAt && GUIUtility.hotControl == 0) FlushBackup();
        if (!playing || !clip || EditorApplication.isPlayingOrWillChangePlaymode) return;
        double now = EditorApplication.timeSinceStartup;
        fractionalFrame += (now - lastTick) * clip.frameRate;
        lastTick = now;
        int end = Mathf.CeilToInt(clip.length * clip.frameRate);
        frame = Mathf.Min(end, (int)fractionalFrame);
        if (frame >= end) playing = false;
        Sample(false);
    }

    private void DrawPreview(Rect rect)
    {
        var evt = Event.current;
        if (preview == null) return;
        var camera = preview.camera;
        var rotation = Quaternion.Euler(orbit.y, orbit.x, 0);
        float distance = Mathf.Max(2, bounds.size.magnitude * 1.8f) * zoom;
        camera.transform.position = bounds.center + rotation * new Vector3(0, 0, -distance);
        camera.transform.rotation = rotation;
        camera.aspect = rect.width / Mathf.Max(1, rect.height);
        camera.nearClipPlane = 0.01f; camera.farClipPlane = distance + 1000;
        preview.lights[0].intensity = 1.2f;
        preview.lights[0].transform.rotation = Quaternion.Euler(40, 30, 0);
        preview.lights[1].intensity = 0.7f;
        preview.ambientColor = new Color(0.4f, 0.4f, 0.4f);
        if (evt.type == EventType.Repaint)
        {
            preview.BeginPreview(rect, GUIStyle.none);
            preview.Render(true);
            GUI.DrawTexture(rect, preview.EndPreview(), ScaleMode.StretchToFill, false);
        }

        bool orbitInput = rect.Contains(evt.mousePosition) &&
            (evt.button == 1 || evt.button == 0 && evt.alt);
        if (orbitInput && evt.type == EventType.MouseDrag)
        { orbit += evt.delta * 0.5f; evt.Use(); Repaint(); }
        if (rect.Contains(evt.mousePosition) && evt.type == EventType.ScrollWheel)
        { zoom = Mathf.Clamp(zoom * Mathf.Exp(evt.delta.y * 0.08f), 0.1f, 10); evt.Use(); Repaint(); }
        if (rect.Contains(evt.mousePosition) && evt.type == EventType.KeyDown && !EditorGUIUtility.editingTextField)
        {
            if (evt.keyCode == KeyCode.W) { transformTool = 0; evt.Use(); }
            if (evt.keyCode == KeyCode.E) { transformTool = 1; evt.Use(); }
            if (evt.keyCode == KeyCode.R) { transformTool = 2; evt.Use(); }
        }
        if (Configs.Length == 0 || effects == null) return;
        int selected = Mathf.Clamp(effectIndex, 0, Configs.Length - 1);
        if (!effects[selected]) return;
        var target = effects[selected].transform;
        var config = Configs[selected];
        // Built-in Unity handles provide axis, plane and ring dragging with snapping.
        GUI.BeginGroup(rect);
        var previousColor = Handles.color;
        var previousMatrix = Handles.matrix;
        Handles.matrix = Matrix4x4.identity;
        Handles.SetCamera(new Rect(0, 0, rect.width, rect.height), camera);
        EditorGUI.BeginChangeCheck();
        Vector3 worldPosition = target.position;
        Quaternion worldRotation = target.rotation;
        Vector3 localScale = target.localScale;
        Quaternion axes = localAxes ? target.rotation : Quaternion.identity;
        if (transformTool == 0) worldPosition = Handles.PositionHandle(worldPosition, axes);
        else if (transformTool == 1) worldRotation = Handles.RotationHandle(worldRotation, worldPosition);
        else localScale = Handles.ScaleHandle(localScale, worldPosition, axes, HandleUtility.GetHandleSize(worldPosition));
        if (EditorGUI.EndChangeCheck())
        {
            playing = false;
            if (!handleDragging)
            {
                Undo.IncrementCurrentGroup();
                handleUndoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Drag FX transform");
                handleDragging = true;
            }
            Undo.RecordObject(display, "Drag FX transform");
            target.position = worldPosition;
            target.rotation = worldRotation;
            target.localScale = localScale;
            config.position = target.localPosition;
            config.eulerAngles = target.localEulerAngles;
            config.scale = target.localScale;
            MarkDirty(); Sample();
        }
        Handles.matrix = previousMatrix;
        Handles.color = previousColor;
        GUI.EndGroup();
        if (handleDragging && GUIUtility.hotControl == 0)
        { Undo.CollapseUndoOperations(handleUndoGroup); handleDragging = false; }
    }
}
