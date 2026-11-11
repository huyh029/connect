using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private sealed class RankingEntry
        {
            public RecoilScoreRow Score;
            public string Rank, Points;
        }
        private readonly List<RankingEntry> redRanking = new(), blueRanking = new();
        private Vector2 rankingScroll;
        private GUIStyle rankingNameStyle, rankingPointsStyle, rankingRankStyle, rankingTitleStyle;
        private GUIStyle rankingIndexStyle, rankingIndexOutlineStyle;
        private GUIStyle rankingNameShadowStyle;
        private Texture2D rankingFade, rankingRowFade;

        [SerializeField] private Texture2D endgameRedRankingIcon, endgameBlueRankingIcon;
        [SerializeField] private Texture2D endgameCoinImage;
        [SerializeField, Min(.2f)] private float poolAwardAnimationSeconds = 1.2f;
        [SerializeField, Min(0)] private float poolAwardAnimationStagger = .4f;
        private const float PoolCoinIntroSeconds = 1.65f;

        private float PoolAwardProgress(RecoilScoreRow row, float reveal)
        {
            if (row.PoolBonus <= 0) return 1;
            float start = 3.1f + Mathf.Max(0, row.DonationRank - 1) * poolAwardAnimationStagger;
            return Mathf.SmoothStep(0, 1, Mathf.Clamp01((reveal - start - .45f) / Mathf.Max(.2f, poolAwardAnimationSeconds)));
        }

        private decimal AnimatedRankingPoints(RecoilScoreRow row, float reveal) =>
            row.FixedPoints + row.StreakBonus + row.PoolBonus * (decimal)PoolAwardProgress(row, reveal);

        private void BuildEndgameRanking(bool blueWins)
        {
            var ordered = new List<RecoilScoreRow>(matchScoring.Results);
            ordered.Sort((a, b) => a.RankingPoints != b.RankingPoints ? b.RankingPoints.CompareTo(a.RankingPoints) :
                a.Coins != b.Coins ? b.Coins.CompareTo(a.Coins) : string.CompareOrdinal(a.UserId, b.UserId));
            var selected = new List<RecoilScoreRow>(200);
            int redCount = 0, blueCount = 0;
            foreach (var row in ordered)
            {
                if (row.Blue ? blueCount >= 100 : redCount >= 100) continue;
                selected.Add(row);
                if (row.Blue) blueCount++; else redCount++;
            }
            var honored = new HashSet<string>();
            for (int i = 0; i < 3; i++)
            {
                var donor = _topDonors[blueWins ? 1 : 0, i];
                if (donor != null) honored.Add(donor.Id);
            }
            foreach (var row in ordered)
            {
                if (!honored.Contains(row.UserId) || selected.Contains(row)) continue;
                for (int i = selected.Count - 1; i >= 0; i--)
                    if (selected[i].Blue == row.Blue && !honored.Contains(selected[i].UserId))
                    { selected.RemoveAt(i); break; }
                selected.Add(row);
            }
            redRanking.Clear(); blueRanking.Clear();
            rankingScroll = Vector2.zero;
            for (int i = 0; i < ordered.Count; i++)
            {
                var row = ordered[i];
                if (!selected.Contains(row)) continue;
                if (!_donationTopUsers.TryGetValue(row.UserId, out var portrait))
                {
                    portrait = new DonationTopUser { Id = row.UserId, Name = row.Name, Blue = row.Blue, Order = _donationTopUsers.Count };
                    _donationTopUsers.Add(row.UserId, portrait);
                }
                UpdateDonationAvatar(portrait, null);
                var teamRanking = row.Blue ? blueRanking : redRanking;
                teamRanking.Add(new RankingEntry { Score = row, Rank = (teamRanking.Count + 1).ToString(),
                    Points = row.RankingPoints.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " điểm" });
            }
        }

        private void DrawMatchScoring()
        {
            if (!_matchFinished || matchScoring == null || !matchScoring.Settled || _endgameLeaving || _endgameStartedAt < 0) return;
            float reveal = Time.unscaledTime - _endgameStartedAt - endgameCameraSeconds - 1f;
            float fade = Mathf.Clamp01((reveal - 2f) / .6f);
            float scale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            float width = Mathf.Min(Screen.width - 24, 620 * scale);
            var panel = new Rect((Screen.width - width) * .5f, 610 * scale, width, 315 * scale);
            var poolRect = new Rect(panel.x, panel.y - 53 * scale, panel.width, 32 * scale);
            var coinSize = 38f * scale;
            var coinRect = new Rect(poolRect.x + 4f * scale, poolRect.center.y - coinSize * .5f,
                coinSize, coinSize);
            DrawPoolCoinIntro(coinRect, reveal, scale);
            if (fade <= 0) return;

            var oldColor = GUI.color;
            GUI.color = new Color(1, 1, 1, fade);
            EnsureRankingStyles(scale);
            decimal remaining = matchScoring.PoolPoints;
            foreach (var row in matchScoring.Results)
                remaining -= row.PoolBonus * (decimal)PoolAwardProgress(row, reveal);
            var poolTextRect = new Rect(poolRect.x + coinSize + 8f * scale, poolRect.y,
                poolRect.width - coinSize - 8f * scale, poolRect.height);
            GUI.Label(poolTextRect, $"BỂ ĐIỂM {matchScoring.TotalFixedPoints:N0}  |  QUỸ CHIA 20%: {remaining:N0}", rankingRankStyle);
            GUI.Label(new Rect(panel.x, panel.y - 13 * scale, panel.width, 46 * scale), "BẢNG XẾP HẠNG", rankingTitleStyle);
            float headerY = panel.y + 39 * scale, half = (panel.width - 20) * .5f;
            DrawRankingTeamHeader(new Rect(panel.x, headerY, half, 52 * scale), false, scale);
            DrawRankingTeamHeader(new Rect(panel.x + half + 2, headerY, half, 52 * scale), true, scale);
            float bodyY = headerY + 73 * scale, height = Mathf.Max(1, panel.yMax - bodyY - 5 * scale);
            float rowHeight = Mathf.Max(46, 64 * scale);
            float contentHeight = Mathf.Max(height, Mathf.Max(redRanking.Count, blueRanking.Count) * (rowHeight + 1));
            float visibleTop = Mathf.Max(0, rankingScroll.y - bodyY);
            float visibleHeight = Mathf.Min(Screen.height, 975 * scale);
            DrawRankingTeam(new Rect(panel.x, bodyY, half, contentHeight), redRanking, false, scale, visibleTop, visibleHeight);
            DrawRankingTeam(new Rect(panel.x + half + 2, bodyY, half, contentHeight), blueRanking, true, scale, visibleTop, visibleHeight);
            DrawPoolAwardFlights(poolRect.center, new Rect(panel.x, bodyY, panel.width - 18, contentHeight), half, rowHeight, scale, reveal);
            GUI.color = oldColor;
        }

        private void DrawPoolCoinIntro(Rect target, float reveal, float scale)
        {
            if (endgameCoinImage == null || Event.current.type != EventType.Repaint || reveal < 0f)
                return;

            var progress = Mathf.Clamp01(reveal / PoolCoinIntroSeconds);
            var coinRect = target;
            var alpha = 1f;
            var angle = 0f;
            if (progress < 1f)
            {
                var zoomEnd = .22f;
                var travelStart = .58f;
                var center = new Vector2(Screen.width * .5f, Screen.height * .42f);
                var peakSize = Mathf.Max(128f * scale, target.width * 4.5f);
                var size = target.width;
                Vector2 position;

                if (progress < zoomEnd)
                {
                    var zoom = Mathf.SmoothStep(0f, 1f, progress / zoomEnd);
                    size = Mathf.Lerp(target.width * .2f, peakSize, zoom);
                    position = center;
                    angle = progress / PoolCoinIntroSeconds * 1080f;
                }
                else if (progress < travelStart)
                {
                    size = peakSize;
                    position = center;
                    angle = progress / PoolCoinIntroSeconds * 1080f;
                }
                else
                {
                    var travel = Mathf.SmoothStep(0f, 1f,
                        (progress - travelStart) / (1f - travelStart));
                    var zoom = Mathf.Lerp(1f, target.width / peakSize, travel);
                    size = peakSize * zoom;
                    position = Vector2.Lerp(center, target.center, travel);
                    angle = (1f - travel) * 1080f;
                    alpha = Mathf.Lerp(1f, .9f, travel);
                }

                coinRect = new Rect(position.x - size * .5f, position.y - size * .5f, size, size);
            }

            var oldColor = GUI.color;
            var oldMatrix = GUI.matrix;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUIUtility.RotateAroundPivot(angle, coinRect.center);
            GUI.DrawTexture(coinRect, endgameCoinImage, ScaleMode.ScaleToFit, true);
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }

        private void DrawPoolAwardFlights(Vector2 source, Rect viewport, float half, float rowHeight, float scale, float reveal)
        {
            if (Event.current.type != EventType.Repaint) return;
            for (int side = 0; side < 2; side++)
            {
                var rows = side == 0 ? redRanking : blueRanking;
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i].Score;
                    if (row.PoolBonus <= 0) continue;
                    float start = 3.1f + Mathf.Max(0, row.DonationRank - 1) * poolAwardAnimationStagger;
                    float elapsed = reveal - start;
                    if (elapsed < 0 || elapsed > poolAwardAnimationSeconds + .9f) continue;
                    float targetY = viewport.y + i * (rowHeight + 1) + rowHeight * .75f;
                    float screenY = targetY - rankingScroll.y;
                    if (screenY < 0 || screenY > Mathf.Min(Screen.height, 975 * scale)) continue;
                    var target = new Vector2(viewport.x + (side == 0 ? half * .65f : half + 2 + half * .35f), targetY);
                    for (int p = 0; p < 7; p++)
                    {
                        float t = (elapsed - p * .065f) / .65f;
                        if (t < 0 || t > 1) continue;
                        var position = Vector2.Lerp(source, target, t);
                        position.y -= Mathf.Sin(t * Mathf.PI) * (35 + p * 3) * scale;
                        float size = Mathf.Max(9f, (17 + p % 3 * 2) * scale);
                        var oldMatrix = GUI.matrix;
                        var oldColor = GUI.color;
                        GUI.color = new Color(1f, 1f, 1f, oldColor.a * Mathf.Sin(t * Mathf.PI));
                        var coinRect = new Rect(position.x - size * .5f, position.y - size * .5f, size, size);
                        GUIUtility.RotateAroundPivot((t * 720f + p * 47f) % 360f, coinRect.center);
                        if (endgameCoinImage != null)
                            GUI.DrawTexture(coinRect, endgameCoinImage, ScaleMode.ScaleToFit, true);
                        else
                            RankingFill(coinRect, new Color(1f, .76f, .16f, 1f));
                        GUI.matrix = oldMatrix;
                        GUI.color = oldColor;
                    }
                }
            }
        }

        private void EnsureRankingStyles(float scale)
        {
            rankingNameStyle ??= new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, fontStyle = FontStyle.Bold, clipping = TextClipping.Clip, padding = new RectOffset() };
            rankingPointsStyle ??= new GUIStyle(rankingNameStyle) { fontStyle = FontStyle.Bold };
            rankingRankStyle ??= new GUIStyle(rankingPointsStyle) { alignment = TextAnchor.MiddleCenter };
            rankingTitleStyle ??= new GUIStyle(rankingRankStyle);
            rankingPointsStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(16 * scale));
            rankingNameStyle.fontSize = Mathf.RoundToInt(rankingPointsStyle.fontSize * 1.5f);
            rankingRankStyle.fontSize = Mathf.Max(11, Mathf.RoundToInt(18 * scale));
            rankingIndexStyle ??= new GUIStyle(rankingRankStyle) { fontStyle = FontStyle.Bold };
            rankingIndexOutlineStyle ??= new GUIStyle(rankingIndexStyle);
            rankingIndexStyle.fontSize = rankingIndexOutlineStyle.fontSize = Mathf.Max(18, Mathf.RoundToInt(28 * scale));
            rankingIndexStyle.normal.textColor = new Color(1, .84f, .25f);
            rankingIndexOutlineStyle.normal.textColor = new Color(.08f, .035f, .015f);
            rankingTitleStyle.fontSize = Mathf.Max(18, Mathf.RoundToInt(32 * scale));
            rankingNameStyle.normal.textColor = Color.white;
            rankingRankStyle.normal.textColor = rankingPointsStyle.normal.textColor = rankingTitleStyle.normal.textColor = new Color(1, .79f, .24f);
            if (rankingFade != null) return;
            if (endgameRedRankingIcon == null) endgameRedRankingIcon = Resources.Load<Texture2D>("InitialArmyIcons/shield");
            if (endgameBlueRankingIcon == null) endgameBlueRankingIcon = Resources.Load<Texture2D>("InitialArmyIcons/shield");
            rankingFade = new Texture2D(128, 1, TextureFormat.RGBA32, false)
            { name = "Ranking team fade", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < 128; x++) rankingFade.SetPixel(x, 0, new Color(1, 1, 1, Mathf.Lerp(.05f, .8f, x / 127f)));
            rankingFade.Apply(false, true);
            rankingRowFade = new Texture2D(128, 1, TextureFormat.RGBA32, false)
            { name = "Ranking row edge fade", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < 128; x++)
                rankingRowFade.SetPixel(x, 0, new Color(1, 1, 1, Mathf.Pow(Mathf.Clamp01(Mathf.Sin(x / 127f * Mathf.PI)), .7f) * .8f));
            rankingRowFade.Apply(false, true);

        }

        private static void RankingFill(Rect rect, Color tint)
        {
            var old = GUI.color;
            GUI.color = new Color(tint.r, tint.g, tint.b, tint.a * old.a);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private void DrawRankingTeamHeader(Rect rect, bool blue, float scale)
        {
            var old = GUI.color;
            var teamColor = blue ? new Color(.19f, .42f, .8f, old.a) : new Color(.7f, .19f, .23f, old.a);
            GUI.color = new Color(1, 1, 1, old.a * .35f);
            GUI.DrawTextureWithTexCoords(rect, rankingFade, blue ? new Rect(0, 0, 1, 1) : new Rect(1, 0, -1, 1));
            GUI.color = old;
            var icon = new Rect(blue ? rect.xMax - 125 * scale : rect.x + 5 * scale, rect.center.y - 64 * scale, 120 * scale, 128 * scale);
            var image = blue ? endgameBlueFlagImage : endgameRedFlagImage;
            if (image != null)
            {
                GUI.DrawTexture(icon, image, ScaleMode.ScaleToFit, true);
                GUI.color = old;
            }
            else
            {
                RankingFill(new Rect(icon.x, icon.y, 2 * scale, icon.height), new Color(.8f, .75f, .55f));
                RankingFill(new Rect(icon.x + 2 * scale, icon.y, icon.width - 2 * scale, icon.height * .6f), teamColor);
            }
        }

        private void DrawRankingTeam(Rect viewport, List<RankingEntry> rows, bool blue, float scale, float scrollY, float visibleHeight)
        {
            float rowHeight = Mathf.Max(46, 64 * scale), pitch = rowHeight + 1;
            float contentWidth = Mathf.Max(1, viewport.width);
            GUI.BeginGroup(viewport);
            for (int i = 0; i < rows.Count; i++)
            {
                // Align clipping strips to screen pixels, including fractional scroll offsets.
                float y = Mathf.Round(viewport.y + i * pitch - rankingScroll.y) - viewport.y + rankingScroll.y;
                if (y + rowHeight < scrollY || y > scrollY + visibleHeight) continue;
                var entry = rows[i];
                float screenY = viewport.y + y - rankingScroll.y;
                float bottom = Mathf.Min(Screen.height, 975 * scale);
                bool fading = screenY + rowHeight > bottom - 105 * scale;
                int strips = fading ? Mathf.CeilToInt(rowHeight) : 1;
                var oldRowColor = GUI.color;
                for (int strip = 0; strip < strips; strip++)
                {
                    float offset = fading ? strip : 0;
                    float stripHeight = fading ? Mathf.Min(1, rowHeight - offset) : rowHeight;
                    float alpha = Mathf.SmoothStep(0, 1, Mathf.Clamp01((bottom - screenY - offset - stripHeight * .5f) / (105 * scale)));
                    if (alpha <= 0) continue;
                    GUI.color = new Color(oldRowColor.r, oldRowColor.g, oldRowColor.b, oldRowColor.a * alpha);
                    GUI.BeginGroup(new Rect(0, y + offset, contentWidth, stripHeight));
                    DrawRankingEntry(entry, blue, scale, contentWidth, rowHeight, -offset);
                    GUI.EndGroup();
                }
                GUI.color = oldRowColor;
            }
            if (rows.Count == 0) GUI.Label(new Rect(0, 10, contentWidth, 30), "Chưa có người chơi", rankingRankStyle);
            GUI.EndGroup();
        }

        private void DrawRankingEntry(RankingEntry entry, bool blue, float scale, float contentWidth, float rowHeight, float y)
        {
                var teamText = blue ? new Color(.3f, .57f, 1f) : new Color(1f, .36f, .32f);
                rankingNameStyle.normal.textColor = rankingIndexStyle.normal.textColor = teamText;
                rankingIndexOutlineStyle.normal.textColor = new Color(.025f, .03f, .05f, .9f);
                float reveal = Time.unscaledTime - _endgameStartedAt - endgameCameraSeconds - 1f;
                float award = PoolAwardProgress(entry.Score, reveal);
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, old.a * .28f);
                GUI.DrawTexture(new Rect(0, y, contentWidth, rowHeight), rankingRowFade);
                GUI.color = old;
                if (entry.Score.PoolBonus > 0 && award > 0 && award < 1)
                {
                    GUI.color = new Color(1, .7f, .12f, old.a * Mathf.Sin(award * Mathf.PI) * .2f);
                    GUI.DrawTexture(new Rect(0, y, contentWidth, rowHeight), rankingRowFade);
                    GUI.color = old;
                }
                float rankWidth = Mathf.Max(38, 52 * scale), avatarSize = rowHeight - 10 * scale;
                float rankX = blue ? contentWidth - rankWidth : 0;
                var rankRect = new Rect(rankX, y, rankWidth, rowHeight);
                float outline = Mathf.Max(1, 1.5f * scale);
                GUI.Label(new Rect(rankRect.x - outline, rankRect.y, rankRect.width, rankRect.height), entry.Rank, rankingIndexOutlineStyle);
                GUI.Label(new Rect(rankRect.x + outline, rankRect.y, rankRect.width, rankRect.height), entry.Rank, rankingIndexOutlineStyle);
                GUI.Label(new Rect(rankRect.x, rankRect.y - outline, rankRect.width, rankRect.height), entry.Rank, rankingIndexOutlineStyle);
                GUI.Label(new Rect(rankRect.x, rankRect.y + outline, rankRect.width, rankRect.height), entry.Rank, rankingIndexOutlineStyle);
                GUI.Label(rankRect, entry.Rank, rankingIndexStyle);
                float avatarX = blue ? rankX - avatarSize - 5 * scale : rankWidth + 5 * scale;
                var avatar = new Rect(avatarX, y + (rowHeight - avatarSize) * .5f, avatarSize, avatarSize);
                EnsureHealthFillRoundTexture();
                var avatarColor = GUI.color;
                GUI.color = new Color(0, 0, 0, avatarColor.a);
                GUI.DrawTexture(avatar, _healthFillRoundTexture, ScaleMode.StretchToFill, true);
                GUI.color = avatarColor;
                if (_donationTopUsers.TryGetValue(entry.Score.UserId, out var donor) && donor.Avatar != null)
                    GUI.DrawTexture(avatar, donor.Avatar, ScaleMode.ScaleToFit, true);
                float nameX = blue ? 10 * scale : avatar.xMax + 12 * scale;
                float nameWidth = blue ? avatar.x - nameX - 12 * scale : contentWidth - nameX - 10 * scale;
                rankingNameStyle.alignment = rankingPointsStyle.alignment = blue ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft;
                rankingNameShadowStyle ??= new GUIStyle(rankingNameStyle);
                rankingNameShadowStyle.fontSize = rankingNameStyle.fontSize;
                rankingNameShadowStyle.alignment = rankingNameStyle.alignment;
                rankingNameShadowStyle.normal.textColor = new Color(.015f, .02f, .035f, .95f);
                var nameRect = new Rect(nameX, y + 2 * scale, nameWidth, rowHeight * .56f);
                float shadowOffset = Mathf.Max(1, 1.5f * scale);
                GUI.Label(new Rect(nameRect.x + shadowOffset, nameRect.y + shadowOffset, nameRect.width, nameRect.height),
                    entry.Score.Name ?? entry.Score.UserId, rankingNameShadowStyle);
                GUI.Label(nameRect, entry.Score.Name ?? entry.Score.UserId, rankingNameStyle);
                string points = award >= 1 ? entry.Points : AnimatedRankingPoints(entry.Score, reveal).ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " điểm";
                GUI.Label(new Rect(nameX, y + rowHeight * .56f, nameWidth, rowHeight * .4f), points, rankingPointsStyle);
        }
    }
}
