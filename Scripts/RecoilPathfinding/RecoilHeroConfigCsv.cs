using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;

namespace RecoilPathfinding
{
    public static class RecoilHeroConfigCsv
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private const string Header = "Id,Name,MaxHealth,Damage,Range,AttackSpeed,MoveSpeed,Armor,SecondaryArmor,AreaRadius,AttackType,UltiCooldown,RespawnSeconds";

        public static string Write(IEnumerable<RecoilHeroConfig> heroes)
        {
            var fields = Header.Split(',');
            var csv = new StringBuilder(string.Join(";", fields.Select(RecoilCsvVietnamese.Header)) + "\r\n");
            foreach (var hero in heroes)
            {
                if (hero == null) continue;
                var s = hero.Stats;
                object[] values = { hero.Id, hero.Name ?? "", s.MaxHealth, s.Damage, s.Range, s.AttackSpeed, s.MoveSpeed, s.Armor, s.SecondaryArmor,
                    s.AreaRadius, s.AttackType, hero.UltiCooldown, hero.RespawnSeconds };
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) csv.Append(';');
                    string text = values[i] is float number ? number.ToString("G7", Invariant) : Convert.ToString(values[i], Invariant) ?? "";
                    text = RecoilCsvVietnamese.Display(fields[i], text);
                    csv.Append(text.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text);
                }
                csv.Append("\r\n");
            }
            return csv.ToString();
        }

        public static RecoilHeroConfig[] Read(string csv, RecoilHeroConfig[] defaults = null)
        {
            var rows = Rows(csv.TrimStart('\uFEFF'));
            if (rows.Count == 0) throw new InvalidDataException("Hero CSV is empty.");
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows[0].Count; i++) columns.Add(RecoilCsvVietnamese.Key(rows[0][i]), i);
            foreach (string required in Header.Split(','))
                if (required != "RespawnSeconds" && !columns.ContainsKey(required)) throw new InvalidDataException("Missing CSV column: " + required);
            var result = new List<RecoilHeroConfig>();
            var ids = new HashSet<int>();
            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.Count == 1 && string.IsNullOrWhiteSpace(row[0])) continue;
                string Cell(string name)
                {
                    int index = columns[name];
                    if (index >= row.Count) throw new InvalidDataException("Missing value at CSV row " + (r + 1) + ": " + name);
                    return name == "Id" || name == "Name" ? row[index].Trim() : RecoilCsvVietnamese.Value(name, row[index].Trim());
                }
                float Number(string name)
                {
                    float value = float.Parse(Cell(name), NumberStyles.Float, Invariant);
                    if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite CSV number: " + name);
                    return value;
                }
                int id = int.Parse(Cell("Id"), NumberStyles.Integer, Invariant);
                if (id < 1 || id > 8 || !ids.Add(id)) throw new InvalidDataException("Hero Id must be a unique integer from 1 to 8 at CSV row " + (r + 1));
                var baseline = default(RecoilUnitCombatStats);
                if (defaults != null) foreach (var entry in defaults)
                    if (entry != null && entry.Id == id) { baseline = entry.Stats; break; }
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in RecoilSettingsCsv.EditableStats) values[name] = Cell(name);
                var hero = new RecoilHeroConfig { Id = id, Name = Cell("Name"), UltiCooldown = Number("UltiCooldown"), RespawnSeconds = columns.ContainsKey("RespawnSeconds") ? Number("RespawnSeconds") : 30f, Stats = RecoilSettingsCsv.ReadEditableStats(values, baseline) };
                hero.Stats.CombatRole = RecoilCombatRole.Unknown;
                if (!hero.Stats.IsConfigured || hero.UltiCooldown < 0 || hero.RespawnSeconds < 0) throw new InvalidDataException("Invalid health or Ulti cooldown at CSV row " + (r + 1));
                result.Add(hero);
            }
            return result.ToArray();
        }

        internal static List<List<string>> Rows(string csv)
        {
            // Detect only the header, outside quotes. Old comma CSVs remain readable.
            char separator = ','; bool headerQuoted = false;
            for (int i = 0; i < csv.Length && csv[i] != '\r' && csv[i] != '\n'; i++)
            {
                if (csv[i] == '"') headerQuoted = !headerQuoted;
                else if (!headerQuoted && csv[i] == ';') { separator = ';'; break; }
            }
            var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < csv.Length; i++)
            {
                char ch = csv[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (!quoted && ch == separator) { row.Add(cell.ToString()); cell.Clear(); }
                else if (!quoted && (ch == '\r' || ch == '\n'))
                {
                    if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>();
                }
                else cell.Append(ch);
            }
            if (quoted) throw new InvalidDataException("Unclosed quote in hero CSV.");
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }
    }
}
