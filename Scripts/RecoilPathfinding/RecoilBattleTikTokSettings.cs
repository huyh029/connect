using System;
using System.IO;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private bool _tikTokSettingsOpen;
        private TikTokArmySettings _tikTokArmySettings;
        private Vector2 _tikTokSettingsScroll;
        private string _tikTokSettingsStatus = "";
        private bool _settingsTableMode;
        private int _selectedGiftRule;
        private Vector2 _combatSettingsScroll, _giftDetailScroll;
        private readonly System.Collections.Generic.Dictionary<string, string> _csvVersions = new();
        private float _nextCsvRefresh;

        private void RememberCsv(string path)
        {
            if (File.Exists(path)) _csvVersions[path] = File.ReadAllText(path);
        }

        private void RefreshExternalSettings()
        {
            if (Time.unscaledTime < _nextCsvRefresh || lobbyUnitStats == null) return;
            _nextCsvRefresh = Time.unscaledTime + .5f;
            foreach (string path in new[] { LobbyUnitStatsPath, CastleSettingsPath, HeroSettingsPath, TikTokArmySettingsPath })
                try { RefreshExternalCsv(path); }
                catch (Exception e) { Debug.LogWarning("Không tải lại CSV " + path + ": " + e.Message, this); }
        }

        // Returns true when disk has changed, so a pending save must not overwrite it.
        private bool RefreshExternalCsv(string path)
        {
            if (!File.Exists(path)) return false;
            string csv = File.ReadAllText(path);
            if (!_csvVersions.TryGetValue(path, out string previous)) { _csvVersions[path] = csv; return false; }
            if (csv == previous) return false;
            if (path == LobbyUnitStatsPath)
            {
                var loaded = RecoilSettingsCsv.ReadUnits(csv, out _, out _, lobbyUnitStats);
                if (loaded.Length != lobbyUnitStats.Length || Array.Exists(loaded, s => !s.IsConfigured))
                    throw new InvalidDataException("CSV lính phải có đủ các dòng cấu hình hợp lệ.");
                lobbyUnitStats = loaded;
                _lobbyStatLimits = null;
                _unitSettingsStatus = "Đã tải thay đổi từ CSV bên ngoài.";
            }
            else if (path == CastleSettingsPath)
            {
                castleHealth = Mathf.Clamp(RecoilSettingsCsv.ReadCastle(csv), MinimumCastleHealth, MaximumCastleHealth);
                _unitSettingsStatus = "Đã tải máu thành từ CSV bên ngoài.";
            }
            else if (path == HeroSettingsPath)
            {
                var loaded = RecoilHeroConfigCsv.Read(csv, lobbyHeroStats);
                if (loaded.Length != lobbyHeroStats.Length) throw new InvalidDataException("CSV tướng phải có đủ các dòng cấu hình.");
                foreach (var entry in loaded)
                {
                    int index = Array.FindIndex(lobbyHeroStats, h => h.Id == entry.Id);
                    if (index >= 0) lobbyHeroStats[index] = entry;
                }
                _heroSettingsDirty = false;
                _heroSettingsStatus = "Đã tải thay đổi từ CSV bên ngoài.";
            }
            else
            {
                var loaded = RecoilSettingsCsv.ReadTikTok(csv);
                if (_tikTokArmySettings != null) foreach (var old in _tikTokArmySettings.Rules)
                {
                    var next = loaded.Rules.Find(r => old.GiftId > 0 ? r.GiftId == old.GiftId : r.Name == old.Name && r.EventType == old.EventType);
                    if (next == null) continue;
                    if (_tikTokProgress.TryGetValue(old, out var progress)) { _tikTokProgress.Remove(old); _tikTokProgress[next] = progress; }
                    if (_tikTokHeroProgress.TryGetValue(old, out var heroes)) { _tikTokHeroProgress.Remove(old); _tikTokHeroProgress[next] = heroes; }
                    foreach (var reward in _heroRewards) if (ReferenceEquals(reward.Rule, old)) reward.Rule = next;
                }
                _tikTokArmySettings = loaded;
                _tikTokSettingsStatus = "Đã tải thay đổi từ CSV bên ngoài.";
            }
            _csvVersions[path] = csv;
            _settingsValueInputs.Clear();
            _focusedSettingsValueInputs.Clear();
            return true;
        }
        private static readonly string[] TikTokEventTypes = { "like", "gift", "follow", "share", "comment" };
        private string TikTokArmySettingsPath => Path.Combine(Application.streamingAssetsPath, "tiktok_army_settings.csv");
        private float SetupNavigationWidth => Mathf.Min(210f, Screen.width * .17f);

        private GUIStyle _settingsNavItemStyle, _settingsNavItemSelectedStyle;

        /// <summary>
        /// Tab / list entry. No hover effect (only real buttons react to hovering); the selected one gets a soft gold
        /// highlight with an accent bar and gold text.
        /// </summary>
        private bool SettingsNavItem(Rect rect, string label, bool selected = false)
        {
            if (_settingsNavItemStyle == null)
            {
                _settingsNavItemStyle = WithoutHover(new GUIStyle(GUIStyle.none)
                {
                    font = SettingsFont, fontSize = 19, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                    wordWrap = true, padding = new RectOffset(16, 8, 4, 6), normal = { textColor = ThemeTextDim }
                });
                _settingsNavItemSelectedStyle = WithoutHover(new GUIStyle(_settingsNavItemStyle) { normal = { textColor = ThemeGoldBright } });
            }
            if (selected && Event.current.type == EventType.Repaint)
            {
                var highlight = new Rect(rect.x, rect.y + 3f, rect.width, rect.height - 6f);
                GUI.DrawTexture(highlight, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                    Alpha(ThemeGold, .12f), 0, 10f);
                GUI.DrawTexture(new Rect(highlight.x, highlight.y + 8f, 3f, Mathf.Max(4f, highlight.height - 16f)),
                    Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, ThemeGold, 0, 1.5f);
            }
            return GUI.Button(rect, new GUIContent(label), selected ? _settingsNavItemSelectedStyle : _settingsNavItemStyle);
        }

        private void DrawSetupNavigation()
        {
            var left = 12f;
            var width = SetupNavigationWidth;
            var modeLabel = new GUIStyle(GUIStyle.none)
            {
                font = SettingsFont, fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = ThemeTextDim }
            };
            // CSV / drag mode only matters to the soldier, gift and hero tabs.
            if (!_initialArmySettingsOpen && !_cursorSettingsOpen)
            {
            GUI.Label(new Rect(left, 440, width, 26), "CSV ↔ Kéo thả", modeLabel);
            var toggleRect = new Rect(left + (width - 72) * .5f, 472, 72, 34);
            bool tableMode = GUI.Toggle(toggleRect, _settingsTableMode,
                GUIContent.none, GUIStyle.none);
            GUI.DrawTexture(toggleRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                ThemeSurfaceRaised, 0, toggleRect.height * .5f);
            GUI.DrawTexture(toggleRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                Alpha(ThemeGold, .55f), 1.5f, toggleRect.height * .5f);
            var thumb = new Rect(tableMode ? toggleRect.x + 4 : toggleRect.xMax - 30, toggleRect.y + 4, 26, 26);
            GUI.DrawTexture(thumb, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                ThemeGold, 0, 13);
            GUI.Label(new Rect(left, 510, width, 24), tableMode ? "Đang dùng CSV" : "Đang dùng kéo thả", modeLabel);
            if (tableMode != _settingsTableMode)
            {
                _settingsTableMode = tableMode;
                _combatSettingsScroll = _giftDetailScroll = Vector2.zero;
                ClearHeroSettingInputs();
            }
            }
            var otherTabOpen = _tikTokSettingsOpen || _heroSettingsOpen || _initialArmySettingsOpen || _cursorSettingsOpen;
            if (SettingsNavItem(new Rect(left, 70f, width, 64f), "Setup lính", !otherTabOpen) && otherTabOpen)
            {
                if (!SaveHeroSettings()) return;
                if (_tikTokArmySettings == null || SaveTikTokArmySettings()) { _tikTokSettingsOpen = false; _heroSettingsOpen = false; _initialArmySettingsOpen = false; _cursorSettingsOpen = false; ClearHeroSettingInputs(); }
            }
            if (SettingsNavItem(new Rect(left, 144f, width, 64f), "Setup quà", _tikTokSettingsOpen))
            {
                if (!SaveHeroSettings()) return;
                _tikTokSettingsOpen = true; _heroSettingsOpen = false; _initialArmySettingsOpen = false; _cursorSettingsOpen = false;
            }
            if (SettingsNavItem(new Rect(left, 218f, width, 64f), "Setup tướng", _heroSettingsOpen) && !_heroSettingsOpen)
            {
                if (_tikTokArmySettings != null && !SaveTikTokArmySettings()) return;
                SaveLobbyUnitStats();
                _heroSettingsOpen = true; _tikTokSettingsOpen = false; _initialArmySettingsOpen = false; _cursorSettingsOpen = false; ClearHeroSettingInputs();
            }
            if (SettingsNavItem(new Rect(left, 292f, width, 64f), "Quân đầu game", _initialArmySettingsOpen))
            {
                if (!SaveHeroSettings()) return;
                if (_tikTokArmySettings != null && !SaveTikTokArmySettings()) return;
                SaveLobbyUnitStats();
                _initialArmySettingsOpen = true;
                _heroSettingsOpen = false;
                _tikTokSettingsOpen = false;
                _cursorSettingsOpen = false;
            }
            if (SettingsNavItem(new Rect(left, 366f, width, 64f), "Setup chuột", _cursorSettingsOpen) && !_cursorSettingsOpen)
            {
                if (!SaveHeroSettings()) return;
                if (_tikTokArmySettings != null && !SaveTikTokArmySettings()) return;
                SaveLobbyUnitStats();
                _cursorSettingsOpen = true;
                _heroSettingsOpen = false;
                _tikTokSettingsOpen = false;
                _initialArmySettingsOpen = false;
                ClearHeroSettingInputs();
            }
            if (SettingsNavItem(new Rect(left, Screen.height - 74f, width, 56f), "← Quay lại"))
            {
                if (_tikTokArmySettings != null && !SaveTikTokArmySettings()) return;
                if (!SaveHeroSettings()) return;
                SaveLobbyUnitStats();
                _tikTokSettingsOpen = false;
                _heroSettingsOpen = false;
                _cursorSettingsOpen = false;
                _settingsOpen = false;
            }
        }

        private void LoadTikTokArmySettings()
        {
            _tikTokArmySettings = new TikTokArmySettings();
            try
            {
                if (!File.Exists(TikTokArmySettingsPath))
                {
                    var previousPath = Path.Combine(Application.persistentDataPath, "tiktok_army_settings.csv");
                    if (File.Exists(previousPath))
                    {
                        Directory.CreateDirectory(Application.streamingAssetsPath);
                        File.Copy(previousPath, TikTokArmySettingsPath, false);
                    }
                }
                if (File.Exists(TikTokArmySettingsPath))
                {
                    _tikTokArmySettings = RecoilSettingsCsv.ReadTikTok(File.ReadAllText(TikTokArmySettingsPath));
                }
                else
                {
                    AddTikTokSampleRows();
                }
                if (_tikTokArmySettings.Rules == null) _tikTokArmySettings.Rules = new System.Collections.Generic.List<TikTokArmyRule>();
                _tikTokArmySettings.Rules.RemoveAll(rule => rule == null);
                foreach (var rule in _tikTokArmySettings.Rules)
                    InitializeTikTokRandomRule(rule);
                if (!File.Exists(TikTokArmySettingsPath)) SaveTikTokArmySettings();
                RememberCsv(TikTokArmySettingsPath);
            }
            catch (Exception e) { _tikTokSettingsStatus = "Không đọc được cấu hình: " + e.Message; }
        }

        private void AddTikTokSampleRows()
        {
            var defaults = RecoilTikTokSample.Create();
            _tikTokArmySettings.HeroRules = defaults.HeroRules;
            var samples = defaults.Rules;
            foreach (var sample in samples)
                if (!_tikTokArmySettings.Rules.Exists(rule => rule.Name == sample.Name))
                {
                    InitializeTikTokRandomRule(sample);
                    _tikTokArmySettings.Rules.Add(sample);
                }
        }

        private static void InitializeTikTokRandomRule(TikTokArmyRule rule)
        {
            if (rule.Name != "Heo" || (rule.RandomRewards != null && rule.RandomRewards.Count > 0)) return;
            rule.RandomRewards = new System.Collections.Generic.List<TikTokRandomReward>
            {
                new TikTokRandomReward { GiftName = "Rosa", Weight = 40 },
                new TikTokRandomReward { GiftName = "Perfume", Weight = 34 },
                new TikTokRandomReward { GiftName = "Doughnut", Weight = 20 },
                new TikTokRandomReward { GiftName = "Hạc Giấy", Weight = 4 },
                new TikTokRandomReward { GiftName = "Kính Râm", Weight = 2 }
            };
        }

        private bool SaveTikTokArmySettings()
        {
            try
            {
                if (RefreshExternalCsv(TikTokArmySettingsPath)) return false;
                Directory.CreateDirectory(Application.streamingAssetsPath);
                RecoilSettingsCsv.Save(TikTokArmySettingsPath, RecoilSettingsCsv.WriteTikTok(_tikTokArmySettings));
                RememberCsv(TikTokArmySettingsPath);
                _tikTokSettingsStatus = "Đã lưu cấu hình.";
                return true;
            }
            catch (Exception e) { _tikTokSettingsStatus = "Không lưu được: " + e.Message; return false; }
        }

        private static int TikTokIntInput(string label, int value, int minimum)
        {
            GUILayout.Label(label);
            var text = GUILayout.TextField(value.ToString(), GUILayout.Width(90));
            return int.TryParse(text, out var parsed) ? Mathf.Max(minimum, parsed) : value;
        }

        private void DrawTikTokArmySettings()
        {
            var randomStyle = new GUIStyle(GUI.skin.toggle) { wordWrap = true, normal = { textColor = ThemeText } };
            bool randomTeam = GUI.Toggle(new Rect(12, 552, SetupNavigationWidth, 55),
                TiktokEventTestPopup.RandomTeamIfUnassigned, "Random phe nếu chưa chọn phe", randomStyle);
            if (randomTeam != TiktokEventTestPopup.RandomTeamIfUnassigned)
                TiktokEventTestPopup.RandomTeamIfUnassigned = randomTeam;
            if (_settingsTableMode) DrawTikTokArmyTable();
            else DrawGiftSliders();
        }

        private float SettingsNumber(Rect rect, string key, float value, float minimum, float maximum, GUIStyle style)
        {
            GUI.SetNextControlName(key);
            if (GUI.GetNameOfFocusedControl() != key || !_settingsValueInputs.ContainsKey(key))
                _settingsValueInputs[key] = value.ToString("G7", System.Globalization.CultureInfo.CurrentCulture);
            string before = _settingsValueInputs[key];
            _settingsValueInputs[key] = GUI.TextField(rect, before, SettingsTextBoxStyle());
            return before != _settingsValueInputs[key] && float.TryParse(_settingsValueInputs[key], out float parsed) && float.IsFinite(parsed)
                ? Mathf.Clamp(parsed, minimum, maximum) : value;
        }

        private void DrawCombatSettings(bool heroes)
        {
            if (heroes && (lobbyHeroStats == null || _heroStatLimits == null)) LoadHeroSettings();
            float left = SetupNavigationWidth + 28, width = Screen.width - left - 20;
            var cell = TikTokTableStyle();
            var heading = TikTokTableStyle(true);
            heading.wordWrap = true;
            DrawSettingsTitle(new Rect(left, 16, width, 36), heroes ? "CẤU HÌNH TƯỚNG — áp dụng cho cả hai phe" : "CẤU HÌNH LÍNH — áp dụng cho cả hai phe");
            int count = heroes ? lobbyHeroStats.Length : lobbyUnitStats.Length;
            string[] fields = { "Máu tối đa", "Sát thương", "Tầm đánh", "Tốc độ đánh", "Tốc độ di chuyển", "Armor gần (%)", "Armor xa (%)", "Bán kính sát thương", "Hồi ulti (giây)" };
            int fieldCount = heroes ? 9 : 8;
            int selected = heroes ? _selectedHero : _selectedLobbyUnitType;
            if (!_settingsTableMode)
            {
                var labels = new string[count];
                for (int i = 0; i < count; i++) labels[i] = heroes ? lobbyHeroStats[i].Name : CharacterTypeLabels[i];
                int next = selected;
                float itemHeight = Mathf.Min(64, Mathf.Max(36, (Screen.height - 200f) / count));
                for (int i = 0; i < count; i++)
                    if (SettingsNavItem(new Rect(left, 64 + i * itemHeight, 180, itemHeight - 2), labels[i], i == selected)) next = i;
                if (next != selected) { selected = next; ClearHeroSettingInputs(); }
                if (heroes) _selectedHero = selected; else _selectedLobbyUnitType = selected;
                left += 196; width -= 196;
            }
            var view = new Rect(left, 64, width, Mathf.Max(80, Screen.height - 200));
            float contentWidth = _settingsTableMode ? Mathf.Max(width - 20, 190 + fieldCount * 135) : Mathf.Max(460, width - 20);
            float radarY = _settingsTableMode ? (count + 1) * 58 + 12 : fieldCount * 48 + 12;
            _combatSettingsScroll = GUI.BeginScrollView(view, _combatSettingsScroll,
                new Rect(0, 0, contentWidth, radarY + 360));
            if (_settingsTableMode)
            {
                GUI.Label(new Rect(0, 0, 188, 56), "Tên / Mã", heading);
                for (int f = 0; f < fieldCount; f++) GUI.Label(new Rect(190 + f * 135, 0, 133, 56), fields[f], heading);
            }
            for (int i = 0; i < count; i++)
            {
                if (!_settingsTableMode && i != selected) continue;
                var stats = heroes ? lobbyHeroStats[i].Stats : lobbyUnitStats[i];
                var previous = stats;
                var limits = heroes ? _heroStatLimits[i] : GetLobbyStatLimits(i);
                float cooldown = heroes ? lobbyHeroStats[i].UltiCooldown : 0;
                float[] values = { stats.MaxHealth, stats.Damage, stats.Range, stats.AttackSpeed, stats.MoveSpeed, stats.Armor * 100, stats.SecondaryArmor * 100, stats.AreaRadius, cooldown };
                float[] maxima = { limits.MaxHealth, limits.Damage, limits.Range, limits.AttackSpeed, limits.MoveSpeed, 100, 100, limits.AreaRadius, Mathf.Max(300, cooldown) };
                float[] minima = { 1, 0, .1f, .1f, .1f, 0, 0, 0, 0 };
                float y = (i + 1) * 58;
                if (_settingsTableMode && SettingsButton(new Rect(0, y, 188, 54),
                    new GUIContent(heroes ? lobbyHeroStats[i].Id + " — " + lobbyHeroStats[i].Name : CharacterTypeLabels[i]), cell))
                {
                    selected = i;
                    if (heroes) _selectedHero = i; else _selectedLobbyUnitType = i;
                }
                for (int f = 0; f < fieldCount; f++)
                {
                    values[f] = _settingsTableMode
                        ? SettingsNumber(new Rect(190 + f * 135, y, 133, 54), "combat_" + heroes + "_" + i + "_" + f, values[f], minima[f], Mathf.Max(maxima[f], values[f]), cell)
                        : DrawStatSlider(0, f * 48, contentWidth - 12, fields[f], values[f], minima[f], Mathf.Max(maxima[f], values[f]), f < 2 ? "0" : "0.##");
                }
                stats.MaxHealth = Mathf.RoundToInt(values[0]); stats.Damage = Mathf.RoundToInt(values[1]);
                stats.Range = values[2]; stats.AttackSpeed = values[3]; stats.MoveSpeed = values[4];
                if (values[5] != stats.Armor * 100) stats.Armor = values[5] / 100;
                if (values[6] != stats.SecondaryArmor * 100) stats.SecondaryArmor = values[6] / 100;
                stats.AreaRadius = values[7];
                if (heroes)
                {
                    stats.CombatRole = RecoilCombatRole.Unknown;
                    if (!previous.Equals(stats) || cooldown != values[8]) _heroSettingsDirty = true;
                    lobbyHeroStats[i].Stats = stats; lobbyHeroStats[i].UltiCooldown = values[8];
                }
                else lobbyUnitStats[i] = stats;
            }
            var radarStats = heroes ? lobbyHeroStats[selected].Stats : lobbyUnitStats[selected];
            var radarLimits = heroes ? _heroStatLimits[selected] : GetLobbyStatLimits(selected);
            GUI.Label(new Rect(0, radarY, Mathf.Min(contentWidth, 520), 32), "Biểu đồ chỉ số — " + (heroes ? lobbyHeroStats[selected].Name : CharacterTypeLabels[selected]) + (_settingsTableMode ? " (nhấn tên trong bảng để chọn)" : ""), heading);
            DrawAttributeRadar(new Rect(30, radarY + 40, Mathf.Min(contentWidth - 60, 460), 290), radarStats, radarLimits);
            GUI.EndScrollView();
            float footer = Screen.height - 120;
            if (!heroes)
            {
                if (_settingsTableMode)
                {
                    float labelWidth = Mathf.Min(180, width * .4f);
                    GUI.Label(new Rect(left, footer, labelWidth, 36), "Máu thành", heading);
                    const string key = "CastleHealthCsvInput";
                    if (GUI.GetNameOfFocusedControl() != key || !_settingsValueInputs.ContainsKey(key))
                        _settingsValueInputs[key] = castleHealth.ToString();
                    GUI.SetNextControlName(key);
                    _settingsValueInputs[key] = GUI.TextField(new Rect(left + labelWidth + 8, footer, Mathf.Min(260, width - labelWidth - 8), 36), _settingsValueInputs[key], SettingsTextBoxStyle());
                    if (int.TryParse(_settingsValueInputs[key], out int health))
                        castleHealth = Mathf.Clamp(health, MinimumCastleHealth, MaximumCastleHealth);
                }
                else castleHealth = Mathf.RoundToInt(DrawStatSlider(left, footer, width, "Máu thành", castleHealth, MinimumCastleHealth, MaximumCastleHealth, "N0W"));
            }
            if (SettingsButton(new Rect(left, Screen.height - 70, 180, 44), new GUIContent("Lưu cấu hình"), heading))
            {
                if (heroes) SaveHeroSettings(); else SaveLobbyUnitStats();
            }
            var statusStyle = new GUIStyle(cell) { wordWrap = true };
            GUI.Label(new Rect(left + 190, Screen.height - 70, Mathf.Max(100, width - 190), 44), heroes ? _heroSettingsStatus : _unitSettingsStatus, statusStyle);
        }

        private void DrawGiftSliders()
        {
            if (_tikTokArmySettings == null) LoadTikTokArmySettings();
            float left = SetupNavigationWidth + 28, width = Screen.width - left - 20;
            var style = TikTokTableStyle(); var heading = TikTokTableStyle(true);
            var rules = _tikTokArmySettings.Rules.FindAll(r => r.EventType != "comment");
            DrawSettingsTitle(new Rect(left, 16, width, 36), "CẤU HÌNH QUÀ — chọn một dòng để kéo thanh trượt");
            float listWidth = 190;
            _tikTokSettingsScroll = GUI.BeginScrollView(new Rect(left, 64, listWidth, Mathf.Max(60, Screen.height - 200)), _tikTokSettingsScroll, new Rect(0, 0, listWidth - 20, rules.Count * 48));
            for (int i = 0; i < rules.Count; i++)
                if (SettingsNavItem(new Rect(0, i * 48, listWidth - 24, 44), string.IsNullOrEmpty(rules[i].Name) ? "Dòng " + (i + 1) : rules[i].Name, i == _selectedGiftRule))
                { _selectedGiftRule = i; ClearHeroSettingInputs(); }
            GUI.EndScrollView();
            left += listWidth + 16; width -= listWidth + 16;
            if (rules.Count > 0)
            {
                _selectedGiftRule = Mathf.Clamp(_selectedGiftRule, 0, rules.Count - 1);
                var rule = rules[_selectedGiftRule];
                _giftDetailScroll = GUI.BeginScrollView(new Rect(left, 64, width, Mathf.Max(60, Screen.height - 200)), _giftDetailScroll, new Rect(0, 0, Mathf.Max(480, width - 20), 650));
                float w = Mathf.Max(460, width - 30);
                rule.Name = GUI.TextField(new Rect(0, 0, w, 36), rule.Name, SettingsTextBoxStyle());
                bool like = rule.MatchesEvent("like"), follow = rule.MatchesEvent("follow"), share = rule.MatchesEvent("share");
                rule.Like = GUI.Toggle(new Rect(0, 46, 95, 32), like, "Like");
                rule.Follow = GUI.Toggle(new Rect(100, 46, 95, 32), follow, "Follow");
                rule.Share = GUI.Toggle(new Rect(200, 46, 95, 32), share, "Share");
                rule.EventType = rule.Like ? "like" : rule.Follow ? "follow" : rule.Share ? "share" : "gift";
                int Number(int row, string label, int value, int min, int max) => Mathf.RoundToInt(DrawStatSlider(0, 90 + row * 48, w, label, value, min, Mathf.Max(max, value), "0"));
                rule.Coins = Number(0, "Số xu", rule.Coins, 0, 1000);
                GUI.Label(new Rect(0, 138, 180, 32), "ID quà", style);
                rule.GiftId = TikTokTableNumber(new Rect(200, 138, w - 200, 32), rule.GiftId, 0, style);
                rule.Threshold = Number(2, "Ngưỡng", rule.Threshold, 1, 1000);
                rule.UnitCount = Number(3, "Số lượng quân", rule.UnitCount, 0, 1000);
                if (SettingsButton(new Rect(0, 282, w, 40),
                    new GUIContent("Loại binh: " + (rule.UnitType < 0 ? "Không có" : CharacterTypeLabels[Mathf.Clamp(rule.UnitType, 0, CharacterTypeLabels.Length - 1)]) + " →"), GUI.skin.button))
                    rule.UnitType = (rule.UnitType + 2) % (CharacterTypeLabels.Length + 1) - 1;
                rule.RedAccumulation = Number(5, "Tích phe đỏ", rule.RedAccumulation, 0, 100);
                rule.RedHeroId = Mathf.Clamp(Number(6, "Mã tướng đỏ (0: không)", rule.RedHeroId, 0, 8), 0, 8);
                rule.BlueAccumulation = Number(7, "Tích phe xanh", rule.BlueAccumulation, 0, 100);
                rule.BlueHeroId = Mathf.Clamp(Number(8, "Mã tướng xanh (0: không)", rule.BlueHeroId, 0, 8), 0, 8);
                rule.Enabled = GUI.Toggle(new Rect(0, 530, w, 32), rule.Enabled, "Bật quy tắc này");
                if (SettingsButton(new Rect(0, 580, 160, 40), new GUIContent("Xóa dòng"), GUI.skin.button))
                { _tikTokArmySettings.Rules.Remove(rule); ClearHeroSettingInputs(); }
                GUI.EndScrollView();
            }
            if (SettingsButton(new Rect(left, Screen.height - 90, 150, 44), new GUIContent("+ Thêm dòng"), GUI.skin.button))
            { _tikTokArmySettings.Rules.Add(new TikTokArmyRule { Like = true }); _selectedGiftRule = rules.Count; ClearHeroSettingInputs(); }
            if (SettingsButton(new Rect(left + 160, Screen.height - 90, 150, 44), new GUIContent("Lưu cấu hình"), GUI.skin.button))
                SaveTikTokArmySettings();
            GUI.Label(new Rect(left, Screen.height - 42, width, 32), _tikTokSettingsStatus, style);
        }

        /// <summary>
        /// Table cell: quiet translucent cell with a hairline border; <paramref name="bold"/> = header cell, tinted gold.
        /// </summary>
        private GUIStyle TikTokTableStyle(bool bold = false)
        {
            var background = bold
                ? CellTexture(Alpha(ThemeGold, .13f), Alpha(ThemeGold, .32f))
                : CellTexture(ThemeSurface, ThemeLine);
            var style = new GUIStyle(GUI.skin.textField)
            {
                font = SettingsFont,
                fontSize = 17, alignment = TextAnchor.MiddleCenter,
                fontStyle = bold ? FontStyle.Bold : FontStyle.Normal,
                border = new RectOffset(1, 1, 1, 1),
                padding = new RectOffset(8, 8, 5, 5)
            };
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                         style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                state.background = background;
                state.scaledBackgrounds = null;
                state.textColor = bold ? ThemeGoldBright : ThemeText;
            }
            return style;
        }

        private int TikTokTableNumber(Rect rect, int value, int minimum, GUIStyle style)
        {
            var text = GUI.TextField(rect, value.ToString(), SettingsTextBoxStyle());
            return int.TryParse(text, out var parsed) ? Mathf.Max(minimum, parsed) : value;
        }

        private GUIStyle SettingsTextBoxStyle()
        {
            var style = TikTokTableStyle();
            style.fontSize = 20;
            style.alignment = TextAnchor.MiddleRight;
            style.wordWrap = false;
            style.padding = new RectOffset(8, 8, 4, 4);
            // Editable boxes stand out a little more than plain cells; the one being typed in gets a gold edge.
            var editable = CellTexture(ThemeSurfaceRaised, ThemeLineStrong);
            foreach (var state in new[] { style.normal, style.hover, style.active, style.onNormal, style.onHover, style.onActive })
                state.background = editable;
            var focused = CellTexture(ThemeSurfaceRaised, ThemeGold);
            style.focused.background = focused;
            style.onFocused.background = focused;
            return style;
        }

        private bool TikTokTableCheckbox(Rect rect, bool value, GUIStyle style)
        {
            GUI.Label(rect, GUIContent.none, style);
            var checkbox = new Rect(rect.center.x - 10f, rect.center.y - 10f, 20f, 20f);
            return GUI.Toggle(checkbox, value, GUIContent.none);
        }

        private void DrawTikTokArmyTable()
        {
            if (_tikTokArmySettings == null) LoadTikTokArmySettings();
            var area = new Rect(SetupNavigationWidth + 28f, 60f,
                Screen.width - SetupNavigationWidth - 44f, Mathf.Max(140f, Screen.height - 80f));
            var oldColor = GUI.color;
            var oldContent = GUI.contentColor;
            var oldBackground = GUI.backgroundColor;
            GUI.color = GUI.contentColor = GUI.backgroundColor = Color.white;
            var cell = TikTokTableStyle();
            var heading = TikTokTableStyle(true);
            GUI.Label(new Rect(area.x + 10, area.y + 4, area.width - 20, 34), "SETUP QUÀ — SỰ KIỆN → QUÂN", heading);
            GUI.Label(new Rect(area.x + 10, area.y + 38, area.width - 20, 30), "Tích chọn Like / Follow / Share để bật. Bỏ chọn cả ba = quà tặng. Mã tướng 0 = không có; 1–8 theo bảng tướng.", cell);
            var viewport = new Rect(area.x + 10, area.y + 78, area.width - 20, Mathf.Max(30, area.height - 180));
            var widths = new[] { 70f, 80f, 70f, 80f, 100f, 90f, 90f, 160f, 130f, 110f, 130f, 110f, 65f };
            var headers = new[] { "Like", "Follow", "Share", "Số xu", "ID", "Ngưỡng", "Số lượng", "Loại binh", "Số lượng tích phe đỏ", "Mã tướng phe đỏ", "Số lượng tích phe xanh", "Mã tướng phe xanh", "Xóa" };
            const float rowHeight = 80f;
            cell.wordWrap = heading.wordWrap = true;
            var tableWidth = 0f;
            var tableRules = _tikTokArmySettings.Rules.FindAll(rule => rule.EventType != "comment");
            foreach (var columnWidth in widths) tableWidth += columnWidth;
            _tikTokSettingsScroll = GUI.BeginScrollView(viewport, _tikTokSettingsScroll,
                new Rect(0, 0, tableWidth, (tableRules.Count + 1) * rowHeight));
            var remove = -1;
            for (var row = -1; row < tableRules.Count; row++)
            {
                var x = 0f;
                var rule = row >= 0 ? tableRules[row] : null;
                if (rule != null)
                {
                    bool like = rule.MatchesEvent("like"), follow = rule.MatchesEvent("follow"), share = rule.MatchesEvent("share");
                    rule.Like = like; rule.Follow = follow; rule.Share = share;
                }
                for (var column = 0; column < widths.Length; column++)
                {
                    var rect = new Rect(x, (row + 1) * rowHeight, widths[column] - 1, rowHeight - 1);
                    x += widths[column];
                    if (row < 0) { GUI.Label(rect, headers[column], heading); continue; }
                    switch (column)
                    {
                        case 0:
                            rule.Like = TikTokTableCheckbox(rect, rule.Like, cell);
                            break;
                        case 1:
                            rule.Follow = TikTokTableCheckbox(rect, rule.Follow, cell);
                            break;
                        case 2:
                            rule.Share = TikTokTableCheckbox(rect, rule.Share, cell);
                            break;
                        case 3:
                            rule.Coins = TikTokTableNumber(rect, rule.Coins, 0, cell); break;
                        case 4: rule.GiftId = TikTokTableNumber(rect, rule.GiftId, 0, cell); break;
                        case 5: rule.Threshold = TikTokTableNumber(rect, rule.Threshold, 1, cell); break;
                        case 6: rule.UnitCount = TikTokTableNumber(rect, rule.UnitCount, 0, cell); break;
                        case 7:
                            rule.UnitType = Mathf.Clamp(rule.UnitType, -1, CharacterTypeLabels.Length - 1);
                            var unitName = rule.UnitType < 0 ? "Chọn loại quân" : CharacterTypeLabels[rule.UnitType];
                            if (SettingsButton(rect, new GUIContent(unitName + " ↻"), cell))
                                rule.UnitType = (rule.UnitType + 2) % (CharacterTypeLabels.Length + 1) - 1;
                            break;
                        case 8: rule.RedAccumulation = TikTokTableNumber(rect, rule.RedAccumulation, 0, cell); break;
                        case 9: rule.RedHeroId = Mathf.Min(8, TikTokTableNumber(rect, rule.RedHeroId, 0, cell)); break;
                        case 10: rule.BlueAccumulation = TikTokTableNumber(rect, rule.BlueAccumulation, 0, cell); break;
                        case 11: rule.BlueHeroId = Mathf.Min(8, TikTokTableNumber(rect, rule.BlueHeroId, 0, cell)); break;
                        case 12: if (SettingsButton(rect, new GUIContent("Xóa"), cell)) remove = row; break;
                    }
                }
                if (rule != null) rule.EventType = rule.Like ? "like" : rule.Follow ? "follow" : rule.Share ? "share" : "gift";
            }
            GUI.EndScrollView();
            if (remove >= 0) _tikTokArmySettings.Rules.Remove(tableRules[remove]);
            var footerY = area.yMax - 90;
            if (SettingsButton(new Rect(area.x + 10, footerY, 170, 38), new GUIContent("+ Thêm dòng"), heading))
                _tikTokArmySettings.Rules.Add(new TikTokArmyRule { Like = true });
            if (SettingsButton(new Rect(area.x + 190, footerY, 170, 38), new GUIContent("Lưu cấu hình"), heading))
                SaveTikTokArmySettings();
            if (SettingsButton(new Rect(area.x + 370, footerY, 200, 38), new GUIContent("Thêm bảng mẫu"), heading))
                AddTikTokSampleRows();
            GUI.Label(new Rect(area.x + 580, footerY, Mathf.Max(100f, area.width - 590f), 38), "Comment 1: Đỏ | 2: Xanh. Không đổi phe.", cell);
            GUI.Label(new Rect(area.x + 10, footerY + 42, area.width - 20, 34), _tikTokSettingsStatus, cell);
            GUI.color = oldColor;
            GUI.contentColor = oldContent;
            GUI.backgroundColor = oldBackground;
        }

    }
}
