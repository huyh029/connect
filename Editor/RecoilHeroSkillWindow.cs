using System;
using System.Collections.Generic;
using System.Linq;
using NhiQuoc.Tools;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class RecoilHeroSkillWindow : EditorWindow
{
    private const string AssetFolder = "Assets/Data/CharacterProperties";
    private const string AssetPath = AssetFolder + "/RecoilHeroSkills.asset";
    private RecoilHeroSkillLibrary library;
    private RecoilHeroPrefabCatalog catalog;
    private SerializedObject serialized;
    private int heroIndex, clipIndex, zoneIndex, frame;
    private Vector2 scroll;
    private bool playing, showFx = true;
    private double updated;
    private float playbackFrame;
    private Scene previewScene;
    private Scene previousScene;
    private GameObject previewRoot, targetMarker;
    private HeroVatPlayer preview;
    private readonly List<GameObject> effects = new();
    private readonly List<int> effectIndices = new();
    private Vector3 target = new Vector3(0, 0, 8);
    private string error;

    [MenuItem("Tools/Nhị Quốc/Kỹ năng tướng/Vùng sát thương")]
    public static void Open() => GetWindow<RecoilHeroSkillWindow>("Vùng sát thương tướng");

    private HeroSkillProfile Profile => library != null && heroIndex < library.Heroes.Length ? library.Heroes[heroIndex] : null;
    private HeroSkillClip Skill => Profile != null && clipIndex < Profile.Clips.Length ? Profile.Clips[clipIndex] : null;
    private HeroHitZone Zone => Skill != null && zoneIndex < Skill.Zones.Length ? Skill.Zones[zoneIndex] : null;
    private HeroVatPlayer Vat
    {
        get
        {
            var prefab = catalog != null && Profile != null ? catalog.Find(Profile.HeroId) : null;
            return prefab != null ? prefab.GetComponent<HeroVatPlayer>() : null;
        }
    }
    private HeroVatPlayer.Clip Clip
    {
        get
        {
            var player = preview != null ? preview : Vat;
            return player != null && Skill != null && Skill.ClipIndex < player.clips.Length ? player.clips[Skill.ClipIndex] : null;
        }
    }

    private void OnEnable()
    {
        minSize = new Vector2(500, 650);
        library = AssetDatabase.LoadAssetAtPath<RecoilHeroSkillLibrary>(AssetPath);
        catalog = Resources.Load<RecoilHeroPrefabCatalog>("RecoilHeroPrefabs");
        if (library != null) serialized = new SerializedObject(library);
        SceneView.duringSceneGui += DrawScene;
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        Undo.undoRedoPerformed += RefreshAfterUndo;
        EditorSceneManager.sceneClosing += SceneClosing;
        AssemblyReloadEvents.beforeAssemblyReload += ClosePreview;
        updated = EditorApplication.timeSinceStartup;
    }

    /// <summary>Another scene replaces the current one: take the preview down first, or its objects stay behind.</summary>
    private void SceneClosing(Scene scene, bool removingScene)
    {
        if (!previewScene.IsValid() || scene == previewScene) return;
        // Unity is closing scenes right now: only remove the objects; it closes the preview scene itself.
        playing = false;
        foreach (var fx in effects) Discard(fx);
        Discard(previewRoot); Discard(targetMarker);
        effects.Clear(); effectIndices.Clear();
        preview = null; previewRoot = null; targetMarker = null;
        previewScene = default;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= DrawScene;
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= PlayModeChanged;
        Undo.undoRedoPerformed -= RefreshAfterUndo;
        EditorSceneManager.sceneClosing -= SceneClosing;
        AssemblyReloadEvents.beforeAssemblyReload -= ClosePreview;
        ClosePreview();
    }

    private void PlayModeChanged(PlayModeStateChange change)
    { if (change == PlayModeStateChange.ExitingEditMode) ClosePreview(); }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Vùng sát thương tướng — Đánh thường / Tuyệt kỹ", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Chế độ quét: chọn frame đầu/cuối và góc đầu/cuối. Thanh sát thương quét theo thời gian, mỗi mục tiêu chỉ nhận một lần trong một lượt quét. Vận tốc được tính tự động từ thời lượng VAT.", MessageType.Info);
        if (Application.isPlaying)
        { EditorGUILayout.HelpBox("Dừng chạy game để chỉnh kỹ năng và xem thử.", MessageType.Info); return; }
        if (library == null)
        {
            if (GUILayout.Button("Tạo cấu hình từ 8 VAT hiện có")) ImportMissing();
            return;
        }
        int nextHero = EditorGUILayout.Popup("Tướng", heroIndex, library.Heroes.Select(h => HeroName(h.HeroId, h.Name)).ToArray());
        if (nextHero != heroIndex) { heroIndex = nextHero; clipIndex = zoneIndex = frame = 0; ClosePreview(); }
        if (Profile == null) return;
        if (!Profile.Enabled)
            EditorGUILayout.HelpBox("Cấu hình mẫu chưa áp dụng. Căn vùng với VAT/FX rồi bật ‘Áp dụng trong game’ và lưu. Tướng chưa bật vẫn dùng cơ chế sát thương hiện tại.", MessageType.Info);
        int nextClip = EditorGUILayout.Popup("Hoạt ảnh", clipIndex, Profile.Clips.Select(c => ClipLabel(c.ClipIndex)).ToArray());
        if (nextClip != clipIndex) { clipIndex = nextClip; zoneIndex = frame = 0; ClosePreview(); }
        var clip = Clip;
        if (clip == null) { EditorGUILayout.HelpBox("Không tìm thấy clip VAT/prefab tương ứng.", MessageType.Warning); return; }
        EditorGUILayout.LabelField($"{clip.frameCount} frame VAT / {clip.duration:0.###} giây | FPS nguồn: {clip.sourceFps}");
        string fxInfo = string.Join("; ", clip.effects.Select((fx, i) => $"FX {i}: {Mathf.Max(0, fx.triggerFrame / Mathf.Max(1, clip.sourceFps)):0.###}s ({fx.prefab?.name})"));
        EditorGUILayout.LabelField(fxInfo, EditorStyles.wordWrappedMiniLabel);
        if (clip.effects.Any(fx => fx.triggerFrame / Mathf.Max(1, clip.sourceFps) > clip.duration))
            EditorGUILayout.HelpBox("Có FX phát sau khi clip kết thúc. Cần kiểm tra lại mốc FX trong VAT trước khi căn sát thương.", MessageType.Warning);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(preview == null ? "Mở xem thử trong Scene" : "Đóng xem thử"))
        { if (preview == null) OpenPreview(); else ClosePreview(); }
        if (GUILayout.Button(playing ? "Tạm dừng" : "Phát"))
        { if (preview == null) OpenPreview(); playing = !playing; playbackFrame = frame; updated = EditorApplication.timeSinceStartup; }
        bool nextShowFx = GUILayout.Toggle(showFx, "Hiện FX");
        if (nextShowFx != showFx) { showFx = nextShowFx; Sample(); }
        EditorGUILayout.EndHorizontal();
        EditorGUI.BeginChangeCheck();
        frame = EditorGUILayout.IntSlider("Frame đang xem", frame, 0, Mathf.Max(0, clip.frameCount - 1));
        target = EditorGUILayout.Vector3Field("Điểm mục tiêu", target);
        if (EditorGUI.EndChangeCheck()) { playbackFrame = frame; Sample(); }
        EditorGUILayout.LabelField($"Thời gian: {FrameSeconds(clip, frame):0.###}s");
        int zoneOperation = 0;
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("+ Vùng tại frame này"))
            zoneOperation = 1;
        using (new EditorGUI.DisabledScope(Zone == null))
        {
            if (GUILayout.Button("Nhân bản"))
                zoneOperation = 2;
            if (GUILayout.Button("Xóa vùng"))
                zoneOperation = 3;
        }
        EditorGUILayout.EndHorizontal();
        if (zoneOperation != 0)
        {
            EditZoneList(zoneOperation, clip);
            GUIUtility.ExitGUI();
        }
        if (Skill.Zones.Length > 0)
            zoneIndex = EditorGUILayout.Popup("Vùng", Mathf.Clamp(zoneIndex, 0, Skill.Zones.Length - 1), Skill.Zones.Select(z => z.Name).ToArray());
        serialized.Update();
        var profileProperty = serialized.FindProperty("Heroes").GetArrayElementAtIndex(heroIndex);
        EditorGUILayout.PropertyField(profileProperty.FindPropertyRelative("Enabled"), new GUIContent("Áp dụng trong game"));
        scroll = EditorGUILayout.BeginScrollView(scroll);
        if (Zone != null)
        {
            var property = profileProperty.FindPropertyRelative("Clips").GetArrayElementAtIndex(clipIndex)
                .FindPropertyRelative("Zones").GetArrayElementAtIndex(zoneIndex);
            DrawZoneSettings(property, clip);
        }
        EditorGUILayout.EndScrollView();
        if (serialized.ApplyModifiedProperties()) { ValidateZone(clip); Changed(); Sample(); }
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Lưu cấu hình")) { AssetDatabase.SaveAssets(); ShowNotification(new GUIContent("Đã lưu vùng sát thương")); }
        if (GUILayout.Button("Bổ sung clip VAT còn thiếu")) ImportMissing();
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
    }

    private static string ClipLabel(int index) => index == 4 ? "Tuyệt kỹ" : index == 5 ? "Đánh thường 2" : "Đánh thường 1";
    private void EditZoneList(int operation, HeroVatPlayer.Clip clip)
    {
        GUI.FocusControl(null);
        EditorGUIUtility.editingTextField = false;
        serialized.ApplyModifiedProperties();
        var skill = Skill;
        if (skill == null) return;
        var zones = skill.Zones.ToList();
        Undo.RecordObject(library, operation == 2 ? "Nhân bản vùng sát thương" : operation == 1 ? "Thêm vùng sát thương" : "Xóa vùng sát thương");
        if (operation == 3)
        {
            if (zoneIndex >= 0 && zoneIndex < zones.Count) zones.RemoveAt(zoneIndex);
            zoneIndex = Mathf.Clamp(zoneIndex, 0, Mathf.Max(0, zones.Count - 1));
        }
        else
        {
            var added = operation == 2 && Zone != null ?
                JsonUtility.FromJson<HeroHitZone>(JsonUtility.ToJson(Zone)) :
                new HeroHitZone { Shape = HeroHitShape.Cone, Anchor = HeroHitAnchor.Caster,
                    StartFrame = frame, EndFrame = Mathf.Min(frame + 30, clip.frameCount - 1) };
            string stem = operation == 2 ? (Zone.Name ?? "Vùng") + " — bản sao" : "Hình quạt";
            string name = stem;
            int number = 2;
            while (zones.Any(z => string.Equals(z.Name, name, StringComparison.Ordinal))) name = stem + " " + number++;
            added.Name = name;
            zones.Add(added);
            zoneIndex = zones.Count - 1;
            ShowNotification(new GUIContent(operation == 2 ? "Đã nhân bản: " + name : "Đã thêm: " + name));
        }
        skill.Zones = zones.ToArray();
        ValidateZone(clip);
        Changed();
        Sample();
    }

    private void RefreshAfterUndo()
    {
        if (library == null) return;
        serialized = new SerializedObject(library);
        if (Skill != null) zoneIndex = Mathf.Clamp(zoneIndex, 0, Mathf.Max(0, Skill.Zones.Length - 1));
        Sample(); Repaint(); SceneView.RepaintAll();
    }
    private static string HeroName(int id, string fallback)
    {
        string[] names = { "Chu Du", "Gia Cát Lượng", "Hạ Hầu Đôn", "Lữ Bố", "Lưu Bị", "Quan Vũ", "Tào Tháo", "Triệu Vân" };
        return id > 0 && id <= names.Length ? names[id - 1] : fallback;
    }

    private void DrawZoneSettings(SerializedProperty zone, HeroVatPlayer.Clip clip)
    {
        void Field(string key, string label) => EditorGUILayout.PropertyField(zone.FindPropertyRelative(key), new GUIContent(label));
        Field("Name", "Tên vùng"); Field("Enabled", "Bật vùng này");
        var shapeProperty = zone.FindPropertyRelative("Shape");
        int[] shapeValues = { (int)HeroHitShape.Cone, (int)HeroHitShape.Circle, (int)HeroHitShape.Ring, (int)HeroHitShape.Box };
        int shapeIndex = Mathf.Max(0, Array.IndexOf(shapeValues, shapeProperty.intValue));
        shapeIndex = EditorGUILayout.Popup("Loại vùng sát thương", shapeIndex,
            new[] { "1. Hình quạt", "2. Hình tròn", "3. Vành khuyên", "4. Hình hộp" });
        shapeProperty.intValue = shapeValues[shapeIndex];
        bool cone = zone.FindPropertyRelative("Shape").intValue == (int)HeroHitShape.Cone;
        if (cone) Field("Mode", "Cách gây sát thương");
        bool sweep = cone && zone.FindPropertyRelative("Mode").intValue == (int)HeroHitMode.Sweep;
        var anchor = zone.FindPropertyRelative("Anchor");
        int[] anchors = { (int)HeroHitAnchor.Caster, (int)HeroHitAnchor.Target, (int)HeroHitAnchor.FollowTarget, (int)HeroHitAnchor.StraightProjectile };
        int anchorIndex = Mathf.Max(0, Array.IndexOf(anchors, anchor.intValue));
        anchorIndex = EditorGUILayout.Popup("Gốc sát thương", anchorIndex,
            new[] { "Bám tâm tướng", "Điểm mục tiêu lúc tung chiêu", "Bám mục tiêu di chuyển", "Đạn bay thẳng" });
        anchor.intValue = anchors[anchorIndex];
        bool projectile = anchor.intValue == (int)HeroHitAnchor.StraightProjectile;
        if (projectile)
        {
            shapeProperty.intValue = (int)HeroHitShape.Circle;
            Field("ProjectileSpeed", "Tốc độ đạn (đơn vị/giây)");
            EditorGUILayout.HelpBox("Đạn dùng chung cơ chế với cung kỵ: tạo lúc đến frame bắt đầu, bay độc lập với hoạt ảnh, trúng quân đầu tiên thì dừng và gây sát thương. Không đuổi mục tiêu; không kết thúc đạn ở frame cuối. Bán kính là độ rộng va chạm của đạn.", MessageType.None);
        }
        if (anchor.intValue == (int)HeroHitAnchor.Target)
            EditorGUILayout.HelpBox("Chốt vị trí mục tiêu khi tung chiêu. Mục tiêu chạy ra khỏi vùng có thể né sát thương.", MessageType.None);
        else if (anchor.intValue == (int)HeroHitAnchor.FollowTarget)
            EditorGUILayout.HelpBox("Tâm vùng và FX theo mục tiêu. Nếu mục tiêu chết, giữ vị trí cuối cùng.", MessageType.None);
        Field("StartFrame", "Frame bắt đầu"); Field("EndFrame", "Frame kết thúc");
        if (sweep)
        {
            Field("SweepStartAngle", "Góc bắt đầu (độ)"); Field("SweepEndAngle", "Góc kết thúc (độ)");
            Field("SweepWidth", "Độ rộng thanh quét (độ)");
            int first = zone.FindPropertyRelative("StartFrame").intValue;
            int last = zone.FindPropertyRelative("EndFrame").intValue;
            float duration = (last - first) * clip.duration / Mathf.Max(1, clip.frameCount - 1);
            float travel = zone.FindPropertyRelative("SweepEndAngle").floatValue - zone.FindPropertyRelative("SweepStartAngle").floatValue;
            if (duration <= 0) EditorGUILayout.HelpBox("Quét cần frame kết thúc lớn hơn frame bắt đầu.", MessageType.Warning);
            else
            {
                float speed = travel / duration;
                float linear = Mathf.Abs(speed) * Mathf.Deg2Rad * zone.FindPropertyRelative("Radius").floatValue;
                EditorGUILayout.HelpBox($"Thời gian quét: {duration:0.###} giây\nVận tốc góc: {speed:0.##} độ/giây\nVận tốc đầu thanh: {linear:0.##} đơn vị/giây\nMỗi mục tiêu chỉ trúng một lần trong lượt quét.", MessageType.None);
            }
        }
        else if (!projectile) Field("RepeatFrames", "Số frame giữa các nhịp (0 = một lần)");
        Field("Offset", "Độ lệch vị trí"); Field("Yaw", "Xoay hướng gốc (độ)");
        var shape = (HeroHitShape)zone.FindPropertyRelative("Shape").intValue;
        if (shape == HeroHitShape.Box) Field("BoxSize", "Kích thước ngang / dài");
        else Field("Radius", "Bán kính / chiều dài thanh");
        if (shape == HeroHitShape.Ring) Field("InnerRadius", "Bán kính trong");
        if (cone && !sweep) Field("Angle", "Góc mở hình quạt (độ)");
        EditorGUILayout.LabelField("Vùng đánh xét trên mặt phẳng XZ, không giới hạn độ cao.", EditorStyles.wordWrappedMiniLabel);
        Field("DamageMultiplier", "Hệ số sát thương"); Field("EdgeDamage", "Tỉ lệ sát thương ở rìa");
        Field("Force", "Lực tác động");
        EditorGUILayout.LabelField("Hút, đẩy và hất tung chỉ tác động lên lính; tướng chỉ nhận sát thương.", EditorStyles.wordWrappedMiniLabel);
        if (zone.FindPropertyRelative("Force").intValue == (int)HeroHitForce.KnockUp)
        {
            Field("KnockUpHeight", "Độ cao hất tung");
            Field("KnockUpDuration", "Thời gian trên không (giây)");
            Field("ForceDistance", "Đẩy ngang kèm theo (0 = tại chỗ)");
            EditorGUILayout.HelpBox("Quân bị hất tung tạm ngừng di chuyển và tấn công, sau đó rơi về mặt mesh. Vùng sát thương vẫn xét trên XZ.", MessageType.None);
        }
        else if (zone.FindPropertyRelative("Force").intValue == (int)HeroHitForce.RadialPush)
        {
            Field("PushStartRadius", "Bán kính bắt đầu đẩy");
            Field("PushEndRadius", "Bán kính kết thúc đẩy");
            Field("ForceDuration", "Thời gian đẩy (giây)");
            EditorGUILayout.HelpBox("Quân trúng đòn nằm giữa hai bán kính sẽ được đẩy ra vòng kết thúc. Quân nằm trong vòng bắt đầu hoặc ngoài vòng kết thúc không bị đẩy. Sát thương vẫn xét theo vùng đánh.", MessageType.None);
        }
        else if (zone.FindPropertyRelative("Force").intValue == (int)HeroHitForce.AxisPushX ||
                 zone.FindPropertyRelative("Force").intValue == (int)HeroHitForce.AxisPushY)
        {
            bool alongX = zone.FindPropertyRelative("Force").intValue == (int)HeroHitForce.AxisPushX;
            if (shape != HeroHitShape.Box)
                EditorGUILayout.HelpBox("Đẩy theo trục chỉ dùng cho vùng hình hộp. Hãy đổi hình dạng sang Hộp, nếu không vùng này sẽ không đẩy.", MessageType.Warning);
            Field("PushStartRadius", "Khoảng cách tới trục bắt đầu đẩy");
            Field("PushEndRadius", "Khoảng cách tới trục kết thúc đẩy");
            Field("KnockUpHeight", "Độ cao bay khi bị đẩy");
            Field("KnockUpDuration", "Thời gian đẩy (giây)");
            EditorGUILayout.HelpBox(alongX
                ? "Trục X là đường ngang qua tâm vùng. Quân phía trước trục bị đẩy tiến lên, quân phía sau bị đẩy lùi lại, tới đường kết thúc. Quân gần trục hơn khoảng bắt đầu hoặc xa hơn khoảng kết thúc không bị đẩy."
                : "Trục Y là đường dọc qua tâm vùng (hướng tướng nhìn). Quân bên trái bị đẩy sang trái, bên phải sang phải, tới đường kết thúc. Quân gần trục hơn khoảng bắt đầu hoặc xa hơn khoảng kết thúc không bị đẩy.",
                MessageType.None);
        }
        else if (zone.FindPropertyRelative("Force").intValue != 0)
        { Field("ForceDistance", "Quãng đường hút / đẩy"); Field("ForceDuration", "Thời gian hút / đẩy (giây)"); }
    }
    private static float FrameSeconds(HeroVatPlayer.Clip clip, int f) => f * clip.duration / Mathf.Max(1, clip.frameCount - 1);
    private void Changed() { EditorUtility.SetDirty(library); serialized = new SerializedObject(library); SceneView.RepaintAll(); Repaint(); }
    private void ValidateZone(HeroVatPlayer.Clip clip)
    {
        if (Zone == null) return;
        Zone.StartFrame = Mathf.Clamp(Zone.StartFrame, 0, clip.frameCount - 1);
        Zone.EndFrame = Mathf.Clamp(Zone.EndFrame, Zone.StartFrame, clip.frameCount - 1);
        if (Zone.IsSweep)
        {
            Zone.StartFrame = Mathf.Min(Zone.StartFrame, Mathf.Max(0, clip.frameCount - 2));
            Zone.EndFrame = Mathf.Clamp(Zone.EndFrame, Zone.StartFrame + 1, clip.frameCount - 1);
        }
        Zone.RepeatFrames = Mathf.Max(0, Zone.RepeatFrames);
        Zone.Radius = Mathf.Max(.01f, Zone.Radius);
        Zone.InnerRadius = Mathf.Clamp(Zone.InnerRadius, 0, Zone.Radius);
        Zone.PushStartRadius = Mathf.Max(0, Zone.PushStartRadius);
        Zone.PushEndRadius = Mathf.Max(Zone.PushStartRadius + .01f, Zone.PushEndRadius);
        Zone.BoxSize = new Vector2(Mathf.Max(.01f, Zone.BoxSize.x), Mathf.Max(.01f, Zone.BoxSize.y));
    }

    private void ImportMissing()
    {
        if (catalog == null) return;
        if (!AssetDatabase.IsValidFolder(AssetFolder)) AssetDatabase.CreateFolder("Assets/Data", "CharacterProperties");
        if (library == null)
        {
            library = CreateInstance<RecoilHeroSkillLibrary>();
            AssetDatabase.CreateAsset(library, AssetPath);
        }
        if (catalog.Skills != library)
        {
            Undo.RecordObject(catalog, "Gắn cấu hình kỹ năng tướng");
            catalog.Skills = library;
            EditorUtility.SetDirty(catalog);
        }
        Undo.RecordObject(library, "Nhập dữ liệu kỹ năng từ VAT");
        var heroes = library.Heroes.ToList();
        foreach (var entry in catalog.Heroes)
        {
            var player = entry.Prefab != null ? entry.Prefab.GetComponent<HeroVatPlayer>() : null;
            if (player == null) continue;
            var profile = heroes.Find(h => h.HeroId == entry.Id);
            if (profile == null) { profile = new HeroSkillProfile { HeroId = entry.Id, Name = entry.Prefab.name }; heroes.Add(profile); }
            var clips = profile.Clips.ToList();
            foreach (int index in new[] { 2, 4, 5 })
            {
                if (index >= player.clips.Length || clips.Exists(c => c.ClipIndex == index)) continue;
                var c = player.clips[index];
                float trigger = c.effects.Length > 0 ? Mathf.Max(0, c.effects[0].triggerFrame / Mathf.Max(1, c.sourceFps)) : c.duration * .4f;
                int hit = Mathf.Clamp(Mathf.RoundToInt(trigger / Mathf.Max(.001f, c.duration) * (c.frameCount - 1)), 0, c.frameCount - 1);
                clips.Add(new HeroSkillClip { ClipIndex = index, Zones = new[] { new HeroHitZone {
                    Name = ClipLabel(index) + " — cần căn với FX", StartFrame = hit, EndFrame = hit,
                    Radius = index == 4 ? 6 : 2, DamageMultiplier = index == 4 ? 2 : 1 } } });
            }
            profile.Clips = clips.ToArray();
        }
        library.Heroes = heroes.ToArray(); Changed(); AssetDatabase.SaveAssets();
    }

    private void OpenPreview()
    {
        try
        {
            ClosePreview();
            previousScene = SceneManager.GetActiveScene();
            previewScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            previewScene.name = "Xem thử kỹ năng tướng (tạm thời)";
            previewRoot = Instantiate(catalog.Find(Profile.HeroId));
            previewRoot.hideFlags = HideFlags.DontSave;
            RecoilPreviewCleanup.Own(previewRoot);
            SceneManager.MoveGameObjectToScene(previewRoot, previewScene);
            previewRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            preview = previewRoot.GetComponent<HeroVatPlayer>(); preview.enabled = false;
            foreach (var collider in previewRoot.GetComponentsInChildren<Collider>()) collider.enabled = false;
            targetMarker = new GameObject("Mục tiêu kỹ năng"); targetMarker.hideFlags = HideFlags.DontSave;
            RecoilPreviewCleanup.Own(targetMarker);
            SceneManager.MoveGameObjectToScene(targetMarker, previewScene);
            var clip = Clip;
            for (int i = 0; i < clip.effects.Length; i++)
            {
                if (clip.effects[i].prefab == null) continue;
                var fx = Instantiate(clip.effects[i].prefab);
                fx.hideFlags = HideFlags.DontSave; SceneManager.MoveGameObjectToScene(fx, previewScene);
                RecoilPreviewCleanup.Own(fx);
                foreach (var audio in fx.GetComponentsInChildren<AudioSource>()) audio.enabled = false;
                effects.Add(fx); effectIndices.Add(i);
            }
            if (previousScene.IsValid()) SceneManager.SetActiveScene(previousScene);
            Sample();
            SceneView.lastActiveSceneView?.Frame(new Bounds(new Vector3(0, 3, 4), Vector3.one * 22), false);
            error = null;
        }
        catch (Exception e) { error = e.Message; ClosePreview(); }
    }

    private void ClosePreview()
    {
        playing = false;
        // DontSave objects are not destroyed with their scene: remove them explicitly.
        foreach (var fx in effects) Discard(fx);
        Discard(previewRoot); Discard(targetMarker);
        effects.Clear(); effectIndices.Clear();
        preview = null; previewRoot = null; targetMarker = null;
        if (previewScene.IsValid()) EditorSceneManager.CloseScene(previewScene, true);
        previewScene = default;
        if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
    }

    private static void Discard(GameObject go)
    {
        if (go == null) return;
        RecoilPreviewCleanup.Release(go);
        DestroyImmediate(go);
    }

    private void Tick()
    {
        double now = EditorApplication.timeSinceStartup;
        if (playing && preview != null && Clip != null)
        {
            var c = Clip;
            playbackFrame += (float)(now - updated) * (c.frameCount - 1) / Mathf.Max(.001f, c.duration);
            if (playbackFrame > c.frameCount - 1) playbackFrame = 0;
            frame = Mathf.FloorToInt(playbackFrame); Sample(); Repaint();
        }
        updated = now;
    }

    private void Sample()
    {
        if (preview == null || Clip == null) return;
        var clip = Clip;
        float seconds = FrameSeconds(clip, frame);
        preview.SamplePreview(Skill.ClipIndex, seconds);
        targetMarker.transform.position = target;
        for (int i = 0; i < effects.Count; i++)
        {
            var instance = effects[i];
            var fx = clip.effects[effectIndices[i]];
            float age = seconds - fx.triggerFrame / Mathf.Max(1, clip.sourceFps);
            instance.SetActive(showFx && age >= 0);
            if (!instance.activeSelf) continue;
            int f = fx.socketPositions != null && fx.socketPositions.Length > 0 ? Mathf.Min(frame, fx.socketPositions.Length - 1) : -1;
            Vector3 socket = f >= 0 ? fx.socketPositions[f] : Vector3.zero;
            Quaternion rotation = f >= 0 && fx.socketRotations.Length > f ? fx.socketRotations[f] : Quaternion.identity;
            var rootScale = preview.transform.lossyScale;
            bool targetSpace = Skill.ClipIndex == 4 && !clip.casterCentered || clip.targetCentered;
            instance.transform.position = targetSpace ? target : Vector3.Scale(socket, rootScale) + rotation * Vector3.Scale(fx.position, rootScale);
            instance.transform.rotation = (targetSpace ? Quaternion.identity : rotation) * Quaternion.Euler(fx.rotation);
            instance.transform.localScale = Vector3.Scale(rootScale, Vector3.Scale(fx.scale,
                f >= 0 && fx.socketScales.Length > f ? fx.socketScales[f] : Vector3.one));
            foreach (var particle in instance.GetComponentsInChildren<ParticleSystem>())
                particle.Simulate(Mathf.Max(0, age), false, true, true);
            foreach (var graph in instance.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>())
            {
                graph.Reinit();
                uint steps = (uint)Mathf.FloorToInt(Mathf.Max(0, age) * 30f);
                if (steps > 0) graph.Simulate(1f / 30f, steps);
            }
        }
        SceneView.RepaintAll();
    }

    private void DrawScene(SceneView view)
    {
        if (preview == null || Skill == null || Clip == null) return;
        EditorGUI.BeginChangeCheck();
        var nextTarget = Handles.PositionHandle(target, Quaternion.identity);
        if (EditorGUI.EndChangeCheck()) { target = nextTarget; Sample(); Repaint(); }
        Handles.Label(target + Vector3.up, "Mục tiêu");
        for (int i = 0; i < Skill.Zones.Length; i++)
        {
            var z = Skill.Zones[i];
            RecoilAutoBattleDemo.HeroZonePose(z, Clip, frame, Vector3.zero, target, Quaternion.identity,
                1f, out var center, out var rotation); // authoring size
            bool active = z.Enabled && frame >= z.StartFrame && frame <= Mathf.Max(z.StartFrame, z.EndFrame);
            if (z.IsProjectile)
            {
                RecoilAutoBattleDemo.HeroZonePose(z, Clip, z.StartFrame, Vector3.zero, target, Quaternion.identity,
                    1f, out var launch, out var flightRotation);
                var end = launch + flightRotation * Vector3.forward * z.ProjectileTravel(z.EndFrame, Clip.duration, Clip.frameCount);
                Handles.color = Color.yellow;
                Handles.DrawDottedLine(launch, end, 4);
                Handles.ArrowHandleCap(0, center, flightRotation, 1, EventType.Repaint);
            }
            Handles.color = active ? new Color(1, .65f, 0, 1) : new Color(.3f, .9f, 1, .45f);
            using (new Handles.DrawingScope(Matrix4x4.TRS(center, rotation, Vector3.one)))
            {
                if (z.Shape == HeroHitShape.Box)
                    Handles.DrawWireCube(Vector3.zero, new Vector3(z.BoxSize.x, .1f, z.BoxSize.y));
                else if (z.Shape == HeroHitShape.Cone)
                {
                    float angle = z.Angle;
                    if (z.IsSweep) z.SweepSample(frame, out _, out angle);
                    var from = Quaternion.Euler(0, -angle * .5f, 0) * Vector3.forward;
                    if (active)
                    {
                        var color = Handles.color;
                        Handles.color = new Color(color.r, color.g, color.b, .18f);
                        Handles.DrawSolidArc(Vector3.zero, Vector3.up, from, angle, z.Radius);
                        Handles.color = color;
                    }
                    Handles.DrawWireArc(Vector3.zero, Vector3.up, from, angle, z.Radius);
                    Handles.DrawLine(Vector3.zero, from * z.Radius);
                    Handles.DrawLine(Vector3.zero, Quaternion.Euler(0, angle * .5f, 0) * Vector3.forward * z.Radius);
                }
                else
                {
                    Handles.DrawWireDisc(Vector3.zero, Vector3.up, z.Radius);
                    if (z.Shape == HeroHitShape.Ring) Handles.DrawWireDisc(Vector3.zero, Vector3.up, z.InnerRadius);
                }
            }
            Handles.Label(center + Vector3.up * .5f, $"{z.Name} [{z.StartFrame}–{z.EndFrame}] ×{z.DamageMultiplier}");
            if (i != zoneIndex) continue;
            EditorGUI.BeginChangeCheck();
            var nextCenter = Handles.PositionHandle(center, rotation);
            float radius = z.Shape == HeroHitShape.Box ? z.Radius : Handles.RadiusHandle(Quaternion.identity, center, z.Radius);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(library, "Di chuyển vùng sát thương");
                float sweepYaw = 0;
                if (z.IsSweep) z.SweepSample(frame, out sweepYaw, out _);
                z.Offset += Quaternion.Inverse(rotation * Quaternion.Euler(0, -z.Yaw - sweepYaw, 0)) * (nextCenter - center);
                z.Radius = Mathf.Max(.01f, radius); Changed();
            }
            if (z.Force != HeroHitForce.None)
            {
                if (z.Force == HeroHitForce.RadialPush)
                {
                    Handles.color = new Color(.3f, 1, .4f, .8f);
                    Handles.DrawWireDisc(center, Vector3.up, z.PushStartRadius);
                    Handles.Label(center + Vector3.right * z.PushStartRadius, "Bắt đầu đẩy");
                    Handles.color = new Color(1, .3f, .2f, .8f);
                    Handles.DrawWireDisc(center, Vector3.up, z.PushEndRadius);
                    Handles.Label(center + Vector3.right * z.PushEndRadius, "Kết thúc đẩy");
                    DrawRadialPushPreview(z, center);
                }
                if (z.IsAxisPush)
                {
                    DrawAxisPushPreview(z, center, rotation);
                    continue;
                }
                var arrow = z.Force == HeroHitForce.KnockUp ? Vector3.up : z.Force == HeroHitForce.Pull ? -(rotation * Vector3.forward) : rotation * Vector3.forward;
                Handles.ArrowHandleCap(0, center + Vector3.up, Quaternion.LookRotation(arrow),
                    z.Force == HeroHitForce.KnockUp ? z.KnockUpHeight : z.Force == HeroHitForce.RadialPush ?
                    Mathf.Max(.01f, z.PushEndRadius - z.PushStartRadius) : z.ForceDistance, EventType.Repaint);
            }
        }
    }

    private void DrawAxisPushPreview(HeroHitZone zone, Vector3 center, Quaternion rotation)
    {
        float start = Mathf.Max(0, zone.PushStartRadius), end = Mathf.Max(start, zone.PushEndRadius);
        float sinceHit = FrameSeconds(Clip, frame) - FrameSeconds(Clip, zone.StartFrame);
        float current = Mathf.Lerp(start, end, Mathf.Clamp01(sinceHit / Mathf.Max(.03f, zone.KnockUpDuration)));
        bool alongX = zone.Force == HeroHitForce.AxisPushX;
        // Local frame: "out" is the push direction, "line" runs along the axis.
        var outward = rotation * (alongX ? Vector3.forward : Vector3.right);
        var line = rotation * (alongX ? Vector3.right : Vector3.forward);
        float half = (alongX ? zone.BoxSize.x : zone.BoxSize.y) * .5f; // the axis spans the box
        var oldDepth = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        center += Vector3.up * .08f;
        Handles.color = new Color(1, 1, 1, .9f);
        Handles.DrawLine(center - line * half, center + line * half, 3);
        Handles.Label(center + line * half, alongX ? "Trục X" : "Trục Y");
        for (int side = -1; side <= 1; side += 2)
        {
            var o = outward * side;
            Handles.color = new Color(.3f, 1, .4f, .8f);
            Handles.DrawLine(center + o * start - line * half, center + o * start + line * half);
            Handles.color = new Color(1, .3f, .2f, .8f);
            Handles.DrawLine(center + o * end - line * half, center + o * end + line * half);
            Handles.color = new Color(1, .85f, .05f, 1);
            Handles.DrawLine(center + o * current - line * half, center + o * current + line * half, 2);
            for (int i = -2; i <= 2; i++)
                Handles.ArrowHandleCap(0, center + o * current + line * (half * i * .4f), Quaternion.LookRotation(o),
                    Mathf.Clamp((end - start) * .18f, .3f, 1.2f), EventType.Repaint);
        }
        Handles.Label(center + outward * end + Vector3.up * 1.5f, $"Đẩy từ trục: {start:0.##} → {end:0.##} | hiện tại: {current:0.##}");
        Handles.zTest = oldDepth;
    }

    private void DrawRadialPushPreview(HeroHitZone zone, Vector3 center)
    {
        float sinceHit = FrameSeconds(Clip, frame) - FrameSeconds(Clip, zone.StartFrame);
        float progress = Mathf.Clamp01(sinceHit / Mathf.Max(.03f, zone.ForceDuration));
        float start = Mathf.Max(0, zone.PushStartRadius);
        float end = Mathf.Max(start, zone.PushEndRadius);
        float radius = Mathf.Lerp(start, end, progress);
        var oldDepth = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        center += Vector3.up * .08f;
        Handles.color = new Color(1, .85f, .05f, .9f);
        Handles.DrawWireDisc(center, Vector3.up, radius);
        if (start == 0) Handles.SphereHandleCap(0, center, Quaternion.identity, .25f, EventType.Repaint);
        for (int i = 0; i < 12; i++)
        {
            var direction = Quaternion.Euler(0, i * 30, 0) * Vector3.forward;
            Handles.color = new Color(1, .85f, .05f, .45f);
            Handles.DrawDottedLine(center + direction * start, center + direction * end, 4);
            Handles.color = new Color(1, .85f, .05f, 1);
            var point = center + direction * radius;
            Handles.SphereHandleCap(0, point, Quaternion.identity, .22f, EventType.Repaint);
            Handles.ArrowHandleCap(0, point, Quaternion.LookRotation(direction),
                Mathf.Clamp((end - start) * .18f, .3f, 1.2f), EventType.Repaint);
        }
        Handles.Label(center + Vector3.up * 1.5f,
            $"Đẩy từ tâm: {start:0.##} → {end:0.##} | {progress * 100:0}% | hiện tại: {radius:0.##}");
        Handles.zTest = oldDepth;
    }
}
