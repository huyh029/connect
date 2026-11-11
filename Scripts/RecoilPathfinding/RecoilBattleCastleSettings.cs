using System;
using System.IO;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private string CastleSettingsPath => Path.Combine(Application.streamingAssetsPath, "recoil_castle_stats.csv");
        private void LoadCastleSettings()
        {
            try
            {
                string path = File.Exists(CastleSettingsPath) ? CastleSettingsPath : Path.Combine(Application.persistentDataPath, "recoil_castle_stats.csv");
                if (File.Exists(path)) castleHealth = RecoilSettingsCsv.ReadCastle(File.ReadAllText(path));
                castleHealth = Mathf.Clamp(castleHealth, MinimumCastleHealth, MaximumCastleHealth);
                if (!File.Exists(CastleSettingsPath)) SaveCastleSettings();
            }
            catch (Exception exception) { Debug.LogError("Could not load castle CSV: " + exception.Message, this); }
        }
        private void SaveCastleSettings()
        {
            RecoilSettingsCsv.Save(CastleSettingsPath, RecoilSettingsCsv.WriteCastle(castleHealth));
        }
    }
}
