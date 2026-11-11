using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using RecoilPathfindingEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace GameToolsEditor
{
    /// <summary>
    /// Sets the body height of each hero and each soldier type and re-bakes them one after another, slowly, so the
    /// editor never holds more than one model's bake data: after each one it saves, frees memory and waits a few
    /// seconds before the next.
    /// <list type="bullet">
    /// <item>Only the look changes: body radius, movement and other gameplay settings are left alone.</item>
    /// <item>Soldiers: the model is resized, the projectile spawn point (and the LOD4 impostor) move with it, then the
    /// VAT and the impostor are baked again; the game's VAT library is rebuilt at the end.</item>
    /// <item>Heroes: VAT bake at the new height, or only a refit of height and centre (no bake). Skill FX sockets
    /// follow the hero's scale.</item>
    /// </list>
    /// The queue survives script reloads (it is kept in the editor session) and can be stopped between two models.
    /// </summary>
    public sealed class CongThanhHeroVatBatchTool : EditorWindow
    {
        private const string SoldierFolder = "Assets/Data/Characters/Prefabs";
        private const string QueueKey = "CongThanh.VatBatch.Queue";
        private const string HeroModeKey = "CongThanh.VatBatch.HeroMode";
        private const string DelayKey = "CongThanh.VatBatch.Delay";
        private const string ImpostorKey = "CongThanh.VatBatch.Impostor";
        private const string ResultsKey = "CongThanh.VatBatch.Results";
        private const string LibraryKey = "CongThanh.VatBatch.NeedsLibrary";

        private enum HeroMode { Bake, HeightOnly }

        private static readonly Dictionary<string, string> SoldierLabels = new()
        {
            { "BO_BINH", "Bộ binh" }, { "CUNG_KY", "Cung binh" }, { "DAN_BINH", "Dân binh" }, { "GIAO_BINH", "Giáo binh" },
            { "KY_BINH", "Kỵ binh" }, { "QUAN_SU", "Pháp sư" }, { "VOI", "Voi chiến" }, { "maybanda_v02", "Máy bắn đá" }
        };

        private sealed class Row
        {
            public string Label, Key;
            public string[] Paths; // soldiers: blue + red prefab; heroes: none
            public bool Selected;
            public float Current, Height;
        }

        private readonly List<Row> _heroes = new();
        private readonly List<Row> _soldiers = new();
        private float _delay = 5f;
        private bool _rebakeImpostor = true;
        private HeroMode _heroMode = HeroMode.Bake;
        private Vector2 _scroll, _resultScroll;

        private static double _nextStepAt;

        [MenuItem("Game Tools/Công Thành/Tướng/Bake VAT tất cả tướng (từ từ)")]
        [MenuItem("Game Tools/Công Thành/Chiều cao tướng + lính (bake từ từ)")]
        [MenuItem("Tools/Nhi Quoc/VAT/Bake All Heroes (Slow, one by one)")]
        public static void Open() => GetWindow<CongThanhHeroVatBatchTool>("Chiều cao + bake VAT");

        private void OnEnable()
        {
            _delay = SessionState.GetFloat(DelayKey, 5f);
            _rebakeImpostor = SessionState.GetBool(ImpostorKey, true);
            Reload();
        }

        /// <summary>Reads the current heights from the prefabs.</summary>
        private void Reload()
        {
            _heroes.Clear();
            foreach (var name in HeroVatBaker.HeroNames)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HeroVatBaker.Output + "/" + name + "/" + name + " VAT.prefab");
                var player = prefab != null ? prefab.GetComponent<NhiQuoc.Tools.HeroVatPlayer>() : null;
                var height = player != null && player.targetBodyHeight > .01f ? player.targetBodyHeight : 6f;
                _heroes.Add(new Row { Label = name, Key = name, Current = height, Height = height });
            }

            _soldiers.Clear();
            var groups = AssetDatabase.FindAssets("t:Prefab", new[] { SoldierFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetDirectoryName(p)?.Replace('\\', '/') == SoldierFolder)
                .GroupBy(p => BaseName(Path.GetFileNameWithoutExtension(p)))
                .OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                var paths = group.OrderBy(p => p).ToArray();
                var presentation = AssetDatabase.LoadAssetAtPath<GameObject>(paths[0])?.GetComponent<RecoilCharacterPresentation>();
                if (presentation == null) continue;
                var height = presentation.NormalizedVisualHeight;
                _soldiers.Add(new Row
                {
                    Label = SoldierLabels.TryGetValue(group.Key, out var label) ? label : group.Key,
                    Key = group.Key, Paths = paths, Current = height, Height = height
                });
            }
        }

        private static string BaseName(string name) =>
            name.EndsWith("_blue", StringComparison.OrdinalIgnoreCase) ? name[..^5]
            : name.EndsWith("_red", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        // ------------------------------------------------------------------ queue (kept in the editor session)
        // Items: "H\t<hero>\t<height>" or "U\t<prefab path>\t<height>".

        private static bool Running => !string.IsNullOrEmpty(SessionState.GetString(QueueKey, ""));

        private static string[] Queue
        {
            get
            {
                var text = SessionState.GetString(QueueKey, "");
                return string.IsNullOrEmpty(text) ? Array.Empty<string>() : text.Split('|');
            }
            set => SessionState.SetString(QueueKey, value == null || value.Length == 0 ? "" : string.Join("|", value));
        }

        private static void AddResult(string line) =>
            SessionState.SetString(ResultsKey, SessionState.GetString(ResultsKey, "") + line + "\n");

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.update -= Step;
            EditorApplication.update += Step;
            _nextStepAt = EditorApplication.timeSinceStartup + 3; // after a script reload, wait a moment
        }

        private void StartQueue()
        {
            var items = new List<string>();
            foreach (var row in _soldiers.Where(r => r.Selected))
                foreach (var path in row.Paths)
                    items.Add("U\t" + path + "\t" + row.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var row in _heroes.Where(r => r.Selected))
                items.Add("H\t" + row.Key + "\t" + row.Height.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            if (items.Count == 0) return;
            SessionState.SetInt(HeroModeKey, (int)_heroMode);
            SessionState.SetFloat(DelayKey, _delay);
            SessionState.SetBool(ImpostorKey, _rebakeImpostor);
            SessionState.SetBool(LibraryKey, false);
            SessionState.SetString(ResultsKey, "");
            Queue = items.ToArray();
            _nextStepAt = EditorApplication.timeSinceStartup + .5;
        }

        private static void Stop()
        {
            if (!Running) return;
            AddResult("Đã dừng; còn " + Queue.Length + " mục chưa làm.");
            Queue = null;
            FinishQueue();
        }

        private static void FinishQueue()
        {
            if (!SessionState.GetBool(LibraryKey, false)) return;
            SessionState.SetBool(LibraryKey, false);
            try { RecoilVatBaker.BuildGameLibrary(); AddResult("Đã cập nhật thư viện VAT của game."); }
            catch (Exception error) { AddResult("Cập nhật thư viện VAT LỖI — " + error.Message); Debug.LogException(error); }
        }

        /// <summary>One model per step, and only while the editor is idle.</summary>
        private static void Step()
        {
            if (!Running || EditorApplication.timeSinceStartup < _nextStepAt) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || AssetDatabase.IsAssetImportWorkerProcess())
            {
                _nextStepAt = EditorApplication.timeSinceStartup + 1;
                return;
            }
            var queue = Queue;
            var item = queue[0].Split('\t');
            Queue = queue.Length > 1 ? queue[1..] : null; // taken off first, so a crash never repeats the same model
            var height = float.Parse(item[2], System.Globalization.CultureInfo.InvariantCulture);
            var label = item[0] == "H" ? item[1] : Path.GetFileNameWithoutExtension(item[1]);
            var started = EditorApplication.timeSinceStartup;
            var memoryBefore = Profiler.GetTotalAllocatedMemoryLong();
            try
            {
                if (item[0] == "H") AddResult($"{label}: {DoHero(item[1], height)} ({EditorApplication.timeSinceStartup - started:0}s)");
                else AddResult($"{label}: {DoSoldier(item[1], height)} ({EditorApplication.timeSinceStartup - started:0}s)");
            }
            catch (Exception error)
            {
                AddResult($"{label}: LỖI — {error.Message}");
                Debug.LogException(error);
            }
            finally
            {
                // Free what this model used before the next one starts.
                AssetDatabase.SaveAssets();
                Resources.UnloadUnusedAssets();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EditorUtility.UnloadUnusedAssetsImmediate(true);
                Debug.Log($"[Chiều cao + bake VAT] {label} xong. Bộ nhớ: {memoryBefore / 1048576f:N0} MB → " +
                          $"{Profiler.GetTotalAllocatedMemoryLong() / 1048576f:N0} MB.");
                _nextStepAt = EditorApplication.timeSinceStartup + Mathf.Max(1f, SessionState.GetFloat(DelayKey, 5f));
                if (!Running) { FinishQueue(); AddResult("Xong hàng đợi."); }
                foreach (var window in Resources.FindObjectsOfTypeAll<CongThanhHeroVatBatchTool>()) { window.Reload(); window.Repaint(); }
            }
        }

        private static string DoHero(string name, float height)
        {
            if ((HeroMode)SessionState.GetInt(HeroModeKey, 0) == HeroMode.HeightOnly)
            {
                HeroVatSizeRepair.Repair(name, height);
                return "đã chỉnh chiều cao " + height;
            }
            HeroVatBaker.BakeOne(name, height);
            var report = HeroVatBaker.Output + "/" + name + "/BakeReport.txt";
            var last = File.Exists(report) ? File.ReadAllLines(report).LastOrDefault() ?? "" : "";
            return last.StartsWith("SUCCESS", StringComparison.Ordinal) ? "bake xong, cao " + height
                : last.StartsWith("FAILED", StringComparison.Ordinal) ? "bake LỖI (xem Console)" : "đã chạy bake";
        }

        /// <summary>
        /// Resizes one soldier prefab's look only, then re-bakes its VAT (and LOD4 impostor). The root, its body radius
        /// and every gameplay component stay as they are.
        /// </summary>
        private static string DoSoldier(string path, float height)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            float ratio;
            try
            {
                var presentation = root.GetComponent<RecoilCharacterPresentation>();
                if (presentation == null) throw new InvalidOperationException("Thiếu RecoilCharacterPresentation.");
                var serialized = new SerializedObject(presentation);
                var heightProperty = serialized.FindProperty("normalizedVisualHeight");
                ratio = height / Mathf.Max(.001f, heightProperty.floatValue);
                if (!Mathf.Approximately(ratio, 1f))
                {
                    heightProperty.floatValue = height;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    presentation.ApplyVisualSettings(); // the model (Visual Offset) follows the new height
                    // The projectile leaves from the same place on the bigger / smaller body.
                    var spawn = serialized.FindProperty("projectileSpawnPoint").objectReferenceValue as Transform;
                    var visual = serialized.FindProperty("visualRoot").objectReferenceValue as Transform;
                    if (spawn != null && (visual == null || !spawn.IsChildOf(visual))) spawn.localPosition *= ratio;
                    var impostor = root.transform.Find("LOD4_UpperImpostor");
                    if (impostor != null)
                    {
                        impostor.localPosition *= ratio;
                        impostor.localScale *= ratio;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            if (!RecoilVatBaker.BakeOnePrefab(path, out var error)) return "đổi chiều cao xong nhưng bake VAT LỖI — " + error;
            SessionState.SetBool(LibraryKey, true);
            if (SessionState.GetBool(ImpostorKey, true))
            {
                try { RecoilUpperImpostorBaker.BakeOnePrefab(path); }
                catch (Exception exception) { Debug.LogException(exception); return $"cao {height}, bake VAT xong, impostor LỖI — {exception.Message}"; }
            }
            return $"cao {height} (x{ratio:0.##}), bake VAT xong";
        }

        // ------------------------------------------------------------------ window

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Chiều cao tướng + lính, bake lại lần lượt từng con", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Chỉ đổi phần hiển thị: vùng chiếm, tốc độ, tầm đánh… giữ nguyên. Điểm bắn đạn của lính và hiệu ứng chiêu " +
                "của tướng tự co giãn theo chiều cao mới.\nMỗi lần chỉ nạp và bake một model; xong một model thì lưu, dọn " +
                "bộ nhớ, nghỉ vài giây rồi mới sang model tiếp theo. Dừng được giữa chừng.", MessageType.Info);
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
            {
                EditorGUILayout.HelpBox("Editor đang chạy Direct3D 12, từng bị crash khi bake. Nên khởi động lại bằng Direct3D 11.",
                    MessageType.Warning);
                if (GUILayout.Button("Lưu scene và khởi động lại với Direct3D 11")) HeroVatBakeWindow.RestartWithD3D11();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUI.DisabledScope(Running))
            {
                DrawRows("Lính (chiều cao thân, đơn vị Unity)", _soldiers);
                _rebakeImpostor = EditorGUILayout.ToggleLeft("Bake lại cả impostor LOD4 của lính", _rebakeImpostor);
                EditorGUILayout.Space(6);
                DrawRows("Tướng (chiều cao thân lúc đứng)", _heroes);
                _heroMode = (HeroMode)EditorGUILayout.Popup("Với tướng", (int)_heroMode,
                    new[] { "Bake lại VAT (lâu)", "Chỉ chỉnh chiều cao + tâm (nhanh, không bake)" });
                EditorGUILayout.Space(6);
                _delay = Mathf.Clamp(EditorGUILayout.FloatField("Nghỉ giữa 2 model (giây)", _delay), 1f, 120f);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            var busy = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling;
            if (!Running)
            {
                var count = _soldiers.Where(r => r.Selected).Sum(r => r.Paths.Length) + _heroes.Count(r => r.Selected);
                using (new EditorGUI.DisabledScope(busy || count == 0))
                    if (GUILayout.Button($"Bắt đầu ({count} model)", GUILayout.Height(32))) StartQueue();
                if (busy) EditorGUILayout.HelpBox("Thoát Play Mode / chờ biên dịch xong rồi mới chạy được.", MessageType.None);
            }
            else
            {
                var queue = Queue;
                var wait = Math.Max(0, _nextStepAt - EditorApplication.timeSinceStartup);
                var next = queue.Length > 0 ? queue[0].Split('\t') : null;
                EditorGUILayout.HelpBox($"Đang chạy… còn {queue.Length} model" +
                                        (next != null ? $", tiếp theo: {(next[0] == "H" ? next[1] : Path.GetFileNameWithoutExtension(next[1]))}" : "") +
                                        (wait > 0 ? $" (sau {wait:0} giây)" : ""), MessageType.None);
                if (GUILayout.Button("Dừng (sau model đang làm)", GUILayout.Height(28))) Stop();
                Repaint();
            }

            var results = SessionState.GetString(ResultsKey, "");
            if (!string.IsNullOrEmpty(results))
            {
                EditorGUILayout.LabelField("Kết quả", EditorStyles.boldLabel);
                _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.MinHeight(70), GUILayout.MaxHeight(160));
                EditorGUILayout.HelpBox(results.TrimEnd(), MessageType.None);
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.LabelField($"Bộ nhớ đang dùng: {Profiler.GetTotalAllocatedMemoryLong() / 1048576f:N0} MB",
                EditorStyles.miniLabel);
        }

        private static void DrawRows(string title, List<Row> rows)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                if (GUILayout.Button("Chọn hết", GUILayout.Width(70))) rows.ForEach(r => r.Selected = true);
                if (GUILayout.Button("Bỏ hết", GUILayout.Width(60))) rows.ForEach(r => r.Selected = false);
            }
            foreach (var row in rows)
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.Selected = EditorGUILayout.ToggleLeft(row.Label, row.Selected, GUILayout.Width(150));
                    EditorGUILayout.LabelField($"hiện {row.Current:0.##}", GUILayout.Width(70));
                    var height = Mathf.Max(.1f, EditorGUILayout.FloatField(row.Height, GUILayout.Width(60)));
                    if (!Mathf.Approximately(height, row.Height)) { row.Height = height; row.Selected = true; }
                }
        }
    }
}
