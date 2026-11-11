using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Match time limit, set on the lobby clock: hours × 60 + minutes at its indicator when Start is pressed
    /// (0 giờ 0 phút, or no lobby clock = no limit). The timer runs on battle time from the moment combat starts.
    /// When it runs out, the side whose castle has more health left wins; if both are level the match goes on until
    /// the first castle hit decides it.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        private int _matchTimeLimitMinutes; // 0 = no limit
        private float _matchTimerStart = -1f;
        private bool _matchSuddenDeath;
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
            _matchSuddenDeath = false;
        }

        /// <summary>When combat starts: the countdown before it does not count.</summary>
        private void StartMatchTimer()
        {
            _matchTimerStart = MatchClockSeconds;
            _matchSuddenDeath = false;
        }

        private void UpdateMatchTimer()
        {
            if (!HasMatchTimeLimit || !_combatStarted || _matchFinished || _matchTimerStart < 0f) return;
            if (MatchSecondsLeft > 0f) return;
            if (_blueCastleHealthRemaining > _redCastleHealthRemaining)
                FinishMatch("HẾT GIỜ — ĐỘI XANH CHIẾN THẮNG", true);
            else if (_redCastleHealthRemaining > _blueCastleHealthRemaining)
                FinishMatch("HẾT GIỜ — ĐỘI ĐỎ CHIẾN THẮNG", false);
            else
                _matchSuddenDeath = true; // level: checked again every frame, the first castle hit decides
        }

        /// <summary>Time left, above the score pool at the top of the battle screen.</summary>
        private void DrawMatchTimer(Rect rect, float scale)
        {
            if (!HasMatchTimeLimit) return;
            _matchTimerStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
            _matchTimerStyle.fontSize = Mathf.RoundToInt(30f * scale);

            string text;
            if (_matchSuddenDeath)
            {
                text = "HIỆP PHỤ";
                _matchTimerStyle.normal.textColor = new Color(1f, .35f, .3f, 1f);
            }
            else
            {
                var seconds = Mathf.CeilToInt(MatchSecondsLeft);
                // Under an hour: minutes and seconds only; otherwise hours, minutes and seconds.
                text = seconds >= 3600
                    ? $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}"
                    : $"{seconds / 60:00}:{seconds % 60:00}";
                _matchTimerStyle.normal.textColor = seconds <= 60 && _combatStarted
                    ? new Color(1f, .45f, .3f, 1f) // last minute
                    : Color.white;
            }
            DrawHighlightedLabel(rect, text, _matchTimerStyle, scale);
        }
    }
}
