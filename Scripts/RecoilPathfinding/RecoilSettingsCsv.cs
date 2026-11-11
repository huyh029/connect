using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace RecoilPathfinding
{
    // Nested settings use explicit row types and parent IDs, never JSON inside CSV cells.
    public static class RecoilSettingsCsv
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        public static readonly string[] EditableStats = { "MaxHealth", "Damage", "Range", "AttackSpeed", "MoveSpeed", "Armor", "SecondaryArmor", "AreaRadius", "AttackType" };
        public static RecoilUnitCombatStats ReadEditableStats(Dictionary<string, string> row, RecoilUnitCombatStats baseline)
        {
            object stats = baseline;
            foreach (string name in EditableStats)
            {
                if (!row.TryGetValue(name, out string text)) throw new InvalidDataException("Missing CSV column: " + name);
                var field = typeof(RecoilUnitCombatStats).GetField(name);
                field.SetValue(stats, ReadValue(text.Trim(), field.FieldType));
            }
            var result = (RecoilUnitCombatStats)stats;
            if (result.MaxHealth < 1 || result.Damage < 0 || result.Range <= 0 || result.AttackSpeed <= 0 || result.MoveSpeed <= 0 ||
                result.Armor < 0 || result.Armor > 1 || result.SecondaryArmor < 0 || result.SecondaryArmor > 1 || result.AreaRadius < 0)
                throw new InvalidDataException("Unit CSV values are outside the permitted ranges.");
            result.SecondaryDamage = result.AttackType == RecoilAttackType.Ranged;
            return result;
        }
        private static FieldInfo[] Fields(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => !typeof(System.Collections.IList).IsAssignableFrom(f.FieldType)).ToArray();
        private static string Text(object value) => value is float number ? number.ToString("G7", Invariant) : Convert.ToString(value, Invariant) ?? "";
        private static string Quote(string value) => value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        private static string Encode(string[] header, List<Dictionary<string, string>> rows)
        {
            var csv = new StringBuilder(string.Join(";", header.Select(RecoilCsvVietnamese.Header)) + "\r\n");
            foreach (var row in rows) csv.Append(string.Join(";", header.Select(h => Quote(row.TryGetValue(h, out var value) ?
                h == "Id" && row.TryGetValue("RowType", out var kind) && kind == "Unit" ? RecoilCsvVietnamese.UnitName(value) : RecoilCsvVietnamese.Display(h, value) : "")))).Append("\r\n");
            return csv.ToString();
        }
        private static List<Dictionary<string, string>> Decode(string csv)
        {
            var cells = RecoilHeroConfigCsv.Rows(csv.TrimStart('\uFEFF'));
            if (cells.Count == 0) throw new InvalidDataException("CSV is empty.");
            var result = new List<Dictionary<string, string>>();
            for (int r = 1; r < cells.Count; r++)
            {
                if (cells[r].All(string.IsNullOrWhiteSpace)) continue;
                if (cells[r].Count != cells[0].Count) throw new InvalidDataException("CSV column count mismatch at row " + (r + 1));
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < cells[0].Count; c++)
                {
                    string key = RecoilCsvVietnamese.Key(cells[0][c]);
                    row.Add(key, RecoilCsvVietnamese.Value(key, cells[r][c]));
                }
                result.Add(row);
            }
            return result;
        }
        private static Dictionary<string, string> Record(string kind, string id, object data)
        {
            var row = new Dictionary<string, string> { ["RowType"] = kind, ["Id"] = id };
            foreach (var field in Fields(data.GetType())) row[field.Name] = Text(field.GetValue(data));
            return row;
        }
        private static object ReadValue(string text, Type type)
        {
            if (type == typeof(string)) return text;
            if (type.IsEnum)
            {
                var value = Enum.Parse(type, text, true);
                if (!Enum.IsDefined(type, value)) throw new InvalidDataException("Invalid enum value: " + text);
                return value;
            }
            var parsed = Convert.ChangeType(text, type, Invariant);
            if (parsed is float number && !float.IsFinite(number)) throw new InvalidDataException("Non-finite CSV number.");
            return parsed;
        }
        private static T ObjectFrom<T>(Dictionary<string, string> row) where T : new()
        {
            object value = new T();
            foreach (var field in Fields(typeof(T)))
            {
                if (!row.TryGetValue(field.Name, out var text))
                {
                    if (typeof(T) == typeof(TikTokArmyRule) && (field.FieldType == typeof(bool) || field.Name.EndsWith("Accumulation") || field.Name.EndsWith("HeroId"))) continue;
                    throw new InvalidDataException("Missing CSV column: " + field.Name);
                }
                field.SetValue(value, ReadValue(text, field.FieldType));
            }
            return (T)value;
        }
        public static string WriteUnits(RecoilUnitCombatStats[] units)
        {
            var rows = new List<Dictionary<string, string>>();
            for (int i = 0; i < units.Length; i++) rows.Add(Record("Unit", Text(i), units[i]));
            return Encode(new[] { "Id" }.Concat(EditableStats).ToArray(), rows);
        }
        public static string WriteCastle(int health) => Encode(new[] { "Id", "CastleHealth" },
            new List<Dictionary<string, string>> { new Dictionary<string, string> { ["Id"] = "battle", ["CastleHealth"] = Text(health) } });
        public static int ReadCastle(string csv)
        {
            var rows = Decode(csv);
            if (rows.Count != 1 || rows[0]["Id"] != "battle") throw new InvalidDataException("Expected one battle castle settings row.");
            int health = int.Parse(rows[0]["CastleHealth"], Invariant);
            if (health <= 0) throw new InvalidDataException("CastleHealth must be positive.");
            return health;
        }
        public static RecoilUnitCombatStats[] ReadUnits(string csv, out int castleHealth, out int spawnHealth, RecoilUnitCombatStats[] defaults = null)
        {
            castleHealth = 0; spawnHealth = 0;
            var units = new SortedDictionary<int, RecoilUnitCombatStats>();
            bool settings = false;
            foreach (var row in Decode(csv))
            {
                string kind = row.TryGetValue("RowType", out var rowType) ? rowType : "Unit";
                if (kind == "Settings")
                {
                    if (settings) throw new InvalidDataException("Duplicate settings row.");
                    settings = true;
                    castleHealth = int.Parse(row["CastleHealth"], Invariant);
                    spawnHealth = int.Parse(row["SpawnTargetHealth"], Invariant);
                }
                else if (kind == "Unit")
                {
                    int id = int.Parse(row["Id"], Invariant);
                    if (id < 0 || id >= 8 || units.ContainsKey(id)) throw new InvalidDataException("Invalid or duplicate unit ID.");
                    units.Add(id, ReadEditableStats(row, defaults != null && id < defaults.Length ? defaults[id] : default));
                }
                else throw new InvalidDataException("Unknown unit CSV row type.");
            }
            var result = new RecoilUnitCombatStats[units.Count == 0 ? 0 : units.Keys.Max() + 1];
            foreach (var unit in units) result[unit.Key] = unit.Value;
            return result;
        }
        public static string WriteTikTok(TikTokArmySettings settings) => RecoilTikTokTableCsv.Write(settings);
        public static string WriteTikTokDetails(TikTokArmySettings settings)
        {
            var rows = new List<Dictionary<string, string>> { new Dictionary<string, string> { ["RowType"] = "Settings", ["Id"] = "tiktok", ["HeroRules"] = settings.HeroRules ?? "" } };
            for (int i = 0; i < settings.Rules.Count; i++)
            {
                var rule = settings.Rules[i];
                rows.Add(Record("Rule", Text(i), rule));
                for (int r = 0; r < (rule.RandomRewards?.Count ?? 0); r++)
                {
                    var reward = Record("Reward", i + "." + r, rule.RandomRewards[r]);
                    reward["ParentId"] = Text(i); rows.Add(reward);
                }
            }
            return Encode(new[] { "RowType", "Id", "ParentId", "HeroRules" }.Concat(Fields(typeof(TikTokArmyRule)).Select(f => f.Name))
                .Concat(Fields(typeof(TikTokRandomReward)).Select(f => f.Name)).Distinct().ToArray(), rows);
        }
        public static TikTokArmySettings ReadTikTok(string csv, TikTokArmySettings details = null)
        {
            var first = RecoilHeroConfigCsv.Rows(csv.TrimStart('\uFEFF'));
            if (first.Count > 0 && first[0].Contains("Like")) return RecoilTikTokTableCsv.Read(csv, details);
            return ReadTikTokDetails(csv);
        }
        public static TikTokArmySettings ReadTikTokDetails(string csv)
        {
            var result = new TikTokArmySettings();
            var rows = Decode(csv);
            var rules = new Dictionary<string, TikTokArmyRule>();
            var keys = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!keys.Add(row["RowType"] + ":" + row["Id"])) throw new InvalidDataException("Duplicate TikTok CSV row ID.");
                switch (row["RowType"])
                {
                    case "Settings": result.HeroRules = row["HeroRules"]; break;
                    case "Rule":
                        var rule = ObjectFrom<TikTokArmyRule>(row); rules.Add(row["Id"], rule); result.Rules.Add(rule); break;
                    case "Reward": break;
                    default: throw new InvalidDataException("Unknown TikTok CSV row type.");
                }
            }
            foreach (var row in rows.Where(r => r["RowType"] == "Reward"))
            {
                if (!rules.TryGetValue(row["ParentId"], out var rule)) throw new InvalidDataException("Reward references a missing rule.");
                rule.RandomRewards.Add(ObjectFrom<TikTokRandomReward>(row));
            }
            return result;
        }
        public static void Save(string path, string csv)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, csv, new UTF8Encoding(true));
            try
            {
                if (!File.Exists(path)) { File.Move(temporary, path); return; }
                // A viewer may lock the old backup. That must not block the main CSV.
                try { File.Copy(path, path + ".bak", true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { File.Replace(temporary, path, null); }
                catch (IOException) { File.Copy(temporary, path, true); }
                catch (UnauthorizedAccessException) { File.Copy(temporary, path, true); }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
