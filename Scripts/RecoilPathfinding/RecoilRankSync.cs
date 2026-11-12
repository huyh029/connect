using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Win streaks live on the rank server (<see cref="RecoilRankServer"/>), per TikTok user, per week — the server
    /// resets its weekly boards itself. The server's <c>killCount</c> IS the win streak.
    /// <list type="bullet">
    /// <item>When a viewer joins a match, their weekly streak is read (<c>GET /api/rank/weekly/kill/{id}</c>) and
    /// becomes the match's starting balance; a viewer the server does not know starts at 1, as the rules say.</item>
    /// <item>When the match settles, one <c>POST /api/rank/update</c> carries every viewer: <c>matchPoint</c> = this
    /// match's ranking points, <c>killCount</c> = the streak change (negative for the losers). The server is expected
    /// to add these to its weekly and monthly totals.</item>
    /// </list>
    /// Simulated viewers (the test popup's "sim-" users) are left out unless rankServerSyncSimulatedUsers is on.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        [Header("Đồng bộ rank server")]
        [SerializeField, Tooltip("Đọc chuỗi thắng của người xem từ rank server khi vào trận và gửi kết quả lên khi hết trận.")]
        private bool rankServerSync = true;
        [SerializeField, Tooltip("Gửi cả người xem giả lập (sim-…) của popup test lên server. Tắt khi chạy thật.")]
        private bool rankServerSyncSimulatedUsers;

        private const int RankPushChunk = 200;
        private readonly HashSet<string> _rankStreakRequested = new(StringComparer.Ordinal);
        private string _rankSyncStatus;

        private static bool IsSimulatedUser(string user) =>
            user != null && user.StartsWith(GiftSimulationUserPrefix, StringComparison.Ordinal);

        private bool RankSyncs(string user) =>
            rankServerSync && Application.isPlaying && !string.IsNullOrWhiteSpace(user) &&
            (rankServerSyncSimulatedUsers || !IsSimulatedUser(user));

        /// <summary>A new match: every viewer's streak is read again when they join it.</summary>
        private void ResetRankSync() => _rankStreakRequested.Clear();

        /// <summary>Reads the viewer's weekly win streak from the server, once per match, and makes it their starting balance.</summary>
        private void RequestRankStreak(string user)
        {
            if (!RankSyncs(user) || matchScoring == null || matchScoring.Settled || !_rankStreakRequested.Add(user)) return;
            var match = matchScoring;
            StartCoroutine(RecoilRankServer.FetchPlayer(rankServerUrl, RecoilRankServer.Board.WeeklyStreak, user, (entry, error) =>
            {
                if (this == null || match != matchScoring) return; // a later match asks again itself
                if (error != null)
                {
                    _rankStreakRequested.Remove(user); // the next event from this viewer tries again
                    Debug.LogWarning($"Rank server: không đọc được chuỗi thắng của {user}: {error}", this);
                    return;
                }
                var streak = entry != null ? (decimal)Math.Round(entry.Value) : 0m;
                match.SetStartingStreak(user, streak);
            }));
            // Their place on the weekly points board as they join: the card shows how many places the match moved them.
            StartCoroutine(RecoilRankServer.FetchPlayer(rankServerUrl, RecoilRankServer.Board.WeeklyPoint, user, (entry, error) =>
            {
                if (this == null || match != matchScoring || error != null || entry == null) return;
                match.SetStartingRank(user, entry.Rank);
            }));
        }

        /// <summary>After settlement: every viewer's match points and streak change, in one request (chunked when large).</summary>
        private void PushMatchResultsToRankServer()
        {
            if (!rankServerSync || !Application.isPlaying || matchScoring == null || !matchScoring.Settled) return;
            var players = new List<RecoilRankServer.Entry>();
            foreach (var row in matchScoring.Results)
            {
                if (!RankSyncs(row.UserId)) continue;
                // The change against what the server holds; when its value never arrived, against the local balance.
                var serverStreak = row.ServerStreakKnown ? row.ServerStreak : row.StartingStreak;
                _donationAvatarUrls.TryGetValue(row.UserId, out var avatar);
                players.Add(new RecoilRankServer.Entry
                {
                    tikTokUserId = row.UserId,
                    username = row.Name ?? row.UserId,
                    avatar = avatar ?? "",
                    matchPoint = (int)Math.Round(Math.Max(0m, row.RankingPoints)),
                    killCount = (int)Math.Round(row.MatchEndingStreak - serverStreak)
                });
            }
            if (players.Count == 0) return;
            StartCoroutine(PushRankResults(matchScoring, players));
        }

        private IEnumerator PushRankResults(RecoilMatchScoring match, List<RecoilRankServer.Entry> players)
        {
            var sent = 0;
            for (var start = 0; start < players.Count; start += RankPushChunk)
            {
                var chunk = players.GetRange(start, Math.Min(RankPushChunk, players.Count - start));
                string error = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    yield return RecoilRankServer.Push(rankServerUrl, chunk, e => error = e);
                    if (error == null) break;
                    yield return new WaitForSecondsRealtime(5f * (attempt + 1));
                }
                if (error != null)
                {
                    _rankSyncStatus = $"Rank server: gửi kết quả thất bại ({sent}/{players.Count} người đã gửi): {error}";
                    Debug.LogWarning(_rankSyncStatus, this);
                    yield break;
                }
                sent += chunk.Count;
            }
            _rankSyncStatus = $"Rank server: đã gửi kết quả của {sent} người.";
            Debug.Log(_rankSyncStatus, this);
            _cloudFetchedAt = -999f; // the leaderboard panel reads the new standings next time it opens
            yield return FetchEndingRanks(match);
        }

        /// <summary>
        /// After the results went up: everyone's new place on the weekly points board, from one read of the board;
        /// the first few the board left out (it may be cut short) are asked for one by one.
        /// </summary>
        private IEnumerator FetchEndingRanks(RecoilMatchScoring match)
        {
            List<RecoilRankServer.Entry> board = null;
            string error = null;
            yield return RecoilRankServer.Fetch(rankServerUrl, RecoilRankServer.Board.WeeklyPoint, (entries, e) => { board = entries; error = e; });
            if (this == null || match != matchScoring) yield break;
            if (board == null)
            {
                Debug.LogWarning("Rank server: không đọc được bảng tuần sau trận: " + error, this);
                yield break;
            }
            foreach (var entry in board)
                if (entry.Rank > 0 && !string.IsNullOrEmpty(entry.tikTokUserId)) match.SetEndingRank(entry.tikTokUserId, entry.Rank);
            var asked = 0;
            foreach (var row in match.Results)
            {
                if (row.EndingRank > 0 || !RankSyncs(row.UserId)) continue;
                if (asked++ >= 3) break;
                var user = row.UserId;
                yield return RecoilRankServer.FetchPlayer(rankServerUrl, RecoilRankServer.Board.WeeklyPoint, user, (entry, e) =>
                {
                    if (entry != null && entry.Rank > 0 && match == matchScoring) match.SetEndingRank(user, entry.Rank);
                });
            }
        }

        // ---------------------------------------------------------------- the card's three lines

        private static readonly System.Globalization.CultureInfo ViCulture = System.Globalization.CultureInfo.GetCultureInfo("vi-VN");
        private static readonly Color DetailUp = new(.6f, 1f, .6f, 1f), DetailDown = new(1f, .55f, .5f, 1f);

        private static string N0(decimal value) => value.ToString("N0", ViCulture);

        /// <summary>"Fixed points (+bonus)"; just the points when there is no bonus.</summary>
        private static string PointsText(decimal fixedPoints, decimal bonus) =>
            bonus > 0 ? $"{N0(fixedPoints)} (+{N0(bonus)})" : N0(fixedPoints);

        /// <summary>"Streak now (±change since joining)".</summary>
        private static string StreakText(RecoilScoreRow row)
        {
            var delta = row.StreakDelta;
            var sign = delta > 0 ? "+" : delta < 0 ? "−" : "±";
            return $"{N0(row.MatchEndingStreak)} ({sign}{N0(Math.Abs(delta))})";
        }

        /// <summary>"Place (▲ places gained / ▼ lost / =)"; "—" until the server answered.</summary>
        private static string RankText(RecoilScoreRow row)
        {
            if (row.EndingRank <= 0) return "—";
            if (row.StartingRank <= 0) return row.EndingRank.ToString();
            var gained = row.StartingRank - row.EndingRank;
            return gained > 0 ? $"{row.EndingRank} (▲{gained})" : gained < 0 ? $"{row.EndingRank} (▼{-gained})" : $"{row.EndingRank} (=)";
        }

        private static Color StreakColor(RecoilScoreRow row) =>
            row.StreakDelta > 0 ? DetailUp : row.StreakDelta < 0 ? DetailDown : Color.white;

        private static Color RankColor(RecoilScoreRow row) =>
            row.EndingRank <= 0 || row.StartingRank <= 0 || row.StartingRank == row.EndingRank ? Color.white
            : row.EndingRank < row.StartingRank ? DetailUp : DetailDown;
    }
}
