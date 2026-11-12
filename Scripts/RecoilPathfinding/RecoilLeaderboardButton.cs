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

        // Deep, saturated gold (the icon is white, tinted here).
        private static readonly Color HudIconGold = new(1f, .74f, .02f, 1f);
        private static readonly Color HudIconGoldHover = new(1f, .85f, .18f, 1f);
        private static readonly Color LeaderboardGold = new(1f, .78f, .1f, 1f);

        private static Texture2D _leaderboardIcon;
        private static Texture2D LeaderboardIcon =>
            _leaderboardIcon != null ? _leaderboardIcon : _leaderboardIcon = Resources.Load<Texture2D>(LeaderboardIconPath);

        private bool _leaderboardOpen;
        private float _leaderboardProgress; // 0 closed .. 1 open, moves toward _leaderboardOpen
        private float _battleTopsTop;       // top of the top 1-2-3 column (set while drawing it)
        private Vector2 _leaderboardScroll;
        private GUIStyle _leaderboardTitleStyle, _leaderboardNameStyle, _leaderboardPointsStyle, _leaderboardRankStyle;
        private static Texture2D _leaderboardFadeRight;

        /// <summary>0 = the top 1-2-3 columns show, 1 = fully pulled in (score bars first, then the frames).</summary>
        private float BattleTopsHidden => Mathf.Clamp01(_leaderboardProgress / .55f);

        /// <summary>0 = the leaderboard is off screen, 1 = in place. Starts once the tops are mostly in.</summary>
        private float LeaderboardShown => Mathf.Clamp01((_leaderboardProgress - .45f) / .55f);

        private readonly struct SampleRanking
        {
            public readonly string Name;
            public readonly long Points;
            public SampleRanking(string name, long points) { Name = name; Points = points; }
        }

        /// <summary>Made-up players, best first, until the real leaderboard data is wired in.</summary>
        private static readonly SampleRanking[] SampleLeaderboard =
        {
            new("Rồng Lửa 99", 1_284_500), new("Tiểu Long Nữ", 1_102_350), new("Anh Ba Sài Gòn", 986_200),
            new("Hổ Vằn Bắc Bộ", 845_900), new("Bé Mèo Cam", 731_400), new("Kiếm Sĩ Mù", 652_050),
            new("Gió Bắc", 540_800), new("Thợ Săn Đêm", 433_300), new("Hoa Hồng Gai", 371_900),
            new("Sói Cô Đơn", 298_450), new("Mây Trắng Bay", 214_700), new("Quân Sư Quạt Mo", 156_200),
            new("Đại Bàng Núi", 132_800), new("Cô Ba Miền Tây", 118_400), new("Lão Hạc", 97_650),
            new("Trăng Khuyết", 86_300), new("Bão Cát", 71_900), new("Hiệp Sĩ Bóng Đêm", 64_250),
            new("Mèo Mun", 52_100), new("Tướng Quân Nhỏ", 43_800), new("Cáo Già", 35_500),
            new("Sấm Sét", 27_900), new("Chim Sẻ", 19_400), new("Người Lạ", 12_050),
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
            _leaderboardScroll = Vector2.zero;
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

        /// <summary>White, full on the left and fading out to nothing on the right.</summary>
        private static Texture2D LeaderboardFadeRight
        {
            get
            {
                if (_leaderboardFadeRight != null) return _leaderboardFadeRight;
                const int width = 256;
                _leaderboardFadeRight = new Texture2D(width, 1, TextureFormat.RGBA32, false)
                {
                    name = "Leaderboard fade right", hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
                };
                var pixels = new Color[width];
                for (var i = 0; i < width; i++)
                    pixels[i] = new Color(1f, 1f, 1f, 1f - Mathf.SmoothStep(0f, 1f, i / (width - 1f)));
                _leaderboardFadeRight.SetPixels(pixels);
                _leaderboardFadeRight.Apply(false, true);
                return _leaderboardFadeRight;
            }
        }

        private static void DrawFadeRight(Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, LeaderboardFadeRight, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        private void DrawSampleLeaderboard(float scale, float bottom)
        {
            var shown = LeaderboardShown;
            var ease = EndgameBack(Mathf.SmoothStep(0f, 1f, shown));
            var width = Mathf.Min(460f * scale, Screen.width * .45f);
            var top = _battleTopsTop > 0f ? _battleTopsTop : 170f * scale;
            var height = bottom - top;
            if (height < 160f * scale) return;
            var margin = 10f * scale;
            var panel = new Rect(Mathf.LerpUnclamped(-width - 30f * scale, margin, ease), top, width, height);
            var alpha = GUI.color.a * Mathf.Clamp01(shown * 2.5f);
            EnsureLeaderboardStyles();

            // No frame: a dark shade that fades out to the right, like the rows.
            DrawFadeRight(panel, new Color(0f, 0f, 0f, .55f * alpha));

            // Title: icon, gold heading, a gold rule fading to the right, close cross.
            var header = new Rect(panel.x + 16f * scale, panel.y + 12f * scale, panel.width - 32f * scale, 48f * scale);
            var icon = LeaderboardIcon;
            if (icon != null && Event.current.type == EventType.Repaint)
            {
                var old = GUI.color;
                GUI.color = new Color(HudIconGold.r, HudIconGold.g, HudIconGold.b, alpha);
                GUI.DrawTexture(new Rect(header.x, header.y + 4f * scale, 40f * scale, 40f * scale), icon, ScaleMode.ScaleToFit, true);
                GUI.color = old;
            }
            _leaderboardTitleStyle.fontSize = Mathf.RoundToInt(30f * scale);
            ShadowLabel(new Rect(header.x + 52f * scale, header.y, header.width - 96f * scale, header.height),
                "BẢNG XẾP HẠNG", _leaderboardTitleStyle, new Color(LeaderboardGold.r, LeaderboardGold.g, LeaderboardGold.b, alpha), scale);
            var close = new Rect(header.xMax - 36f * scale, header.y + 6f * scale, 36f * scale, 36f * scale);
            _leaderboardRankStyle.fontSize = Mathf.RoundToInt(28f * scale);
            ShadowLabel(close, "×", _leaderboardRankStyle,
                new Color(1f, 1f, 1f, (close.Contains(Event.current.mousePosition) ? 1f : .65f) * alpha), scale);
            if (GUI.Button(close, GUIContent.none, GUIStyle.none)) _leaderboardOpen = false;
            DrawFadeRight(new Rect(header.x, header.yMax + 6f * scale, header.width, Mathf.Max(1f, 2f * scale)),
                new Color(LeaderboardGold.r, LeaderboardGold.g, LeaderboardGold.b, .85f * alpha));

            // Rows, scrolled with the mouse wheel (no bar drawn; a thin gold mark shows where the list is).
            var rowHeight = 68f * scale;
            var rowGap = 6f * scale;
            var view = new Rect(panel.x, header.yMax + 18f * scale, panel.width, panel.yMax - header.yMax - 26f * scale);
            var content = new Rect(0f, 0f, view.width - 10f * scale, SampleLeaderboard.Length * (rowHeight + rowGap) - rowGap);
            _leaderboardScroll = GUI.BeginScrollView(view, _leaderboardScroll, content, false, false,
                GUIStyle.none, GUIStyle.none);
            var firstVisible = Mathf.Max(0, Mathf.FloorToInt(_leaderboardScroll.y / (rowHeight + rowGap)));
            var lastVisible = Mathf.Min(SampleLeaderboard.Length - 1,
                Mathf.CeilToInt((_leaderboardScroll.y + view.height) / (rowHeight + rowGap)));
            for (var i = firstVisible; i <= lastVisible; i++)
            {
                // Rows follow the panel in, one a little after the other (counted from the first one shown).
                var rowT = Mathf.Clamp01((shown - .25f - (i - firstVisible) * .04f) / .45f);
                if (rowT <= 0f) continue;
                var rowEase = EndgameBack(rowT);
                var row = new Rect(10f * scale - (1f - rowEase) * 60f * scale, i * (rowHeight + rowGap),
                    content.width - 10f * scale, rowHeight);
                DrawSampleLeaderboardRow(row, i, SampleLeaderboard[i], alpha * Mathf.Clamp01(rowT * 2f), scale);
            }
            GUI.EndScrollView();

            // Where the list is scrolled to.
            if (content.height > view.height && Event.current.type == EventType.Repaint)
            {
                var track = new Rect(view.xMax - 6f * scale, view.y, Mathf.Max(2f, 3f * scale), view.height);
                var thumbHeight = Mathf.Max(24f * scale, track.height * view.height / content.height);
                var travel = Mathf.Clamp01(_leaderboardScroll.y / Mathf.Max(1f, content.height - view.height));
                GUI.DrawTexture(track, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(1f, 1f, 1f, .08f * alpha), 0f, track.width * .5f);
                GUI.DrawTexture(new Rect(track.x, track.y + (track.height - thumbHeight) * travel, track.width, thumbHeight),
                    Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                    new Color(LeaderboardGold.r, LeaderboardGold.g, LeaderboardGold.b, .8f * alpha), 0f, track.width * .5f);
            }
        }

        private void DrawSampleLeaderboardRow(Rect row, int rank, SampleRanking entry, float alpha, float scale)
        {
            var top3 = rank < 3;
            // The row: dark on the left fading out to the right (a touch of gold on the top 3).
            DrawFadeRight(row, top3 ? new Color(.35f, .24f, 0f, .55f * alpha) : new Color(0f, 0f, 0f, .5f * alpha));

            // Rank number: gold for the top 3, white after.
            var rankRect = new Rect(row.x + 4f * scale, row.y, 44f * scale, row.height);
            _leaderboardRankStyle.fontSize = Mathf.RoundToInt((top3 ? 28f : 24f) * scale);
            ShadowLabel(rankRect, (rank + 1).ToString(), _leaderboardRankStyle,
                top3 ? new Color(LeaderboardGold.r, LeaderboardGold.g, LeaderboardGold.b, alpha) : new Color(1f, 1f, 1f, .9f * alpha), scale);

            // Avatar: a big round placeholder with a thin gold ring and the player's initial.
            var avatarSize = row.height - 8f * scale;
            var avatar = new Rect(rankRect.xMax + 6f * scale, row.center.y - avatarSize * .5f, avatarSize, avatarSize);
            EnsureHealthFillRoundTexture();
            if (Event.current.type == EventType.Repaint)
            {
                var old = GUI.color;
                var ring = Mathf.Max(1.5f, 2.5f * scale);
                GUI.color = new Color(LeaderboardGold.r, LeaderboardGold.g, LeaderboardGold.b, .95f * alpha);
                GUI.DrawTexture(new Rect(avatar.x - ring, avatar.y - ring, avatar.width + ring * 2f, avatar.height + ring * 2f),
                    _healthFillRoundTexture, ScaleMode.StretchToFill, true);
                GUI.color = new Color(.12f, .11f, .1f, alpha);
                GUI.DrawTexture(avatar, _healthFillRoundTexture, ScaleMode.StretchToFill, true);
                GUI.color = old;
            }
            _leaderboardRankStyle.fontSize = Mathf.RoundToInt(26f * scale);
            ShadowLabel(avatar, Initial(entry.Name), _leaderboardRankStyle, new Color(1f, 1f, 1f, .9f * alpha), scale);

            // Name (white) above its points (gold).
            var textX = avatar.xMax + 12f * scale;
            var textWidth = row.xMax - textX - 8f * scale;
            _leaderboardNameStyle.fontSize = Mathf.RoundToInt(24f * scale);
            var name = FitTopName(entry.Name, _leaderboardNameStyle, textWidth);
            ShadowLabel(new Rect(textX, row.y + 4f * scale, textWidth, row.height * .52f), name, _leaderboardNameStyle,
                new Color(1f, 1f, 1f, alpha), scale);
            _leaderboardPointsStyle.fontSize = Mathf.RoundToInt(20f * scale);
            ShadowLabel(new Rect(textX, row.y + row.height * .5f, textWidth, row.height * .44f), entry.Points.ToString("N0") + " điểm",
                _leaderboardPointsStyle, new Color(LeaderboardGold.r, LeaderboardGold.g, LeaderboardGold.b, alpha), scale);
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
                { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow, wordWrap = false };
            _leaderboardNameStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, wordWrap = false };
            _leaderboardPointsStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, wordWrap = false };
            _leaderboardRankStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
            foreach (var style in new[] { _leaderboardTitleStyle, _leaderboardNameStyle, _leaderboardPointsStyle, _leaderboardRankStyle })
                style.padding = new RectOffset();
        }

        /// <summary>Text with a dark drop shadow so it reads on the battlefield.</summary>
        private static void ShadowLabel(Rect rect, string text, GUIStyle style, Color color, float scale, bool shadow = true)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (shadow)
            {
                SetLabelTextColor(style, new Color(0f, 0f, 0f, .8f * color.a));
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
                    GUI.color = new Color(0f, 0f, 0f, .7f * oldColor.a);
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
