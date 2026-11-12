using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Leaderboard button in the battle screen's bottom-left corner: the Font Awesome "ranking-star" icon
    /// (Font Awesome Free 6.7.2, CC BY 4.0 — see _Game/UI/FontAwesome/ATTRIBUTION.txt).
    /// Clicking it pulls the top 1-2-3 of both sides in (score bars into their frames, frames off the screen edges),
    /// then slides a leaderboard in on the left. Clicking again plays it backwards. The leaderboard shows sample
    /// (made-up) players for now.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        public const string LeaderboardIconPath = "HudIcons/ranking-star";
        private const float LeaderboardIconSize = 84f;   // design pixels at 1920 wide
        private const float LeaderboardIconMargin = 24f;
        private const float LeaderboardAnimSeconds = .9f; // tops in, then the board out

        private static readonly Color HudIconGold = new(1f, .82f, .32f, 1f);
        private static readonly Color HudIconGoldHover = new(1f, .93f, .62f, 1f);

        private static Texture2D _leaderboardIcon;
        private static Texture2D LeaderboardIcon =>
            _leaderboardIcon != null ? _leaderboardIcon : _leaderboardIcon = Resources.Load<Texture2D>(LeaderboardIconPath);

        private bool _leaderboardOpen;
        private float _leaderboardProgress; // 0 closed .. 1 open, moves toward _leaderboardOpen
        private float _battleTopsTop;       // top of the top 1-2-3 column (set while drawing it)
        private GUIStyle _leaderboardTitleStyle, _leaderboardNameStyle, _leaderboardPointsStyle, _leaderboardRankStyle;

        /// <summary>0 = the top 1-2-3 columns show, 1 = fully pulled in (score bars first, then the frames).</summary>
        private float BattleTopsHidden => Mathf.Clamp01(_leaderboardProgress / .55f);

        /// <summary>0 = the leaderboard is off screen, 1 = in place. Starts once the tops are mostly in.</summary>
        private float LeaderboardShown => Mathf.Clamp01((_leaderboardProgress - .45f) / .55f);

        private struct SampleRanking
        {
            public string Name;
            public long Points;
            public bool Blue;
            public SampleRanking(string name, long points, bool blue) { Name = name; Points = points; Blue = blue; }
        }

        /// <summary>Made-up players, best first, until the real leaderboard data is wired in.</summary>
        private static readonly SampleRanking[] SampleLeaderboard =
        {
            new("Rồng Lửa 99", 1_284_500, false),
            new("Tiểu Long Nữ", 1_102_350, true),
            new("Anh Ba Sài Gòn", 986_200, false),
            new("Hổ Vằn Bắc Bộ", 845_900, true),
            new("Bé Mèo Cam", 731_400, true),
            new("Kiếm Sĩ Mù", 652_050, false),
            new("Gió Bắc", 540_800, true),
            new("Thợ Săn Đêm", 433_300, false),
            new("Hoa Hồng Gai", 371_900, true),
            new("Sói Cô Đơn", 298_450, false),
            new("Mây Trắng Bay", 214_700, true),
            new("Quân Sư Quạt Mo", 156_200, false),
        };

        private void DrawBattleLeaderboardButton()
        {
            var scale = Mathf.Clamp(Screen.width / 1920f, .62f, 1f);
            if (Event.current.type == EventType.Repaint)
                _leaderboardProgress = Mathf.MoveTowards(_leaderboardProgress, _leaderboardOpen ? 1f : 0f,
                    Time.unscaledDeltaTime / LeaderboardAnimSeconds);

            var size = LeaderboardIconSize * scale;
            var margin = LeaderboardIconMargin * scale;
            var iconRect = new Rect(margin, Screen.height - margin - size, size, size);
            if (LeaderboardShown > 0f) DrawSampleLeaderboard(scale, iconRect.y - 14f * scale);

            var icon = LeaderboardIcon;
            if (icon != null && HudIconButton(iconRect, icon, _leaderboardOpen)) _leaderboardOpen = !_leaderboardOpen;
        }

        /// <summary>Closed and the top columns back in place (new match / retry).</summary>
        private void ResetLeaderboardPanel()
        {
            _leaderboardOpen = false;
            _leaderboardProgress = 0f;
        }

        /// <summary>The top 1-2-3 frame and its score bar while the leaderboard pulls them in.</summary>
        private void ApplyBattleTopHide(ref Rect border, ref float scoreReveal, ref float alpha, bool blue, float scale)
        {
            var hidden = BattleTopsHidden;
            if (hidden <= 0f) return;
            // First the score bar slides back into its frame, then the frame slides off its screen edge.
            scoreReveal *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(hidden / .5f));
            var slide = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((hidden - .4f) / .6f));
            border.x += (blue ? 1f : -1f) * slide * (border.width + 40f * scale);
            alpha *= 1f - slide * slide;
        }

        private void DrawSampleLeaderboard(float scale, float bottom)
        {
            var shown = LeaderboardShown;
            var ease = EndgameBack(Mathf.SmoothStep(0f, 1f, shown));
            var width = Mathf.Min(400f * scale, Screen.width * .42f);
            var top = _battleTopsTop > 0f ? _battleTopsTop : 170f * scale;
            var height = bottom - top;
            if (height < 140f * scale) return;
            var margin = 10f * scale;
            var panel = new Rect(Mathf.LerpUnclamped(-width - 30f * scale, margin, ease), top, width, height);

            var oldColor = GUI.color;
            var alpha = oldColor.a * Mathf.Clamp01(shown * 2.5f);
            EnsureLeaderboardStyles();
            if (Event.current.type == EventType.Repaint)
            {
                var radius = 14f * scale;
                GUI.DrawTexture(panel, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(.05f, .04f, .03f, .82f * alpha), 0f, radius);
                GUI.DrawTexture(panel, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(1f, .78f, .3f, .9f * alpha), Mathf.Max(1.5f, 2.5f * scale), radius);
            }

            // Header: icon, title, close cross.
            var header = new Rect(panel.x + 14f * scale, panel.y + 10f * scale, panel.width - 28f * scale, 44f * scale);
            var icon = LeaderboardIcon;
            if (icon != null && Event.current.type == EventType.Repaint)
            {
                GUI.color = new Color(HudIconGold.r, HudIconGold.g, HudIconGold.b, alpha);
                GUI.DrawTexture(new Rect(header.x, header.y + 6f * scale, 32f * scale, 32f * scale), icon, ScaleMode.ScaleToFit, true);
            }
            _leaderboardTitleStyle.fontSize = Mathf.RoundToInt(24f * scale);
            ShadowLabel(new Rect(header.x + 42f * scale, header.y, header.width - 80f * scale, header.height),
                "BẢNG XẾP HẠNG", _leaderboardTitleStyle, new Color(1f, .84f, .38f, alpha), scale);
            var close = new Rect(header.xMax - 32f * scale, header.y + 6f * scale, 32f * scale, 32f * scale);
            _leaderboardRankStyle.fontSize = Mathf.RoundToInt(22f * scale);
            ShadowLabel(close, "×", _leaderboardRankStyle,
                new Color(1f, 1f, 1f, (close.Contains(Event.current.mousePosition) ? .95f : .6f) * alpha), scale);
            if (GUI.Button(close, GUIContent.none, GUIStyle.none)) _leaderboardOpen = false;

            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(panel.x + 14f * scale, header.yMax + 4f * scale, panel.width - 28f * scale, Mathf.Max(1f, 1.5f * scale)),
                    Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(1f, .78f, .3f, .45f * alpha), 0f, 0f);

            // Rows: as many as fit.
            var rowHeight = 48f * scale;
            var rowGap = 4f * scale;
            var listTop = header.yMax + 12f * scale;
            var rows = Mathf.Min(SampleLeaderboard.Length, Mathf.FloorToInt((panel.yMax - 10f * scale - listTop + rowGap) / (rowHeight + rowGap)));
            for (var i = 0; i < rows; i++)
            {
                // Each row follows the panel in a little later than the one above it.
                var rowT = Mathf.Clamp01((shown - .25f - i * .04f) / .45f);
                if (rowT <= 0f) continue;
                var rowEase = EndgameBack(rowT);
                var row = new Rect(panel.x + 10f * scale - (1f - rowEase) * 60f * scale, listTop + i * (rowHeight + rowGap),
                    panel.width - 20f * scale, rowHeight);
                DrawSampleLeaderboardRow(row, i, SampleLeaderboard[i], alpha * Mathf.Clamp01(rowT * 2f), scale);
            }
            GUI.color = oldColor;
        }

        private static readonly Color[] MedalColors =
        {
            new(1f, .80f, .25f), new(.82f, .86f, .92f), new(.86f, .56f, .30f)
        };

        private void DrawSampleLeaderboardRow(Rect row, int rank, SampleRanking entry, float alpha, float scale)
        {
            var medal = rank < MedalColors.Length;
            var repaint = Event.current.type == EventType.Repaint;
            if (repaint)
            {
                var background = medal
                    ? new Color(MedalColors[rank].r, MedalColors[rank].g, MedalColors[rank].b, .16f * alpha)
                    : new Color(1f, 1f, 1f, (rank % 2 == 0 ? .06f : .03f) * alpha);
                GUI.DrawTexture(row, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, background, 0f, 8f * scale);
            }

            // Rank: a medal disc for the top 3, a plain number after that.
            var badgeSize = row.height * .66f;
            var badge = new Rect(row.x + 8f * scale, row.center.y - badgeSize * .5f, badgeSize, badgeSize);
            EnsureHealthFillRoundTexture();
            if (medal && repaint)
            {
                GUI.color = new Color(MedalColors[rank].r, MedalColors[rank].g, MedalColors[rank].b, alpha);
                GUI.DrawTexture(badge, _healthFillRoundTexture, ScaleMode.StretchToFill, true);
            }
            _leaderboardRankStyle.fontSize = Mathf.RoundToInt((medal ? 17f : 18f) * scale);
            GUI.color = Color.white;
            ShadowLabel(badge, (rank + 1).ToString(), _leaderboardRankStyle,
                medal ? new Color(.18f, .12f, .04f, alpha) : new Color(1f, 1f, 1f, .85f * alpha), scale, !medal);

            // Avatar: a disc in the player's side colour with their initial.
            var avatarSize = row.height * .74f;
            var avatar = new Rect(badge.xMax + 10f * scale, row.center.y - avatarSize * .5f, avatarSize, avatarSize);
            var side = entry.Blue ? new Color(.16f, .42f, .95f) : new Color(.86f, .2f, .14f);
            if (repaint)
            {
                GUI.color = new Color(1f, .85f, .45f, .9f * alpha);
                GUI.DrawTexture(new Rect(avatar.x - 2f * scale, avatar.y - 2f * scale, avatar.width + 4f * scale, avatar.height + 4f * scale),
                    _healthFillRoundTexture, ScaleMode.StretchToFill, true);
                GUI.color = new Color(side.r, side.g, side.b, alpha);
                GUI.DrawTexture(avatar, _healthFillRoundTexture, ScaleMode.StretchToFill, true);
            }
            _leaderboardRankStyle.fontSize = Mathf.RoundToInt(18f * scale);
            ShadowLabel(avatar, Initial(entry.Name), _leaderboardRankStyle, new Color(1f, 1f, 1f, alpha), scale);

            // Points on the right, the name takes what is left.
            _leaderboardPointsStyle.fontSize = Mathf.RoundToInt(17f * scale);
            var points = entry.Points.ToString("N0");
            var pointsWidth = _leaderboardPointsStyle.CalcSize(new GUIContent(points)).x + 4f * scale;
            var pointsRect = new Rect(row.xMax - 10f * scale - pointsWidth, row.y, pointsWidth, row.height);
            ShadowLabel(pointsRect, points, _leaderboardPointsStyle, new Color(1f, .84f, .38f, alpha), scale);

            var nameRect = new Rect(avatar.xMax + 10f * scale, row.y, pointsRect.x - avatar.xMax - 18f * scale, row.height);
            _leaderboardNameStyle.fontSize = Mathf.RoundToInt(18f * scale);
            var name = FitTopName(entry.Name, _leaderboardNameStyle, nameRect.width);
            ShadowLabel(nameRect, name, _leaderboardNameStyle, new Color(1f, 1f, 1f, alpha), scale);
            GUI.color = Color.white;
        }

        private static string Initial(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return "?";
            var starts = System.Globalization.StringInfo.ParseCombiningCharacters(name);
            var end = starts.Length > 1 ? starts[1] : name.Length;
            return name.Substring(0, end).ToUpperInvariant();
        }

        private void EnsureLeaderboardStyles()
        {
            if (_leaderboardTitleStyle != null) return;
            _leaderboardTitleStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
            _leaderboardNameStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, wordWrap = false };
            _leaderboardPointsStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow, wordWrap = false };
            _leaderboardRankStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow, padding = new RectOffset() };
            foreach (var style in new[] { _leaderboardTitleStyle, _leaderboardNameStyle, _leaderboardPointsStyle, _leaderboardRankStyle })
                style.padding = new RectOffset();
        }

        /// <summary>Text with a dark drop shadow so it reads on the battlefield.</summary>
        private static void ShadowLabel(Rect rect, string text, GUIStyle style, Color color, float scale, bool shadow = true)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (shadow)
            {
                SetLabelTextColor(style, new Color(0f, 0f, 0f, .75f * color.a));
                GUI.Label(new Rect(rect.x + 1.5f * scale, rect.y + 1.5f * scale, rect.width, rect.height), text, style);
            }
            SetLabelTextColor(style, color);
            GUI.Label(rect, text, style);
        }

        /// <summary>A white icon drawn gold with a soft dark shadow; grows, lifts and brightens on hover (and while active).</summary>
        private bool HudIconButton(Rect rect, Texture2D icon, bool active = false)
        {
            var hover = Mathf.Max(SettingsButtonHover(rect), active ? .6f : 0f);
            var oldMatrix = GUI.matrix;
            var oldColor = GUI.color;
            try
            {
                GUIUtility.ScaleAroundPivot(Vector2.one * (1f + .08f * hover), rect.center);
                GUI.matrix = Matrix4x4.Translate(new Vector3(0f, -3f * hover, 0f)) * GUI.matrix;
                var clicked = GUI.Button(rect, GUIContent.none, GUIStyle.none);
                if (Event.current.type == EventType.Repaint)
                {
                    var shadow = Mathf.Max(2f, rect.height * .035f);
                    GUI.color = new Color(0f, 0f, 0f, .65f * oldColor.a);
                    GUI.DrawTexture(new Rect(rect.x + shadow, rect.y + shadow, rect.width, rect.height), icon,
                        ScaleMode.ScaleToFit, true);
                    var gold = Color.Lerp(HudIconGold, HudIconGoldHover, hover);
                    gold.a *= oldColor.a;
                    GUI.color = gold;
                    GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit, true);
                }
                return clicked;
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.color = oldColor;
            }
        }
    }
}
