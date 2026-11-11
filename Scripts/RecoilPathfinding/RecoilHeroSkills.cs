using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private sealed class HeroCast
        {
            public Soldier Caster;
            public HeroSkillClip Skill;
            public NhiQuoc.Tools.HeroVatPlayer.Clip Clip;
            public float Started;
            public Vector3 Target;
            public Vector3 Origin;
            public Vector3[] LaunchOrigins;
            public bool[] Launched;
            public Soldier TargetUnit;
            public Vector3 FollowPosition;
            public Quaternion Facing;
            public int[] NextFrames;
            public RecoilUnitCombatStats Stats;
            public bool Castle;
            public Bounds CastleBounds;
            public HashSet<Entity>[] SweptTargets;
            public bool[] SweptCastle;
        }
        private sealed class HeroDisplacement
        {
            public Soldier Target;
            public Vector3 Remaining;
            public float Seconds;
            public bool Impulse;
            public Vector3 Velocity;
        }
        private sealed class HeroKnockUp
        {
            public Soldier Target;
            public float Started, Duration, Height, InitialHeight;
        }
        private readonly List<HeroKnockUp> _heroKnockUps = new();
        private readonly List<HeroCast> _heroCasts = new();
        private readonly List<HeroDisplacement> _heroDisplacements = new();
        private RecoilHeroSkillLibrary _heroSkills;
        private bool _heroSkillsLoaded;

        private readonly List<int> _heroSkillCandidates = new();

        private float ConfiguredHeroRange(Soldier hero, float fallback)
        {
            var config = lobbyHeroStats != null ? System.Array.Find(lobbyHeroStats, h => h.Id == hero.HeroId) : null;
            return config != null ? config.Stats.Range : fallback;
        }

        private bool HeroUltiReady(Soldier hero) =>
            hero.HeroUltiCooldown > 0 && SimulationNow >= hero.NextHeroUlti &&
            hero.HeroVat != null && hero.HeroVat.clips != null && hero.HeroVat.clips.Length > 4;

        /// <summary>
        /// Skills the hero may use now, in the order it prefers them: the ulti when ready, then the attack whose turn
        /// it is, then the other attack.
        /// </summary>
        private void HeroSkillCandidates(Soldier hero, List<int> into)
        {
            into.Clear();
            if (hero.HeroVat == null || hero.HeroVat.clips == null) return;
            if (HeroUltiReady(hero)) into.Add(4);
            int next = hero.HeroVat.NextAttackClipIndex;
            into.Add(next);
            int other = next == 5 ? 2 : 5;
            if (other < hero.HeroVat.clips.Length) into.Add(other);
        }

        /// <summary>How far skill clip <paramref name="clipIndex"/> reaches, with a small safety margin; -1 without hit data.</summary>
        private float HeroSkillReach(Soldier hero, int clipIndex, float configured)
        {
            var clips = hero.HeroVat != null ? hero.HeroVat.clips : null;
            if (clips == null || clipIndex < 0 || clipIndex >= clips.Length) return -1;
            var skill = _heroSkills != null ? _heroSkills.Find(hero.HeroId)?.Find(clipIndex) : null;
            if (skill == null) return -1;
            float reach = 0;
            foreach (var zone in skill.Zones)
                if (zone.Enabled) reach = Mathf.Max(reach, zone.EngagementReach(configured, HeroZoneScale(hero.HeroVat)));
            if (reach <= 0) return -1;
            return Mathf.Max(.1f, reach - Mathf.Min(.15f, reach * .05f));
        }

        /// <summary>
        /// The hero's attack range: the longest reach among every skill it may use now (attack 1, attack 2 and the ulti
        /// when ready). Combat stops the hero inside this range, so at least one skill always lands from there; it no
        /// longer stands idle because the one skill that was probed happens to fall short.
        /// </summary>
        private float HeroEngagementRange(Soldier hero, float fallback)
        {
            float configured = ConfiguredHeroRange(hero, fallback);
            if (_heroSkills == null || _heroSkills.Find(hero.HeroId) == null || hero.HeroVat == null) return configured;
            HeroSkillCandidates(hero, _heroSkillCandidates);
            float best = -1;
            foreach (var clip in _heroSkillCandidates) best = Mathf.Max(best, HeroSkillReach(hero, clip, configured));
            return best > 0 ? best : configured;
        }

        /// <summary>The first usable skill (ulti first) that reaches <paramref name="distance"/>; -1 if none does.</summary>
        private int ChooseHeroSkill(Soldier hero, float distance, float configured)
        {
            HeroSkillCandidates(hero, _heroSkillCandidates);
            foreach (var clip in _heroSkillCandidates)
            {
                var reach = HeroSkillReach(hero, clip, configured);
                if (reach > 0 && distance <= reach) return clip;
            }
            return -1;
        }

        /// <summary>The usable skill reaching farthest; the plain attack when no skill has hit data.</summary>
        private int LongestHeroSkill(Soldier hero, float configured)
        {
            HeroSkillCandidates(hero, _heroSkillCandidates);
            int best = -1;
            float bestReach = 0;
            foreach (var clip in _heroSkillCandidates)
            {
                var reach = HeroSkillReach(hero, clip, configured);
                if (reach > bestReach) { bestReach = reach; best = clip; }
            }
            return best >= 0 ? best : HeroUltiReady(hero) ? 4 : hero.HeroVat.NextAttackClipIndex;
        }

        private void ClearHeroSkillCasts()
        {
            ReleaseRootedHeroes();
            foreach (var flight in _heroKnockUps) SetHeroAirborne(flight.Target, false, 0);
            foreach (var movement in _heroDisplacements) SetForcedDisplacement(movement.Target, false);
            _heroKnockUps.Clear(); _heroCasts.Clear(); _heroDisplacements.Clear(); _corpseFlights.Clear();
        }

        private readonly List<Soldier> _rootedHeroes = new();

        /// <summary>A hero performing a skill (any attack / skill / ulti clip still playing) does not move.</summary>
        private bool HeroPerformingSkill(Soldier hero) =>
            hero != null && !hero.Retired && hero.HeroVat != null && hero.HeroAnimation >= 2 &&
            SimulationNow < hero.HeroAnimationUntil;

        /// <summary>
        /// Holds every hero in place while it performs a skill and releases it afterwards. The movement system keeps
        /// its order and path, so it walks on as soon as the clip ends.
        /// </summary>
        private void UpdateHeroRooting()
        {
            for (int i = _rootedHeroes.Count - 1; i >= 0; i--)
            {
                var hero = _rootedHeroes[i];
                if (HeroPerformingSkill(hero)) continue;
                SetHeroRooted(hero, false);
                _rootedHeroes.RemoveAt(i);
            }
            if (_matchFinished) return;
            for (int i = 0; i < _combatSoldiers.Count; i++)
            {
                var hero = _combatSoldiers[i];
                if (hero == null || hero.HeroRooted || !HeroPerformingSkill(hero)) continue;
                SetHeroRooted(hero, true);
                if (hero.HeroRooted) _rootedHeroes.Add(hero);
            }
        }

        private void ReleaseRootedHeroes()
        {
            foreach (var hero in _rootedHeroes) SetHeroRooted(hero, false);
            _rootedHeroes.Clear();
        }

        private void SetHeroRooted(Soldier hero, bool rooted)
        {
            hero.HeroRooted = false;
            if (!_hasEntityManager || _world == null || !_world.IsCreated || hero.Simulation == null || !hero.Simulation.Exists) return;
            var navigation = _entityManager.GetComponentData<RecoilUnitNavigation>(hero.Entity);
            navigation.Rooted = (byte)(rooted ? 1 : 0);
            _entityManager.SetComponentData(hero.Entity, navigation);
            if (rooted)
            {
                // Stop where it stands this very tick, not after the movement system brakes it.
                var pose = _entityManager.GetComponentData<RecoilUnitPose>(hero.Entity);
                pose.Speed = 0f;
                pose.Velocity = float3.zero;
                _entityManager.SetComponentData(hero.Entity, pose);
            }
            hero.HeroRooted = rooted;
        }

        private void SetForcedDisplacement(Soldier target, bool active)
        {
            if (!_hasEntityManager || _world == null || !_world.IsCreated || target?.Simulation == null || !target.Simulation.Exists) return;
            var navigation = _entityManager.GetComponentData<RecoilUnitNavigation>(target.Entity);
            navigation.ForcedDisplacement = (byte)(active ? 1 : 0);
            _entityManager.SetComponentData(target.Entity, navigation);
        }

        private void RemoveHeroDisplacement(int index)
        {
            var target = _heroDisplacements[index].Target;
            _heroDisplacements.RemoveAt(index);
            if (!_heroDisplacements.Exists(m => ReferenceEquals(m.Target, target))) SetForcedDisplacement(target, false);
        }

        private void SetHeroAirborne(Soldier target, bool airborne, float height)
        {
            if (!_hasEntityManager || _world == null || !_world.IsCreated || target?.Simulation == null || !target.Simulation.Exists) return;
            var pose = target.Simulation.Pose;
            pose.PresentationLift = height;
            pose.Velocity = float3.zero; pose.Speed = 0;
            var navigation = _entityManager.GetComponentData<RecoilUnitNavigation>(target.Entity);
            navigation.Airborne = (byte)(airborne ? 1 : 0);
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(target.Entity);
            state.Airborne = navigation.Airborne;
            _entityManager.SetComponentData(target.Entity, pose);
            _entityManager.SetComponentData(target.Entity, navigation);
            _entityManager.SetComponentData(target.Entity, state);
        }

        private void LaunchHeroKnockUp(Soldier target, HeroHitZone zone)
        {
            var flight = _heroKnockUps.Find(f => ReferenceEquals(f.Target, target));
            if (flight == null) { flight = new HeroKnockUp { Target = target }; _heroKnockUps.Add(flight); }
            flight.InitialHeight = target.Simulation.Pose.PresentationLift;
            flight.Started = SimulationNow;
            flight.Height = Mathf.Max(.01f, zone.KnockUpHeight);
            flight.Duration = Mathf.Max(.03f, zone.KnockUpDuration);
            SetHeroAirborne(target, true, flight.InitialHeight);
            if (target.HeroVat != null)
            {
                target.HeroVat.Play(0); target.HeroAnimation = 0;
                target.HeroAnimationUntil = SimulationNow + flight.Duration;
            }
        }

        private void PrepareHeroSkillAttacks()
        {
            if (!_heroSkillsLoaded)
            {
                if (_heroPrefabs == null) _heroPrefabs = Resources.Load<RecoilHeroPrefabCatalog>("RecoilHeroPrefabs");
                _heroSkills = _heroPrefabs != null ? _heroPrefabs.Skills : null;
                _heroSkillsLoaded = true;
            }
            if (_heroSkills == null) return;
            for (int i = 0; i < _combatSoldiers.Count; i++)
            {
                var hero = _combatSoldiers[i];
                if (hero.HeroVat == null || hero.HeroId <= 0) continue;
                var profile = _heroSkills.Find(hero.HeroId);
                if (profile == null) continue;
                var attack = _contactAttacks[i];
                // Configured heroes never also apply the ordinary contact/projectile damage.
                var suppressed = attack;
                suppressed.Damage = 0;
                suppressed.TargetIndex = -1;
                _contactAttacks[i] = suppressed;
                if (attack.Damage > 0 && attack.TargetIndex >= 0) hero.DiagFired++;
                if (hero.Retired || attack.Damage <= 0 || attack.TargetIndex < 0 ||
                    attack.TargetIndex >= _combatSoldiers.Count || SimulationNow < hero.HeroAnimationUntil) continue;
                var target = _combatSoldiers[attack.TargetIndex];
                BeginHeroSkill(hero, target.Simulation.Position, _combatSnapshots[i].Stats, targetUnit: target);
            }
        }

        private void BeginHeroSkill(Soldier hero, Vector3 targetPosition, RecoilUnitCombatStats stats,
            bool castle = false, Bounds castleBounds = default, Soldier targetUnit = null)
        {
            // Pick among all three skills the first (ulti first) that actually reaches, so the ulti is only fired when
            // it lands and a short attack never leaves the hero idle while another one would hit.
            float configured = ConfiguredHeroRange(hero, stats.Range);
            int chosenClip;
            if (!castle)
            {
                if (!HeroSkillAlive(targetUnit)) return;
                targetPosition = targetUnit.Simulation.Position;
                var offset = targetPosition - hero.Simulation.Position;
                offset.y = 0;
                chosenClip = ChooseHeroSkill(hero, offset.magnitude, configured);
                if (chosenClip < 0)
                {
                    // None reaches yet: walk in (the hero's range already covers its longest skill).
                    hero.DiagWalkIns++;
                    var waiting = _entityManager.GetComponentData<RecoilBattleEcsState>(hero.Entity);
                    waiting.AttackCooldown = 0;
                    _entityManager.SetComponentData(hero.Entity, waiting);
                    hero.Simulation.SetDestination(targetPosition);
                    return;
                }
            }
            else
            {
                // The castle: the first skill that reaches its walls, otherwise the one that reaches farthest.
                var offset = castleBounds.ClosestPoint(hero.Simulation.Position) - hero.Simulation.Position;
                offset.y = 0;
                chosenClip = ChooseHeroSkill(hero, offset.magnitude, configured);
                if (chosenClip < 0) chosenClip = LongestHeroSkill(hero, configured);
            }
            var clips = hero.HeroVat.clips;
            if (clips == null || chosenClip < 0 || chosenClip >= clips.Length) return;
            var direction = targetPosition - hero.Agent.transform.position;
            direction.y = 0;
            if (direction.sqrMagnitude > .0001f) hero.Agent.transform.rotation = Quaternion.LookRotation(direction);
            hero.HeroVat.SetAttackTarget(targetPosition, targetUnit?.Agent != null ? targetUnit.Agent.transform : null);
            hero.HeroVat.PlayExact(chosenClip);
            hero.DiagCasts++;
            hero.TotalCasts++;
            int actualClip = hero.HeroVat.ActiveClipIndex;
            var clip = clips[actualClip];
            var skill = _heroSkills.Find(hero.HeroId)?.Find(actualClip);
            hero.HeroVat.ExternalProjectileEffects = skill != null && System.Array.Exists(skill.Zones, z => z.Enabled && z.IsProjectile);
            hero.HeroAnimation = actualClip;
            hero.HeroAnimationUntil = SimulationNow + clip.duration;
            if (actualClip == 4) hero.NextHeroUlti = SimulationNow + hero.HeroUltiCooldown; // only a real ulti spends it
            if (skill == null) return;
            var frames = new int[skill.Zones.Length];
            for (int z = 0; z < frames.Length; z++) frames[z] = Mathf.Max(0, skill.Zones[z].StartFrame);
            _heroCasts.Add(new HeroCast { Caster = hero, Skill = skill, Clip = clip,
                Started = SimulationNow, Origin = hero.Simulation.Position, Target = targetPosition, TargetUnit = targetUnit,
                FollowPosition = targetPosition, Facing = hero.Agent.transform.rotation,
                NextFrames = frames, Stats = stats, Castle = castle, CastleBounds = castleBounds,
                SweptTargets = new HashSet<Entity>[frames.Length], SweptCastle = new bool[frames.Length],
                LaunchOrigins = new Vector3[frames.Length], Launched = new bool[frames.Length] });
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(hero.Entity);
            state.AttackCooldown = Mathf.Max(state.AttackCooldown, clip.duration);
            _entityManager.SetComponentData(hero.Entity, state);
        }

        private void ProcessHeroSkillHits()
        {
            UpdateHeroRooting();
            LogHeroDiagnostics();
            if (_matchFinished) { ClearHeroSkillCasts(); return; }
            for (int i = _heroCasts.Count - 1; i >= 0; i--)
            {
                var cast = _heroCasts[i];
                if (!HeroSkillAlive(cast.Caster) || _entityManager.GetComponentData<RecoilBattleEcsState>(cast.Caster.Entity).Airborne != 0)
                { _heroCasts.RemoveAt(i); continue; }
                if (HeroSkillAlive(cast.TargetUnit)) cast.FollowPosition = cast.TargetUnit.Simulation.Position;
                if (System.Array.Exists(cast.Skill.Zones, zone => zone.Enabled && zone.Anchor == HeroHitAnchor.FollowTarget))
                    cast.Caster.HeroVat.UpdateActiveEffectTarget(cast.FollowPosition);
                float elapsed = SimulationNow - cast.Started;
                int frame = HeroSkillTimeline.FrameAt(elapsed, cast.Clip.duration, cast.Clip.frameCount);
                for (int z = 0; z < cast.Skill.Zones.Length; z++)
                {
                    var zone = cast.Skill.Zones[z];
                    int end = Mathf.Clamp(Mathf.Max(zone.StartFrame, zone.EndFrame), 0, cast.Clip.frameCount - 1);
                    while (zone.Enabled && cast.NextFrames[z] <= frame && cast.NextFrames[z] <= end)
                    {
                        int hitFrame = cast.NextFrames[z];
                        if (zone.IsProjectile && !cast.Launched[z])
                        {
                            cast.LaunchOrigins[z] = cast.Caster.Simulation.Position; cast.Launched[z] = true;
                            LaunchHeroCombatProjectile(cast, zone);
                            cast.NextFrames[z] = int.MaxValue;
                            break;
                        }
                        ResolveHeroSkillZone(cast, zone, hitFrame, z);
                        cast.NextFrames[z] = HeroSkillTimeline.NextPulse(hitFrame, zone.IsSweep || zone.IsProjectile ? 1 : zone.RepeatFrames, end);
                    }
                }
                if (elapsed >= cast.Clip.duration) _heroCasts.RemoveAt(i);
            }
            float dt = Mathf.Max(.001f, SimulationDelta);
            for (int i = _heroDisplacements.Count - 1; i >= 0; i--)
            {
                var movement = _heroDisplacements[i];
                if (!HeroSkillAlive(movement.Target))
                {
                    // Killed while flying: the body keeps going.
                    CorpseKeepsMoving(movement.Target, movement.Impulse
                        ? movement.Velocity * Mathf.Max(0, movement.Seconds) : movement.Remaining, movement.Seconds);
                    RemoveHeroDisplacement(i); continue;
                }
                var step = movement.Impulse
                    ? movement.Velocity * Mathf.Min(dt, Mathf.Max(0, movement.Seconds))
                    : movement.Remaining * Mathf.Clamp01(dt / Mathf.Max(.001f, movement.Seconds));
                if (pathService.TryApplyHeroDisplacement(movement.Target.Simulation, step)) _projectileGridStale = true;
                movement.Remaining -= step;
                movement.Seconds -= dt;
                if (movement.Seconds <= 0) RemoveHeroDisplacement(i);
            }
            for (int i = _heroKnockUps.Count - 1; i >= 0; i--)
            {
                var flight = _heroKnockUps[i];
                float t = (SimulationNow - flight.Started) / flight.Duration;
                if (t < 1 && !HeroSkillAlive(flight.Target))
                {
                    CorpseKeepsFlying(flight.Target, flight);
                    _heroKnockUps.RemoveAt(i); continue;
                }
                bool active = HeroSkillAlive(flight.Target) && t < 1;
                SetHeroAirborne(flight.Target, active, active ?
                    HeroSkillTimeline.AirborneHeight(t, flight.Height, flight.InitialHeight) : 0);
                if (!active) _heroKnockUps.RemoveAt(i);
            }
            UpdateCorpseFlights();
        }

        private bool HeroSkillAlive(Soldier soldier)
        {
            if (soldier == null || soldier.Retired || soldier.Simulation == null || !soldier.Simulation.Exists) return false;
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            return state.Active != 0 && state.Health > 0;
        }

        private void LaunchHeroCombatProjectile(HeroCast cast, HeroHitZone zone)
        {
            var hero = cast.Caster;
            float speed = Mathf.Max(.01f, zone.ProjectileSpeed);
            var zoneScale = HeroZoneScale(hero.HeroVat);
            Vector3 origin = hero.Simulation.Position + cast.Facing * (zone.Offset * zoneScale) + Vector3.up;
            var facing = cast.Facing * Quaternion.Euler(0, zone.Yaw, 0);
            float range = Mathf.Max(cast.Stats.Range + 2, Vector3.Distance(origin, cast.Target) + 2);
            var destination = origin + facing * Vector3.forward * range;
            if (cast.Castle)
            {
                destination = new Vector3(cast.Target.x, origin.y, cast.Target.z);
                range = Vector2.Distance(new Vector2(origin.x, origin.z), new Vector2(destination.x, destination.z));
            }
            float duration = RecoilCombatProjectileTrajectory.TravelDuration(range, speed);
            GameObject visual = null;
            int fxIndex = Mathf.Clamp(zone.EffectIndex, 0, Mathf.Max(0, cast.Clip.effects.Length - 1));
            if (cast.Clip.effects.Length > 0 && cast.Clip.effects[fxIndex].prefab != null)
                visual = GetOrCreateVisualProjectilePool().Spawn(cast.Clip.effects[fxIndex].prefab, origin, facing,
                    hero.HeroVat.transform.lossyScale.x, destination, speed, true);
            _pendingCombatProjectiles.Add(new PendingCombatProjectile
            {
                FlightStartTime = SimulationNow, LastProcessedTime = SimulationNow,
                FlightStartPoint = origin, ImpactPoint = destination, TravelDuration = duration,
                ImpactTime = SimulationNow + duration, ArcHeight = 0, Attacker = hero.Entity,
                Team = hero.IsBlue ? 0 : 1, IntendedTarget = cast.TargetUnit != null ? cast.TargetUnit.Entity : Entity.Null,
                Damage = Mathf.Max(0, Mathf.RoundToInt(cast.Stats.Damage * zone.DamageMultiplier)),
                AttackerStats = cast.Stats, SecondaryDamage = RecoilUnitDamage.UsesRangedArmor(cast.Stats),
                AreaRadius = 0, CollisionRadius = Mathf.Max(.01f, zone.Radius * zoneScale), CastleTarget = cast.Castle,
                CastleTeam = hero.IsBlue ? RecoilCastleTeam.Red : RecoilCastleTeam.Blue, HeroVisual = visual
            });
        }

        /// <summary>Skill damage zones were authored for heroes of this body height.</summary>
        internal const float HeroZoneAuthoringHeight = 6f;

        /// <summary>
        /// How much a hero's damage zones grow or shrink with its size: its body height (set by the VAT bake / size
        /// tool) over <see cref="HeroZoneAuthoringHeight"/>. 1 when unknown.
        /// </summary>
        internal static float HeroZoneScale(NhiQuoc.Tools.HeroVatPlayer vat) =>
            vat != null && vat.targetBodyHeight > .01f ? vat.targetBodyHeight / HeroZoneAuthoringHeight : 1f;

        /// <param name="zoneScale">The hero's size relative to the authoring size (<see cref="HeroZoneScale"/>):
        /// offsets and flight distances are multiplied by it; shape tests then use local / zoneScale.</param>
        public static void HeroZonePose(HeroHitZone zone, NhiQuoc.Tools.HeroVatPlayer.Clip clip, int frame,
            Vector3 caster, Vector3 target, Quaternion facing, float zoneScale, out Vector3 center, out Quaternion rotation)
        {
            center = zone.OriginPosition(caster, target);
            rotation = facing;
            // The selected origin changes the center; all damage areas remain on XZ.
            var planarForward = rotation * Vector3.forward;
            planarForward.y = 0;
            if (planarForward.sqrMagnitude < .0001f)
            { planarForward = facing * Vector3.forward; planarForward.y = 0; }
            rotation = planarForward.sqrMagnitude > .0001f ? Quaternion.LookRotation(planarForward) : Quaternion.identity;
            center += rotation * (zone.Offset * zoneScale);
            rotation *= Quaternion.Euler(0, zone.Yaw, 0);
            if (zone.IsProjectile)
                center += rotation * Vector3.forward * (zone.ProjectileTravel(frame, clip.duration, clip.frameCount) * zoneScale);
            if (zone.IsSweep)
            {
                zone.SweepSample(frame, out var sweepYaw, out _);
                rotation *= Quaternion.Euler(0, sweepYaw, 0);
            }
        }

        private void ResolveHeroSkillZone(HeroCast cast, HeroHitZone zone, int frame, int zoneIndex)
        {
            // Zone shapes are tested at their authored size: offsets from the zone are divided by the hero's scale.
            var zoneScale = HeroZoneScale(cast.Caster.HeroVat);
            var toAuthored = 1f / zoneScale;
            HeroZonePose(zone, cast.Clip, frame, zone.IsProjectile ? cast.LaunchOrigins[zoneIndex] : cast.Caster.Simulation.Position,
                zone.Anchor == HeroHitAnchor.FollowTarget ? cast.FollowPosition : cast.Target,
                cast.Facing, zoneScale, out var center, out var rotation);
            var inverse = Quaternion.Inverse(rotation);
            float angle = -1;
            if (zone.IsSweep) zone.SweepSample(frame, out _, out angle);
            bool once = zone.IsSweep || zone.IsProjectile;
            var hitTargets = once ? cast.SweptTargets[zoneIndex] ??= new HashSet<Entity>() : null;
            float segment = zone.IsProjectile && frame > zone.StartFrame ?
                zone.ProjectileTravel(frame, cast.Clip.duration, cast.Clip.frameCount) -
                zone.ProjectileTravel(frame - 1, cast.Clip.duration, cast.Clip.frameCount) : 0;
            var enemies = cast.Caster.IsBlue ? _redTeam : _blueTeam;
            if (cast.Castle)
            {
                var point = cast.CastleBounds.ClosestPoint(center);
                var local = inverse * (point - center) * toAuthored;
                bool castleHit = zone.IsProjectile ? zone.ContainsProjectileSegment(local, segment, 0) : zone.Contains(local, 0, angle);
                if (castleHit && (!once || !cast.SweptCastle[zoneIndex]))
                {
                    if (once) cast.SweptCastle[zoneIndex] = true;
                    int damage = Mathf.Max(0, Mathf.RoundToInt(cast.Stats.Damage * zone.DamageScale(local)));
                    if (cast.Caster.IsBlue) _redCastleHealthRemaining = Mathf.Max(0, _redCastleHealthRemaining - damage);
                    else _blueCastleHealthRemaining = Mathf.Max(0, _blueCastleHealthRemaining - damage);
                    if (damage > 0) { _castleAttackCount++; _castleDamageTotal += damage; }
                }
            }
            for (int i = 0; i < enemies.Count; i++)
            {
                var target = enemies[i];
                if (!HeroSkillAlive(target)) continue;
                var position = target.Simulation.Position;
                var local = inverse * (position - center) * toAuthored;
                var bodyRadius = GetCombatBodyRadius(target) * toAuthored;
                bool hit = zone.IsProjectile ? zone.ContainsProjectileSegment(local, segment, bodyRadius) :
                    zone.Contains(local, bodyRadius, angle);
                if (!hit ||
                    hitTargets != null && !hitTargets.Add(target.Entity)) continue;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(target.Entity);
                int damage = RecoilUnitDamage.Calculate(cast.Stats.Damage * zone.DamageScale(local), cast.Stats, state.Stats);
                if (damage > 0)
                {
                    state.Health = Mathf.Max(0, state.Health - damage);
                    state.LastAttacker = cast.Caster.Entity;
                    state.LastAttackedTime = SimulationNow;
                    if (state.Health == 0) { state.Active = 0; state.Target = Entity.Null; }
                    _entityManager.SetComponentData(target.Entity, state);
                    if (state.Health == 0) { KillSoldier(target); continue; }
                }
                // Heroes receive skill damage, but are immune to displacement and knock-up.
                if (target.HeroId > 0) continue;
                if (zone.Force == HeroHitForce.KnockUp || zone.IsPushOut || zone.Force == HeroHitForce.ForwardPush)
                    LaunchHeroKnockUp(target, zone);
                if (zone.Force == HeroHitForce.None) continue;
                Vector3 direction;
                float distance;
                if (zone.IsAxisPush)
                {
                    // Out to the end line on the target's own side of the axis.
                    direction = rotation * zone.AxisPushDirection(local, out var fromAxis);
                    direction.y = 0;
                    distance = zone.RadialPushDistance(fromAxis) * zoneScale;
                }
                else
                {
                    direction = zone.Force == HeroHitForce.ForwardPush ? cast.Facing * Vector3.forward : position - center;
                    direction.y = 0;
                    distance = (zone.Force == HeroHitForce.RadialPush ?
                        zone.RadialPushDistance(direction.magnitude * toAuthored) : zone.ForceDistance) * zoneScale;
                }
                if (distance <= 0) continue;
                if (zone.Force == HeroHitForce.Pull)
                { distance = Mathf.Min(distance, direction.magnitude); direction = -direction; }
                if (direction.sqrMagnitude <= .0001f) direction = cast.Facing * Vector3.forward;
                bool impulse = zone.IsPushOut || zone.Force == HeroHitForce.ForwardPush;
                float duration = Mathf.Max(.03f,
                    impulse || zone.Force == HeroHitForce.KnockUp ? zone.KnockUpDuration : zone.ForceDuration);
                SetForcedDisplacement(target, true);
                _heroDisplacements.Add(new HeroDisplacement { Target = target,
                    // Capture the launch velocity once; airborne motion then carries the target.
                    Impulse = impulse, Velocity = direction.normalized * (distance / duration),
                    Remaining = direction.normalized * distance, Seconds = duration });
            }
        }
    }
}
