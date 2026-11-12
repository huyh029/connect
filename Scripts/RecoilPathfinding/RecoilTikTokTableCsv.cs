using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RecoilPathfinding
{
    public static class RecoilTikTokTableCsv
    {
        public static readonly string[] Headers = { "Like", "Follow", "Share", "Số xu", "ID", "Ngưỡng", "Số lượng", "Loại binh", "Số lượng tích phe đỏ", "Mã tướng phe đỏ", "Số lượng tích phe xanh", "Mã tướng phe xanh" };
        // Optional when reading (older files): without it, a gift with a hero takes half its accumulation (20 → x10, 10 → x5).
        public const string ComboHeader = "Combo ra tướng";
        private static readonly string[] ExtraHeaders = { "Tên", "Hành động", "Giới thiệu", "Combo phe 1", "Combo phe 2", "Bật", "Quy định tướng", "Quà ngẫu nhiên", "Trọng số ngẫu nhiên", "Hệ số ngẫu nhiên" };
        private static string Quote(string text) => text.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        public static void ImportLegacyCombos(TikTokArmySettings settings)
        {
            (int count, int hero) Parse(string text)
            {
                text = text ?? "";
                var match = Regex.Match(text, @"tích\s+(\d+)", RegexOptions.IgnoreCase);
                if (!match.Success) return (0, 0);
                string[] names = { "Chu Du", "Gia Cát Lượng", "Hạ Hầu Đôn", "Lữ Bố", "Lưu Bị", "Quan Vũ", "Tào Tháo", "Triệu Tử Long" };
                for (int i = 0; i < names.Length; i++) if (text.IndexOf(names[i], StringComparison.OrdinalIgnoreCase) >= 0) return (int.Parse(match.Groups[1].Value), i + 1);
                return (0, 0);
            }
            foreach (var rule in settings.Rules)
            {
                // Existing team commands: 1 = blue, 2 = red.
                if (rule.BlueHeroId == 0 && rule.BlueAccumulation == 0) { var blue = Parse(rule.TeamOneCombo); rule.BlueHeroId = blue.hero; rule.BlueAccumulation = blue.count; }
                if (rule.RedHeroId == 0 && rule.RedAccumulation == 0) { var red = Parse(rule.TeamTwoCombo); rule.RedHeroId = red.hero; rule.RedAccumulation = red.count; }
            }
        }
        public static string Write(TikTokArmySettings settings)
        {
            var csv = new StringBuilder(string.Join(";", Headers.Append(ComboHeader).Concat(ExtraHeaders)) + "\r\n");
            bool first = true;
            foreach (var rule in settings.Rules)
            {
                if (rule.EventType == "comment") continue;
                var values = new[] { rule.MatchesEvent("like").ToString().ToLowerInvariant(), rule.MatchesEvent("follow").ToString().ToLowerInvariant(), rule.MatchesEvent("share").ToString().ToLowerInvariant(),
                    rule.Coins.ToString(CultureInfo.InvariantCulture), rule.GiftId.ToString(CultureInfo.InvariantCulture), rule.Threshold.ToString(CultureInfo.InvariantCulture),
                    rule.UnitCount.ToString(CultureInfo.InvariantCulture), RecoilCsvVietnamese.UnitName(rule.UnitType.ToString(CultureInfo.InvariantCulture)),
                    rule.RedAccumulation.ToString(CultureInfo.InvariantCulture), rule.RedHeroId.ToString(CultureInfo.InvariantCulture),
                    rule.BlueAccumulation.ToString(CultureInfo.InvariantCulture), rule.BlueHeroId.ToString(CultureInfo.InvariantCulture),
                    rule.ComboThreshold.ToString(CultureInfo.InvariantCulture) };
                var rewards = rule.RandomRewards ?? new List<TikTokRandomReward>();
                var extra = new[] { rule.Name ?? "", rule.Action ?? "", rule.Intro ?? "", rule.TeamOneCombo ?? "", rule.TeamTwoCombo ?? "",
                    rule.Enabled.ToString().ToLowerInvariant(), first ? settings.HeroRules ?? "" : "",
                    string.Join("|", rewards.Select(r => r.GiftName)), string.Join("|", rewards.Select(r => r.Weight.ToString(CultureInfo.InvariantCulture))),
                    string.Join("|", rewards.Select(r => r.CountMultiplier.ToString("R", CultureInfo.InvariantCulture))) };
                csv.Append(string.Join(";", values.Concat(extra).Select(Quote))).Append("\r\n");
                first = false;
            }
            return csv.ToString();
        }
        public static TikTokArmySettings Read(string csv, TikTokArmySettings details = null)
        {
            var rows = RecoilHeroConfigCsv.Rows(csv.TrimStart('\uFEFF'));
            if (rows.Count == 0) throw new InvalidDataException("TikTok CSV is empty.");
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows[0].Count; i++) columns.Add(rows[0][i].Trim(), i);
            foreach (var required in Headers) if (!columns.ContainsKey(required)) throw new InvalidDataException("Missing column: " + required);
            var result = new TikTokArmySettings { HeroRules = details?.HeroRules ?? new TikTokArmySettings().HeroRules };
            for (int row = 1; row < rows.Count; row++)
            {
                if (rows[row].All(string.IsNullOrWhiteSpace)) continue;
                string Cell(string name) => columns[name] < rows[row].Count ? rows[row][columns[name]].Trim() : throw new InvalidDataException("Missing cell at row " + (row + 1));
                int Number(string name, int minimum = 0)
                {
                    int value = int.Parse(Cell(name), CultureInfo.InvariantCulture);
                    if (value < minimum) throw new InvalidDataException("Invalid " + name + " at row " + (row + 1));
                    return value;
                }
                var rule = new TikTokArmyRule { Like = bool.Parse(Cell("Like")), Follow = bool.Parse(Cell("Follow")), Share = bool.Parse(Cell("Share")),
                    Coins = Number("Số xu"), GiftId = Number("ID"), Threshold = Number("Ngưỡng", 1), UnitCount = Number("Số lượng"),
                    UnitType = int.Parse(RecoilCsvVietnamese.Value("UnitType", Cell("Loại binh")), CultureInfo.InvariantCulture),
                    RedAccumulation = Number("Số lượng tích phe đỏ"), RedHeroId = Number("Mã tướng phe đỏ"),
                    BlueAccumulation = Number("Số lượng tích phe xanh"), BlueHeroId = Number("Mã tướng phe xanh") };
                if (rule.UnitType < -1 || rule.UnitType > 7 || rule.RedHeroId > 8 || rule.BlueHeroId > 8) throw new InvalidDataException("Invalid unit or hero ID at row " + (row + 1));
                rule.ComboThreshold = columns.ContainsKey(ComboHeader) && !string.IsNullOrWhiteSpace(Cell(ComboHeader))
                    ? Number(ComboHeader)
                    : (rule.RedHeroId > 0 || rule.BlueHeroId > 0) ? Math.Max(rule.RedAccumulation, rule.BlueAccumulation) / 2 : 0;
                rule.EventType = rule.Like ? "like" : rule.Follow ? "follow" : rule.Share ? "share" : "gift";
                // Join by stable event/gift identity, never by row number, so sorting
                // the main CSV does not associate a gift with another reward rule.
                var legacy = details?.Rules.FirstOrDefault(old => old.EventType != "comment" &&
                    (rule.GiftId > 0 ? old.GiftId == rule.GiftId : old.GiftId == 0 && old.Coins == rule.Coins &&
                     old.MatchesEvent("like") == rule.Like && old.MatchesEvent("follow") == rule.Follow && old.MatchesEvent("share") == rule.Share));
                if (legacy != null)
                {
                    rule.Name = legacy.Name; rule.Action = legacy.Action; rule.Intro = legacy.Intro;
                    rule.TeamOneCombo = legacy.TeamOneCombo; rule.TeamTwoCombo = legacy.TeamTwoCombo;
                    rule.Enabled = legacy.Enabled; rule.RandomRewards = legacy.RandomRewards ?? new List<TikTokRandomReward>();
                }
                string Optional(string name) => columns.TryGetValue(name, out int index) && index < rows[row].Count ? rows[row][index] : "";
                if (columns.ContainsKey("Tên"))
                {
                    rule.Name = Optional("Tên"); rule.Action = Optional("Hành động"); rule.Intro = Optional("Giới thiệu");
                    rule.TeamOneCombo = Optional("Combo phe 1"); rule.TeamTwoCombo = Optional("Combo phe 2");
                    if (!string.IsNullOrWhiteSpace(Optional("Bật"))) rule.Enabled = bool.Parse(Optional("Bật"));
                    if (!string.IsNullOrEmpty(Optional("Quy định tướng"))) result.HeroRules = Optional("Quy định tướng");
                    rule.RandomRewards = new List<TikTokRandomReward>();
                    if (!string.IsNullOrWhiteSpace(Optional("Quà ngẫu nhiên")))
                    {
                        var names = Optional("Quà ngẫu nhiên").Split('|'); var weights = Optional("Trọng số ngẫu nhiên").Split('|'); var counts = Optional("Hệ số ngẫu nhiên").Split('|');
                        if (names.Length != weights.Length || names.Length != counts.Length) throw new InvalidDataException("Random reward columns must contain the same number of entries.");
                        for (int i = 0; i < names.Length; i++)
                        {
                            int weight = int.Parse(weights[i], CultureInfo.InvariantCulture);
                            float multiplier = float.Parse(counts[i], CultureInfo.InvariantCulture);
                            if (weight < 0 || !float.IsFinite(multiplier) || multiplier < 0) throw new InvalidDataException("Invalid random reward weight or multiplier.");
                            rule.RandomRewards.Add(new TikTokRandomReward { GiftName = names[i], Weight = weight, CountMultiplier = multiplier });
                        }
                    }
                }
                result.Rules.Add(rule);
            }
            if (details != null) result.Rules.AddRange(details.Rules.Where(rule => rule.EventType == "comment"));
            return result;
        }
    }
}
