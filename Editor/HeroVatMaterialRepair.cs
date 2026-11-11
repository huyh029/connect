using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class HeroVatMaterialRepair
{
    [InitializeOnLoadMethod]
    private static void QueueTextureTintFix()
    {
        EditorApplication.update -= FixTextureTintsOnce;
        EditorApplication.update += FixTextureTintsOnce;
    }

    private static void FixTextureTintsOnce()
    {
        if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
        EditorApplication.update -= FixTextureTintsOnce;
        const string key = "HeroVAT.CorrectCharacterTextures.v2";
        if (SessionState.GetBool(key, false)) return;
        SessionState.SetBool(key, true);
        foreach (string hero in new[] { "Lu Bo", "Quan Vu", "Tao Thao" })
        {
            try { RepairHero(hero); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }
    private static readonly string[] Maps = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_Diffuse" };
    private static string Family(Renderer r)
    {
        // TryGetComponent: an FX renderer (particles, trails) has no MeshFilter, and in the editor GetComponent
        // returns a fake-null object that "?." does not catch, which made reading sharedMesh throw.
        var mesh = r is SkinnedMeshRenderer s ? s.sharedMesh
            : r.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
        return Regex.Replace(mesh ? mesh.name : r.name, "_LOD[0-9]+$", "", RegexOptions.IgnoreCase);
    }
    private static string ColorMap(Material material)
    {
        if (!material) return null;
        return Maps.FirstOrDefault(map => material.HasProperty(map) && material.GetTexture(map));
    }
    private static string CharacterFolder(string hero)
    {
        string folder = hero == "Lu Bo" ? "LU BO" : hero == "Quan Vu" ? "QUAN VU RIEL" :
            hero == "Tao Thao" ? "TAO THAO" : hero == "Luu Bi" ? "LUU BI" : hero == "Chu Du" ? "CHU DU" :
            hero == "Gia Cat Luong" ? "GIA CAT LUONG" : hero == "Ha Hau Don" ? "Ha hau don" : "TRIEU VAN";
        return "Assets/_Game/ALL TUONG NHI QUOC/" + folder + "/";
    }

    public static void CopySurface(Renderer source, Renderer[] familyCandidates, int submesh, Material target, string hero)
    {
        var original = source.sharedMaterials.ElementAtOrDefault(submesh);
        string folder = CharacterFolder(hero);
        bool CharacterTexture(Material material)
        {
            string property = ColorMap(material);
            return property != null && AssetDatabase.GetAssetPath(material.GetTexture(property)).StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
        // A Final prefab may texture a lower LOD while the FBX's highest LOD
        // retains an empty imported material. Borrow only within the same mesh family.
        if (!CharacterTexture(original))
        {
            string family = Family(source);
            var replacement = familyCandidates.Where(r => Family(r) == family)
                .Select(r => r.sharedMaterials.ElementAtOrDefault(submesh)).FirstOrDefault(CharacterTexture);
            if (replacement) original = replacement;
            else
            {
                string primary = hero == "Tao Thao" ? "ALL.mat" : hero == "Gia Cat Luong" ? "Ao.mat" : "New Material.mat";
                var canonical = AssetDatabase.LoadAssetAtPath<Material>(folder + primary);
                if (CharacterTexture(canonical)) original = canonical;
                else throw new InvalidOperationException("Cannot resolve character color texture for " + hero + "/" + source.name + ". Refusing to use an FX mask as body albedo.");
            }
        }
        string map = ColorMap(original);
        if (map != null)
        {
            target.SetTexture("_BaseMap", original.GetTexture(map));
            target.SetTextureScale("_BaseMap", original.GetTextureScale(map));
            target.SetTextureOffset("_BaseMap", original.GetTextureOffset(map));
        }
        if (original)
        {
            Color tint = original.HasProperty("_BaseColor") ? original.GetColor("_BaseColor") :
                original.HasProperty("_Color") ? original.GetColor("_Color") : Color.white;
            // HDR tints authored for glowing source materials must not become
            // hundreds of times brighter when copied into the VAT albedo shader.
            float intensity = Mathf.Max(1, tint.r, tint.g, tint.b);
            // The texture already contains the character's colors. Multiplying it
            // by a saturated blue/green/orange glow tint removes other channels.
            target.SetColor("_BaseColor", map != null ? Color.white : new Color(Mathf.Max(0, tint.r) / intensity,
                Mathf.Max(0, tint.g) / intensity, Mathf.Max(0, tint.b) / intensity, Mathf.Clamp01(tint.a)));
            target.SetFloat("_Cutoff", original.HasProperty("_Cutoff") && original.IsKeywordEnabled("_ALPHATEST_ON") ? original.GetFloat("_Cutoff") : 0);
        }
    }

    public static void RepairHero(string name)
    {
        string path = HeroVatBaker.Output + "/" + name + "/" + name + " VAT.prefab";
        if (!File.Exists(path) || EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = EditorSceneManager.NewPreviewScene();
        GameObject vat = null;
        try
        {
            var hero = HeroAnimationDisplaySetup.CreateSingleBakeSource(name, scene);
            var sources = hero.displayRoot.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer && r.GetComponent<MeshFilter>()).ToArray();
            vat = PrefabUtility.LoadPrefabContents(path);
            int fixedCount = 0;
            foreach (var renderer in vat.GetComponentsInChildren<MeshRenderer>(true))
            {
                var source = sources.FirstOrDefault(r => r.name == renderer.name);
                if (!source) { Debug.LogWarning("Cannot match VAT material source: " + name + "/" + renderer.name); continue; }
                for (int sub = 0; sub < renderer.sharedMaterials.Length; sub++)
                {
                    var material = renderer.sharedMaterials[sub];
                    if (!material) continue;
                    CopySurface(source, sources, sub, material, name);
                    EditorUtility.SetDirty(material);
                    AssetDatabase.SaveAssetIfDirty(material);
                    fixedCount++;
                }
            }
            Debug.Log(name + ": updated " + fixedCount + " VAT materials from Final. No animation rebake.");
        }
        finally
        {
            if (vat) PrefabUtility.UnloadPrefabContents(vat);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
