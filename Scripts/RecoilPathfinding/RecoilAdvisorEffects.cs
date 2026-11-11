using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Effects of the Quân sư (character type 5, not heroes):
    /// <list type="bullet">
    /// <item>Buff 1 (magic circle) under its feet when it is summoned into the battle, following it.</item>
    /// <item>Buff 2 (fire burst) under its feet each time it strikes.</item>
    /// <item>Buff 5 (shock wave) where its strike lands.</item>
    /// </list>
    /// Each effect has a small pool; when every copy is playing, a new one is skipped, so a big army of advisors stays
    /// cheap. The prefabs come from Resources/RecoilBattleEffects unless set here.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        private const int AdvisorCharacterType = 5;

        [Header("Hiệu ứng Quân sư")]
        [SerializeField] private bool advisorEffects = true;
        [SerializeField, Range(1, 64), Tooltip("Số bản chạy cùng lúc tối đa của mỗi hiệu ứng")] private int advisorEffectsPerKind = 16;
        // Prefabs, sizes, timing and offsets: Resources/RecoilBattleEffects (shared with the Quân sư test ground).

        private RecoilAdvisorOrbs _advisorOrbs;
        private RecoilAdvisorOrbs AdvisorOrbs => _advisorOrbs ??= new RecoilAdvisorOrbs(transform, advisorEffectsPerKind * 3);

        private sealed class AdvisorEffect
        {
            public GameObject Object;
            public ParticleSystem[] Particles;
            public Soldier Follow;
        }

        private readonly List<AdvisorEffect>[] _advisorEffects = { new(), new(), new() };

        private static GameObject AdvisorEffectPrefab(int kind) => RecoilBattleEffects.Load()?.AdvisorPrefab(kind);

        private static bool IsAdvisor(Soldier soldier) =>
            soldier != null && soldier.HeroId == 0 && soldier.CharacterType == AdvisorCharacterType;

        private AdvisorEffect CreateAdvisorEffect(int kind, GameObject prefab)
        {
            // The prefabs loop; here each one plays once.
            var instance = RecoilBattleEffects.CreateOneShot(prefab, transform, out var particles);
            if (kind == 1) RecoilBattleEffects.Load().HideCastParts(instance); // its orbs fly instead
            return new AdvisorEffect { Object = instance, Particles = particles };
        }

        /// <summary>Plays effect <paramref name="kind"/> at <paramref name="position"/>, following <paramref name="follow"/> if set.</summary>
        private void PlayAdvisorEffect(int kind, Vector3 position, Soldier follow = null)
        {
            if (!advisorEffects) return;
            var prefab = AdvisorEffectPrefab(kind);
            if (prefab == null) return;
            var pool = _advisorEffects[kind];
            AdvisorEffect effect = null;
            foreach (var candidate in pool)
                if (candidate.Object != null && !candidate.Object.activeSelf) { effect = candidate; break; }
            if (effect == null)
            {
                if (pool.Count >= advisorEffectsPerKind) return; // all busy: skip rather than cut one short
                pool.Add(effect = CreateAdvisorEffect(kind, prefab));
            }
            effect.Follow = follow;
            RecoilBattleEffects.PlayOnce(effect.Object, effect.Particles, prefab, position,
                RecoilBattleEffects.Load().AdvisorScale(kind));
        }

        private static Vector3 AdvisorFeet(Soldier soldier) =>
            soldier.Agent != null ? soldier.Agent.transform.position :
            soldier.Simulation != null && soldier.Simulation.Exists ? soldier.Simulation.Position : Vector3.zero;

        /// <summary>A Quân sư joined the battle (summoned, not the opening army).</summary>
        private void OnAdvisorSummoned(Soldier soldier)
        {
            if (IsAdvisor(soldier)) PlayAdvisorEffect(0, AdvisorFeet(soldier), soldier);
        }

        /// <summary>Seconds from the start of the strike to the moment it lands.</summary>
        private float AdvisorHitDelay(Soldier attacker)
        {
            var duration = attacker.Vat != null ? attacker.Vat.AttackDuration :
                attacker.Presentation != null && attacker.Presentation.AttackClip != null ? attacker.Presentation.AttackClip.length : 0f;
            var settings = RecoilBattleEffects.Load();
            return RecoilAdvisorOrbs.HitDelay(settings, duration);
        }

        private float AdvisorAttackDuration(Soldier attacker) =>
            attacker.Vat != null ? attacker.Vat.AttackDuration :
            attacker.Presentation != null && attacker.Presentation.AttackClip != null ? attacker.Presentation.AttackClip.length : 1f;

        /// <summary>
        /// A Quân sư strikes <paramref name="target"/>: the magic circle under it, its orbs circle it and fly to the
        /// target, and the fire plays where they land.
        /// </summary>
        private void OnAdvisorAttack(Soldier attacker, Soldier target, Vector3 targetPosition, int damage)
        {
            if (!IsAdvisor(attacker) || attacker.Agent == null) return; // only drawn advisors
            var settings = RecoilBattleEffects.Load();
            if (settings == null) return;
            var feet = AdvisorFeet(attacker);
            PlayAdvisorEffect(1, RecoilBattleEffects.Place(feet, attacker.Agent.transform.forward, settings.AdvisorCastOffset));
            var delay = AdvisorHitDelay(attacker);
            Vector3? TargetNow() => target != null && !target.Retired && target.Agent != null
                ? target.Agent.transform.position : null;
            // A strike cut short (the advisor died before throwing) does not land.
            AdvisorOrbs.Begin(attacker.Agent.transform, TargetNow, targetPosition, SimulationNow, AdvisorAttackDuration(attacker),
                () => !attacker.Retired && attacker.Agent != null, (where, direction) =>
                {
                    PlayAdvisorEffect(2, RecoilBattleEffects.Place(where, direction, RecoilBattleEffects.Load().AdvisorHitOffset));
                    RecordWatchedHit(attacker, target);
                });
            RecordWatchedAttack(attacker, target, targetPosition, damage, delay);
        }

        private void UpdateAdvisorEffects()
        {
            _advisorOrbs?.Update(SimulationNow);
            foreach (var pool in _advisorEffects)
                foreach (var effect in pool)
                {
                    if (effect.Object == null || !effect.Object.activeSelf) continue;
                    var alive = false;
                    foreach (var particle in effect.Particles)
                        if (particle.IsAlive(false)) { alive = true; break; }
                    if (!alive) { effect.Object.SetActive(false); effect.Follow = null; continue; }
                    var follow = effect.Follow;
                    if (follow == null) continue;
                    if (follow.Retired || follow.Agent == null) { effect.Follow = null; continue; } // stays where it died
                    effect.Object.transform.position = follow.Agent.transform.position;
                }
        }

        /// <summary>
        /// Every hidden copy the battle may use (each effect up to its limit, and the orbs), made while loading so
        /// none is created mid-battle. Spread over frames so the loading bar keeps moving.
        /// </summary>
        private System.Collections.IEnumerator PrewarmAdvisorEffects()
        {
            if (!advisorEffects) yield break;
            var made = 0;
            for (var kind = 0; kind < _advisorEffects.Length; kind++)
            {
                var prefab = AdvisorEffectPrefab(kind);
                if (prefab == null) continue;
                var pool = _advisorEffects[kind];
                while (pool.Count < advisorEffectsPerKind)
                {
                    pool.Add(CreateAdvisorEffect(kind, prefab));
                    if (++made % 4 == 0) yield return null;
                }
            }
            AdvisorOrbs.Prewarm(advisorEffectsPerKind * 3);
            yield return null;
        }

        private void ReleaseAdvisorEffects()
        {
            foreach (var pool in _advisorEffects)
            {
                foreach (var effect in pool)
                    if (effect.Object != null) Destroy(effect.Object);
                pool.Clear();
            }
            _advisorOrbs?.Clear();
            _advisorOrbs = null;
        }
    }
}
