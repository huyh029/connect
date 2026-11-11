using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Video;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [SerializeField] private VideoClip[] heroIntroClips = new VideoClip[8];
        private struct HeroIntroRequest { public string User, Name; public int HeroId; public bool Blue; }
        private readonly Queue<HeroIntroRequest> _heroIntroQueue = new();
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
                { User = user, Name = string.IsNullOrWhiteSpace(displayName) ? user : displayName, HeroId = heroId, Blue = blue });
        }

        private void UpdateHeroIntros()
        {
            if (_matchFinished || !_lobbyStarted || _initialFormationLoading) return;
            if (!_heroIntroPending)
            {
                if (_heroIntroQueue.Count == 0) return;
                EnsureHeroIntroPlayer();
                _currentHeroIntro = _heroIntroQueue.Dequeue();
                _heroIntroPlayer.clip = heroIntroClips[_currentHeroIntro.HeroId - 1];
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
            var width = Mathf.Min(560f * scale, Screen.width * .38f);
            var aspect = _heroIntroPlayer.texture.width / (float)Mathf.Max(1, _heroIntroPlayer.texture.height);
            var height = Mathf.Min(width / aspect, Screen.height * .42f);
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
            _heroIntroUserStyle ??= new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = false, clipping = TextClipping.Clip };
            _heroIntroTitleStyle ??= new GUIStyle(_heroIntroUserStyle);

            _heroIntroUserStyle.fontSize = Mathf.Max(18, Mathf.RoundToInt(25f * scale));
            _heroIntroTitleStyle.fontSize = Mathf.Max(16, Mathf.RoundToInt(25f * scale));
            _heroIntroUserStyle.normal.textColor = new Color(1, .84f, .3f);
            _heroIntroTitleStyle.normal.textColor = Color.white;
            DrawHighlightedLabel(name, _currentHeroIntro.Name, _heroIntroUserStyle, scale);
            var heroName = HeroLabels[_currentHeroIntro.HeroId - 1];
            DrawHighlightedLabel(new Rect(name.x, name.yMax, name.width, 30 * scale),
                "TRIỆU HỒI " + heroName.ToUpperInvariant() + (_currentHeroIntro.Blue ? " • PHE XANH" : " • PHE ĐỎ"), _heroIntroTitleStyle, scale);
            GUI.color = previousColor;
            GUI.depth = previousDepth;
        }
    }
}
