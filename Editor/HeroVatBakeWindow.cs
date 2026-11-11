using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;

public sealed class HeroVatBakeWindow : EditorWindow
{
    private int selected;
    private float targetHeight = 6f;
    private void OnEnable() { targetHeight = 6f; }
    [MenuItem("Tools/Nhi Quoc/VAT/Bake One Hero")]
    public static void Open() => GetWindow<HeroVatBakeWindow>("Bake One Hero");
    private void OnGUI()
    {
        EditorGUILayout.LabelField("One hero per bake | Default height 6 | 30 FPS", EditorStyles.boldLabel);
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
        {
            EditorGUILayout.HelpBox("Worker logs show a Direct3D 12 crash while rendering mesh thumbnails. Restart with Direct3D 11 before baking more heroes.", MessageType.Warning);
            if (GUILayout.Button("Save scenes and restart Editor with Direct3D 11")) RestartWithD3D11();
        }
        selected = EditorGUILayout.Popup("Hero", selected, HeroVatBaker.HeroNames);
        targetHeight = Mathf.Max(.01f, EditorGUILayout.FloatField("Target body height", targetHeight));
        EditorGUILayout.HelpBox("Only the selected hero is loaded and baked. After completion, choose another hero. Saved FX settings are restored from the latest preset when no scene Display is present.", MessageType.Info);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
            if (GUILayout.Button("Bake " + HeroVatBaker.HeroNames[selected])) HeroVatBaker.BakeOne(HeroVatBaker.HeroNames[selected], targetHeight);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
        {
            if (GUILayout.Button("Preview selected hero (Open + Play)"))
            {
                string name = HeroVatBaker.HeroNames[selected];
                EditorApplication.delayCall += () => HeroVatBaker.PreviewOne(name);
            }
            if (GUILayout.Button("Create and open preview of completed heroes"))
                EditorApplication.delayCall += HeroVatBaker.CreatePreviewFromAssets;
        }
        if (GUILayout.Button("Fix asset name warnings (selected hero)")) HeroVatBaker.FixAssetNames(HeroVatBaker.HeroNames[selected]);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
            if (GUILayout.Button("Repair colors of selected hero (No Rebake)"))
                HeroVatMaterialRepair.RepairHero(HeroVatBaker.HeroNames[selected]);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
            if (GUILayout.Button("Fix center and body height (No Rebake)"))
                HeroVatSizeRepair.Repair(HeroVatBaker.HeroNames[selected], targetHeight);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating))
            if (GUILayout.Button("Use original FX (No particle reduction)"))
                HeroFxParticleClone.ApplyHero(HeroVatBaker.HeroNames[selected]);
    }

    [MenuItem("Tools/Nhi Quoc/VAT/Save Scenes and Restart with Direct3D 11")]
    public static void RestartWithD3D11()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
        if (!EditorSceneManager.SaveOpenScenes()) return;
        AssetDatabase.SaveAssets();
        EditorApplication.OpenProject(System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..")), "-force-d3d11");
    }
}
