using System;
using System.Linq;
using System.IO;
using NhiQuoc.Tools;
using UnityEditor;
using UnityEngine;

public static class HeroVatSizeRepair
{
    private const string HeightRequest = "Assets/Editor/HeroVatGroundHeight6.request";
    private static double nextCheck;
    [InitializeOnLoadMethod]
    private static void QueueHeightUpdate()
    {
        EditorApplication.update -= ApplyNextHeight;
        EditorApplication.update += ApplyNextHeight;
    }
    private static void ApplyNextHeight()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < nextCheck) return;
        nextCheck = EditorApplication.timeSinceStartup + 2;
        if (!File.Exists(HeightRequest)) return;
        var names = File.ReadAllLines(HeightRequest).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (names.Length == 0) return;
        string name = names[0].Trim();
        try
        {
            if (File.Exists(HeroVatBaker.Output + "/" + name + "/" + name + " VAT.prefab")) Repair(name, 6f);
            File.WriteAllLines(HeightRequest, names.Skip(1));
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            nextCheck = EditorApplication.timeSinceStartup + 30;
        }
    }
    public static (Bounds bounds, Vector3 pivot) MeasureBody(Vector3[] vertices)
    {
        if (vertices == null || vertices.Length == 0) throw new InvalidOperationException("No Idle body vertices.");
        var bounds = new Bounds(vertices[0], Vector3.zero);
        foreach (var v in vertices) bounds.Encapsulate(v);
        if (bounds.size.y < .00001f) throw new InvalidOperationException("Invalid body height.");
        // Center on the feet instead of the full silhouette: raised arms, a long
        // weapon or a plume must not move the horizontal rotation center.
        Vector3 feet = Vector3.zero; int count = 0;
        float cutoff = bounds.min.y + bounds.size.y * .08f;
        foreach (var v in vertices) if (v.y <= cutoff) { feet += v; count++; }
        feet = count > 0 ? feet / count : bounds.center;
        return (bounds, new Vector3(feet.x, bounds.min.y, feet.z));
    }

    public static void Repair(string name, float targetHeight)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || targetHeight <= 0) return;
        string path = HeroVatBaker.Output + "/" + name + "/" + name + " VAT.prefab";
        var root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            var player = root.GetComponent<HeroVatPlayer>();
            if (!player || player.parts == null) throw new InvalidOperationException("Missing VAT body: " + name);
            var body = player.parts.Where(p => p && (p.TryGetComponent<MeshFilter>(out var meshFilter2) ? meshFilter2.sharedMesh : null) != null)
                .OrderByDescending(p => p.GetComponent<MeshFilter>().sharedMesh.vertexCount).FirstOrDefault();
            if (!body) throw new InvalidOperationException("Missing VAT mesh: " + name);
            var matrix = root.transform.worldToLocalMatrix * body.transform.localToWorldMatrix;
            var vertices = body.GetComponent<MeshFilter>().sharedMesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
            var measured = MeasureBody(vertices);
            // Keep VAT textures unchanged; apply one shared offset to visual parts
            // and the exact same offset to every animated FX socket.
            foreach (var part in player.parts)
                if (part) part.transform.localPosition -= measured.pivot;
            foreach (var clip in player.clips) foreach (var fx in clip.effects)
                for (int f = 0; f < fx.socketPositions.Length; f++) fx.socketPositions[f] -= measured.pivot;
            root.transform.localScale = Vector3.one * (targetHeight / measured.bounds.size.y);
            player.referenceBodyHeight = measured.bounds.size.y;
            player.targetBodyHeight = targetHeight;
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log(name + ": normalized by Idle body " + body.name + ", height " + targetHeight + ", root scale " + root.transform.localScale.x + ", foot offset " + measured.pivot + ". No texture rebake.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
