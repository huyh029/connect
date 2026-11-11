using System;
using System.Collections.Generic;

// Incremental ticks award immediately; final combo summaries award only the remainder.
internal sealed class TikTokGiftNormalizer
{
    private sealed class Combo
    {
        public int Applied, Total;
        public double Timestamp;
        public float SeenAt;
        public bool Closed;
    }

    private readonly Dictionary<string, Combo> combos = new(StringComparer.Ordinal);
    public void Clear() => combos.Clear();

    public bool TryNormalize(TikEvent source, float now, out TikEvent result)
    {
        result = source;
        var kind = source.type?.ToLowerInvariant();
        if (kind != "gift_tick" && kind != "gift_final") return true;
        var user = !string.IsNullOrWhiteSpace(source.user?.user_id) ? source.user.user_id : source.user?.unique_id;
        if (string.IsNullOrWhiteSpace(user) || source.gift == null) return false;
        var giftKey = source.gift.id > 0 ? "id:" + source.gift.id :
            "cost:" + source.gift.price + ":" + (source.gift.name ?? "");
        var key = user + ":" + giftKey;
        if (!combos.TryGetValue(key, out var combo)) combos[key] = combo = new Combo();
        if (source.ts > 0 && source.ts == combo.Timestamp) return false;
        int quantity;
        if (kind == "gift_tick")
        {
            if (combo.Closed || now - combo.SeenAt > 60f ||
                source.repeat_count > 0 && source.repeat_count <= combo.Total)
            {
                combo.Applied = combo.Total = 0;
                combo.Closed = false;
            }
            quantity = source.delta > 0 ? source.delta : source.repeat_count > 0 ?
                Math.Max(0, source.repeat_count - combo.Total) : Math.Max(0, source.count);
            combo.Applied += quantity;
            combo.Total = source.repeat_count > 0 ? source.repeat_count : combo.Applied;
        }
        else
        {
            int total = source.repeat_count > 0 ? source.repeat_count : source.count;
            // A delta-only final is an independent donation, not a cumulative combo summary.
            quantity = total <= 0 && source.delta > 0 ? source.delta :
                combo.Applied > 0 && now - combo.SeenAt <= 60f ?
                Math.Max(0, total - combo.Applied) : Math.Max(0, total > 0 ? total : source.delta);
            combo.Applied += quantity;
            combo.Closed = true;
        }
        combo.Timestamp = source.ts;
        combo.SeenAt = now;
        if (quantity <= 0) return false;
        result = new TikEvent { type = "gift", user = source.user, gift = source.gift,
            delta = quantity, count = quantity, repeat_count = source.repeat_count,
            ts = source.ts, rawJson = source.rawJson };
        return true;
    }
}
