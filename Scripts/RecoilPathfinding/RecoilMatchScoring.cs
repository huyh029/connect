using System;
using System.Collections.Generic;

namespace RecoilPathfinding
{
    // Runtime-only balances. Each match takes its own snapshot and commits deltas once.
    public sealed class RecoilScoreSession
    {
        private readonly Dictionary<string, decimal> streaks = new Dictionary<string, decimal>(StringComparer.Ordinal);
        public decimal GetWinStreakPoints(string id) => streaks.TryGetValue(id, out var points) ? points : 1m;
        public RecoilMatchScoring BeginMatch() => new RecoilMatchScoring(this, new Dictionary<string, decimal>(streaks));
        internal void Apply(string id, decimal delta) => streaks[id] = GetWinStreakPoints(id) + delta;
    }

    public sealed class RecoilScoreRow
    {
        public string UserId { get; internal set; }
        public string Name { get; internal set; }
        public bool Blue { get; internal set; }
        public int DonationRank { get; internal set; }
        public decimal Coins { get; internal set; }
        public decimal FixedPoints => Coins * 50m;
        public decimal PoolBonus { get; internal set; }
        public decimal StartingStreak { get; internal set; }
        public decimal StreakDelta { get; internal set; }
        public decimal MatchEndingStreak => StartingStreak + StreakDelta;
        public bool Settled { get; internal set; }
        public decimal StreakBonus => Settled ? FixedPoints * RecoilMatchScoring.StreakRate(MatchEndingStreak) : 0m;
        public decimal RankingPoints => FixedPoints + PoolBonus + StreakBonus;
        internal int Order;
    }

    public sealed class RecoilMatchScoring
    {
        private static readonly decimal[] PoolShares = { .40m, .25m, .15m, .12m, .08m };
        private static readonly decimal[] StreakShares = { .30m, .24m, .20m, .16m, .10m };
        private readonly RecoilScoreSession session;
        private readonly Dictionary<string, decimal> snapshot;
        private readonly Dictionary<string, RecoilScoreRow> users = new Dictionary<string, RecoilScoreRow>(StringComparer.Ordinal);
        private readonly List<RecoilScoreRow> results = new List<RecoilScoreRow>();
        private readonly List<RecoilScoreRow> participants = new List<RecoilScoreRow>();
        public IReadOnlyList<RecoilScoreRow> Participants => participants.AsReadOnly();
        public IReadOnlyList<RecoilScoreRow> Results => results.AsReadOnly();
        public bool Settled { get; private set; }
        public decimal PoolPoints { get; private set; }
        public decimal TotalFixedPoints { get; private set; }

        internal RecoilMatchScoring(RecoilScoreSession session, Dictionary<string, decimal> snapshot)
        { this.session = session; this.snapshot = snapshot; }

        public void Join(string id, string name, bool blue)
        {
            if (Settled || string.IsNullOrWhiteSpace(id)) return;
            if (!users.TryGetValue(id, out var row))
            {
                row = new RecoilScoreRow { UserId = id, Blue = blue, Order = users.Count,
                    StartingStreak = snapshot.TryGetValue(id, out var points) ? points : 1m };
                users.Add(id, row);
                participants.Add(row);
            }
            if (!string.IsNullOrWhiteSpace(name)) row.Name = name.Replace('\r', ' ').Replace('\n', ' ');
        }

        public void Donate(string id, string name, bool blue, decimal coins)
        {
            if (Settled || coins <= 0 || string.IsNullOrWhiteSpace(id)) return;
            Join(id, name, blue);
            var row = users[id];
            row.Coins += coins;
            TotalFixedPoints += coins * 50m;
            PoolPoints += coins * 50m * .20m;
            // A tied total is awarded to the donor who reached that total first.
            row.Order = donationSequence++;
        }
        private int donationSequence;

        public IReadOnlyList<RecoilScoreRow> Settle(bool blueWins)
        {
            if (Settled) return Results;
            var donors = new List<RecoilScoreRow>();
            var winners = new List<RecoilScoreRow>();
            decimal lost = 0;
            foreach (var row in users.Values)
            {
                if (row.Blue != blueWins)
                {
                    // Round the retained balance first, then transfer the points actually lost.
                    row.StreakDelta = RoundStreak(row.StartingStreak * .60m) - row.StartingStreak;
                    lost -= row.StreakDelta;
                }
                else
                {
                    winners.Add(row);
                }
                if (row.Coins <= 0) continue;
                donors.Add(row);
            }
            donors.Sort(CompareDonations);
            winners.Sort(CompareDonations);
            for (int i = 0; i < donors.Count; i++)
            {
                donors[i].DonationRank = i + 1;
                if (i < PoolShares.Length) donors[i].PoolBonus = PoolPoints * PoolShares[i];
            }
            for (int i = 0; i < Math.Min(winners.Count, StreakShares.Length); i++)
                winners[i].StreakDelta = RoundStreak(lost * StreakShares[i]);
            foreach (var row in users.Values)
            {
                session.Apply(row.UserId, row.StreakDelta);
                row.Settled = true;
                results.Add(row);
            }
            results.Sort((a, b) => a.Coins != b.Coins ? b.Coins.CompareTo(a.Coins) :
                a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.UserId, b.UserId));
            Settled = true;
            return Results;
        }

        private static int CompareDonations(RecoilScoreRow a, RecoilScoreRow b) =>
            a.Coins != b.Coins ? b.Coins.CompareTo(a.Coins) : a.Order.CompareTo(b.Order);

        private static decimal RoundStreak(decimal points) => decimal.Round(points, 0, MidpointRounding.AwayFromZero);

        public static decimal StreakRate(decimal points)
        {
            if (points <= 10m) return .05m;
            if (points <= 50m) return .10m;
            if (points <= 100m) return .15m;
            if (points <= 150m) return .20m;
            if (points <= 200m) return .25m;
            if (points <= 300m) return .30m;
            if (points <= 400m) return .40m;
            if (points < 500m) return .50m;
            return .60m;
        }
    }
}
