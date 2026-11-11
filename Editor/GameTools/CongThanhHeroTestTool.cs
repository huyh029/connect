using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    /// <summary>
    /// Quick hero fight: pick a side for each of the 8 heroes, press Run. The tool enters Play Mode if needed, starts a
    /// match (optionally with no soldiers), summons the heroes the way TikTok gifts do and starts the fight; while it
    /// runs it shows each hero's health and how many skills it has cast.
    /// </summary>
    public sealed class CongThanhHeroTestTool : EditorWindow
    {
        private const string ScenePath = "Assets/Scenes/new.unity";
        private const string ArenaScenePath = "Assets/Scenes/HeroArena.unity";
        private const string CsvPath = "Assets/StreamingAssets/recoil_hero_stats.csv";
        private const string PendingKey = "CongThanhHeroTest.Pending";
        private static readonly string[] Sides = { "Xanh", "Đỏ", "Không" };
        private static readonly string[] Modes = { "Đấu trường nhanh", "Trận thật (new.unity)" };

        private sealed class HeroRow
        {
            public int Id;
            public string Name;
            public bool Melee;
        }

        // Side per hero (index = hero id - 1): 0 = blue, 1 = red, 2 = out. Default: 2 melee + 2 ranged a side.
        [SerializeField] private int[] _side = { 0, 1, 0, 1, 0, 1, 1, 0 };
        [SerializeField] private bool _heroesOnly = true;
        [SerializeField] private bool _skipIntro = true;
        [SerializeField] private int _mode; // 0 = light hero arena scene, 1 = the real battle in new.unity
        [SerializeField] private bool _quickRespawn = true;

        private List<HeroRow> _rows;
        private readonly List<RecoilAutoBattleDemo.TestHeroInfo> _live = new();
        private double _nextRepaint;
        private string _status;

        [MenuItem("Game Tools/Công Thành/Battle/Test 8 tướng")]
        private static void Open()
        {
            var window = GetWindow<CongThanhHeroTestTool>("Test 8 tướng");
            window.minSize = new Vector2(440f, 480f);
            window.Show();
        }

        private void OnEnable()
        {
            _rows = LoadRows();
            if (_side == null || _side.Length < _rows.Count) DefaultSplit();
            EditorApplication.update += RepaintWhilePlaying;
        }

        private void OnDisable() => EditorApplication.update -= RepaintWhilePlaying;

        private void RepaintWhilePlaying()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup < _nextRepaint) return;
            _nextRepaint = EditorApplication.timeSinceStartup + .5;
            Repaint();
        }

        // ---------------------------------------------------------------- GUI

        private void OnGUI()
        {
            _mode = GUILayout.Toolbar(_mode, Modes, GUILayout.Height(24f));
            EditorGUILayout.HelpBox(_mode == 0
                    ? "Đấu trường nhanh: scene nhẹ riêng (" + ArenaScenePath + "), vào Play gần như ngay. Tướng thật, skill " +
                      "và chỉ số CSV thật, đánh nhau trên sân phẳng; không có lính, bản đồ, tìm đường."
                    : "Trận thật: tool vào Play new.unity, bắt đầu trận, gọi tướng như quà TikTok (từng tướng một, chết thì " +
                      "hồi sinh theo cấu hình) rồi cho giao chiến. Chậm vì phải tải trận.", MessageType.Info);

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{row.Id}. {row.Name}", GUILayout.Width(150f));
                    EditorGUILayout.LabelField(row.Melee ? "cận chiến" : "đánh xa", EditorStyles.miniLabel, GUILayout.Width(70f));
                    _side[i] = GUILayout.Toolbar(_side[i], Sides);
                }
            }
            var blue = _rows.Where((r, i) => _side[i] == 0).ToList();
            var red = _rows.Where((r, i) => _side[i] == 1).ToList();
            EditorGUILayout.LabelField(" ", $"Xanh {blue.Count} ({blue.Count(r => r.Melee)} gần)  ·  Đỏ {red.Count} ({red.Count(r => r.Melee)} gần)",
                EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Chia mặc định (2 gần + 2 xa mỗi phe)")) DefaultSplit();
                if (GUILayout.Button("Đảo phe")) for (var i = 0; i < _side.Length; i++) if (_side[i] < 2) _side[i] = 1 - _side[i];
            }

            EditorGUILayout.Space(4f);
            if (_mode == 0)
            {
                _quickRespawn = EditorGUILayout.ToggleLeft("Hồi sinh nhanh (5 giây, thay vì thời gian trong CSV)", _quickRespawn);
            }
            else
            {
                _heroesOnly = EditorGUILayout.ToggleLeft("Chỉ tướng (không có lính ban đầu)", _heroesOnly);
                _skipIntro = EditorGUILayout.ToggleLeft("Bỏ màn giới thiệu tướng", _skipIntro);
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(blue.Count + red.Count == 0))
            {
                if (GUILayout.Button(EditorApplication.isPlaying ? "▶ Chạy test" : "▶ Chạy test (vào Play)", GUILayout.Height(32f)))
                    Run();
            }

            if (EditorApplication.isPlaying) DrawLive();
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.None);
        }

        private void DrawLive()
        {
            var arena = UnityEngine.Object.FindFirstObjectByType<RecoilHeroArena>();
            if (arena != null)
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Đấu trường đang chạy", EditorStyles.boldLabel);
                arena.GetStatus(_live);
                DrawHeroRows(true);
                return;
            }
            var demo = FindDemo();
            if (demo == null) return;
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Trận đang chạy", demo.TestStatus, EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!demo.TestMatchRunning))
                {
                    if (GUILayout.Button("Gọi thêm 1 lượt các tướng đã chọn"))
                    {
                        Selection(out var ids, out var blues);
                        demo.TestSummonHeroes(ids, blues, _skipIntro);
                    }
                    if (GUILayout.Button("Giao chiến ngay", GUILayout.Width(120f))) demo.TestStartCombat();
                }
            }

            demo.TestGetHeroes(_live);
            DrawHeroRows(false);
        }

        private void DrawHeroRows(bool withDamage)
        {
            if (_live.Count == 0) return;
            foreach (var hero in _live.OrderBy(h => h.Blue ? 0 : 1).ThenBy(h => h.Id))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var color = GUI.color;
                    GUI.color = hero.Blue ? new Color(.55f, .7f, 1f) : new Color(1f, .55f, .5f);
                    EditorGUILayout.LabelField(hero.Blue ? "Xanh" : "Đỏ", GUILayout.Width(36f));
                    GUI.color = color;
                    EditorGUILayout.LabelField(hero.Name, GUILayout.Width(140f));
                    var bar = GUILayoutUtility.GetRect(60f, 16f, GUILayout.ExpandWidth(true));
                    EditorGUI.ProgressBar(bar, hero.Alive ? hero.Health01 : 0f,
                        hero.Alive ? $"{hero.Health01 * 100f:0}%" : hero.Waiting ? "đang chờ ra / hồi sinh" : "đã chết");
                    var casts = hero.Casts;
                    var label = new GUIContent($"{casts} chiêu", "Số chiêu (đánh / ulti) tướng đã tung từ lúc ra trận.");
                    GUI.color = hero.Alive && casts == 0 ? new Color(1f, .8f, .3f) : color; // alive but never attacked
                    EditorGUILayout.LabelField(label, GUILayout.Width(60f));
                    GUI.color = color;
                    if (withDamage)
                        EditorGUILayout.LabelField(new GUIContent($"{hero.Damage:N0} st", "Tổng sát thương đã gây ra."), GUILayout.Width(80f));
                }
            }
        }

        // ---------------------------------------------------------------- run

        private void Run()
        {
            Selection(out var ids, out var blues);
            if (ids.Count == 0) return;
            if (_mode == 0)
            {
                RunArena(ids, blues);
                return;
            }
            if (EditorApplication.isPlaying)
            {
                var demo = FindDemo();
                if (demo == null) { _status = "Không tìm thấy RecoilAutoBattleDemo trong scene đang chạy."; return; }
                demo.TestRunHeroBattle(ids, blues, _heroesOnly, _skipIntro);
                _status = null;
                return;
            }
            if (FindDemo() == null)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            SessionState.SetString(PendingKey, Serialize(ids, blues, _heroesOnly, _skipIntro));
            _status = "Đang vào Play…";
            EditorApplication.isPlaying = true;
        }

        /// <summary>
        /// The light arena: in Play, restart it with this selection; otherwise open (or create) its scene, store the
        /// selection on the arena and enter Play.
        /// </summary>
        private void RunArena(List<int> ids, List<bool> blues)
        {
            var entries = ids.Select((id, i) => new RecoilHeroArena.Entry { heroId = id, blue = blues[i] }).ToList();
            if (EditorApplication.isPlaying)
            {
                var running = UnityEngine.Object.FindFirstObjectByType<RecoilHeroArena>();
                if (running != null) { running.Restart(entries, _quickRespawn); _status = null; }
                else _status = "Đang Play một scene khác: dừng Play rồi bấm Chạy test.";
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = File.Exists(ArenaScenePath)
                ? EditorSceneManager.OpenScene(ArenaScenePath, OpenSceneMode.Single)
                : CreateArenaScene();
            var arena = scene.GetRootGameObjects().Select(go => go.GetComponentInChildren<RecoilHeroArena>(true))
                .FirstOrDefault(found => found != null);
            if (arena == null)
            {
                arena = new GameObject("Hero Arena", typeof(RecoilHeroArena)).GetComponent<RecoilHeroArena>();
                SceneManager.MoveGameObjectToScene(arena.gameObject, scene);
            }
            Undo.RecordObject(arena, "Test 8 tướng");
            arena.heroes = entries;
            arena.quickRespawn = _quickRespawn;
            EditorUtility.SetDirty(arena);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            _status = null;
            EditorApplication.isPlaying = true;
        }

        /// <summary>A bare scene: camera looking down on a flat ground, a sun, and the arena component.</summary>
        private static Scene CreateArenaScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camera.tag = "MainCamera";
            camera.transform.SetPositionAndRotation(new Vector3(0f, 52f, -52f), Quaternion.Euler(45f, 0f, 0f));
            var cam = camera.GetComponent<Camera>();
            cam.farClipPlane = 600f;
            cam.fieldOfView = 50f;
            var sun = new GameObject("Directional Light", typeof(Light));
            var light = sun.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(14f, 1f, 9f);
            new GameObject("Hero Arena", typeof(RecoilHeroArena));
            EditorSceneManager.SaveScene(scene, ArenaScenePath);
            return scene;
        }

        [InitializeOnLoadMethod]
        private static void HookPlayMode()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        /// <summary>After entering Play Mode for a pending test: hand it to the battle demo.</summary>
        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            var pending = SessionState.GetString(PendingKey, string.Empty);
            if (string.IsNullOrEmpty(pending)) return;
            SessionState.EraseString(PendingKey);
            if (!Deserialize(pending, out var ids, out var blues, out var heroesOnly, out var skipIntro)) return;
            var demo = FindDemo();
            if (demo == null)
            {
                Debug.LogWarning("[Test 8 tướng] Không tìm thấy RecoilAutoBattleDemo trong scene đang chạy.");
                return;
            }
            demo.TestRunHeroBattle(ids, blues, heroesOnly, skipIntro);
        }

        private void Selection(out List<int> ids, out List<bool> blues)
        {
            ids = new List<int>();
            blues = new List<bool>();
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_side[i] > 1) continue;
                ids.Add(_rows[i].Id);
                blues.Add(_side[i] == 0);
            }
        }

        private void DefaultSplit()
        {
            _side = new int[Math.Max(_rows.Count, 8)];
            // Alternate within melee and within ranged, so each side gets half of both.
            int melee = 0, ranged = 0;
            for (var i = 0; i < _rows.Count; i++)
                _side[i] = _rows[i].Melee ? melee++ % 2 : ranged++ % 2;
        }

        private static string Serialize(List<int> ids, List<bool> blues, bool heroesOnly, bool skipIntro) =>
            string.Join(",", ids.Select((id, i) => id + ":" + (blues[i] ? 0 : 1))) + "|" + (heroesOnly ? 1 : 0) + "|" + (skipIntro ? 1 : 0);

        private static bool Deserialize(string text, out List<int> ids, out List<bool> blues, out bool heroesOnly, out bool skipIntro)
        {
            ids = new List<int>();
            blues = new List<bool>();
            heroesOnly = skipIntro = false;
            var parts = text.Split('|');
            if (parts.Length != 3) return false;
            foreach (var item in parts[0].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = item.Split(':');
                if (pair.Length != 2 || !int.TryParse(pair[0], out var id)) continue;
                ids.Add(id);
                blues.Add(pair[1] == "0");
            }
            heroesOnly = parts[1] == "1";
            skipIntro = parts[2] == "1";
            return ids.Count > 0;
        }

        private static RecoilAutoBattleDemo FindDemo() =>
            EditorApplication.isPlaying
                ? UnityEngine.Object.FindFirstObjectByType<RecoilAutoBattleDemo>()
                : Resources.FindObjectsOfTypeAll<RecoilAutoBattleDemo>()
                    .FirstOrDefault(d => !EditorUtility.IsPersistent(d) && d.gameObject.scene == SceneManager.GetActiveScene());

        /// <summary>The 8 heroes from the hero stats CSV (id; name; ...; melee flag in column 11).</summary>
        private static List<HeroRow> LoadRows()
        {
            var rows = new List<HeroRow>();
            try
            {
                var lines = File.ReadAllLines(CsvPath);
                for (var i = 1; i < lines.Length; i++)
                {
                    var cells = lines[i].Split(';');
                    if (cells.Length < 11 || !int.TryParse(cells[0].Trim('﻿', ' '), out var id)) continue;
                    rows.Add(new HeroRow { Id = id, Name = cells[1].Trim(), Melee = cells[10].Trim().Equals("true", StringComparison.OrdinalIgnoreCase) });
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Test 8 tướng] Không đọc được " + CsvPath + ": " + exception.Message);
            }
            if (rows.Count == 0)
                for (var id = 1; id <= 8; id++) rows.Add(new HeroRow { Id = id, Name = "Tướng " + id, Melee = id % 2 == 0 });
            rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            return rows;
        }
    }
}
