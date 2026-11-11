using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameToolsEditor
{
    public sealed class CastleMaterialStateSetupWindow : EditorWindow
    {
        private const string BlueBurningPath =
            "Assets/_Game/asset/Castle_NhiQuoc/New Folder/lúc cháy blue.prefab";
        private const string RedBurningPath =
            "Assets/_Game/asset/Castle_NhiQuoc/New Folder/lúc cháy red.prefab";
        private const string BlueCastleRootName = "CASTLE_CHAY_blue";
        private const string RedCastleRootName = "CASTLE_CHAY_red (1)";

        [SerializeField] private RecoilAutoBattleDemo battleDemo;
        [SerializeField] private GameObject blueBurningPrefab;
        [SerializeField] private GameObject redBurningPrefab;
        private string report;

        [MenuItem("Game Tools/Công Thành/Ghép lâu đài cháy")]
        private static void Open()
        {
            var window = GetWindow<CastleMaterialStateSetupWindow>("Ghép lâu đài cháy");
            window.minSize = new Vector2(460f, 260f);
            window.Show();
        }

        private void OnEnable()
        {
            if (battleDemo == null) battleDemo = Object.FindFirstObjectByType<RecoilAutoBattleDemo>();
            if (blueBurningPrefab == null)
                blueBurningPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BlueBurningPath);
            if (redBurningPrefab == null)
                redBurningPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RedBurningPath);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Ghép mô hình và hiệu ứng cháy", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Khi đạn chạm thành, mesh lâu đài nguyên vẹn sẽ ẩn; mô hình cháy và các ParticleSystem từ prefab 'lúc cháy' sẽ hiện thay thế.",
                MessageType.Info);
            battleDemo = (RecoilAutoBattleDemo)EditorGUILayout.ObjectField(
                "Battle Demo", battleDemo, typeof(RecoilAutoBattleDemo), true);
            blueBurningPrefab = (GameObject)EditorGUILayout.ObjectField(
                "Prefab cháy xanh", blueBurningPrefab, typeof(GameObject), false);
            redBurningPrefab = (GameObject)EditorGUILayout.ObjectField(
                "Prefab cháy đỏ", redBurningPrefab, typeof(GameObject), false);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Gán prefab lâu đài cháy cho trận đấu", GUILayout.Height(36f)))
                    Configure();
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorGUILayout.HelpBox("Hãy dừng Play Mode trước khi cấu hình.", MessageType.Info);
            if (!string.IsNullOrEmpty(report))
                EditorGUILayout.HelpBox(report, report.StartsWith("Lỗi:", System.StringComparison.Ordinal)
                    ? MessageType.Error : MessageType.Info);
        }

        private void Configure()
        {
            if (battleDemo == null)
            {
                report = "Lỗi: Không tìm thấy RecoilAutoBattleDemo trong scene.";
                return;
            }
            if (!battleDemo.gameObject.scene.IsValid())
            {
                report = "Lỗi: Battle Demo phải nằm trong scene đang mở.";
                return;
            }

            if (!ValidatePrefab(blueBurningPrefab, BlueCastleRootName, out report)) return;
            if (!ValidatePrefab(redBurningPrefab, RedCastleRootName, out report)) return;

            var serializedDemo = new SerializedObject(battleDemo);
            var blueProperty = serializedDemo.FindProperty("blueCastleBurningPrefab");
            var redProperty = serializedDemo.FindProperty("redCastleBurningPrefab");
            if (blueProperty == null || redProperty == null)
            {
                report = "Lỗi: Không tìm thấy cấu hình particle cháy trên RecoilAutoBattleDemo.";
                return;
            }

            Undo.RecordObject(battleDemo, "Configure castle burning particles");
            blueProperty.objectReferenceValue = blueBurningPrefab;
            redProperty.objectReferenceValue = redBurningPrefab;
            serializedDemo.ApplyModifiedProperties();
            EditorUtility.SetDirty(battleDemo);
            EditorSceneManager.MarkSceneDirty(battleDemo.gameObject.scene);
            report = "Đã gán prefab lâu đài cháy xanh/đỏ. Hãy lưu scene.";
        }

        private static bool ValidatePrefab(GameObject prefab, string castleRootName, out string error)
        {
            if (prefab == null)
            {
                error = $"Lỗi: Chưa gán prefab chứa {castleRootName}.";
                return false;
            }

            var rootFound = false;
            foreach (var child in prefab.GetComponentsInChildren<Transform>(true))
                if (child.name == castleRootName && child.parent == prefab.transform)
                    rootFound = true;

            if (!rootFound)
            {
                error = $"Lỗi: Không tìm thấy root con trực tiếp '{castleRootName}' trong {prefab.name}.";
                return false;
            }
            if (prefab.GetComponentsInChildren<ParticleSystem>(true).Length == 0)
            {
                error = $"Lỗi: Không có ParticleSystem nào trong {prefab.name}.";
                return false;
            }

            error = null;
            return true;
        }
    }
}
