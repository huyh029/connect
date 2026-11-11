using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Match time limit, set on the lobby clock: hours × 60 + minutes at its indicator when Start is pressed
    /// (0 giờ 0 phút, or no lobby clock = no limit). The timer runs on battle time from the moment combat starts.
    /// When it runs out, the side whose castle has more health left wins (no extra time): level castles go to the
    /// side with more units alive, and a full tie to blue.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        private int _matchTimeLimitMinutes; // 0 = no limit
        private float _matchTimerStart = -1f;
        private GUIStyle _matchTimerStyle;

        /// <summary>Battle time: the simulation clock when it runs (follows its speed), otherwise game time.</summary>
        private static float MatchClockSeconds =>
            RecoilSimulationClock.IsRunning ? RecoilSimulationClock.SimulationSeconds : Time.time;

        private bool HasMatchTimeLimit => _matchTimeLimitMinutes > 0;

        /// <summary>Seconds left in the match (the full length until combat starts).</summary>
        private float MatchSecondsLeft
        {
            get
            {
                var length = _matchTimeLimitMinutes * 60f;
                if (!_combatStarted || _matchTimerStart < 0f) return length;
                return Mathf.Max(0f, length - (MatchClockSeconds - _matchTimerStart));
            }
        }

        /// <summary>When Start is pressed in the lobby: the time on the lobby clock becomes the match length.</summary>
        private void CaptureMatchTimeLimit()
        {
            _matchTimeLimitMinutes = _lobbyClockDial != null ? _lobbyClockDial.SelectedTotalMinutes : 0;
            _matchTimerStart = -1f;
        }

        /// <summary>When combat starts: the countdown before it does not count.</summary>
        private void StartMatchTimer()
        {
            _matchTimerStart = MatchClockSeconds;
        }

        private void UpdateMatchTimer()
        {
            if (!HasMatchTimeLimit || !_combatStarted || _matchFinished || _matchTimerStart < 0f) return;
            if (MatchSecondsLeft > 0f) return;
            // No extra time: more castle health wins; level castles -> more units alive; still level -> blue.
            bool blueWins = _blueCastleHealthRemaining != _redCastleHealthRemaining
                ? _blueCastleHealthRemaining > _redCastleHealthRemaining
                : AliveCount(_blueTeam) >= AliveCount(_redTeam);
            FinishMatch(blueWins ? "HẾT GIỜ — ĐỘI XANH CHIẾN THẮNG" : "HẾT GIỜ — ĐỘI ĐỎ CHIẾN THẮNG", blueWins);
        }

        private static int AliveCount(System.Collections.Generic.List<Soldier> team)
        {
            var count = 0;
            foreach (var soldier in team)
                if (soldier != null && !soldier.Retired) count++;
            return count;
        }

        /// <summary>
        /// Time left, above the score pool: the very same lettering as the pool's points (font, weight, gold colour,
        /// dark outline), 1.2 times their size.
        /// </summary>
        private void DrawMatchTimer(Rect rect, GUIStyle scoreStyle, float outlineScale)
        {
            if (!HasMatchTimeLimit) return;
            _matchTimerStyle = new GUIStyle(scoreStyle)
            {
                fontSize = Mathf.Max(1, Mathf.RoundToInt(scoreStyle.fontSize * 1.2f)),
                clipping = TextClipping.Overflow
            };
            var seconds = Mathf.CeilToInt(MatchSecondsLeft);
            // Under an hour: minutes and seconds only; otherwise hours, minutes and seconds.
            var text = seconds >= 3600 ? $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}"
                : $"{seconds / 60:00}:{seconds % 60:00}";
            DrawHighlightedLabel(rect, text, _matchTimerStyle, outlineScale * 1.2f);
        }
    }
}
