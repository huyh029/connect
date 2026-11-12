using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Explosion test ground (Game Tools > Công Thành > Battle > Test hiệu ứng > "Sân test nổ"): soldiers stand in rings
    /// around a point; a siege stone blast plays the battle's explosion effect and sound and tosses them with the
    /// battle's own toss rule (Resources/RecoilBattleEffects), optionally killing them in the air. The endgame meteor
    /// explosion (bigger effect, louder sound) can be tried too. Changes made to that asset in Play Mode are kept.
    /// </summary>
    public sealed class RecoilExplosionTestGround : MonoBehaviour
    {
        public GameObject soldierPrefab;
        [Range(1, 80)] public int soldierCount = 30;
        [Min(.5f), Tooltip("Bán kính vòng lính ngoài cùng")] public float formationRadius = 5f;
        [Min(.5f), Tooltip("Bán kính nổ của đá (trong trận lấy theo chỉ số của máy bắn đá)")] public float blastRadius = 4f;
        [Tooltip("Lính trúng nổ chết luôn (vừa bay vừa chết)")] public bool killOnBlast;
        public bool autoRepeat;
        [Min(.5f)] public float repeatSeconds = 3f;
        [Min(.1f), Tooltip("Thiên thạch cuối trận to gấp chừng này (như trong trận)")] public float endgameScale = 3f;
        [Min(.05f), Tooltip("Bán kính thân lính để tính khoảng cách tới mép")] public float bodyRadius = .5f;

        private sealed class Dummy
        {
            public GameObject Object;
            public RecoilVatAnimation Vat;
            public Vector3 Home, Start, Horizontal;
            public float Height, Duration, Started = -1f;
            public bool Dead;
        }

        public struct Event
        {
            public float Time;
            public string Text;
        }

        private readonly List<Dummy> _dummies = new();
        private readonly List<Event> _log = new();
        private readonly System.Random _random = new(7919);
        private GameObject _impact;
        private ParticleSystem[] _impactParticles;
        private AudioSource _audio;
        private float _nextRepeat;
        private Vector3 _lastBlast;

        public IReadOnlyList<Event> Log => _log;
        public Vector3 LastBlast => _lastBlast;
        private static RecoilBattleEffects Settings => RecoilBattleEffects.Load();

        private void Start()
        {
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;
            var settings = Settings;
            if (settings != null && settings.ExplosionSound != null) settings.ExplosionSound.LoadAudioData();
            Rebuild();
        }

        /// <summary>(Re)places the soldiers in rings around the centre, alive and standing.</summary>
        public void Rebuild()
        {
            foreach (var dummy in _dummies) if (dummy.Object != null) Destroy(dummy.Object);
            _dummies.Clear();
            if (soldierPrefab == null) return;
            // Rings of growing radius, each holding as many soldiers as fit around it (about 1.6 m apart).
            var placed = 0;
            for (var ring = 0; placed < soldierCount; ring++)
            {
                var radius = ring == 0 ? 0f : formationRadius * ring / Mathf.Max(1f, Mathf.Ceil(formationRadius / 1.6f));
                if (ring > 0 && radius > formationRadius + .01f) radius = formationRadius;
                var fit = ring == 0 ? 1 : Mathf.Max(1, Mathf.FloorToInt(2f * Mathf.PI * radius / 1.6f));
                for (var i = 0; i < fit && placed < soldierCount; i++, placed++)
                {
                    var angle = (i + .5f * (ring % 2)) * Mathf.PI * 2f / fit;
                    var position = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                    var facing = radius > 0f ? -position.normalized : Vector3.back;
                    var instance = Instantiate(soldierPrefab, position, Quaternion.LookRotation(facing));
                    var vat = instance.GetComponentInChildren<RecoilVatAnimation>(true);
                    if (vat != null) vat.Play(RecoilVatAnimation.AnimationState.Idle);
                    _dummies.Add(new Dummy { Object = instance, Vat = vat, Home = position });
                }
                if (ring > 50) break;
            }
            _log.Clear();
        }

        /// <summary>Everyone back on their spot, standing.</summary>
        public void ResetSoldiers()
        {
            foreach (var dummy in _dummies)
            {
                if (dummy.Object == null) continue;
                dummy.Object.transform.position = dummy.Home;
                dummy.Started = -1f;
                dummy.Dead = false;
                if (dummy.Vat != null) dummy.Vat.Play(RecoilVatAnimation.AnimationState.Idle);
            }
            Note("↺ Đặt lại lính");
        }

        /// <summary>A siege stone lands at <paramref name="point"/>: effect, sound, toss.</summary>
        public void SiegeBlast(Vector3 point)
        {
            var settings = Settings;
            _lastBlast = point;
            PlayImpact(point, 1f);
            if (settings != null && settings.ExplosionSound != null)
                _audio.PlayOneShot(settings.ExplosionSound, settings.SiegeExplosionVolume);
            var tossed = 0;
            if (settings != null)
                foreach (var dummy in _dummies)
                {
                    if (dummy.Object == null || dummy.Dead) continue;
                    var position = dummy.Object.transform.position;
                    var surface = Mathf.Max(0f, Vector3.Distance(position, point) - bodyRadius);
                    if (!settings.SiegeToss(surface, blastRadius, position - point, _random,
                            out var height, out var horizontal, out var duration)) continue;
                    dummy.Start = position;
                    dummy.Horizontal = horizontal;
                    dummy.Height = height;
                    dummy.Duration = duration;
                    dummy.Started = Time.time;
                    tossed++;
                    if (killOnBlast)
                    {
                        dummy.Dead = true;
                        if (dummy.Vat != null) dummy.Vat.Play(RecoilVatAnimation.AnimationState.Death);
                    }
                }
            Note($"💥 Nổ đá (bán kính {blastRadius:0.#}) — hất {tossed} lính{(killOnBlast ? ", chết luôn" : "")}");
        }

        /// <summary>A stone at a random spot near the centre.</summary>
        public void SiegeBlastRandom()
        {
            var offset = Random.insideUnitCircle * formationRadius * .6f;
            SiegeBlast(new Vector3(offset.x, 0f, offset.y));
        }

        /// <summary>The endgame meteor explosion: the same effect, bigger, and the louder sound (no toss).</summary>
        public void EndgameBlast()
        {
            var settings = Settings;
            PlayImpact(Vector3.zero, endgameScale);
            if (settings != null && settings.ExplosionSound != null)
                _audio.PlayOneShot(settings.ExplosionSound, settings.EndgameExplosionVolume);
            Note("☄ Nổ thiên thạch cuối trận");
        }

        private void PlayImpact(Vector3 point, float scale)
        {
            var prefab = Settings != null ? Settings.SiegeImpactPrefab : null;
            if (prefab == null) return;
            if (_impact == null) _impact = RecoilBattleEffects.CreateOneShot(prefab, transform, out _impactParticles);
            RecoilBattleEffects.PlayOnce(_impact, _impactParticles, prefab, point, scale);
        }

        private void Update()
        {
            foreach (var dummy in _dummies)
            {
                if (dummy.Object == null || dummy.Started < 0f) continue;
                var t = Mathf.Clamp01((Time.time - dummy.Started) / Mathf.Max(.05f, dummy.Duration));
                dummy.Object.transform.position = dummy.Start + dummy.Horizontal * t +
                    Vector3.up * HeroSkillTimeline.AirborneHeight(t, dummy.Height);
                if (t >= 1f) dummy.Started = -1f;
            }
            if (autoRepeat && Time.time >= _nextRepeat)
            {
                _nextRepeat = Time.time + repeatSeconds;
                if (killOnBlast) ResetSoldiers();
                SiegeBlastRandom();
            }
            if (_impact != null && _impact.activeSelf)
            {
                var alive = false;
                foreach (var particle in _impactParticles) alive |= particle.IsAlive(false);
                if (!alive) _impact.SetActive(false);
            }
        }

        private void Note(string text)
        {
            _log.Add(new Event { Time = Time.time, Text = text });
            if (_log.Count > 30) _log.RemoveAt(0);
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, .4f, .1f);
            Gizmos.DrawWireSphere(_lastBlast, blastRadius);
        }
    }
}
