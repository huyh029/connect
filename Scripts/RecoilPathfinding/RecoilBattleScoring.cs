using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private static RecoilScoreSession scoreSession = new RecoilScoreSession();
        private RecoilMatchScoring matchScoring;
        public IReadOnlyList<RecoilScoreRow> MatchScoreResults => matchScoring?.Results;
        public IReadOnlyList<RecoilScoreRow> MatchScoreParticipants => matchScoring?.Participants;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeScoreSession() => scoreSession = new RecoilScoreSession();

        [ContextMenu("Validate in-game scoring")]
        private void ValidateInGameScoring()
        {
            RecoilMatchScoringValidation.Run();
            Debug.Log("In-game scoring validation passed.", this);
        }

        private void BeginMatchScoring()
        {
            matchScoring = scoreSession.BeginMatch();
            ResetRankSync();
            redRanking.Clear(); blueRanking.Clear();
            rankingScroll = Vector2.zero;
        }

        private void JoinMatchScoring(string user, string name, bool blue)
        {
            if (matchScoring == null) BeginMatchScoring();
            matchScoring.Join(user, name, blue);
            RequestRankStreak(user); // their weekly streak on the rank server is the starting balance
        }

        private void SettleMatchScoring(bool blueWins)
        {
            if (matchScoring == null) BeginMatchScoring();
            matchScoring.Settle(blueWins);
            PushMatchResultsToRankServer();
            BuildEndgameRanking(blueWins);
            Debug.Log($"Match scoring settled: {matchScoring.Results.Count} participants, pool {matchScoring.PoolPoints:N2} points.", this);
        }

    }
}
