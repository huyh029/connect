using System;
using System.Collections.Generic;

namespace RecoilPathfinding
{
    public static class RecoilTikTokGiftMapping
    {
        public static TikTokArmyRule FindReward(List<TikTokArmyRule> rules, TikGift gift)
        {
            if (rules == null || gift == null) return null;
            bool Eligible(TikTokArmyRule rule) => rule.Enabled && rule.MatchesEvent("gift");
            var match = gift.id > 0 ? rules.Find(r => Eligible(r) && r.GiftId == gift.id) : null;
            if (match != null) return match;
            if (!string.IsNullOrWhiteSpace(gift.name))
                match = rules.Find(r => Eligible(r) && string.Equals(r.Name?.Trim(), gift.name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
            // Cost is also valid for rows which have an ID. Pick just one row for shared costs.
            return gift.price > 0 ? rules.Find(r => Eligible(r) && r.Coins == gift.price) : null;
        }

        public static int CoinCost(List<TikTokArmyRule> rules, TikGift gift)
        {
            if (gift == null) return 0;
            // Pricing remains available even when that gift's unit reward is disabled.
            var exact = gift.id > 0 ? rules?.Find(r => r.MatchesEvent("gift") && r.GiftId == gift.id) : null;
            var mapped = exact ?? FindReward(rules, gift);
            return Math.Max(0, mapped != null ? mapped.Coins : gift.price);
        }
    }
}
