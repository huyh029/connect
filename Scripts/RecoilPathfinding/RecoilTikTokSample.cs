using System.Collections.Generic;

namespace RecoilPathfinding
{
    public static class RecoilTikTokSample
    {
        public static TikTokArmySettings Create()
        {
            var settings = new TikTokArmySettings { HeroRules =
                "Mỗi user tặng quà đủ mốc sẽ ra tướng. Mỗi tướng có intro riêng khi xuất hiện.\n" +
                "Nếu nhiều tướng khác nhau xuất hiện đồng thời: chưa chốt cách xử lý intro.\n" +
                "Đạt mốc trước khi nhấn L: tướng đi ra và đứng trong đội hình. Đạt mốc sau khi trận bắt đầu: xuất hiện như quân tiếp viện.\n" +
                "Mỗi user chỉ có một tướng của mỗi loại quà đang tồn tại. Tướng có HP cao, tên và avatar user, thanh máu riêng, VFX nhận diện; kỹ năng đánh gần, đánh xa hoặc sát thương AOE; có thể bị tiêu diệt và hồi sinh." };
            settings.Rules.Add(new TikTokArmyRule { Name = "50 Tym", Like = true, EventType = "like", Threshold = 50, UnitType = 1, UnitCount = 2,
                Action = "+2 Dân Binh", Intro = "Intro quân lính nhỏ bằng khoảng 1/4 màn hình, thời gian tối đa 2s" });
            settings.Rules.Add(new TikTokArmyRule { Name = "Follow", Follow = true, EventType = "follow", Threshold = 1, UnitType = -1, UnitCount = 0 });
            TikTokArmyRule Gift(string name, int coins, int id, int unit, int count, string action, string intro)
            {
                var rule = new TikTokArmyRule { Name = name, EventType = "gift", Threshold = 1, Coins = coins, GiftId = id,
                    UnitType = unit, UnitCount = count, Action = action, Intro = intro };
                settings.Rules.Add(rule); return rule;
            }
            Gift("Hoa Hồng", 1, 0, 7, 50, "+50 Bộ Binh", "Intro Bộ binh + số lượng");
            var rosa = Gift("Rosa", 10, 0, 0, 50, "+50 Cung Binh", "Intro Cung binh + số lượng");
            rosa.BlueAccumulation = rosa.RedAccumulation = 20; rosa.BlueHeroId = 8; rosa.RedHeroId = 3;
            rosa.TeamOneCombo = "x10 ra tướng Triệu Tử Long (hoặc tích đủ 20 Rosa) — đánh gần";
            rosa.TeamTwoCombo = "x10 ra tướng Hạ Hầu Đôn (hoặc tích đủ 20 Rosa) — đánh gần";
            var perfume = Gift("Perfume", 20, 0, 2, 20, "+20 Giáo Binh", "Intro Giáo binh + số lượng");
            perfume.BlueAccumulation = perfume.RedAccumulation = 20; perfume.BlueHeroId = 6; perfume.RedHeroId = 4;
            perfume.TeamOneCombo = "x10 ra tướng Quan Vũ (hoặc tích đủ 20 Perfume) — đánh gần";
            perfume.TeamTwoCombo = "x10 ra tướng Lữ Bố (hoặc tích đủ 20 Perfume) — đánh gần";
            var doughnut = Gift("Doughnut", 30, 5879, 3, 20, "+20 Kỵ Binh", "Intro Kỵ binh + số lượng");
            doughnut.BlueAccumulation = doughnut.RedAccumulation = 20; doughnut.BlueHeroId = 2; doughnut.RedHeroId = 1;
            doughnut.TeamOneCombo = "x10 ra tướng Gia Cát Lượng (hoặc tích đủ 20 Doughnut) — đánh xa";
            doughnut.TeamTwoCombo = "x10 ra tướng Chu Du (hoặc tích đủ 20 Doughnut) — đánh xa";
            var crane = Gift("Hạc Giấy", 99, 5659, 6, 10, "+10 Voi Chiến", "Intro Voi chiến + số lượng");
            crane.BlueAccumulation = crane.RedAccumulation = 10; crane.BlueHeroId = 5; crane.RedHeroId = 7;
            crane.TeamOneCombo = "x5 ra tướng Lưu Bị (hoặc tích đủ 10 Hạc Giấy) — đánh xa";
            crane.TeamTwoCombo = "x5 ra tướng Tào Tháo (hoặc tích đủ 10 Hạc Giấy) — đánh xa";
            Gift("Kính Râm", 199, 5509, 4, 4, "+4 Máy Bắn Đá", "Intro Máy bắn đá + số lượng");
            Gift("Corgi", 299, 6267, 5, 2, "+2 Pháp Sư", "Intro Pháp sư + số lượng");
            var pig = Gift("Heo", 10, 8825, -1, 0, "Random số lượng bằng 1/2 so với quà trực tiếp: 40% Rosa; 34% Perfume; 20% Doughnut; 4% Hạc Giấy; 2% Kính Râm", "");
            pig.RandomRewards = new List<TikTokRandomReward> {
                new TikTokRandomReward { GiftName = "Rosa", Weight = 40, CountMultiplier = .5f },
                new TikTokRandomReward { GiftName = "Perfume", Weight = 34, CountMultiplier = .5f },
                new TikTokRandomReward { GiftName = "Doughnut", Weight = 20, CountMultiplier = .5f },
                new TikTokRandomReward { GiftName = "Hạc Giấy", Weight = 4, CountMultiplier = .5f },
                new TikTokRandomReward { GiftName = "Kính Râm", Weight = 2, CountMultiplier = .5f } };
            return settings;
        }
    }
}
