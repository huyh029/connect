using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [SerializeField] private VideoClip[] heroIntroClips = new VideoClip[8];
        [SerializeField, Tooltip("Intro khi quà TikTok gọi lính, theo loại lính (thứ tự Blue Units By Type), phe xanh.")]
        private VideoClip[] soldierIntroBlueClips = new VideoClip[CharacterTypeCount];
        [SerializeField, Tooltip("Intro khi quà TikTok gọi lính, theo loại lính, phe đỏ.")]
        private VideoClip[] soldierIntroRedClips = new VideoClip[CharacterTypeCount];
        // Soldier intros wait behind at most this many queued soldier intros; beyond that they are skipped.
        private const int MaxQueuedSoldierIntros = 2;
        private struct HeroIntroRequest
        {
            public string User, Name, Title;
            public VideoClip Clip;
            public int HeroId;
            public bool Blue, Soldier;
        }
        // Heroes always go first; soldier intros only play when no hero intro is waiting, so they never cover one.
        private readonly Queue<HeroIntroRequest> _heroIntroQueue = new();
        private readonly Queue<HeroIntroRequest> _soldierIntroQueue = new();
        private readonly HashSet<string> _heroIntrosShown = new();
        private VideoPlayer _heroIntroPlayer;
        private Material _heroIntroEdgeMaterial;
        private AudioSource _heroIntroAudio;
        private HeroIntroRequest _currentHeroIntro;
        private bool _heroIntroPending, _heroIntroStarted;
        private float _heroIntroPreparedAt, _heroIntroShownAt, _heroIntroEndAt = -1f;
        private GUIStyle _heroIntroUserStyle, _heroIntroTitleStyle;

        private void QueueHeroSummonIntro(string user, string displayName, int heroId, bool blue)
        {
            if (_matchFinished || string.IsNullOrWhiteSpace(user) || heroId < 1 || heroId > heroIntroClips.Length ||
                heroIntroClips[heroId - 1] == null) return;
            var key = user + ":" + heroId + ":" + blue;
            if (!_heroIntrosShown.Add(key)) return;
            _heroIntroQueue.Enqueue(new HeroIntroRequest
            {
                User = user, Name = string.IsNullOrWhiteSpace(displayName) ? user : displayName, HeroId = heroId, Blue = blue,
                Clip = heroIntroClips[heroId - 1],
                Title = "TRIỆU HỒI " + HeroLabels[heroId - 1].ToUpperInvariant() + (blue ? " • PHE XANH" : " • PHE ĐỎ")
            });
        }

        /// <summary>
        /// A TikTok gift called soldiers of <paramref name="type"/>: that unit's intro, once per viewer, type and side.
        /// Smaller than a hero intro and played only when no hero intro is waiting.
        /// </summary>
        private void QueueSoldierSummonIntro(string user, string displayName, int type, bool blue)
        {
            var clips = blue ? soldierIntroBlueClips : soldierIntroRedClips;
            if (_matchFinished || string.IsNullOrWhiteSpace(user) || clips == null || type < 0 || type >= clips.Length ||
                clips[type] == null || _soldierIntroQueue.Count >= MaxQueuedSoldierIntros) return;
            if (!_heroIntrosShown.Add("unit:" + user + ":" + type + ":" + blue)) return;
            _soldierIntroQueue.Enqueue(new HeroIntroRequest
            {
                User = user, Name = string.IsNullOrWhiteSpace(displayName) ? user : displayName, Blue = blue,
                Soldier = true, Clip = clips[type],
                Title = "TRIỆU HỒI " + CharacterTypeLabels[type].ToUpperInvariant() + (blue ? " • PHE XANH" : " • PHE ĐỎ")
            });
        }

        private void UpdateHeroIntros()
        {
            if (_matchFinished || !_lobbyStarted || _initialFormationLoading) return;
            if (!_heroIntroPending)
            {
                if (_heroIntroQueue.Count == 0 && _soldierIntroQueue.Count == 0) return;
                EnsureHeroIntroPlayer();
                _currentHeroIntro = _heroIntroQueue.Count > 0 ? _heroIntroQueue.Dequeue() : _soldierIntroQueue.Dequeue();
                _heroIntroPlayer.clip = _currentHeroIntro.Clip;
                var hasAudio = _heroIntroPlayer.clip.audioTrackCount > 0;
                _heroIntroPlayer.controlledAudioTrackCount = (ushort)(hasAudio ? 1 : 0);
                if (hasAudio) _heroIntroPlayer.SetTargetAudioSource(0, _heroIntroAudio);
                _heroIntroPending = true;
                _heroIntroStarted = false;
                _heroIntroEndAt = -1;
                _heroIntroPreparedAt = Time.unscaledTime;
                _heroIntroPlayer.Prepare();
            }
            if (!_heroIntroStarted)
            {
                if (_heroIntroPlayer.isPrepared)
                {
                    _heroIntroStarted = true;
                    _heroIntroShownAt = Time.unscaledTime;
                    _heroIntroPlayer.Play();
                }
                else if (Time.unscaledTime - _heroIntroPreparedAt > 15f) FinishHeroIntro();
                return;
            }
            if (_heroIntroEndAt >= 0 && Time.unscaledTime - _heroIntroEndAt >= .25f) FinishHeroIntro();
            else if (Time.unscaledTime - _heroIntroShownAt > Mathf.Max(10f, (float)_heroIntroPlayer.clip.length + 5f)) FinishHeroIntro();
        }

        private void EnsureHeroIntroPlayer()
        {
            if (_heroIntroPlayer != null) return;
            var root = new GameObject("Hero summon intro player");
            root.transform.SetParent(transform, false);
            _heroIntroAudio = root.AddComponent<AudioSource>();
            _heroIntroAudio.playOnAwake = false;
            _heroIntroAudio.spatialBlend = 0;
            _heroIntroAudio.volume = .8f;
            _heroIntroPlayer = root.AddComponent<VideoPlayer>();
            _heroIntroPlayer.playOnAwake = false;
            _heroIntroPlayer.isLooping = false;
            _heroIntroPlayer.waitForFirstFrame = true;
            _heroIntroPlayer.skipOnDrop = true;
            _heroIntroPlayer.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            _heroIntroPlayer.renderMode = VideoRenderMode.APIOnly;
            _heroIntroPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _heroIntroPlayer.loopPointReached += OnHeroIntroEnded;
            _heroIntroPlayer.errorReceived += OnHeroIntroError;
        }

        private void OnHeroIntroEnded(VideoPlayer player) => _heroIntroEndAt = Time.unscaledTime;
        private void OnHeroIntroError(VideoPlayer player, string error)
        {
            Debug.LogWarning("Không phát được intro tướng: " + error, this);
            FinishHeroIntro();
        }
        private void FinishHeroIntro()
        {
            if (_heroIntroPlayer != null) _heroIntroPlayer.Stop();
            _heroIntroPending = _heroIntroStarted = false;
            _heroIntroEndAt = -1;
        }
        private void StopHeroIntros(bool resetUsers)
        {
            _heroIntroQueue.Clear();
            _soldierIntroQueue.Clear();
            FinishHeroIntro();
            if (resetUsers) _heroIntrosShown.Clear();
        }

        private void DrawHeroIntro()
        {
            if (!_heroIntroPending || !_heroIntroStarted || _heroIntroPlayer == null || _heroIntroPlayer.texture == null) return;
            var previousDepth = GUI.depth;
            var previousColor = GUI.color;
            GUI.depth = -50; // Above world labels; fixed HUD remains in front at -100.
            var alpha = Mathf.Clamp01((Time.unscaledTime - _heroIntroShownAt) / .25f);
            if (_heroIntroEndAt >= 0) alpha *= 1f - Mathf.Clamp01((Time.unscaledTime - _heroIntroEndAt) / .25f);
            var scale = Mathf.Clamp(Mathf.Min(Screen.width / 1920f, Screen.height / 1080f), .6f, 1f);
            var margin = 18f * scale;
            var size = _currentHeroIntro.Soldier ? 1f : 2f; // hero intros are twice the soldier ones
            var width = Mathf.Min(560f * scale * size, Screen.width * .38f * size);
            var aspect = _heroIntroPlayer.texture.width / (float)Mathf.Max(1, _heroIntroPlayer.texture.height);
            var height = Mathf.Min(width / aspect, Screen.height * Mathf.Min(.42f * size, .8f));
            width = Mathf.Min(width, height * aspect);
            var footer = 0f;
            var x = _currentHeroIntro.Blue ? Screen.width - margin - width : margin;
            var video = new Rect(x, Screen.height - margin - footer - height, width, height);
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
                Graphics.DrawTexture(video, _heroIntroPlayer.texture, _heroIntroEdgeMaterial);
            }
            else if (_heroIntroEdgeMaterial == null)
            {
                GUI.color = new Color(1, 1, 1, alpha);
                GUI.DrawTexture(video, _heroIntroPlayer.texture, ScaleMode.ScaleToFit, true);
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
            _heroIntroUserStyle.normal.textColor = UserNameColor(_currentHeroIntro.Name);
            _heroIntroTitleStyle.normal.textColor = Color.white;
            DrawHighlightedLabel(name, _currentHeroIntro.Name, _heroIntroUserStyle, scale);
            DrawHighlightedLabel(new Rect(name.x, name.yMax, name.width, 30 * scale), _currentHeroIntro.Title,
                _heroIntroTitleStyle, scale);
            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }
    }
}
