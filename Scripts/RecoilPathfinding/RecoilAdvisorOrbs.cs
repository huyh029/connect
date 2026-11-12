using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// The Quân sư's orbs: they appear around the caster and circle it while it winds up, then fly to the target
    /// (following it if it moves) and, when they arrive, the hit effect plays there. Built from one child of the cast
    /// effect prefab (a glow), re-set to a world-space comet so the orbs leave a trail. Shared by the battle and the
    /// Quân sư test ground; the time base is passed in (battle seconds or Time.time).
    /// </summary>
    public sealed class RecoilAdvisorOrbs
    {
        private sealed class Orb
        {
            public GameObject Object;
            public ParticleSystem[] Particles;
            public bool Busy;
        }

        private sealed class Volley
        {
            public Transform Caster;
            public Func<Vector3?> Target;
            public Vector3 CasterPosition, TargetPosition;
            public float Start, Launch, Arrive;
            public float Phase;
            public readonly List<Orb> Orbs = new();
            public Vector3[] LaunchPoints;
            public Func<bool> CasterAlive;
            public Action<Vector3, Vector3> OnArrive; // where, direction of travel
            public bool Launched;
        }

        private readonly Transform _parent;
        private readonly List<Orb> _orbs = new();
        private readonly List<Volley> _volleys = new();
        private readonly int _maxOrbs;
        private GameObject _template;
        private RecoilBattleEffects _builtFrom;
        private string _builtChild;

        public RecoilAdvisorOrbs(Transform parent, int maxOrbs)
        {
            _parent = parent;
            _maxOrbs = Mathf.Max(0, maxOrbs);
        }

        public int ActiveVolleys => _volleys.Count;

        /// <summary>Seconds from the start of the strike until the orbs reach the target.</summary>
        public static float HitDelay(RecoilBattleEffects settings, float attackDuration) =>
            settings == null ? 0f : attackDuration * Mathf.Clamp01(settings.AdvisorOrbLaunch) + Mathf.Max(0f, settings.AdvisorOrbFlight);

        /// <summary>
        /// Starts a volley now: orbs circle <paramref name="caster"/> until the launch moment of the
        /// <paramref name="attackDuration"/>-long strike, then fly to where <paramref name="target"/> says the target
        /// is (its last known place, starting at <paramref name="targetPosition"/>, once it returns null) and call
        /// <paramref name="onArrive"/> there.
        /// </summary>
        public void Begin(Transform caster, Func<Vector3?> target, Vector3 targetPosition, float now, float attackDuration,
            Func<bool> casterAlive, Action<Vector3, Vector3> onArrive)
        {
            var settings = RecoilBattleEffects.Load();
            if (settings == null || caster == null) return;
            var volley = new Volley
            {
                Caster = caster, Target = target, CasterPosition = caster.position, TargetPosition = targetPosition,
                Start = now, Launch = now + attackDuration * Mathf.Clamp01(settings.AdvisorOrbLaunch),
                Phase = UnityEngine.Random.value * 360f, CasterAlive = casterAlive, OnArrive = onArrive
            };
            volley.Arrive = volley.Launch + Mathf.Max(0f, settings.AdvisorOrbFlight);
            for (var i = 0; i < Mathf.Max(0, settings.AdvisorOrbCount); i++)
            {
                var orb = TakeOrb(settings);
                if (orb == null) break; // all busy: fewer orbs, the hit still lands on time
                volley.Orbs.Add(orb);
            }
            volley.LaunchPoints = new Vector3[volley.Orbs.Count];
            for (var i = 0; i < volley.Orbs.Count; i++)
            {
                var orb = volley.Orbs[i];
                orb.Object.transform.position = OrbitPoint(settings, volley, i, now);
                orb.Object.transform.localScale = Vector3.one * Mathf.Max(.05f, settings.AdvisorOrbSize);
                orb.Object.SetActive(true);
                foreach (var particle in orb.Particles) { particle.Clear(false); particle.Play(false); }
            }
            _volleys.Add(volley);
        }

        public void Update(float now)
        {
            var settings = RecoilBattleEffects.Load();
            for (var v = _volleys.Count - 1; v >= 0; v--)
            {
                var volley = _volleys[v];
                if (volley.Caster != null) volley.CasterPosition = volley.Caster.position;
                var seen = volley.Target?.Invoke();
                if (seen.HasValue) volley.TargetPosition = seen.Value;
                if (!volley.Launched && volley.CasterAlive != null && !volley.CasterAlive())
                {
                    // The caster fell before throwing: the orbs fade where they are.
                    Release(volley);
                    _volleys.RemoveAt(v);
                    continue;
                }
                if (now < volley.Launch)
                {
                    for (var i = 0; i < volley.Orbs.Count; i++)
                        volley.Orbs[i].Object.transform.position = OrbitPoint(settings, volley, i, now);
                    continue;
                }
                if (!volley.Launched)
                {
                    volley.Launched = true;
                    for (var i = 0; i < volley.Orbs.Count; i++)
                        volley.LaunchPoints[i] = volley.Orbs[i].Object.transform.position;
                }
                var flight = Mathf.Max(.001f, volley.Arrive - volley.Launch);
                var u = Mathf.Clamp01((now - volley.Launch) / flight);
                var aim = volley.TargetPosition + Vector3.up * settings.AdvisorOrbTargetHeight;
                var eased = u * u * (3f - 2f * u);
                for (var i = 0; i < volley.Orbs.Count; i++)
                    volley.Orbs[i].Object.transform.position = Vector3.Lerp(volley.LaunchPoints[i], aim, eased) +
                        Vector3.up * (Mathf.Sin(u * Mathf.PI) * settings.AdvisorOrbArc);
                if (u < 1f) continue;
                var direction = volley.TargetPosition - volley.CasterPosition;
                volley.OnArrive?.Invoke(volley.TargetPosition, direction);
                Release(volley);
                _volleys.RemoveAt(v);
            }
            // Orbs that were let go stay visible until their trail has faded.
            foreach (var orb in _orbs)
            {
                if (orb.Busy || !orb.Object.activeSelf) continue;
                var alive = false;
                foreach (var particle in orb.Particles) alive |= particle.IsAlive(false);
                if (!alive) orb.Object.SetActive(false);
            }
        }

        private static Vector3 OrbitPoint(RecoilBattleEffects settings, Volley volley, int index, float now)
        {
            var count = Mathf.Max(1, volley.Orbs.Count);
            var angle = (volley.Phase + index * 360f / count + settings.AdvisorOrbSpin * (now - volley.Start)) * Mathf.Deg2Rad;
            // They grow out from the caster during the first moments.
            var grow = Mathf.Clamp01((now - volley.Start) / .25f);
            var radius = settings.AdvisorOrbRadius * grow;
            return volley.CasterPosition + new Vector3(Mathf.Cos(angle) * radius, settings.AdvisorOrbHeight * (.6f + .4f * grow),
                Mathf.Sin(angle) * radius);
        }

        private void Release(Volley volley)
        {
            foreach (var orb in volley.Orbs)
            {
                orb.Busy = false;
                foreach (var particle in orb.Particles) particle.Stop(false, ParticleSystemStopBehavior.StopEmitting);
            }
            volley.Orbs.Clear();
        }

        private Orb TakeOrb(RecoilBattleEffects settings)
        {
            if (!EnsureTemplate(settings)) return null;
            foreach (var orb in _orbs)
                if (!orb.Busy && !orb.Object.activeSelf) { orb.Busy = true; return orb; }
            if (_orbs.Count >= _maxOrbs) return null;
            var instance = UnityEngine.Object.Instantiate(_template, _parent);
            instance.SetActive(false);
            var created = new Orb { Object = instance, Particles = instance.GetComponentsInChildren<ParticleSystem>(true), Busy = true };
            _orbs.Add(created);
            return created;
        }

        /// <summary>The orb look: the cast prefab's glow child, made a looping world-space comet with a trail.</summary>
        private bool EnsureTemplate(RecoilBattleEffects settings)
        {
            var source = settings.AdvisorOrbSource != null ? settings.AdvisorOrbSource : settings.AdvisorCastPrefab;
            if (source == null) return false;
            if (_template != null && _builtFrom == settings && _builtChild == settings.AdvisorOrbChild) return true;
            Clear();
            var child = string.IsNullOrEmpty(settings.AdvisorOrbChild) ? null : FindChild(source.transform, settings.AdvisorOrbChild);
            var glowSource = child != null ? child.gameObject : source;
            _template = UnityEngine.Object.Instantiate(glowSource, _parent);
            _template.name = "Quân sư orb";
            _template.SetActive(false);
            _template.transform.localRotation = Quaternion.identity;
            foreach (var particle in _template.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particle.main;
                main.loop = true;
                main.playOnAwake = false;
                main.duration = 1f;
                main.startDelay = 0f;
                main.startSpeed = 0f;
                main.startLifetime = Mathf.Max(.05f, settings.AdvisorOrbTrail);
                main.startSize = new ParticleSystem.MinMaxCurve(.75f, 1f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.maxParticles = 64;
                var emission = particle.emission;
                emission.enabled = true;
                emission.SetBursts(Array.Empty<ParticleSystem.Burst>());
                emission.rateOverTime = 40f;
                emission.rateOverDistance = 6f;
                var shape = particle.shape;
                shape.enabled = false;
                var velocity = particle.velocityOverLifetime;
                velocity.enabled = false;
            }
            _builtFrom = settings;
            _builtChild = settings.AdvisorOrbChild;
            return true;
        }

        private static Transform FindChild(Transform root, string name)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }

        /// <summary>Builds the orb look and <paramref name="count"/> hidden orbs ahead of use (loading).</summary>
        public void Prewarm(int count)
        {
            var settings = RecoilBattleEffects.Load();
            if (settings == null || !EnsureTemplate(settings)) return;
            count = Mathf.Min(count, _maxOrbs);
            while (_orbs.Count < count)
            {
                var instance = UnityEngine.Object.Instantiate(_template, _parent);
                instance.SetActive(false);
                _orbs.Add(new Orb { Object = instance, Particles = instance.GetComponentsInChildren<ParticleSystem>(true) });
            }
        }

        /// <summary>Drops every orb (e.g. after the orb settings changed).</summary>
        /// <summary>Drops every strike in progress without landing it (the match was cleared); the orbs stay pooled.</summary>
        public void CancelVolleys()
        {
            foreach (var volley in _volleys) Release(volley);
            _volleys.Clear();
            foreach (var orb in _orbs)
            {
                if (orb.Object == null) continue;
                foreach (var particle in orb.Particles) particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                orb.Object.SetActive(false);
            }
        }

        public void Clear()
        {
            foreach (var volley in _volleys) volley.Orbs.Clear();
            _volleys.Clear();
            foreach (var orb in _orbs) if (orb.Object != null) UnityEngine.Object.Destroy(orb.Object);
            _orbs.Clear();
            if (_template != null) UnityEngine.Object.Destroy(_template);
            _template = null;
            _builtFrom = null;
        }
    }
}
