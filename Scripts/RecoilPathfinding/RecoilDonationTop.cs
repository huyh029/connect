using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private sealed class DonationTopUser
        {
            public string Id, Name, AvatarUrl, LoadingAvatarUrl;
            public Texture2D Avatar;
            public float LoadingAvatarAt;
            public long Points;
            public int Order;
            public bool Blue;
        }
        private readonly DonationTopUser[,] _topDonors = new DonationTopUser[2,3];
        private readonly DonationTopUser[] _endgameDonors = new DonationTopUser[3];
        private readonly Dictionary<string, string> _donationAvatarUrls = new(StringComparer.Ordinal);

        private static string FitTopName(string name, GUIStyle style, float width)
        {
            name = (name ?? "").Replace('\r', ' ').Replace('\n', ' ');
            var minimum = Mathf.Max(10, Mathf.RoundToInt(style.fontSize * .7f));
            var content = new GUIContent(name);
            while (style.fontSize > minimum && style.CalcSize(content).x > width)
                style.fontSize--;
            if (style.CalcSize(content).x <= width) return name;
            // Trim at text-element boundaries so emoji and accented names stay intact.
            var starts = System.Globalization.StringInfo.ParseCombiningCharacters(name);
            int low = 0, high = starts.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                int end = mid < starts.Length ? starts[mid] : name.Length;
                content.text = name.Substring(0, end) + "…";
                if (style.CalcSize(content).x <= width) low = mid;
                else high = mid - 1;
            }
            return name.Substring(0, low < starts.Length ? starts[low] : name.Length) + "…";
        }

        private void UpdateDonationAvatar(DonationTopUser donor, TikUser user)
        {
            var url = user?.avatar_url;
            if (string.IsNullOrWhiteSpace(url)) _donationAvatarUrls.TryGetValue(donor.Id, out url);
            if (string.IsNullOrWhiteSpace(url) || donor.LoadingAvatarUrl == url && Time.unscaledTime - donor.LoadingAvatarAt < 30f ||
                donor.AvatarUrl == url && donor.Avatar != null) return;
            donor.AvatarUrl = url;
            donor.LoadingAvatarUrl = url;
            donor.LoadingAvatarAt = Time.unscaledTime;
            TikTokAvatarCache.RequestAvatar(this, url, texture =>
            {
                if (this == null || donor.AvatarUrl != url ||
                    !_donationTopUsers.TryGetValue(donor.Id, out var current) || !ReferenceEquals(current, donor)) return;
                donor.LoadingAvatarUrl = null;
                if (texture == null) return;
                const int size = 128;
                var pixels = new Color[size * size];
                float crop = Mathf.Min(texture.width, texture.height);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        var uv = new Vector2((x + .5f) / size, (y + .5f) / size);
                        var color = texture.GetPixelBilinear(.5f + (uv.x - .5f) * crop / texture.width,
                            .5f + (uv.y - .5f) * crop / texture.height);
                        color.a *= Mathf.Clamp01((.5f - Vector2.Distance(uv, Vector2.one * .5f)) * size);
                        pixels[y * size + x] = color;
                    }
                var avatar = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { name = "Top Avatar", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                avatar.SetPixels(pixels);
                avatar.Apply(false, true);
                if (donor.Avatar != null) Destroy(donor.Avatar);
                donor.Avatar = avatar;
            });
        }

        /// <summary>The donor's picture in the opening of the top <paramref name="rank"/> frame drawn in <paramref name="frame"/>.</summary>
        private static void DrawDonationAvatar(Rect frame, DonationTopUser donor, int rank)
        {
            var avatar = BattleTopAvatarRect(frame, rank);
            DrawBattleTopAvatarBackground(avatar);
            if (donor?.Avatar != null)
                GUI.DrawTexture(avatar, donor.Avatar, ScaleMode.StretchToFill, true);
        }

        private void ReleaseDonationAvatars()
        {
            foreach (var donor in _donationTopUsers.Values)
                if (donor.Avatar != null) Destroy(donor.Avatar);
            Array.Clear(_topDonors, 0, _topDonors.Length);
            Array.Clear(_endgameDonors, 0, _endgameDonors.Length);
        }
        private readonly Dictionary<string, DonationTopUser> _donationTopUsers = new(StringComparer.Ordinal);
        private readonly List<DonationTopUser> _donationTopSorted = new();
        private readonly string[,] _topAnimatedUsers = new string[2,3];
        private readonly float[,] _topAppearedAt = new float[2,3];

        private void RecordDonationTop(TikEvent message, string user, bool blue)
        {
            if (message.gift == null) return;
            // Gift ID pricing comes from the configured coin cost, even if the event
            // omits its price or reports a different value. Unmapped gifts keep their event price.
            var coinCost = RecoilTikTokGiftMapping.CoinCost(_tikTokArmySettings?.Rules, message.gift);
            if (coinCost <= 0) return;
            var count = Math.Max(0, message.delta > 0 ? message.delta : message.count);
            if (count == 0) return;
            if (!_donationTopUsers.TryGetValue(user, out var donor))
            {
                donor = new DonationTopUser { Id=user, Blue=blue, Order=_donationTopUsers.Count };
                _donationTopUsers.Add(user, donor);
            }
            donor.Name = !string.IsNullOrWhiteSpace(message.user?.nickname) ? message.user.nickname : message.user?.unique_id ?? user;
            UpdateDonationAvatar(donor, message.user);
            var added = (long)coinCost * count;
            JoinMatchScoring(user, donor.Name, blue);
            matchScoring.Donate(user, donor.Name, blue, added);
            donor.Points = Math.Min(long.MaxValue - added, donor.Points) + added;
            RefreshDonationTop(false);
            RefreshDonationTop(true);
        }

        private void RefreshDonationTop(bool blue)
        {
            _donationTopSorted.Clear();
            foreach (var donor in _donationTopUsers.Values)
                if (donor.Blue == blue && donor.Points > 0) _donationTopSorted.Add(donor);
            _donationTopSorted.Sort((a,b) => a.Points != b.Points ? b.Points.CompareTo(a.Points) : a.Order.CompareTo(b.Order));
            var names = new string[3]; var scores = new int[3];
            for (var i=0;i<3;i++) _topDonors[blue ? 1 : 0,i]=null;
            for (var i=0; i<Math.Min(3,_donationTopSorted.Count);i++)
            {
                var donor = _donationTopSorted[i];
                _topDonors[blue ? 1 : 0,i] = donor;
                names[i] = donor.Name;
                // Points on the donor record are coins; the live HUD shows fixed ranking points.
                scores[i] = (int)Math.Min(int.MaxValue, (decimal)donor.Points * 50m);
            }
            if (blue) { battleBlueTopNames=names; battleBlueTopScores=scores; }
            else { battleRedTopNames=names; battleRedTopScores=scores; }
        }

        private int BattleTopSourceIndex(bool blue, int rank)
        {
            var names=blue ? battleBlueTopNames : battleRedTopNames;
            var scores=blue ? battleBlueTopScores : battleRedTopScores;
            if(names==null || scores==null) return -1;
            var present=0;
            for(var i=0;i<Math.Min(names.Length,scores.Length);i++)
                if(!string.IsNullOrWhiteSpace(names[i]) && scores[i]>0 && present++==rank) return i;
            return -1;
        }

        private void DrawAnimatedBattleTop(Rect border, Texture2D image, bool blue, int rank, float scale)
        {
            var source=BattleTopSourceIndex(blue,rank);
            var side=blue ? 1 : 0;
            if(source<0) { _topAnimatedUsers[side,rank]=null; return; }
            var name=(blue ? battleBlueTopNames : battleRedTopNames)[source];
            if(_topAnimatedUsers[side,rank]!=name)
            { _topAnimatedUsers[side,rank]=name; _topAppearedAt[side,rank]=Time.unscaledTime+rank*.08f; }
            var t=Mathf.Clamp01((Time.unscaledTime-_topAppearedAt[side,rank])/.55f);
            var ease=EndgameBack(t);
            border.x+=(blue ? 1 : -1)*(1-ease)*85f*scale;
            border.y-=(1-ease)*10f*scale;
            var oldColor=GUI.color; var oldMatrix=GUI.matrix;
            GUI.color=new Color(oldColor.r,oldColor.g,oldColor.b,oldColor.a*Mathf.SmoothStep(0,1,t));
            GUIUtility.ScaleAroundPivot(Vector2.one*Mathf.LerpUnclamped(.85f,1f,ease),border.center);
            var scoreT=Mathf.Clamp01((Time.unscaledTime-_topAppearedAt[side,rank]-.55f)/.45f);
            if(scoreT>0f)
            {
                var reveal=Mathf.SmoothStep(0,1,scoreT);
                var barWidth=Mathf.Min(310f*scale,Screen.width*.5f-border.width-20f*scale);
                var clip=new Rect(blue ? border.center.x-barWidth : border.center.x,
                    border.y-8f*scale,barWidth,border.height+16f*scale);
                var scoreBorder=border;
                scoreBorder.x+=(blue ? 1f : -1f)*barWidth*(1f-reveal);
                scoreBorder.position-=clip.position;
                GUI.BeginGroup(clip);
                DrawBattleTopScore(scoreBorder,blue,source,scale);
                GUI.EndGroup();
            }
            DrawDonationAvatar(border, _topDonors[side,source], rank);
            GUI.DrawTexture(border,image,ScaleMode.StretchToFill,true);
            GUI.matrix=oldMatrix; GUI.color=oldColor;
        }

        private void ResetDonationTop()
        {
            BeginMatchScoring();
            ReleaseDonationAvatars();
            _donationAvatarUrls.Clear();
            _donationTopUsers.Clear(); _donationTopSorted.Clear();
            battleBlueTopNames=new string[3]; battleRedTopNames=new string[3];
            battleBlueTopScores=new int[3]; battleRedTopScores=new int[3];
            Array.Clear(_topAnimatedUsers,0,_topAnimatedUsers.Length);
            Array.Clear(_topAppearedAt,0,_topAppearedAt.Length);
        }
    }
}
