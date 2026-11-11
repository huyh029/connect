using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Editor previews (hero skill zones, effect test) create their objects with <see cref="HideFlags.DontSave"/> so they
/// are never saved into a scene. Unity does not destroy such objects when another scene is opened, Play starts or
/// scripts reload, and the window that made them loses track of them: they stay in every scene. This removes them:
/// automatically after a scene opens, after Play ends and after scripts reload, and from the menu.
/// Only root objects whose flags are exactly DontSave are touched (Unity's own hidden objects use HideAndDontSave).
/// </summary>
[InitializeOnLoad]
public static class RecoilPreviewCleanup
{
    /// <summary>Objects a preview that is open right now still owns; never removed.</summary>
    private static readonly HashSet<GameObject> Owned = new();

    static RecoilPreviewCleanup()
    {
        EditorSceneManager.sceneOpened += (_, _) => Clean(false);
        EditorApplication.playModeStateChanged += change =>
        {
            if (change == PlayModeStateChange.EnteredEditMode) Clean(false);
        };
        // After a script reload no window holds its old previews any more.
        EditorApplication.delayCall += () => { Owned.Clear(); Clean(false); };
    }

    public static void Own(GameObject go) { if (go != null) Owned.Add(go); }
    public static void Release(GameObject go) { if (go != null) Owned.Remove(go); }

    [MenuItem("Tools/Nhị Quốc/Kỹ năng tướng/Dọn FX xem thử bị sót")]
    [MenuItem("Game Tools/Công Thành/Dọn FX xem thử bị sót")]
    private static void CleanFromMenu()
    {
        var removed = Clean(true);
        EditorUtility.DisplayDialog("Dọn FX xem thử", removed > 0
            ? $"Đã xóa {removed} vật xem thử bị sót (tướng, mục tiêu, FX)."
            : "Không còn vật xem thử nào bị sót.", "OK");
    }

    /// <summary>Destroys leftover preview objects; returns how many roots were removed.</summary>
    public static int Clean(bool includeOwned)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return 0;
        Owned.RemoveWhere(go => go == null);
        var leftovers = new List<GameObject>();
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null || go.hideFlags != HideFlags.DontSave || go.transform.parent != null) continue;
            if (EditorUtility.IsPersistent(go)) continue; // an asset, not a scene object
            if (!includeOwned && Owned.Contains(go)) continue;
            leftovers.Add(go);
        }
        foreach (var go in leftovers)
        {
            Owned.Remove(go);
            Object.DestroyImmediate(go);
        }
        // Temporary preview scenes left open (e.g. after a script reload).
        for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (SceneManager.sceneCount > 1 && string.IsNullOrEmpty(scene.path) && scene.name.Contains("(tạm thời)") &&
                (includeOwned || scene.rootCount == 0))
                EditorSceneManager.CloseScene(scene, true);
        }
        if (leftovers.Count > 0) SceneView.RepaintAll();
        return leftovers.Count;
    }
}
