using System;
using System.IO;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class RecoilCastleCsvMigration
{
    private static double next;
    static RecoilCastleCsvMigration() { EditorApplication.update += Update; }
    private static void Update()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < next) return;
        next = EditorApplication.timeSinceStartup + 5;
        string unitPath = "Assets/StreamingAssets/recoil_unit_stats.csv";
        string castlePath = "Assets/StreamingAssets/recoil_castle_stats.csv";
        if (!File.Exists(unitPath) || !File.Exists(castlePath)) return;
        try
        {
            string text = File.ReadAllText(unitPath);
            if (!text.Split('\n')[0].Contains("CastleHealth")) { EditorApplication.update -= Update; return; }
            var units = RecoilSettingsCsv.ReadUnits(text, out _, out _);
            RecoilSettingsCsv.Save(unitPath, RecoilSettingsCsv.WriteUnits(units));
            if (File.Exists(unitPath + ".split.tmp")) File.Delete(unitPath + ".split.tmp");
            EditorApplication.update -= Update;
            Debug.Log("Castle settings are stored separately in recoil_castle_stats.csv; removed legacy castle columns from unit CSV.");
        }
        catch (IOException) { /* Retry when the CSV editor releases the file lock. */ }
        catch (Exception exception) { EditorApplication.update -= Update; Debug.LogException(exception); }
    }
}
