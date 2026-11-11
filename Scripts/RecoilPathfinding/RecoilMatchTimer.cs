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
        /// Time left, above the score pool: the same lettering as the pool's points (font, weight, gold colour),
        /// 1.8 times their size, with a thinner dark edge.
        /// </summary>
        private void DrawMatchTimer(Rect rect, GUIStyle scoreStyle, float outlineScale)
        {
            if (!HasMatchTimeLimit) return;
            _matchTimerStyle = new GUIStyle(scoreStyle)
            {
                fontSize = Mathf.Max(1, Mathf.RoundToInt(scoreStyle.fontSize * 1.8f)),
                clipping = TextClipping.Overflow
            };
            var seconds = Mathf.CeilToInt(MatchSecondsLeft);
            // Under an hour: minutes and seconds only; otherwise hours, minutes and seconds.
            var text = seconds >= 3600 ? $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}"
                : $"{seconds / 60:00}:{seconds % 60:00}";
            DrawMatchTimerText(rect, text, _matchTimerStyle, outlineScale);
        }

        private GUIStyle _matchTimerOutlineStyle, _matchTimerShadowStyle;

        /// <summary>
        /// The pool's gold lettering with a thin, slightly soft dark edge (the shared outline is too heavy at this
        /// size and blurs the digits).
        /// </summary>
        private void DrawMatchTimerText(Rect rect, string text, GUIStyle style, float scale)
        {
            _matchTimerOutlineStyle = new GUIStyle(style);
            SetLabelTextColor(_matchTimerOutlineStyle, new Color(0f, 0f, 0f, .7f));
            _matchTimerShadowStyle = new GUIStyle(style);
            SetLabelTextColor(_matchTimerShadowStyle, new Color(.62f, .25f, .015f, 1f));
            var edge = Mathf.Clamp(1.2f * scale, 1f, 2f);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    GUI.Label(new Rect(rect.x + dx * edge, rect.y + dy * edge, rect.width, rect.height), text, _matchTimerOutlineStyle);
            GUI.Label(new Rect(rect.x, rect.y + Mathf.Max(1f, scale), rect.width, rect.height), text, _matchTimerShadowStyle);
            GUI.Label(rect, text, style);
        }
    }
}
