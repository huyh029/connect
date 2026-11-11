using System;
using System.Collections.Generic;

namespace RecoilPathfinding
{
    public static class RecoilCsvVietnamese
    {
        private static readonly Dictionary<string, string> Headers = new Dictionary<string, string> {
            ["Id"]="Mã", ["MaxHealth"]="Máu tối đa", ["Damage"]="Sát thương", ["Range"]="Tầm đánh",
            ["AttackSpeed"]="Tốc độ đánh", ["MoveSpeed"]="Tốc độ di chuyển", ["Armor"]="Giáp đánh gần",
            ["SecondaryArmor"]="Giáp đánh xa", ["AreaRadius"]="Bán kính sát thương", ["AttackType"]="Kiểu đánh (đánh gần)",
            ["RespawnSeconds"]="Hồi sinh (giây)", ["UltiCooldown"]="Hồi Ulti (giây)", ["CastleHealth"]="Máu thành", ["RowType"]="Loại dòng",
            ["ParentId"]="Mã quy tắc cha", ["HeroRules"]="Quy định tướng", ["Name"]="Tên", ["Coins"]="Xu",
            ["Action"]="Hành động", ["Intro"]="Giới thiệu", ["TeamOneCombo"]="Combo phe 1", ["TeamTwoCombo"]="Combo phe 2",
            ["Enabled"]="Bật", ["EventType"]="Loại sự kiện", ["Threshold"]="Ngưỡng", ["GiftId"]="Mã quà",
            ["Comment"]="Bình luận", ["UnitType"]="Loại lính", ["UnitCount"]="Số lượng lính", ["GiftName"]="Tên quà",
            ["Weight"]="Trọng số", ["CountMultiplier"]="Hệ số số lượng"
            , ["RedAccumulation"]="Số lượng tích phe đỏ", ["RedHeroId"]="Mã tướng phe đỏ", ["BlueAccumulation"]="Số lượng tích phe xanh", ["BlueHeroId"]="Mã tướng phe xanh"
        };
        private static readonly string[] Units = { "Cung binh", "Dân binh", "Giáo binh", "Kỵ binh", "Máy bắn đá", "Pháp sư", "Voi chiến", "Bộ binh" };
        private static readonly string[] HeroIds = { "chu_du", "gia_cat_luong", "ha_hau_don", "lu_bo", "luu_bi", "quan_vu", "tao_thao", "trieu_van" };
        private static readonly string[] Heroes = { "Chu Du", "Gia Cát Lượng", "Hạ Hầu Đôn", "Lữ Bố", "Lưu Bị", "Quan Vũ", "Tào Tháo", "Triệu Vân" };
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string> {
            ["AttackType:Melee"]="Đánh gần", ["AttackType:Ranged"]="Đánh xa", ["AttackType:Unspecified"]="Chưa chọn",
            ["RowType:Settings"]="Cấu hình", ["RowType:Rule"]="Quy tắc", ["RowType:Reward"]="Thưởng ngẫu nhiên", ["RowType:Unit"]="Lính",
            ["EventType:like"]="Thích", ["EventType:gift"]="Quà tặng", ["EventType:follow"]="Theo dõi", ["EventType:share"]="Chia sẻ", ["EventType:comment"]="Bình luận",
            ["Enabled:True"]="Có", ["Enabled:False"]="Không"
        };
        public static string Header(string key) => Headers.TryGetValue(key, out var label) ? label : key;
        public static string Key(string label)
        {
            label = label.Trim();
            if (string.Equals(label, "Kiểu đánh", StringComparison.OrdinalIgnoreCase)) return "AttackType";
            foreach (var entry in Headers) if (string.Equals(entry.Value, label, StringComparison.OrdinalIgnoreCase)) return entry.Key;
            return label;
        }
        public static string UnitName(string id) => id == "-1" ? "Không có" : int.TryParse(id, out int index) && index >= 0 && index < Units.Length ? Units[index] : id;
        public static string Display(string field, string value)
        {
            if (field == "AttackType")
            {
                if (value == "Melee") return "true";
                if (value == "Ranged") return "false";
            }
            if (field == "Id") { int index = Array.IndexOf(HeroIds, value); if (index >= 0) return Heroes[index]; }
            if (field == "UnitType") return UnitName(value);
            return Values.TryGetValue(field + ":" + value, out var label) ? label : value;
        }
        public static string Value(string field, string value)
        {
            if (field == "Id" || field == "UnitType")
            {
                if (value.Trim() == "Cung kỵ") return "0";
                if (value.Trim() == "Đao binh") return "1";
                if (value.Trim() == "Quân sư") return "5";
                if (value.Trim() == "Không có") return "-1";
            }
            if (field == "AttackType" && bool.TryParse(value.Trim(), out bool melee)) return melee ? "Melee" : "Ranged";
            if (field == "Id" || field == "UnitType")
                for (int i = 0; i < Units.Length; i++) if (string.Equals(value.Trim(), Units[i], StringComparison.OrdinalIgnoreCase)) return i.ToString();
            if (field == "Id")
                for (int i = 0; i < Heroes.Length; i++) if (string.Equals(value.Trim(), Heroes[i], StringComparison.OrdinalIgnoreCase)) return HeroIds[i];
            foreach (var entry in Values)
                if (entry.Key.StartsWith(field + ":", StringComparison.Ordinal) && string.Equals(entry.Value, value.Trim(), StringComparison.OrdinalIgnoreCase)) return entry.Key.Substring(field.Length + 1);
            return value;
        }
    }
}
