using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Quân sư test ground (Game Tools > Công Thành > Battle > Test hiệu ứng > "Sân test Quân sư"): one advisor and
    /// one target face each other on a flat ground; the advisor strikes again and again with the battle's effects,
    /// timing and offsets from Resources/RecoilBattleEffects, so they can be tuned while watching. Changes made to
    /// that asset in Play Mode are kept.
    /// </summary>
    public sealed class RecoilAdvisorTestGround : MonoBehaviour
    {
        public GameObject advisorPrefab;
        public GameObject targetPrefab;
        [Min(.5f)] public float distance = 3f;
        [Min(0f), Tooltip("Nghỉ giữa hai đòn (giây)")] public float pause = .6f;
        public bool autoRepeat = true;
        public bool playSummonOnStart = true;

        public struct Event
        {
            public float Time;
            public string Text;
        }

        private RecoilVatAnimation _advisor, _target;
        private GameObject _advisorObject, _targetObject;
        private float _strikeStarted = float.NegativeInfinity;
        private bool _hitPlayed = true;
        private float _nextStrike;
        private readonly List<(GameObject Object, ParticleSystem[] Particles, int Kind)> _effects = new();
        private readonly List<Event> _log = new();

        public IReadOnlyList<Event> Log => _log;
        public float AttackDuration => _advisor != null ? _advisor.AttackDuration : 0f;
        public float HitDelay => RecoilAdvisorOrbs.HitDelay(Settings, AttackDuration);
        public float LaunchDelay => AttackDuration * Mathf.Clamp01(Settings != null ? Settings.AdvisorOrbLaunch : .45f);
        private RecoilAdvisorOrbs _orbs;
        private RecoilAdvisorOrbs Orbs => _orbs ??= new RecoilAdvisorOrbs(transform, 24);
        /// <summary>Seconds since the current strike started (negative before the first one).</summary>
        public float StrikeTime => Time.time - _strikeStarted;
        public bool Ready => _advisor != null;
        private static RecoilBattleEffects Settings => RecoilBattleEffects.Load();

        private void Start() => Rebuild();

        /// <summary>(Re)creates the advisor and the target at the current distance.</summary>
        public void Rebuild()
        {
            if (_advisorObject != null) Destroy(_advisorObject);
            if (_targetObject != null) Destroy(_targetObject);
            _advisor = Spawn(advisorPrefab, Vector3.zero, Vector3.forward, out _advisorObject);
            _target = Spawn(targetPrefab, Vector3.forward * distance, Vector3.back, out _targetObject);
            _log.Clear();
            _strikeStarted = float.NegativeInfinity;
            _hitPlayed = true;
            _nextStrike = Time.time + (playSummonOnStart ? 1.5f : .3f);
            if (playSummonOnStart && _advisorObject != null) PlaySummon();
        }

        private RecoilVatAnimation Spawn(GameObject prefab, Vector3 position, Vector3 facing, out GameObject instance)
        {
            instance = null;
            if (prefab == null) return null;
            instance = Instantiate(prefab, position, Quaternion.LookRotation(facing));
            var vat = instance.GetComponentInChildren<RecoilVatAnimation>(true);
            if (vat != null) vat.Play(RecoilVatAnimation.AnimationState.Idle);
            return vat;
        }

        /// <summary>Moves the target to <see cref="distance"/> in front of the advisor.</summary>
        public void ApplyDistance()
        {
            if (_targetObject != null) _targetObject.transform.position = Vector3.forward * distance;
        }

        public void PlaySummon()
        {
            if (_advisorObject == null) return;
            Play(0, _advisorObject.transform.position);
            Note("✧ Triệu hồi (vòng phép)");
        }

        /// <summary>One strike now: cast effect, attack animation, hit effect when the blow lands.</summary>
        public void Strike()
        {
            if (_advisor == null) return;
            var settings = Settings;
            _advisor.Play(RecoilVatAnimation.AnimationState.Attack);
            _strikeStarted = Time.time;
            _hitPlayed = false;
            var feet = _advisorObject.transform.position;
            if (settings != null) Play(1, RecoilBattleEffects.Place(feet, _advisorObject.transform.forward, settings.AdvisorCastOffset));
            var target = _targetObject;
            Orbs.Begin(_advisorObject.transform, () => target != null ? target.transform.position : (Vector3?)null,
                target != null ? target.transform.position : feet + _advisorObject.transform.forward * distance,
                Time.time, AttackDuration, () => _advisor != null, (where, direction) =>
                {
                    var s = Settings;
                    if (s != null) Play(2, RecoilBattleEffects.Place(where, direction, s.AdvisorHitOffset));
                    Note("✦ Cầu tới mục tiêu — thiêu");
                });
            Note($"⚔ Ra đòn (hoạt ảnh {AttackDuration:0.00}s, phóng cầu {LaunchDelay:0.00}s, tới nơi {HitDelay:0.00}s)");
            _launchNoted = false;
        }

        private bool _launchNoted = true;

        private void Update()
        {
            if (_advisor == null) return;
            Orbs.Update(Time.time);
            if (!_launchNoted && StrikeTime >= LaunchDelay)
            {
                _launchNoted = true;
                Note("➶ Phóng cầu");
            }
            if (_strikeStarted > float.NegativeInfinity && StrikeTime >= AttackDuration &&
                _advisor.State == RecoilVatAnimation.AnimationState.Attack)
            {
                _advisor.Play(RecoilVatAnimation.AnimationState.Idle);
                _nextStrike = Time.time + pause;
            }
            if (autoRepeat && _advisor.State != RecoilVatAnimation.AnimationState.Attack && Time.time >= _nextStrike)
                Strike();

            foreach (var effect in _effects)
            {
                if (!effect.Object.activeSelf) continue;
                var alive = false;
                foreach (var particle in effect.Particles) alive |= particle.IsAlive(false);
                if (!alive) effect.Object.SetActive(false);
            }
        }

        private void Play(int kind, Vector3 position)
        {
            var settings = Settings;
            var prefab = settings != null ? settings.AdvisorPrefab(kind) : null;
            if (prefab == null) return;
            (GameObject Object, ParticleSystem[] Particles, int Kind) slot = default;
            var found = false;
            foreach (var effect in _effects)
                if (effect.Kind == kind && !effect.Object.activeSelf) { slot = effect; found = true; break; }
            if (!found)
            {
                var instance = RecoilBattleEffects.CreateOneShot(prefab, transform, out var particles);
                if (kind == 1) settings.HideCastParts(instance); // its orbs fly instead
                slot = (instance, particles, kind);
                _effects.Add(slot);
            }
            RecoilBattleEffects.PlayOnce(slot.Object, slot.Particles, prefab, position, settings.AdvisorScale(kind));
        }

        /// <summary>Drops the effect copies so the next ones are made from the (possibly changed) prefabs.</summary>
        public void ResetEffects()
        {
            foreach (var effect in _effects) if (effect.Object != null) Destroy(effect.Object);
            _effects.Clear();
            _orbs?.Clear();
        }

        private void Note(string text)
        {
            _log.Add(new Event { Time = Time.time, Text = text });
            if (_log.Count > 30) _log.RemoveAt(0);
        }

        private void OnDrawGizmos()
        {
            if (_advisorObject == null || _targetObject == null) return;
            Gizmos.color = new Color(1f, .3f, .2f);
            Gizmos.DrawLine(_advisorObject.transform.position + Vector3.up * .2f, _targetObject.transform.position + Vector3.up * .2f);
        }
    }
}
