using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NhiQuoc.Tools;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class HeroVatFxRepair
{
    private const string Folder = "Assets/_Game/HeroVAT/PreparedFX";
    private static bool pending = true;
    static HeroVatFxRepair() { EditorApplication.update += Check; }
    private static void Check()
    {
        if (!pending || AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        const string key = "HeroVAT.PreparedFX.v1";
        pending = false;
        if (SessionState.GetBool(key, false)) return;
        SessionState.SetBool(key, true);
        RepairExisting();
    }

    public static GameObject Prepare(GameObject source, Dictionary<GameObject, GameObject> cache)
    {
        return HeroFxParticleClone.Prepare(source, cache);
    }

    [MenuItem("Tools/Nhi Quoc/VAT/Repair FX on Baked Heroes (No Rebake)")]
    public static void RepairExisting()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var cache = new Dictionary<GameObject, GameObject>();
        int repaired = 0;
        foreach (string name in HeroVatBaker.HeroNames)
        {
            string path = HeroVatBaker.Output + "/" + name + "/" + name + " VAT.prefab";
            if (!File.Exists(path)) continue;
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var player = root.GetComponent<HeroVatPlayer>();
                if (!player || player.clips == null) continue;
                bool changed = false;
                foreach (var clip in player.clips) foreach (var fx in clip.effects)
                {
                    var prepared = Prepare(fx.prefab, cache);
                    if (prepared != fx.prefab) { fx.prefab = prepared; changed = true; }
                }
                if (changed) { PrefabUtility.SaveAsPrefabAsset(root, path); repaired++; }
            }
            catch (Exception error) { Debug.LogException(error); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        AssetDatabase.SaveAssets();
        Debug.Log("VAT FX repair complete: updated " + repaired + " hero prefabs. No VAT texture rebake required.");
    }
}
