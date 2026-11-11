using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

namespace RecoilPathfinding
{
    /// <summary>
    /// Summon intros (TikTok gifts): videos with the viewer's name, in the bottom corner of their side (blue right,
    /// red left).
    /// <list type="bullet">
    /// <item>Each side has its own lanes, so blue and red intros never wait for each other.</item>
    /// <item>Each side has a hero lane and a smaller soldier lane. A soldier intro plays together with a hero intro of
    /// the same viewer (stacked above it); one of another viewer waits until that side's hero intro is over.</item>
    /// <item>Every lane prepares its next clip while the current one plays, so intros follow each other without a gap.</item>
    /// </list>
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        [SerializeField] private VideoClip[] heroIntroClips = new VideoClip[8];
        [SerializeField, Tooltip("Intro khi quà TikTok gọi lính, theo loại lính (thứ tự Blue Units By Type), phe xanh.")]
        private VideoClip[] soldierIntroBlueClips = new VideoClip[CharacterTypeCount];
        [SerializeField, Tooltip("Intro khi quà TikTok gọi lính, theo loại lính, phe đỏ.")]
        private VideoClip[] soldierIntroRedClips = new VideoClip[CharacterTypeCount];
        // Per side: soldier intros wait behind at most this many queued ones; beyond that they are skipped.
        private const int MaxQueuedSoldierIntros = 2;
        // A soldier intro waits at most this long for the same viewer's hero intro to start, then plays alone.
        private const float SoldierIntroPairWait = 3f;
        private const float IntroFadeSeconds = .25f;

        private struct HeroIntroRequest
        {
            public string User, Name, Title;
            public VideoClip Clip;
            public int HeroId;
            public bool Blue, Soldier;
            public float QueuedAt;
        }

        /// <summary>One video player with the intro it holds (preparing, or playing).</summary>
        private sealed class IntroSlot
        {
            public VideoPlayer Player;
            public AudioSource Audio;
            public HeroIntroRequest Request;
            public bool Loaded, Playing;
            public float PreparedAt, ShownAt, EndAt = -1f;
        }

        /// <summary>Hero or soldier intros of one side: the one playing, and the next one already preparing.</summary>
        private sealed class IntroLane
        {
            public bool Blue, Soldier;
            public readonly List<HeroIntroRequest> Queue = new();
            public IntroSlot Current, Next;
            public bool Showing => Current is { Playing: true };
        }

        private IntroLane[] _introLanes; // blue hero, blue soldier, red hero, red soldier
        private readonly HashSet<string> _heroIntrosShown = new();
        private Material _heroIntroEdgeMaterial;
        private GUIStyle _heroIntroUserStyle, _heroIntroTitleStyle;

        private IntroLane IntroLaneFor(bool blue, bool soldier)
        {
            EnsureHeroIntroPlayer();
            return _introLanes[(blue ? 0 : 2) + (soldier ? 1 : 0)];
        }

        private void QueueHeroSummonIntro(string user, string displayName, int heroId, bool blue)
        {
            if (_matchFinished || string.IsNullOrWhiteSpace(user) || heroId < 1 || heroId > heroIntroClips.Length ||
                heroIntroClips[heroId - 1] == null) return;
            if (!_heroIntrosShown.Add(user + ":" + heroId + ":" + blue)) return;
            IntroLaneFor(blue, false).Queue.Add(new HeroIntroRequest
            {
                User = user, Name = string.IsNullOrWhiteSpace(displayName) ? user : displayName, HeroId = heroId, Blue = blue,
                Clip = heroIntroClips[heroId - 1], QueuedAt = Time.unscaledTime,
                Title = "TRIỆU HỒI " + HeroLabels[heroId - 1].ToUpperInvariant() + (blue ? " • PHE XANH" : " • PHE ĐỎ")
            });
        }

        /// <summary>
        /// A TikTok gift called soldiers of <paramref name="type"/>: that unit's intro, once per viewer, type and side.
        /// Smaller than a hero intro; plays with the same viewer's hero intro, otherwise only when no hero intro shows.
        /// </summary>
        private void QueueSoldierSummonIntro(string user, string displayName, int type, bool blue)
        {
            var clips = blue ? soldierIntroBlueClips : soldierIntroRedClips;
            if (_matchFinished || string.IsNullOrWhiteSpace(user) || clips == null || type < 0 || type >= clips.Length ||
                clips[type] == null) return;
            var lane = IntroLaneFor(blue, true);
            if (lane.Queue.Count >= MaxQueuedSoldierIntros) return;
            if (!_heroIntrosShown.Add("unit:" + user + ":" + type + ":" + blue)) return;
            lane.Queue.Add(new HeroIntroRequest
            {
                User = user, Name = string.IsNullOrWhiteSpace(displayName) ? user : displayName, Blue = blue,
                Soldier = true, Clip = clips[type], QueuedAt = Time.unscaledTime,
                Title = "TRIỆU HỒI " + CharacterTypeLabels[type].ToUpperInvariant() + (blue ? " • PHE XANH" : " • PHE ĐỎ")
            });
        }

        private void UpdateHeroIntros()
        {
            if (_matchFinished || !_lobbyStarted || _initialFormationLoading || _introLanes == null) return;
            // Hero lanes first: a soldier intro then sees the hero intro that started this same frame.
            UpdateIntroLane(_introLanes[0]);
            UpdateIntroLane(_introLanes[2]);
            UpdateIntroLane(_introLanes[1]);
            UpdateIntroLane(_introLanes[3]);
        }

        private void UpdateIntroLane(IntroLane lane)
        {
            var now = Time.unscaledTime;
            var current = lane.Current;
            if (current.Playing)
            {
                var ended = current.EndAt >= 0f ||
                            now - current.ShownAt > Mathf.Max(10f, (float)current.Request.Clip.length + 5f);
                if (ended && current.EndAt < 0f) current.EndAt = now;
                if (ended && NextIntroReady(lane)) StartNextIntro(lane, true); // straight on, no gap
                else if (ended && now - current.EndAt >= IntroFadeSeconds) StopIntroSlot(current); // last one fades out
            }
            if (!lane.Current.Playing && NextIntroReady(lane)) StartNextIntro(lane, false);

            var next = lane.Next;
            if (next.Loaded && !next.Player.isPrepared && now - next.PreparedAt > 15f) StopIntroSlot(next); // never loaded
            // A soldier intro prepared for one viewer while the hero intro now is another viewer's: swap in the matching one.
            if (lane.Soldier && next.Loaded && !SoldierIntroMayPlay(lane, next.Request))
            {
                var match = lane.Queue.FindIndex(r => SoldierIntroMayPlay(lane, r));
                if (match >= 0)
                {
                    lane.Queue.Insert(0, next.Request);
                    StopIntroSlot(next);
                }
            }
            if (!next.Loaded && lane.Queue.Count > 0) LoadIntro(lane, next);
        }

        private bool NextIntroReady(IntroLane lane) =>
            lane.Next.Loaded && lane.Next.Player.isPrepared && (!lane.Soldier || SoldierIntroMayPlay(lane, lane.Next.Request));

        /// <summary>
        /// A soldier intro plays with the same viewer's hero intro, or when that side shows no hero intro (and the
        /// viewer's own hero intro is not about to start; it waits for that a moment).
        /// </summary>
        private bool SoldierIntroMayPlay(IntroLane soldierLane, HeroIntroRequest request)
        {
            var heroLane = IntroLaneFor(soldierLane.Blue, false);
            if (heroLane.Showing) return heroLane.Current.Request.User == request.User;
            if (Time.unscaledTime - request.QueuedAt > SoldierIntroPairWait) return true;
            return !HeroIntroComing(heroLane, request.User);
        }

        private bool HeroIntroComing(IntroLane heroLane, string user)
        {
            if (heroLane.Next.Loaded && heroLane.Next.Request.User == user) return true;
            foreach (var queued in heroLane.Queue)
                if (queued.User == user) return true;
            // The gift's hero is spawned (and its intro queued) a little after its soldiers.
            foreach (var reward in _heroRewards)
                if (reward.Pending && reward.User == user && reward.Blue == heroLane.Blue &&
                    !_heroIntrosShown.Contains(user + ":" + reward.Id + ":" + reward.Blue)) return true;
            return false;
        }

        /// <summary>Picks the lane's next intro (for a soldier lane, the one that may play now, if any) and prepares it.</summary>
        private void LoadIntro(IntroLane lane, IntroSlot slot)
        {
            var index = 0;
            if (lane.Soldier)
            {
                var playable = lane.Queue.FindIndex(r => SoldierIntroMayPlay(lane, r));
                if (playable >= 0) index = playable;
            }
            slot.Request = lane.Queue[index];
            lane.Queue.RemoveAt(index);
            slot.Player.clip = slot.Request.Clip;
            var hasAudio = slot.Request.Clip.audioTrackCount > 0;
            slot.Player.controlledAudioTrackCount = (ushort)(hasAudio ? 1 : 0);
            if (hasAudio) slot.Player.SetTargetAudioSource(0, slot.Audio);
            slot.Loaded = true;
            slot.Playing = false;
            slot.EndAt = -1f;
            slot.PreparedAt = Time.unscaledTime;
            slot.Player.Prepare();
        }

        private static void StartNextIntro(IntroLane lane, bool straightOn)
        {
            StopIntroSlot(lane.Current);
            (lane.Current, lane.Next) = (lane.Next, lane.Current);
            var slot = lane.Current;
            slot.Playing = true;
            slot.EndAt = -1f;
            // Following another intro directly: show it at once instead of fading in from nothing.
            slot.ShownAt = Time.unscaledTime - (straightOn ? IntroFadeSeconds : 0f);
            slot.Player.Play();
        }

        private static void StopIntroSlot(IntroSlot slot)
        {
            if (slot.Player != null && (slot.Loaded || slot.Playing)) slot.Player.Stop();
            slot.Loaded = slot.Playing = false;
            slot.EndAt = -1f;
        }

        /// <summary>Creates the lanes and their video players (also called while loading, so the battle does not).</summary>
        private void EnsureHeroIntroPlayer()
        {
            if (_introLanes != null) return;
            _introLanes = new IntroLane[4];
            for (var i = 0; i < 4; i++)
            {
                var lane = new IntroLane { Blue = i < 2, Soldier = i % 2 == 1 };
                lane.Current = CreateIntroSlot(lane, 0);
                lane.Next = CreateIntroSlot(lane, 1);
                _introLanes[i] = lane;
            }
        }

        private IntroSlot CreateIntroSlot(IntroLane lane, int index)
        {
            var root = new GameObject($"Summon intro player ({(lane.Blue ? "blue" : "red")} {(lane.Soldier ? "soldier" : "hero")} {index})");
            root.transform.SetParent(transform, false);
            var slot = new IntroSlot { Audio = root.AddComponent<AudioSource>() };
            slot.Audio.playOnAwake = false;
            slot.Audio.spatialBlend = 0;
            slot.Audio.volume = .8f;
            var player = slot.Player = root.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.isLooping = false;
            player.waitForFirstFrame = true;
            player.skipOnDrop = true;
            player.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            player.renderMode = VideoRenderMode.APIOnly;
            player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            player.loopPointReached += _ =>
            {
                if (slot.Playing && slot.EndAt < 0f) slot.EndAt = Time.unscaledTime;
            };
            player.errorReceived += (_, error) =>
            {
                Debug.LogWarning("Không phát được intro: " + error, this);
                StopIntroSlot(slot);
            };
            return slot;
        }

        private void StopHeroIntros(bool resetUsers)
        {
            if (_introLanes != null)
                foreach (var lane in _introLanes)
                {
                    lane.Queue.Clear();
                    StopIntroSlot(lane.Current);
                    StopIntroSlot(lane.Next);
                }
            if (resetUsers) _heroIntrosShown.Clear();
        }

        private void DrawHeroIntro()
        {
            if (_introLanes == null) return;
            var previousDepth = GUI.depth;
            var previousColor = GUI.color;
            GUI.depth = -50; // Above world labels; fixed HUD remains in front at -100.
            var scale = Mathf.Clamp(Mathf.Min(Screen.width / 1920f, Screen.height / 1080f), .6f, 1f);
            DrawIntroSide(true, scale);
            DrawIntroSide(false, scale);
            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }

        /// <summary>One side's intros in its half of the screen: the hero intro in the corner, the soldier one above it.</summary>
        private void DrawIntroSide(bool blue, float scale)
        {
            var margin = 18f * scale;
            var plate = 68f * scale; // name + title above each video
            var halfWidth = Screen.width * .5f - margin * 1.5f;
            var bottom = Screen.height - margin;
            var hero = _introLanes[blue ? 0 : 2].Current;
            if (IntroVisible(hero))
            {
                var video = IntroRect(hero, blue, bottom, Mathf.Min(1120f * scale, halfWidth), Screen.height * .55f, margin);
                DrawIntroSlot(hero, video, scale);
                bottom = video.y - plate - margin * .5f;
            }
            var soldier = _introLanes[blue ? 1 : 3].Current;
            if (IntroVisible(soldier))
            {
                var video = IntroRect(soldier, blue, bottom, Mathf.Min(560f * scale, halfWidth),
                    Mathf.Min(Screen.height * .3f, bottom - plate - margin), margin);
                if (video.height > 20f) DrawIntroSlot(soldier, video, scale);
            }
        }

        private static bool IntroVisible(IntroSlot slot) => slot.Playing && slot.Player != null && slot.Player.texture != null;

        private static Rect IntroRect(IntroSlot slot, bool blue, float bottom, float maxWidth, float maxHeight, float margin)
        {
            var texture = slot.Player.texture;
            var aspect = texture.width / (float)Mathf.Max(1, texture.height);
            var height = Mathf.Min(maxWidth / aspect, maxHeight);
            var width = height * aspect;
            var x = blue ? Screen.width - margin - width : margin;
            return new Rect(x, bottom - height, width, height);
        }

        private void DrawIntroSlot(IntroSlot slot, Rect video, float scale)
        {
            var now = Time.unscaledTime;
            var alpha = Mathf.Clamp01((now - slot.ShownAt) / IntroFadeSeconds);
            if (slot.EndAt >= 0) alpha *= 1f - Mathf.Clamp01((now - slot.EndAt) / IntroFadeSeconds);
            GUI.color = Color.white;
            if (_heroIntroEdgeMaterial == null)
            {
                var shader = Resources.Load<Shader>("HeroIntro/EdgeFade");
                if (shader != null && shader.isSupported)
                    _heroIntroEdgeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_heroIntroEdgeMaterial != null && Event.current.type == EventType.Repaint)
            {
                _heroIntroEdgeMaterial.SetFloat("_Opacity", alpha);
                Graphics.DrawTexture(video, slot.Player.texture, _heroIntroEdgeMaterial);
            }
            else if (_heroIntroEdgeMaterial == null)
            {
                GUI.color = new Color(1, 1, 1, alpha);
                GUI.DrawTexture(video, slot.Player.texture, ScaleMode.ScaleToFit, true);
            }
            var name = new Rect(video.x, video.y - 68f * scale, video.width, 32f * scale);
            EnsureEndgameNameGradient();
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.DrawTexture(new Rect(name.x, name.y, name.width, 64f * scale), _endgameNameGradient, ScaleMode.StretchToFill, true);
            // A dark plate over the gradient so the viewer's name reads clearly on any video or battlefield.
            GUI.DrawTexture(new Rect(name.x, name.y - 4f * scale, name.width, 70f * scale), Texture2D.whiteTexture,
                ScaleMode.StretchToFill, true, 0, new Color(0f, 0f, 0f, .62f * alpha), 0, 10f * scale);
            _heroIntroUserStyle ??= new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = false, clipping = TextClipping.Clip };
            _heroIntroTitleStyle ??= new GUIStyle(_heroIntroUserStyle);

            _heroIntroUserStyle.fontSize = Mathf.Max(18, Mathf.RoundToInt(25f * scale));
            _heroIntroTitleStyle.fontSize = Mathf.Max(16, Mathf.RoundToInt(25f * scale));
            SetLabelTextColor(_heroIntroTitleStyle, Color.white);
            DrawOutlinedNameLabel(name, slot.Request.Name, _heroIntroUserStyle, scale,
                UserNameColor(slot.Request.Name, slot.Request.Blue));
            DrawHighlightedLabel(new Rect(name.x, name.yMax, name.width, 30 * scale), slot.Request.Title,
                _heroIntroTitleStyle, scale);
        }
    }
}
