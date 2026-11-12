using System;

namespace RecoilPathfinding
{
    public static class RecoilMatchScoringValidation
    {
        public static void Run()
        {
            var session = new RecoilScoreSession();
            session.Apply("loser", 99m);
            var match = session.BeginMatch();
            match.Donate("winner", "Winner", true, 1000m);
            match.Join("noGift", "No gift", true);
            match.Donate("loser", "Loser", false, 1000m);
            Equal(match.Participants[0].RankingPoints, 50000m, "Live fixed points only");
            Equal(match.PoolPoints, 20000m, "Pool is 20 percent of all fixed points");
            Equal(match.TotalFixedPoints, 100000m, "Displayed pool includes all fixed points");
            var result = match.Settle(true);
            var winner = Find(result, "winner");
            var loser = Find(result, "loser");
            var noGift = Find(result, "noGift");
            Equal(winner.PoolBonus, 8000m, "Winning Top 1 receives 40 percent of pool");
            Equal(noGift.PoolBonus, 0m, "Non-donor receives no donation pool share");
            Equal(loser.PoolBonus, 5000m, "The pool's Top 2 gets 25 percent, whichever side it fought for");
            Equal(loser.MatchEndingStreak, 60m, "Loser retains 60 percent");
            Equal(winner.StreakDelta, 12m, "Top 1 gets 30 percent of lost streak");
            Equal(noGift.StreakDelta, 10m, "Winning Top 2 award rounds 9.6 to 10");
            Equal(winner.RankingPoints, 63000m, "Fixed + pool + streak bonus");
            match.Settle(false);
            match.Donate("winner", "Late gift", true, 100m);
            Equal(session.GetWinStreakPoints("winner"), 13m, "Settlement is applied once");
            Equal(winner.Coins, 1000m, "Late donations cannot change settled score");

            var tie = session.BeginMatch();
            tie.Donate("a", "A", true, 1m);
            tie.Donate("b", "B", true, 2m);
            tie.Donate("a", "A", true, 1m);
            tie.Settle(true);
            Equal(Find(tie.Results, "b").DonationRank, 1m, "Earlier donor to tied amount wins");

            var topSession = new RecoilScoreSession();
            topSession.Apply("loser", 99m);
            var top = topSession.BeginMatch();
            for (int i = 0; i < 6; i++) top.Donate("top" + i, "Top " + i, true, 6 - i);
            top.Join("loser", "L", false);
            top.Settle(true);
            decimal[] gains = { 12m, 10m, 8m, 6m, 4m, 0m };
            decimal[] poolAwards = { 84m, 52.5m, 31.5m, 25.2m, 16.8m, 0m };
            for (int i = 0; i < gains.Length; i++)
            {
                Equal(Find(top.Results, "top" + i).StreakDelta, gains[i], "Top 5 streak share " + i);
                Equal(Find(top.Results, "top" + i).PoolBonus, poolAwards[i], "Winning Top 5 pool share " + i);
            }

            var mixed = new RecoilScoreSession().BeginMatch();
            mixed.Donate("bigLoser", "Big loser", false, 100m);
            for (int i = 0; i < 6; i++) mixed.Donate("w" + i, "W " + i, true, 6 - i);
            mixed.Settle(true);
            decimal mixedPool = mixed.PoolPoints;
            Equal(Find(mixed.Results, "bigLoser").PoolBonus, mixedPool * .40m, "Top donor on the losing side still gets the Top 1 share");
            decimal[] mixedShares = { .25m, .15m, .12m, .08m, 0m, 0m };
            for (int i = 0; i < mixedShares.Length; i++)
                Equal(Find(mixed.Results, "w" + i).PoolBonus, mixedPool * mixedShares[i], "Top 5 of the whole match, share " + i);

            var roundingSession = new RecoilScoreSession();
            roundingSession.Apply("a", 9m);
            roundingSession.Apply("b", 4m);
            roundingSession.Apply("c", 2m);
            var rounding = roundingSession.BeginMatch();
            rounding.Join("a", "A", false);
            rounding.Join("b", "B", false);
            rounding.Join("c", "C", false);
            rounding.Join("winner", "W", true);
            rounding.Settle(true);
            Equal(Find(rounding.Results, "a").MatchEndingStreak, 6m, "Document example: 10 retains 6");
            Equal(Find(rounding.Results, "b").MatchEndingStreak, 3m, "Document example: 5 retains 3");
            Equal(Find(rounding.Results, "c").MatchEndingStreak, 2m, "Document example: 3 retains rounded 2");
            Equal(Find(rounding.Results, "winner").StreakDelta, 2m, "Award from actual deducted balances");

            var concurrent = new RecoilScoreSession();
            concurrent.Apply("user", 399m);
            concurrent.Apply("enemy", 249m);
            var loss = concurrent.BeginMatch();
            var win = concurrent.BeginMatch();
            loss.Join("user", "U", false);
            win.Donate("first", "First", true, 300m);
            win.Donate("second", "Second", true, 200m);
            win.Donate("user", "U", true, 100m);
            win.Join("enemy", "E", false);
            loss.Settle(true);
            win.Settle(true);
            Equal(Find(win.Results, "user").StartingStreak, 400m, "Independent match snapshot");
            Equal(Find(win.Results, "user").MatchEndingStreak, 420m, "Bonus uses this match's result");
            Equal(concurrent.GetWinStreakPoints("user"), 260m, "Concurrent deltas add to balance");

            var synced = new RecoilScoreSession().BeginMatch();
            synced.Join("s", "S", true);
            synced.Join("z", "Z", false);
            Equal(synced.SetStartingStreak("s", 0m) ? 1m : 0m, 1m, "Server streak can be set after joining");
            synced.SetStartingStreak("z", 40m);
            Equal(Find(synced.Participants, "s").StartingStreak, 1m, "A viewer the server does not know starts at 1");
            Equal(Find(synced.Participants, "z").StartingStreak, 40m, "The server's weekly streak is the starting balance");
            synced.Settle(true);
            Equal(Find(synced.Results, "z").MatchEndingStreak, 24m, "Loser keeps 60 percent of the server streak");
            Equal(Find(synced.Results, "s").StreakDelta, 5m, "Winner gets 30 percent of the 16 lost, rounded");
            Equal(Find(synced.Results, "s").ServerStreak, 0m, "The server's value is kept for the change sent back");
            Equal(synced.SetStartingStreak("z", 99m) ? 1m : 0m, 0m, "A settled match ignores late server values");
            synced.SetStartingRank("s", 7);
            synced.SetEndingRank("s", 4);
            Equal(Find(synced.Results, "s").StartingRank, 7m, "Leaderboard place when joining is kept");
            Equal(Find(synced.Results, "s").EndingRank, 4m, "Leaderboard place after the results were sent is kept");
            Equal(Find(synced.Results, "z").EndingRank, 0m, "Unknown place stays 0");
            Equal(Find(synced.Results, "s").BonusPoints, Find(synced.Results, "s").PoolBonus + Find(synced.Results, "s").StreakBonus, "Bonus is pool plus streak");

            decimal[] points = { 0m, 10m, 10.5m, 50m, 51m, 100m, 101m, 150m, 151m, 200m, 201m, 300m, 301m, 400m, 401m, 499m, 500m };
            decimal[] rates = { .05m, .05m, .10m, .10m, .15m, .15m, .20m, .20m, .25m, .25m, .30m, .30m, .40m, .40m, .50m, .50m, .60m };
            for (int i = 0; i < points.Length; i++)
                Equal(RecoilMatchScoring.StreakRate(points[i]), rates[i], "Streak tier " + points[i]);
            Equal(new RecoilScoreSession().BeginMatch().Settle(true).Count, 0m, "Empty match");
        }

        private static RecoilScoreRow Find(System.Collections.Generic.IReadOnlyList<RecoilScoreRow> rows, string id)
        {
            foreach (var row in rows) if (row.UserId == id) return row;
            throw new InvalidOperationException("Missing score: " + id);
        }

        private static void Equal(decimal actual, decimal expected, string label)
        {
            if (actual != expected) throw new InvalidOperationException(label + ": expected " + expected + ", got " + actual);
        }
    }
}
