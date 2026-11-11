using System;
using System.IO;
using UnityEngine;

namespace RecoilPathfinding
{
    [Serializable]
    public sealed class RecoilHeroConfig
    {
        public int Id;
        public string Name;
        public RecoilUnitCombatStats Stats;
        [Min(0)] public float UltiCooldown = 30f;
        [Min(0)] public float RespawnSeconds = 30f;
    }

    public sealed partial class RecoilAutoBattleDemo
    {
        private static readonly int[] HeroIds = { 1, 2, 3, 4, 5, 6, 7, 8 };
        private static readonly string[] HeroLabels = { "Chu Du", "Gia Cát Lượng", "Hạ Hầu Đôn", "Lữ Bố", "Lưu Bị", "Quan Vũ", "Tào Tháo", "Triệu Vân" };
        [SerializeField] private RecoilHeroConfig[] lobbyHeroStats;
        private RecoilUnitCombatStats[] _heroStatLimits;
        private bool _heroSettingsOpen, _heroSettingsDirty;
        private int _selectedHero;
        private Vector2 _heroSettingsScroll;
        private float _heroSaveAt;
        private string _heroSettingsStatus = "";
        private string HeroSettingsPath => Path.Combine(Application.streamingAssetsPath, "recoil_hero_stats.csv");

        private void LoadHeroSettings()
        {
            var existing = lobbyHeroStats;
            lobbyHeroStats = new RecoilHeroConfig[HeroIds.Length];
            for (int i = 0; i < HeroIds.Length; i++)
            {
                RecoilHeroConfig configured = null;
                if (existing != null) foreach (var entry in existing)
                    if (entry != null && entry.Id == HeroIds[i]) { configured = entry; break; }
                var baseline = lobbyUnitStats[i < 2 || i == 4 || i == 6 ? 5 : 7];
                lobbyHeroStats[i] = configured ?? new RecoilHeroConfig { Id = HeroIds[i], Name = HeroLabels[i], Stats = baseline, UltiCooldown = 30f };
                if (string.IsNullOrWhiteSpace(lobbyHeroStats[i].Name)) lobbyHeroStats[i].Name = HeroLabels[i];
            }
            try
            {
                string path = File.Exists(HeroSettingsPath) ? HeroSettingsPath : Path.Combine(Application.persistentDataPath, "recoil_hero_stats.csv");
                RecoilHeroConfig[] loaded = null;
                if (File.Exists(path))
                    loaded = RecoilHeroConfigCsv.Read(File.ReadAllText(path), lobbyHeroStats);
                if (loaded != null) foreach (var entry in loaded)
                {
                    if (entry == null || !entry.Stats.IsConfigured || !float.IsFinite(entry.UltiCooldown)) continue;
                    int index = Array.IndexOf(HeroIds, entry.Id);
                    if (index >= 0) { entry.UltiCooldown = Mathf.Max(0, entry.UltiCooldown); lobbyHeroStats[index] = entry; }
                }
                if (!File.Exists(HeroSettingsPath)) { _heroSettingsDirty = true; SaveHeroSettings(); }
            }
            catch (Exception exception) { Debug.LogWarning("Could not load hero settings: " + exception.Message, this); _heroSettingsStatus = "Không đọc được file cấu hình: " + exception.Message; }
            // Heroes do not participate in soldier role classification, including
            // when defaults came from a soldier archetype.
            foreach (var hero in lobbyHeroStats) hero.Stats.CombatRole = RecoilCombatRole.Unknown;
            _heroStatLimits = new RecoilUnitCombatStats[HeroIds.Length];
            for (int i = 0; i < HeroIds.Length; i++)
            {
                var initial = lobbyHeroStats[i].Stats;
                _heroStatLimits[i] = new RecoilUnitCombatStats { MaxHealth = Mathf.Max(1000, initial.MaxHealth * 2),
                    Damage = Mathf.Max(200, initial.Damage * 2), Range = Mathf.Max(10, initial.Range * 2),
                    AttackSpeed = Mathf.Max(5, initial.AttackSpeed * 2), MoveSpeed = Mathf.Max(10, initial.MoveSpeed * 2),
                    Armor = 1, SecondaryArmor = 1, AreaRadius = Mathf.Max(10, initial.AreaRadius * 2) };
            }
        }

        public bool TryGetHeroConfig(int heroId, out RecoilUnitCombatStats stats, out float ultiCooldown)
        {
            if (lobbyHeroStats != null) foreach (var hero in lobbyHeroStats)
                if (hero != null && hero.Id == heroId)
                { stats = hero.Stats; ultiCooldown = hero.UltiCooldown; return true; }
            stats = default; ultiCooldown = 0; return false;
        }

        private bool SaveHeroSettings()
        {
            if (lobbyHeroStats == null || !_heroSettingsDirty) return true;
            foreach (var hero in lobbyHeroStats) hero.Stats.CombatRole = RecoilCombatRole.Unknown;
            try
            {
                if (RefreshExternalCsv(HeroSettingsPath)) return false;
                RecoilSettingsCsv.Save(HeroSettingsPath, RecoilHeroConfigCsv.Write(lobbyHeroStats));
                RememberCsv(HeroSettingsPath);
                _heroSettingsDirty = false;
                _heroSettingsStatus = "Đã lưu cấu hình tướng.";
                return true;
            }
            catch (Exception exception)
            { _heroSettingsStatus = "Không lưu được: " + exception.Message; Debug.LogError(_heroSettingsStatus, this); return false; }
        }

        private void ClearHeroSettingInputs()
        {
            GUI.FocusControl(null);
            _settingsValueInputs.Clear();
            _focusedSettingsValueInputs.Clear();
        }

        private void DrawHeroSettings()
        {
            if (lobbyHeroStats == null || _heroStatLimits == null) LoadHeroSettings();
            float listWidth = Mathf.Min(220, Screen.width * .20f);
            var labels = new string[lobbyHeroStats.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = lobbyHeroStats[i].Name;
            var heroList = new Rect(SetupNavigationWidth + 24, 70, listWidth, Mathf.Max(160, Screen.height - 160));
            var heroButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 20, wordWrap = true };
            int selected = _selectedHero;
            var heroRowHeight = heroList.height / labels.Length;
            for (var i = 0; i < labels.Length; i++)
            {
                var button = new Rect(heroList.x, heroList.y + heroRowHeight * i, heroList.width, heroRowHeight);
                if (SettingsToggleButton(button, i == _selectedHero, new GUIContent(labels[i]), heroButtonStyle))
                    selected = i;
            }
            if (selected != _selectedHero) { _selectedHero = selected; ClearHeroSettingInputs(); }
            float left = SetupNavigationWidth + listWidth + 44;
            float width = Mathf.Max(260, Screen.width - left - 20);
            GUI.Label(new Rect(left, 62, width, 38), lobbyHeroStats[_selectedHero].Name, SettingsUnitNameStyle);
            GUI.Label(new Rect(left, 106, width, 28), "Chỉ số áp dụng cho cả hai phe", SettingsAttributeLabelStyle);
            var view = new Rect(left, 146, width, Mathf.Max(100, Screen.height - 250));
            _heroSettingsScroll = GUI.BeginScrollView(view, _heroSettingsScroll, new Rect(0, 0, width - 24, 540));
            var entry = lobbyHeroStats[_selectedHero];
            var previous = entry.Stats;
            float previousCooldown = entry.UltiCooldown;
            float previousRespawn = entry.RespawnSeconds;
            var stats = entry.Stats;
            var limits = _heroStatLimits[_selectedHero];
            float y = 0, row = 44, rowWidth = width - 34;
            stats.MaxHealth = Mathf.RoundToInt(DrawStatSlider(0, y, rowWidth, "Máu tối đa", stats.MaxHealth, 1, limits.MaxHealth, "0")); y += row;
            stats.Damage = Mathf.RoundToInt(DrawStatSlider(0, y, rowWidth, "Sát thương", stats.Damage, 0, limits.Damage, "0")); y += row;
            stats.Range = DrawStatSlider(0, y, rowWidth, "Tầm đánh", stats.Range, .1f, limits.Range, "0.0"); y += row;
            stats.AttackSpeed = DrawStatSlider(0, y, rowWidth, "Tốc độ đánh", stats.AttackSpeed, .1f, limits.AttackSpeed, "0.00"); y += row;
            stats.MoveSpeed = DrawStatSlider(0, y, rowWidth, "Tốc độ di chuyển", stats.MoveSpeed, .1f, limits.MoveSpeed, "0.0"); y += row;
            stats.Armor = DrawStatSlider(0, y, rowWidth, "Armor (đánh gần)", stats.Armor, 0, 1, "P0"); y += row;
            stats.SecondaryArmor = DrawStatSlider(0, y, rowWidth, "P. Armor (đánh xa)", stats.SecondaryArmor, 0, 1, "P0"); y += row;
            stats.AreaRadius = DrawStatSlider(0, y, rowWidth, "Bán kính sát thương", stats.AreaRadius, 0, limits.AreaRadius, "0.0"); y += row;
            entry.UltiCooldown = DrawStatSlider(0, y, rowWidth, "Hồi Ulti (giây)", entry.UltiCooldown, 0, Mathf.Max(300, previousCooldown), "0.0"); y += row;
            entry.RespawnSeconds = DrawStatSlider(0, y, rowWidth, "Hồi sinh (giây)", entry.RespawnSeconds, 0, Mathf.Max(600, previousRespawn), "0.0"); y += row;
            stats.SecondaryDamage = stats.AttackType == RecoilAttackType.Ranged;
            stats.CombatRole = RecoilCombatRole.Unknown;
            entry.Stats = stats;
            GUI.EndScrollView();
            if (!previous.Equals(stats) || !Mathf.Approximately(previousCooldown, entry.UltiCooldown) || !Mathf.Approximately(previousRespawn, entry.RespawnSeconds))
            { _heroSettingsDirty = true; _heroSaveAt = Time.unscaledTime + .75f; _heroSettingsStatus = "Chưa lưu thay đổi..."; }
            if (_heroSettingsDirty && Time.unscaledTime >= _heroSaveAt) SaveHeroSettings();
            if (SettingsButton(new Rect(left, Screen.height - 92, 180, 46),
                new GUIContent("Lưu cấu hình tướng"), GUI.skin.button))
            { _heroSettingsDirty = true; SaveHeroSettings(); }
            GUI.Label(new Rect(left + 190, Screen.height - 88, Mathf.Max(100, width - 190), 42), _heroSettingsStatus, new GUIStyle(GUI.skin.label) { wordWrap = true });
        }
    }
}
