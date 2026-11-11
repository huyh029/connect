using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NhiQuoc.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Portable data only: no scene instance IDs or references to scene transforms.
public static class HeroEffectPresetStore
{
    public const string Folder = "Assets/_Game/AnimationDisplayPresets";
    public const string Latest = Folder + "/HeroFx.latest.json";

    [InitializeOnLoadMethod]
    private static void BackUpExistingSceneOnce()
    {
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || File.Exists(Latest)) return;
            var display = UnityEngine.Object.FindFirstObjectByType<HeroAnimationDisplay>();
            if (!display) return;
            try { Save(display, true); }
            catch (Exception exception) { Debug.LogException(exception); }
        };
    }
    [Serializable] private sealed class Preset
    {
        public int version = 1;
        public string savedUtc;
        public List<Entry> effects = new List<Entry>();
    }
    [Serializable] private sealed class Entry
    {
        public string hero, action, prefabGuid, attachmentPath;
        public int occurrence, triggerFrame;
        public Vector3 position, rotation, scale;
    }
    private static IEnumerable<(string action, HeroAnimationDisplay.AttackEffect[] effects)> Groups(HeroAnimationDisplay.Hero hero)
    {
        yield return ("Attack1", hero.attackEffects);
        yield return ("Attack2", hero.alternateAttackEffects);
        yield return ("Ulti", hero.ultiEffects);
    }

    public static void Save(HeroAnimationDisplay display, bool snapshot)
    {
        if (!display || EditorApplication.isPlaying) return;
        var preset = new Preset { savedUtc = DateTime.UtcNow.ToString("O") };
        foreach (var hero in display.heroes)
            foreach (var group in Groups(hero))
            {
                var occurrences = new Dictionary<string, int>();
                foreach (var fx in group.effects ?? Array.Empty<HeroAnimationDisplay.AttackEffect>())
                {
                    if (!fx.prefab) continue;
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fx.prefab));
                    occurrences.TryGetValue(guid, out int occurrence);
                    occurrences[guid] = occurrence + 1;
                    preset.effects.Add(new Entry { hero = hero.name, action = group.action,
                        prefabGuid = guid, occurrence = occurrence, triggerFrame = fx.triggerFrame,
                        position = fx.position, rotation = fx.eulerAngles, scale = fx.scale,
                        attachmentPath = fx.attachment && hero.displayRoot ? AnimationUtility.CalculateTransformPath(fx.attachment, hero.displayRoot) : "" });
                }
            }
        if (preset.effects.Count == 0) return;
        Directory.CreateDirectory(Folder);
        string json = JsonUtility.ToJson(preset, true);
        // Write a complete temporary file before replacing the last valid backup.
        string temporary = Latest + ".tmp";
        File.WriteAllText(temporary, json);
        if (File.Exists(Latest)) File.Replace(temporary, Latest, Latest + ".previous");
        else File.Move(temporary, Latest);
        AssetDatabase.ImportAsset(Latest);
        if (snapshot)
        {
            string path = Folder + "/HeroFx." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json";
            File.WriteAllText(path, json);
            AssetDatabase.ImportAsset(path);
            Debug.Log("Saved FX preset snapshot: " + path, display);
        }
    }

    public static int Load(HeroAnimationDisplay display, string path)
    {
        if (!display || !File.Exists(path)) return 0;
        var preset = JsonUtility.FromJson<Preset>(File.ReadAllText(path));
        if (preset == null || preset.version != 1 || preset.effects == null)
            throw new InvalidDataException("Unsupported or damaged FX preset: " + path);
        Undo.RecordObject(display, "Restore FX preset");
        int count = 0;
        foreach (var hero in display.heroes)
            foreach (var group in Groups(hero))
            {
                var occurrences = new Dictionary<string, int>();
                foreach (var fx in group.effects ?? Array.Empty<HeroAnimationDisplay.AttackEffect>())
                {
                    if (!fx.prefab) continue;
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(fx.prefab));
                    occurrences.TryGetValue(guid, out int occurrence);
                    occurrences[guid] = occurrence + 1;
                    var saved = preset.effects.FirstOrDefault(e => e.hero == hero.name && e.action == group.action && e.prefabGuid == guid && e.occurrence == occurrence);
                    if (saved == null) continue;
                    fx.position = saved.position; fx.eulerAngles = saved.rotation; fx.scale = saved.scale; fx.triggerFrame = saved.triggerFrame;
                    var attachment = string.IsNullOrEmpty(saved.attachmentPath) ? hero.displayRoot : hero.displayRoot.Find(saved.attachmentPath);
                    if (attachment) fx.attachment = attachment;
                    count++;
                }
            }
        EditorUtility.SetDirty(display);
        PrefabUtility.RecordPrefabInstancePropertyModifications(display);
        EditorSceneManager.MarkSceneDirty(display.gameObject.scene);
        Debug.Log("Restored " + count + " FX settings from " + path, display);
        return count;
    }
}
