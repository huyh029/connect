using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CongThanh.EditorTools
{
    /// <summary>
    /// Test of the battle effects:
    /// <list type="bullet">
    /// <item>Outside Play: previews each effect prefab in the Scene view (at the view's centre), played once.</item>
    /// <item>In a match: summon Quân sư / siege engines, play the Quân sư effects on a live advisor, drop a test siege
    /// stone blast (explosion + toss, optional damage) on an army.</item>
    /// </list>
    /// </summary>
    public sealed class CongThanhEffectTestTool : EditorWindow
    {
        private static readonly string[] Sides = { "Xanh", "Đỏ" };
        private static readonly string[] Modes = { "Trận thật / xem trước", "Sân test Quân sư", "Sân test nổ" };
        private const string BlastScenePath = "Assets/Scenes/ExplosionTestGround.unity";
        private int _blastSoldier = -1;
        private const string GroundScenePath = "Assets/Scenes/AdvisorTestGround.unity";
        private const string VatFolder = "Assets/Data/Characters/Prefabs VAT";
        private const string ReturnSceneKey = "CongThanhEffectTest.ReturnScene";

        private int _mode;
        private int _groundSide;
        private int _groundTarget = -1;
        private Vector2 _groundScroll;
        private SerializedObject _settings;

        private int _side;
        private int _summonAmount = 5;
        private float _blastRadius = 4f;
        private int _blastDamage;
        private float _previewScale = 1f;
        private string _status = "";
        private double _nextRepaint;

        // Following one Quân sư.
        private bool _watching;
        private bool _followCamera = true;
        private float _speed = 1f;
        private Vector2 _logScroll;
        private Vector3 _watchPosition, _watchTarget;
        private bool _watchHasTarget;

        // Edit-mode preview.
        private GameObject _preview;
        private ParticleSystem[] _previewParticles;
        private double _previewStarted;

        [MenuItem("Game Tools/Công Thành/Battle/Test hiệu ứng")]
        private static void Open()
        {
            var window = GetWindow<CongThanhEffectTestTool>("Test hiệu ứng");
            window.minSize = new Vector2(380f, 360f);
            window.Show();
        }

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
            EditorSceneManager.sceneClosing += StopPreviewOnSceneClose;
            EditorApplication.update += Tick;
            SceneView.duringSceneGui += DrawWatchInScene;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
            EditorSceneManager.sceneClosing -= StopPreviewOnSceneClose;
            SceneView.duringSceneGui -= DrawWatchInScene;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            StopPreview();
            SetSpeed(1f);
        }

        private void StopPreviewOnSceneClose(Scene scene, bool removing) => StopPreview();

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) StopPreview();
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            _watching = false;
            SetSpeed(1f);
            Time.timeScale = 1f;
            // Keep what was tuned on the test ground.
            var settings = RecoilBattleEffects.Load();
            if (settings != null) { EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings); }
        }

        /// <summary>Battle speed (simulation and particles together).</summary>
        private void SetSpeed(float speed)
        {
            _speed = speed;
            if (!EditorApplication.isPlaying) return;
            RecoilSimulationClock.Speed = speed;
            Time.timeScale = speed;
        }

        private void Tick()
        {
            if (_preview != null) UpdatePreview();
            if (_watching && EditorApplication.isPlaying) FollowWatched();
            if (EditorApplication.isPlaying && EditorApplication.timeSinceStartup >= _nextRepaint)
            {
                _nextRepaint = EditorApplication.timeSinceStartup + (_watching || _mode == 1 ? .05 : .5);
                Repaint();
            }
        }

        private void OnGUI()
        {
            _mode = GUILayout.Toolbar(_mode, Modes, GUILayout.Height(24f));
            if (_mode == 1) DrawGround();
            else if (_mode == 2) DrawBlastGround();
            else if (!EditorApplication.isPlaying) DrawPreview();
            else DrawLive();
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.None);
        }

        // ---------------------------------------------------------------- explosion test ground

        private void DrawBlastGround()
        {
            var ground = EditorApplication.isPlaying ? Object.FindFirstObjectByType<RecoilExplosionTestGround>() : null;
            if (ground != null)
            {
                _groundScroll = EditorGUILayout.BeginScrollView(_groundScroll);
                DrawBlastLive(ground);
                EditorGUILayout.EndScrollView();
                return;
            }
            EditorGUILayout.HelpBox("Sân test nổ: scene nhẹ riêng (" + BlastScenePath + ") có một vòng lính đứng quanh tâm. " +
                                    "Thả đá nổ để xem hiệu ứng, nghe tiếng nổ và xem lính bị hất (đúng công thức trong trận); " +
                                    "thử cả vụ nổ thiên thạch cuối trận. Chỉnh xong được lưu vào Resources/RecoilBattleEffects.",
                MessageType.Info);
            if (EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Đang Play một scene khác: dừng Play rồi mở sân test.", MessageType.Warning);
                return;
            }
            var prefabs = TargetPrefabs();
            if (prefabs.Length == 0)
            {
                EditorGUILayout.HelpBox("Không thấy prefab VAT trong " + VatFolder + ".", MessageType.Warning);
                return;
            }
            var names = prefabs.Select(p => Path.GetFileNameWithoutExtension(p).Replace(" VAT", "")).ToArray();
            if (_blastSoldier < 0 || _blastSoldier >= names.Length)
                _blastSoldier = Mathf.Max(0, System.Array.FindIndex(names, n => n == "BO_BINH_red"));
            _blastSoldier = EditorGUILayout.Popup("Loại lính đứng", _blastSoldier, names);
            if (GUILayout.Button("▶ Mở sân test nổ (vào Play)", GUILayout.Height(30f))) OpenBlastGround(prefabs[_blastSoldier]);
            var back = SessionState.GetString(ReturnSceneKey, "");
            if (!string.IsNullOrEmpty(back) && SceneManager.GetActiveScene().path == BlastScenePath &&
                GUILayout.Button("Quay lại scene trước (" + Path.GetFileNameWithoutExtension(back) + ")"))
            {
                EditorSceneManager.OpenScene(back, OpenSceneMode.Single);
                SessionState.EraseString(ReturnSceneKey);
            }
        }

        private void OpenBlastGround(string soldierPath)
        {
            var soldier = AssetDatabase.LoadAssetAtPath<GameObject>(soldierPath);
            if (soldier == null) { _status = "Không thấy " + soldierPath; return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var current = SceneManager.GetActiveScene().path;
            if (!string.IsNullOrEmpty(current) && current != BlastScenePath && current != GroundScenePath)
                SessionState.SetString(ReturnSceneKey, current);
            Scene scene;
            if (File.Exists(BlastScenePath)) scene = EditorSceneManager.OpenScene(BlastScenePath, OpenSceneMode.Single);
            else
            {
                scene = CreateGroundScene(BlastScenePath, typeof(RecoilExplosionTestGround), "Sân test nổ");
                var cameraObject = scene.GetRootGameObjects().FirstOrDefault(go => go.GetComponent<Camera>() != null);
                if (cameraObject != null)
                {
                    cameraObject.transform.position = new Vector3(0f, 14f, -16f);
                    cameraObject.transform.LookAt(new Vector3(0f, 1f, 0f));
                }
                EditorSceneManager.SaveScene(scene);
            }
            var ground = scene.GetRootGameObjects().Select(go => go.GetComponentInChildren<RecoilExplosionTestGround>(true))
                .FirstOrDefault(found => found != null);
            if (ground == null)
            {
                ground = new GameObject("Sân test nổ", typeof(RecoilExplosionTestGround)).GetComponent<RecoilExplosionTestGround>();
                SceneManager.MoveGameObjectToScene(ground.gameObject, scene);
            }
            ground.soldierPrefab = soldier;
            EditorUtility.SetDirty(ground);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            _status = null;
            EditorApplication.isPlaying = true;
        }

        private void DrawBlastLive(RecoilExplosionTestGround ground)
        {
            var settings = RecoilBattleEffects.Load();
            if (settings == null)
            {
                EditorGUILayout.HelpBox("Không tìm thấy Resources/RecoilBattleEffects.asset.", MessageType.Warning);
                return;
            }
            if (_settings == null || _settings.targetObject != settings) _settings = new SerializedObject(settings);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("💥 Nổ ở tâm", GUILayout.Height(28f))) ground.SiegeBlast(Vector3.zero);
                if (GUILayout.Button("💥 Nổ chỗ ngẫu nhiên", GUILayout.Height(28f))) ground.SiegeBlastRandom();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("☄ Nổ thiên thạch cuối trận")) ground.EndgameBlast();
                if (GUILayout.Button("↺ Đặt lại lính")) ground.ResetSoldiers();
            }
            ground.killOnBlast = EditorGUILayout.ToggleLeft("Lính trúng nổ chết luôn (vừa bay vừa chết)", ground.killOnBlast);
            using (new EditorGUILayout.HorizontalScope())
            {
                ground.autoRepeat = EditorGUILayout.ToggleLeft("Tự nổ lặp lại mỗi", ground.autoRepeat, GUILayout.Width(130f));
                ground.repeatSeconds = EditorGUILayout.Slider(ground.repeatSeconds, .5f, 10f);
            }
            var speed = EditorGUILayout.Slider("Tốc độ", Time.timeScale, .05f, 1f);
            if (!Mathf.Approximately(speed, Time.timeScale)) Time.timeScale = speed;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("×0.1")) Time.timeScale = .1f;
                if (GUILayout.Button("×0.25")) Time.timeScale = .25f;
                if (GUILayout.Button("×0.5")) Time.timeScale = .5f;
                if (GUILayout.Button("×1")) Time.timeScale = 1f;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Sân", EditorStyles.boldLabel);
            ground.blastRadius = EditorGUILayout.Slider("Bán kính nổ", ground.blastRadius, .5f, 12f);
            EditorGUI.BeginChangeCheck();
            ground.soldierCount = EditorGUILayout.IntSlider("Số lính", ground.soldierCount, 1, 80);
            ground.formationRadius = EditorGUILayout.Slider("Bán kính vòng lính", ground.formationRadius, .5f, 12f);
            if (EditorGUI.EndChangeCheck()) ground.Rebuild();
            ground.endgameScale = EditorGUILayout.Slider("Thiên thạch to gấp", ground.endgameScale, .5f, 6f);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Tiếng nổ và hất tung (lưu vào RecoilBattleEffects, trận thật dùng chung)", EditorStyles.boldLabel);
            _settings.Update();
            void Field(string name, string label) => EditorGUILayout.PropertyField(_settings.FindProperty(name), new GUIContent(label));
            Field("ExplosionSound", "Tiếng nổ");
            Field("SiegeExplosionVolume", "Âm lượng nổ đá");
            Field("SiegeExplosionMinInterval", "Khoảng cách 2 tiếng nổ đá (giây)");
            Field("EndgameExplosionVolume", "Âm lượng nổ cuối trận");
            Field("SiegeImpactPrefab", "Hiệu ứng nổ");
            Field("SiegeBlastToss", "Hất tung");
            Field("SiegeBlastHeight", "Độ cao tối đa");
            Field("SiegeBlastDistance", "Quãng văng tối đa");
            Field("SiegeBlastDuration", "Thời gian trên không (giây)");
            Field("SiegeBlastRandomness", "Độ ngẫu nhiên");
            Field("SiegeBlastAngleJitter", "Lệch hướng tối đa (độ)");
            if (_settings.ApplyModifiedProperties()) EditorUtility.SetDirty(settings);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("💾 Lưu"))
                {
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssetIfDirty(settings);
                    _status = "Đã lưu vào Resources/RecoilBattleEffects.asset.";
                }
                if (GUILayout.Button("Chọn asset")) Selection.activeObject = settings;
            }

            EditorGUILayout.Space(4f);
            var log = ground.Log;
            EditorGUILayout.LabelField("Nhật ký (mới nhất ở trên)", EditorStyles.miniBoldLabel);
            for (var i = log.Count - 1; i >= 0 && i >= log.Count - 12; i--)
                EditorGUILayout.LabelField($"  {Time.time - log[i].Time,5:0.00}s trước   {log[i].Text}", EditorStyles.miniLabel);
        }

        // ---------------------------------------------------------------- Quân sư test ground

        private static string[] TargetPrefabs()
        {
            if (!AssetDatabase.IsValidFolder(VatFolder)) return new string[0];
            return AssetDatabase.FindAssets("t:GameObject", new[] { VatFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(" VAT.prefab") && Path.GetDirectoryName(p).Replace('\\', '/') == VatFolder)
                .OrderBy(p => p).ToArray();
        }

        private void DrawGround()
        {
            var ground = EditorApplication.isPlaying ? Object.FindFirstObjectByType<RecoilAdvisorTestGround>() : null;
            if (ground == null)
            {
                DrawGroundSetup();
                return;
            }
            _groundScroll = EditorGUILayout.BeginScrollView(_groundScroll);
            DrawGroundLive(ground);
            EditorGUILayout.EndScrollView();
        }

        private void DrawGroundSetup()
        {
            EditorGUILayout.HelpBox("Sân test: scene nhẹ riêng (" + GroundScenePath + ") chỉ có 1 Quân sư và 1 mục tiêu đứng đối diện. " +
                                    "Quân sư đánh lặp lại với đúng hiệu ứng, thời điểm và vị trí của trận thật; chỉnh trong tool là thấy ngay, " +
                                    "và được lưu vào Resources/RecoilBattleEffects (trận thật dùng chung).", MessageType.Info);
            if (EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Đang Play một scene khác: dừng Play rồi mở sân test.", MessageType.Warning);
                return;
            }
            _groundSide = GUILayout.Toolbar(_groundSide, new[] { "Quân sư xanh", "Quân sư đỏ" });
            var prefabs = TargetPrefabs();
            if (prefabs.Length == 0)
            {
                EditorGUILayout.HelpBox("Không thấy prefab VAT trong " + VatFolder + ".", MessageType.Warning);
                return;
            }
            var names = prefabs.Select(p => Path.GetFileNameWithoutExtension(p).Replace(" VAT", "")).ToArray();
            if (_groundTarget < 0 || _groundTarget >= names.Length)
                _groundTarget = Mathf.Max(0, System.Array.FindIndex(names, n => n == (_groundSide == 0 ? "BO_BINH_red" : "BO_BINH_blue")));
            _groundTarget = EditorGUILayout.Popup("Mục tiêu", _groundTarget, names);
            if (GUILayout.Button("▶ Mở sân test (vào Play)", GUILayout.Height(30f))) OpenGround(prefabs[_groundTarget]);

            var back = SessionState.GetString(ReturnSceneKey, "");
            if (!string.IsNullOrEmpty(back) && SceneManager.GetActiveScene().path is GroundScenePath or BlastScenePath &&
                GUILayout.Button("Quay lại scene trước (" + Path.GetFileNameWithoutExtension(back) + ")"))
            {
                EditorSceneManager.OpenScene(back, OpenSceneMode.Single);
                SessionState.EraseString(ReturnSceneKey);
            }
        }

        private void OpenGround(string targetPath)
        {
            var advisorPath = $"{VatFolder}/QUAN_SU_{(_groundSide == 0 ? "blue" : "red")} VAT.prefab";
            var advisor = AssetDatabase.LoadAssetAtPath<GameObject>(advisorPath);
            var target = AssetDatabase.LoadAssetAtPath<GameObject>(targetPath);
            if (advisor == null) { _status = "Không thấy " + advisorPath; return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var current = SceneManager.GetActiveScene().path;
            if (!string.IsNullOrEmpty(current) && current != GroundScenePath && current != BlastScenePath)
                SessionState.SetString(ReturnSceneKey, current);
            var scene = File.Exists(GroundScenePath)
                ? EditorSceneManager.OpenScene(GroundScenePath, OpenSceneMode.Single)
                : CreateGroundScene(GroundScenePath, typeof(RecoilAdvisorTestGround), "Sân test Quân sư");
            var ground = scene.GetRootGameObjects().Select(go => go.GetComponentInChildren<RecoilAdvisorTestGround>(true))
                .FirstOrDefault(found => found != null);
            if (ground == null)
            {
                ground = new GameObject("Sân test Quân sư", typeof(RecoilAdvisorTestGround)).GetComponent<RecoilAdvisorTestGround>();
                SceneManager.MoveGameObjectToScene(ground.gameObject, scene);
            }
            ground.advisorPrefab = advisor;
            ground.targetPrefab = target;
            EditorUtility.SetDirty(ground);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            _status = null;
            EditorApplication.isPlaying = true;
        }

        /// <summary>A bare scene: camera on the side, a sun, a flat ground, and a <paramref name="ground"/> component.</summary>
        private static Scene CreateGroundScene(string path, System.Type ground, string groundName)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(9f, 6f, -4f);
            camera.transform.LookAt(new Vector3(0f, 1.5f, 1.5f));
            camera.GetComponent<Camera>().fieldOfView = 45f;
            var sun = new GameObject("Directional Light", typeof(Light));
            var light = sun.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Ground";
            floor.transform.localScale = new Vector3(4f, 1f, 4f);
            new GameObject(groundName, ground);
            EditorSceneManager.SaveScene(scene, path);
            return scene;
        }

        private void DrawGroundLive(RecoilAdvisorTestGround ground)
        {
            var settings = RecoilBattleEffects.Load();
            if (settings == null)
            {
                EditorGUILayout.HelpBox("Không tìm thấy Resources/RecoilBattleEffects.asset.", MessageType.Warning);
                return;
            }
            if (_settings == null || _settings.targetObject != settings) _settings = new SerializedObject(settings);

            // Timeline of the current strike.
            var duration = Mathf.Max(.01f, ground.AttackDuration);
            var t = Mathf.Clamp01(ground.StrikeTime / duration);
            var bar = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(bar, ground.StrikeTime >= 0f && ground.StrikeTime <= duration ? t : 0f,
                $"Đòn đánh {Mathf.Clamp(ground.StrikeTime, 0f, duration):0.00} / {duration:0.00}s");
            var launchX = bar.x + bar.width * Mathf.Clamp01(ground.LaunchDelay / duration);
            EditorGUI.DrawRect(new Rect(launchX - 1f, bar.y - 2f, 3f, bar.height + 4f), new Color(.3f, .7f, 1f));
            var hitX = bar.x + bar.width * Mathf.Clamp01(ground.HitDelay / duration);
            EditorGUI.DrawRect(new Rect(hitX - 1f, bar.y - 2f, 3f, bar.height + 4f), new Color(1f, .3f, .2f));
            EditorGUILayout.LabelField(" ", $"vạch xanh = phóng cầu ({ground.LaunchDelay:0.00}s) · vạch đỏ = cầu tới, thiêu ({ground.HitDelay:0.00}s)" +
                (ground.HitDelay > duration ? " — sau khi hoạt ảnh xong" : ""), EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("⚔ Đánh 1 đòn")) ground.Strike();
                if (GUILayout.Button("✧ Triệu hồi")) ground.PlaySummon();
                ground.autoRepeat = GUILayout.Toggle(ground.autoRepeat, "Đánh lặp", "Button");
            }
            var speed = EditorGUILayout.Slider("Tốc độ", Time.timeScale, .05f, 1f);
            if (!Mathf.Approximately(speed, Time.timeScale)) Time.timeScale = speed;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("×0.1")) Time.timeScale = .1f;
                if (GUILayout.Button("×0.25")) Time.timeScale = .25f;
                if (GUILayout.Button("×0.5")) Time.timeScale = .5f;
                if (GUILayout.Button("×1")) Time.timeScale = 1f;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Sân", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            ground.distance = EditorGUILayout.Slider("Khoảng cách tới mục tiêu", ground.distance, .5f, 12f);
            if (EditorGUI.EndChangeCheck()) ground.ApplyDistance();
            ground.pause = EditorGUILayout.Slider("Nghỉ giữa hai đòn (giây)", ground.pause, 0f, 3f);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Hiệu ứng (lưu vào RecoilBattleEffects, trận thật dùng chung)", EditorStyles.boldLabel);
            _settings.Update();
            EditorGUI.BeginChangeCheck();
            void Field(string name, string label) => EditorGUILayout.PropertyField(_settings.FindProperty(name), new GUIContent(label));
            EditorGUILayout.LabelField("Ra đòn (vòng phép dưới chân)", EditorStyles.miniBoldLabel);
            Field("AdvisorCastPrefab", "Prefab");
            Field("AdvisorCastScale", "Tỉ lệ");
            Field("AdvisorCastOffset", "Vị trí (phải, lên, trước)");
            Field("AdvisorCastHide", "Ẩn các phần");
            EditorGUILayout.LabelField("Quả cầu", EditorStyles.miniBoldLabel);
            Field("AdvisorOrbCount", "Số quả cầu");
            Field("AdvisorOrbRadius", "Bán kính xoay");
            Field("AdvisorOrbHeight", "Độ cao xoay");
            Field("AdvisorOrbSpin", "Tốc độ xoay (độ/giây)");
            Field("AdvisorOrbSize", "Kích thước");
            Field("AdvisorOrbTrail", "Vệt sáng (giây)");
            Field("AdvisorOrbLaunch", "Lúc phóng (phần của hoạt ảnh)");
            Field("AdvisorOrbFlight", "Thời gian bay (giây)");
            Field("AdvisorOrbArc", "Độ vồng");
            Field("AdvisorOrbTargetHeight", "Cao hơn chân mục tiêu");
            Field("AdvisorOrbSource", "Prefab lấy hình cầu (trống = prefab ra đòn)");
            Field("AdvisorOrbChild", "Phần dùng làm cầu");
            EditorGUILayout.LabelField("Cầu tới mục tiêu (thiêu)", EditorStyles.miniBoldLabel);
            Field("AdvisorHitPrefab", "Prefab");
            Field("AdvisorHitScale", "Tỉ lệ");
            Field("AdvisorHitOffset", "Vị trí (phải, lên, theo hướng đòn)");
            EditorGUILayout.LabelField("Triệu hồi", EditorStyles.miniBoldLabel);
            Field("AdvisorSummonPrefab", "Prefab");
            Field("AdvisorSummonScale", "Tỉ lệ");
            var before = (settings.AdvisorCastPrefab, settings.AdvisorHitPrefab, settings.AdvisorSummonPrefab,
                settings.AdvisorOrbSource, settings.AdvisorOrbChild, settings.AdvisorCastHide, settings.AdvisorOrbTrail);
            if (EditorGUI.EndChangeCheck())
            {
                _settings.ApplyModifiedProperties();
                // Prefabs, hidden parts or the orb look changed: rebuild the copies.
                if (before != (settings.AdvisorCastPrefab, settings.AdvisorHitPrefab, settings.AdvisorSummonPrefab,
                        settings.AdvisorOrbSource, settings.AdvisorOrbChild, settings.AdvisorCastHide, settings.AdvisorOrbTrail))
                    ground.ResetEffects();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("💾 Lưu"))
                {
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssetIfDirty(settings);
                    _status = "Đã lưu vào Resources/RecoilBattleEffects.asset.";
                }
                if (GUILayout.Button("Chọn asset")) Selection.activeObject = settings;
            }

            EditorGUILayout.Space(4f);
            var log = ground.Log;
            EditorGUILayout.LabelField("Nhật ký (mới nhất ở trên)", EditorStyles.miniBoldLabel);
            for (var i = log.Count - 1; i >= 0 && i >= log.Count - 12; i--)
                EditorGUILayout.LabelField($"  {Time.time - log[i].Time,5:0.00}s trước   {log[i].Text}", EditorStyles.miniLabel);
        }

        // ---------------------------------------------------------------- outside Play: preview

        private void DrawPreview()
        {
            EditorGUILayout.HelpBox("Ngoài Play: bấm để phát thử hiệu ứng một lần ở giữa khung Scene. " +
                                    "Vào Play và bắt đầu trận để test trong trận thật (Quân sư, nổ đá, hất tung).", MessageType.Info);
            var effects = Resources.Load<RecoilBattleEffects>("RecoilBattleEffects");
            if (effects == null)
            {
                EditorGUILayout.HelpBox("Không tìm thấy Resources/RecoilBattleEffects.asset.", MessageType.Warning);
                return;
            }
            _previewScale = EditorGUILayout.Slider("Tỉ lệ xem trước", _previewScale, .2f, 4f);
            PreviewButton("Quân sư được triệu hồi (Buff 1)", effects.AdvisorSummonPrefab);
            PreviewButton("Quân sư ra đòn (Buff 2)", effects.AdvisorCastPrefab);
            PreviewButton("Quân sư đánh trúng (Buff 5)", effects.AdvisorHitPrefab);
            PreviewButton("Đá máy bắn đá nổ", effects.SiegeImpactPrefab);
            using (new EditorGUI.DisabledScope(_preview == null))
                if (GUILayout.Button("Dừng xem trước")) StopPreview();
            EditorGUILayout.Space(6f);
            if (GUILayout.Button("▶ Vào Play", GUILayout.Height(28f))) EditorApplication.isPlaying = true;
        }

        private void PreviewButton(string label, GameObject prefab)
        {
            using (new EditorGUI.DisabledScope(prefab == null))
                if (GUILayout.Button(prefab != null ? $"{label}  —  {prefab.name}" : label + " (chưa gán prefab)"))
                    StartPreview(prefab);
        }

        private void StartPreview(GameObject prefab)
        {
            StopPreview();
            var view = SceneView.lastActiveSceneView;
            var position = view != null ? view.pivot : Vector3.zero;
            _preview = Instantiate(prefab, position, prefab.transform.rotation);
            _preview.name = "[Xem trước] " + prefab.name;
            _preview.hideFlags = HideFlags.DontSave;
            RecoilPreviewCleanup.Own(_preview);
            _preview.transform.localScale = prefab.transform.localScale * _previewScale;
            _previewParticles = _preview.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var particle in _previewParticles)
            {
                var main = particle.main;
                main.loop = false;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            _previewStarted = EditorApplication.timeSinceStartup;
            _status = $"Đang xem trước {prefab.name} tại giữa khung Scene.";
        }

        private void UpdatePreview()
        {
            var elapsed = (float)(EditorApplication.timeSinceStartup - _previewStarted);
            var alive = false;
            foreach (var particle in _previewParticles)
            {
                if (particle == null) continue;
                // Replayed from the start each tick: exact time, no need for Play Mode.
                particle.Simulate(elapsed, false, true, true);
                alive |= particle.particleCount > 0 || elapsed < particle.main.duration;
            }
            SceneView.RepaintAll();
            if (!alive || elapsed > 8f) StopPreview();
        }

        private void StopPreview()
        {
            if (_preview != null) { RecoilPreviewCleanup.Release(_preview); DestroyImmediate(_preview); }
            _preview = null;
            _previewParticles = null;
            SceneView.RepaintAll();
        }

        // ---------------------------------------------------------------- in Play: live test

        private void DrawLive()
        {
            var demo = Object.FindFirstObjectByType<RecoilAutoBattleDemo>();
            if (demo == null)
            {
                EditorGUILayout.HelpBox("Không có RecoilAutoBattleDemo trong scene đang chạy.", MessageType.Warning);
                return;
            }
            if (!demo.TestMatchRunning)
            {
                EditorGUILayout.HelpBox("Chưa vào trận.", MessageType.Info);
                if (GUILayout.Button("Bắt đầu trận (có lính)", GUILayout.Height(28f)))
                    demo.TestRunHeroBattle(new List<int>(), new List<bool>(), false, true);
                if (!string.IsNullOrEmpty(demo.TestStatus)) EditorGUILayout.LabelField(demo.TestStatus);
                return;
            }

            _side = GUILayout.Toolbar(_side, Sides);
            var blue = _side == 0;
            EditorGUILayout.LabelField(" ", $"Phe {Sides[_side]}: {demo.TestAliveCount(blue, -1)} lính · " +
                $"{demo.TestAliveCount(blue, RecoilAutoBattleDemo.TestAdvisorType)} Quân sư · " +
                $"{demo.TestAliveCount(blue, RecoilAutoBattleDemo.TestSiegeType)} máy bắn đá", EditorStyles.miniBoldLabel);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Quân sư", EditorStyles.boldLabel);
            demo.TestAdvisorEffectsEnabled = EditorGUILayout.ToggleLeft("Bật hiệu ứng Quân sư", demo.TestAdvisorEffectsEnabled);
            _summonAmount = EditorGUILayout.IntSlider("Số lượng triệu hồi", _summonAmount, 1, 50);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Triệu hồi Quân sư"))
                {
                    demo.TestSummonSoldiers(blue, RecoilAutoBattleDemo.TestAdvisorType, _summonAmount);
                    _status = $"Đã gọi {_summonAmount} Quân sư phe {Sides[_side]} (ra lần lượt, mỗi con có vòng phép).";
                }
                if (GUILayout.Button("Triệu hồi máy bắn đá"))
                {
                    demo.TestSummonSoldiers(blue, RecoilAutoBattleDemo.TestSiegeType, _summonAmount);
                    _status = $"Đã gọi {_summonAmount} máy bắn đá phe {Sides[_side]}.";
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Vòng phép (Buff 1)"))
                    _status = demo.TestPlayAdvisorEffect(blue, 0) ? "Phát vòng phép trên một Quân sư." : NoAdvisor();
                if (GUILayout.Button("Ra đòn + trúng (Buff 2 + 5)"))
                    _status = demo.TestPlayAdvisorEffect(blue, 1) ? "Phát hiệu ứng ra đòn của một Quân sư." : NoAdvisor();
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Nổ đá máy bắn đá", EditorStyles.boldLabel);
            demo.TestTossEnabled = EditorGUILayout.ToggleLeft("Hất tung khi nổ", demo.TestTossEnabled);
            _blastRadius = EditorGUILayout.Slider("Bán kính nổ", _blastRadius, .5f, 12f);
            _blastDamage = EditorGUILayout.IntField(new GUIContent("Sát thương ở tâm", "0 = chỉ hất, không mất máu. Đặt lớn để test lính vừa chết vừa bị hất."), Mathf.Max(0, _blastDamage));
            if (GUILayout.Button($"Thả đá nổ vào phe {Sides[_side]}", GUILayout.Height(26f)))
                _status = demo.TestSiegeBlast(blue, _blastRadius, _blastDamage)
                    ? $"Nổ vào một lính phe {Sides[_side]} (bán kính {_blastRadius:0.#}, sát thương {_blastDamage})."
                    : $"Phe {Sides[_side]} không còn lính nào đang hiện.";
            EditorGUILayout.HelpBox("Chỉnh độ cao, quãng văng, độ ngẫu nhiên của cú hất và tỉ lệ hiệu ứng Quân sư trong Inspector " +
                                    "của RecoilAutoBattleDemo (nhóm \"Hất tung khi đá máy bắn đá nổ\" và \"Hiệu ứng Quân sư\").",
                MessageType.None);
            if (GUILayout.Button("Chọn RecoilAutoBattleDemo")) Selection.activeObject = demo;

            DrawWatch(demo, blue);
        }

        // ---------------------------------------------------------------- following one Quân sư

        private void DrawWatch(RecoilAutoBattleDemo demo, bool blue)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Chuỗi tấn công của một Quân sư", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_watching ? "Đổi Quân sư khác" : $"Theo dõi một Quân sư phe {Sides[_side]}"))
                {
                    _watching = demo.TestWatchAdvisor(blue);
                    _status = _watching ? "Đang theo dõi. Khung Scene bám theo; đường đỏ nối tới mục tiêu." : NoAdvisor();
                }
                using (new EditorGUI.DisabledScope(!_watching))
                    if (GUILayout.Button("Thôi theo dõi", GUILayout.Width(100f)))
                    {
                        _watching = false;
                        demo.TestStopWatching();
                    }
            }
            var speed = EditorGUILayout.Slider("Tốc độ trận", _speed, .05f, 1f);
            if (!Mathf.Approximately(speed, _speed)) SetSpeed(speed);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("×0.1")) SetSpeed(.1f);
                if (GUILayout.Button("×0.25")) SetSpeed(.25f);
                if (GUILayout.Button("×0.5")) SetSpeed(.5f);
                if (GUILayout.Button("×1")) SetSpeed(1f);
            }
            if (!_watching) return;
            _followCamera = EditorGUILayout.ToggleLeft("Khung Scene bám theo Quân sư", _followCamera);

            if (demo.TestWatchedInfo(out _watchPosition, out _watchTarget, out _watchHasTarget, out var state))
                EditorGUILayout.HelpBox(state, MessageType.None);
            else
            {
                EditorGUILayout.HelpBox(string.IsNullOrEmpty(state) ? "Không còn theo dõi." : state, MessageType.Warning);
                _watchHasTarget = false;
            }

            var log = demo.TestWatchLog;
            EditorGUILayout.LabelField($"Nhật ký ({log.Count} mục, mới nhất ở trên)", EditorStyles.miniBoldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(120f), GUILayout.MaxHeight(240f));
            var now = demo.TestBattleTime;
            for (var i = log.Count - 1; i >= 0; i--)
            {
                var e = log[i];
                var ago = now - e.Time;
                EditorGUILayout.LabelField(e.Landed
                    ? $"  {ago,5:0.0}s trước   ✦ Trúng {e.Target} → còn {e.TargetHealth01 * 100f:0}% máu"
                    : $"  {ago,5:0.0}s trước   ⚔ Ra đòn vào {e.Target} ({e.Distance:0.0} m, {e.Damage} sát thương, chạm sau {e.HitDelay:0.00}s, mục tiêu {e.TargetHealth01 * 100f:0}% máu)",
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private void FollowWatched()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) return;
            if (_followCamera && _watchPosition != Vector3.zero)
            {
                var focus = _watchHasTarget ? (_watchPosition + _watchTarget) * .5f : _watchPosition;
                view.pivot = Vector3.Lerp(view.pivot, focus, .2f);
            }
            view.Repaint();
        }

        private void DrawWatchInScene(SceneView view)
        {
            if (!_watching || !EditorApplication.isPlaying || _watchPosition == Vector3.zero) return;
            var up = Vector3.up * .3f;
            Handles.color = new Color(.3f, .8f, 1f, 1f);
            Handles.DrawWireDisc(_watchPosition + up, Vector3.up, 1.2f, 3f);
            Handles.Label(_watchPosition + Vector3.up * 4f, "Quân sư", EditorStyles.whiteBoldLabel);
            if (!_watchHasTarget) return;
            Handles.color = new Color(1f, .25f, .2f, 1f);
            Handles.DrawLine(_watchPosition + up, _watchTarget + up, 4f);
            Handles.DrawWireDisc(_watchTarget + up, Vector3.up, 1f, 3f);
            Handles.Label(_watchTarget + Vector3.up * 3f, "Mục tiêu", EditorStyles.whiteBoldLabel);
        }

        private string NoAdvisor() => $"Phe {Sides[_side]} chưa có Quân sư nào đang hiện — bấm \"Triệu hồi Quân sư\" trước.";
    }
}
