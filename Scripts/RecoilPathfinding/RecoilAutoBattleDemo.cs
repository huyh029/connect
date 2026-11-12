using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using Unity.Profiling;
using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst.Intrinsics;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    [BurstCompile]
    internal struct RecoilBuildCombatIndicesJob : IJob
    {
        [ReadOnly] public NativeArray<Entity> Entities;
        public NativeParallelHashMap<Entity, int> Indices;
        public int Count;
        public void Execute()
        {
            Indices.Clear();
            for (var i = 0; i < Count; i++) Indices.Add(Entities[i], i);
        }
    }

    // Port of QuadField::{CellUnitsIndex,MayHaveEnemyUnits}: four fine quads
    // per coarse cell, with occupancy per team. Keep the existing fine-grid
    // traversal and candidate order; this only rejects provably empty regions.
    internal static class RecoilCombatQuadOccupancy
    {
        public static int3 CoarseCell(int3 fineCell) =>
            new int3((int)math.floor(fineCell.x / 4f), (int)math.floor(fineCell.y / 4f), fineCell.z);

        public static bool MayHaveEnemyUnits(NativeParallelHashMap<int3, int> counts,
            int2 fineCell, int radius, int enemyTeam)
        {
            // Compatibility with callers that don't supply the optional broad phase.
            if (!counts.IsCreated) return true;
            var min = CoarseCell(new int3(fineCell.x - radius, fineCell.y - radius, enemyTeam));
            var max = CoarseCell(new int3(fineCell.x + radius, fineCell.y + radius, enemyTeam));
            for (var z = min.y; z <= max.y; z++)
            for (var x = min.x; x <= max.x; x++)
                if (counts.TryGetValue(new int3(x, z, enemyTeam), out var count) && count > 0)
                    return true;
            return false;
        }

        public static void AddTeamUnit(ref NativeParallelHashMap<int3, int> counts, int3 fineCell)
        {
            var key = CoarseCell(fineCell);
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        public static void EraseTeamUnit(ref NativeParallelHashMap<int3, int> counts, int3 fineCell)
        {
            var key = CoarseCell(fineCell);
            var count = counts[key];
            if (count == 1) counts.Remove(key);
            else counts[key] = count - 1;
        }
    }

    internal static class RecoilTargetSearchBudget
    {
        public static int Apply(NativeArray<RecoilBattleCombatSnapshot> snapshots,
            NativeArray<int> scheduledIndices, int scheduledCount, int budget,
            ref int blueCursor, ref int redCursor)
        {
            scheduledCount = math.min(scheduledCount, scheduledIndices.Length);
            if (scheduledCount <= 0) return 0;

            var blueRequests = 0;
            var redRequests = 0;
            for (var i = 0; i < scheduledCount; i++)
            {
                var snapshot = snapshots[scheduledIndices[i]];
                if (snapshot.SearchTarget == 0) continue;
                if (snapshot.Team == 0) blueRequests++;
                else redRequests++;
            }

            budget = math.max(0, budget);
            var blueBudget = math.min(blueRequests, (budget + 1) / 2);
            var redBudget = math.min(redRequests, budget / 2);
            var remaining = budget - blueBudget - redBudget;
            var extraBlue = math.min(remaining, blueRequests - blueBudget);
            blueBudget += extraBlue;
            remaining -= extraBlue;
            redBudget += math.min(remaining, redRequests - redBudget);

            var blueScheduled = SelectTeam(snapshots, scheduledIndices, scheduledCount,
                0, blueBudget, ref blueCursor);
            var redScheduled = SelectTeam(snapshots, scheduledIndices, scheduledCount,
                1, redBudget, ref redCursor);
            var scheduled = blueScheduled + redScheduled;
            var deferred = 0;
            for (var i = 0; i < scheduledCount; i++)
            {
                var index = scheduledIndices[i];
                var snapshot = snapshots[index];
                if (snapshot.SearchTarget == 2) snapshot.SearchTarget = 1;
                else if (snapshot.SearchTarget == 1)
                {
                    snapshot.SearchTarget = 0;
                    deferred++;
                }
                snapshots[index] = snapshot;
            }
            return deferred;
        }

        private static int SelectTeam(NativeArray<RecoilBattleCombatSnapshot> snapshots,
            NativeArray<int> scheduledIndices, int scheduledCount, int team, int budget, ref int cursor)
        {
            if (budget <= 0) return 0;
            var selected = 0;
            var start = cursor % scheduledCount;
            for (var offset = 0; offset < scheduledCount && selected < budget; offset++)
            {
                var slot = (start + offset) % scheduledCount;
                var index = scheduledIndices[slot];
                var snapshot = snapshots[index];
                if (snapshot.SearchTarget != 1 || snapshot.Team != team) continue;
                snapshot.SearchTarget = 2;
                snapshots[index] = snapshot;
                selected++;
                cursor = (slot + 1) % scheduledCount;
            }
            return selected;
        }
    }

    [BurstCompile]
    internal struct RecoilAggregateDamageJob : IJob
    {
        [ReadOnly] public NativeArray<RecoilContactAttack> Attacks;
        public NativeArray<int> Damage;
        public NativeArray<int> LastAttackerByTarget;
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> Cells;
        public float CellSize;
        public int Count;
        public void Execute()
        {
            for (var i = 0; i < Count; i++)
            {
                Damage[i] = 0;
                if (LastAttackerByTarget.IsCreated) LastAttackerByTarget[i] = -1;
            }
            for (var i = 0; i < Count; i++)
            {
                var hit = Attacks[i];
                // Projectile weapons resolve in the delayed impact pass; this job only
                // aggregates instantaneous contact weapons.
                if (hit.Projectile != 0 || hit.Damage <= 0 || hit.TargetIndex < 0 || hit.TargetIndex >= Count) continue;
                if (!Soldiers.IsCreated || Soldiers.Length < Count)
                {
                    Damage[hit.TargetIndex] = math.min(Damage[hit.TargetIndex], int.MaxValue - hit.Damage) + hit.Damage;
                    if (LastAttackerByTarget.IsCreated) LastAttackerByTarget[hit.TargetIndex] = i;
                    continue;
                }
                var attacker = Soldiers[i];
                var area = attacker.Stats.IsConfigured ? attacker.Stats.AreaRadius : 0f;
                if (area <= 0f || !Cells.IsCreated || CellSize <= 0f)
                {
                    ApplyHit(hit.TargetIndex, hit.Damage, attacker, i);
                    continue;
                }
                var center = Soldiers[hit.TargetIndex].Position;
                var cell = (int2)math.floor(center.xz / CellSize);
                var extent = (int)math.ceil(area / CellSize);
                for (var x = -extent; x <= extent; x++)
                for (var z = -extent; z <= extent; z++)
                {
                    if (!Cells.TryGetFirstValue(new int3(cell.x + x, cell.y + z, attacker.Team == 0 ? 1 : 0),
                        out var target, out var cursor)) continue;
                    do
                    {
                        if (target >= 0 && target < Count && math.distancesq(Soldiers[target].Position.xz, center.xz) <= area * area)
                            ApplyHit(target, hit.Damage, attacker, i);
                    } while (Cells.TryGetNextValue(out target, ref cursor));
                }
            }
        }

        private void ApplyHit(int index, int raw, RecoilBattleCombatSnapshot attacker, int attackerIndex)
        {
            var target = Soldiers[index];
            if (target.Active == 0 || target.Health <= 0 || target.Team == attacker.Team) return;
            var damage = RecoilUnitDamage.Calculate(raw, attacker.Stats, target.Stats);
            if (damage <= 0) return;
            Damage[index] = math.min(Damage[index], int.MaxValue - damage) + damage;
            if (LastAttackerByTarget.IsCreated) LastAttackerByTarget[index] = attackerIndex;
        }
    }

    [BurstCompile]
    internal struct RecoilApplyDamageJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        public ComponentTypeHandle<RecoilBattleEcsState> States;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [ReadOnly] public NativeArray<int> Damage;
        [ReadOnly] public NativeArray<int> LastAttackerByTarget;
        [ReadOnly] public NativeArray<Entity> SnapshotEntities;
        public float Now;
        public NativeList<int>.ParallelWriter Deaths;
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index) || Damage[index] <= 0) continue;
                var state = states[i];
                if (state.Active == 0 || state.Health <= 0) continue;
                state.Health = math.max(0, state.Health - Damage[index]);
                if (LastAttackerByTarget.IsCreated && SnapshotEntities.IsCreated &&
                    index < LastAttackerByTarget.Length && LastAttackerByTarget[index] >= 0 &&
                    LastAttackerByTarget[index] < SnapshotEntities.Length)
                {
                    state.LastAttacker = SnapshotEntities[LastAttackerByTarget[index]];
                    state.LastAttackedTime = Now;
                }
                if (state.Health == 0)
                {
                    state.Active = 0; state.Target = Entity.Null;
                    Deaths.AddNoResize(index);
                }
                states[i] = state;
            }
        }
    }

    [BurstCompile]
    internal struct RecoilResetAliveFlagsJob : IJobParallelFor
    {
        public NativeArray<byte> Alive;
        public void Execute(int index) => Alive[index] = 0;
    }

    [BurstCompile]
    internal struct RecoilResetCombatSnapshotsJob : IJobParallelFor
    {
        public NativeArray<RecoilBattleCombatSnapshot> Snapshots;
        public void Execute(int index) => Snapshots[index] = default;
    }

    [BurstCompile]
    internal struct RecoilCaptureAliveJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        [ReadOnly] public ComponentTypeHandle<RecoilBattleEcsState> States;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Alive;
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index)) continue;
                var state = states[i];
                Alive[index] = (byte)(state.Active != 0 && state.Health > 0 ? 1 : 0);
            }
        }
    }

    internal struct RecoilContactAttack
    {
        public int SearchAttempted;
        public int EmptyRegionSkipped;
        public int CandidateChecks;
        public int TargetWeightEvaluations;
        public int TargetPriorityWeightSkips;
        public int AvoidedCategoryCandidates;
        public int AvoidedCategoryTargetSelected;
        public int LineOfFireChecks;
        public int LineOfFireBlocked;
        public int TargetIndex;
        public int Damage;
        public byte Projectile;
    }
    [BurstCompile]
    internal struct RecoilCloseTargetJob : IJobChunk
    {
        public const int SlowTargetUpdateRate = 15;
        [ReadOnly] public NativeParallelHashMap<int3, int> TeamCellCounts;
        [ReadOnly] public EntityTypeHandle Entities;
        public ComponentTypeHandle<RecoilUnitPose> Poses;
        public ComponentTypeHandle<RecoilBattleEcsState> States;
        public ComponentTypeHandle<RecoilUnitNavigation> Navigations;
        public BufferTypeHandle<RecoilUnitWaypoint> Waypoints;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [ReadOnly] public NativeArray<Entity> SnapshotEntities;
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        [ReadOnly] public NativeArray<int> ScheduledGenerations;
        public int ScheduleGeneration;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> Cells;
        public RecoilTerrainHeightField Heights;
        public float CellSize;
        public float RangeSquared;
        public float MeleeAttackRangeSquared;
        public float ArcherAttackRangeSquared;
        public float SiegeAttackRangeSquared;
        [ReadOnly] public NativeArray<byte> RangedUnits;
        [NativeDisableParallelForRestriction] public NativeArray<RecoilContactAttack> Attacks;
        public float CombatDelta;
        public float AttackInterval;
        public int AttackDamage;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var poses = chunk.GetNativeArray(ref Poses);
            var navigations = chunk.GetNativeArray(ref Navigations);
            var paths = chunk.GetBufferAccessor(ref Waypoints);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index)) continue;
                var soldier = Soldiers[index];
                if (Attacks.IsCreated)
                {
                    Attacks[index] = new RecoilContactAttack { TargetIndex = -1 };
                    var cooling = states[i];
                    cooling.AttackCooldown = math.max(0f, cooling.AttackCooldown - CombatDelta);
                    cooling.SalvoCooldown = math.max(0f, cooling.SalvoCooldown - CombatDelta);
                    cooling.WindupCooldown = math.max(0f, cooling.WindupCooldown - CombatDelta);
                    states[i] = cooling;
                }
                if (soldier.Active == 0 || soldier.Health <= 0 || states[i].Airborne != 0) continue;
                var attackRangeSquared = soldier.Stats.IsConfigured ? soldier.Stats.Range * soldier.Stats.Range : GetAttackRangeSquared(RangedUnits[index]);
                var flatOwnerForward = RecoilCombatFacing.FlatForward(soldier.Rotation);
                var attackRange = math.sqrt(math.max(0f, attackRangeSquared));
                var fallbackDamagePerSecond = AttackDamage / math.max(0.01f, AttackInterval);
                var targetDamagePerSecond = RecoilCombatTargetWeight.DamagePerSecond(
                    soldier.Stats, fallbackDamagePerSecond);
                var searchRangeSquared = math.max(RangeSquared, attackRangeSquared);
                var target = soldier.CurrentTargetIndex;
                var found = -1;
                var nearest = searchRangeSquared;
                var foundPriority = int.MaxValue;
                var foundAvoidedCategory = false;
                var foundWeight = float.PositiveInfinity;
                var targetWeightEvaluations = 0;
                var targetPriorityWeightSkips = 0;
                var hasScheduledTargetScan = ScheduledGenerations[index] == ScheduleGeneration;
                var currentTargetChecked = false;
                var currentTargetLineOfFire = true;
                var currentTargetValid = false;
                var currentTargetDistance = float.PositiveInfinity;
                if (target >= 0 && target < Soldiers.Length && Soldiers[target].Active != 0 &&
                    Soldiers[target].Health > 0 && Soldiers[target].Team != soldier.Team &&
                    IsAllowedWeaponTarget(soldier, Soldiers[target]))
                {
                    var distance = math.distancesq(soldier.Position.xz, Soldiers[target].Position.xz);
                    currentTargetValid = true;
                    currentTargetDistance = distance;
                    var targetLineOfFire = true;
                    if (hasScheduledTargetScan || distance <= attackRangeSquared)
                        targetLineOfFire = HasLineOfFire(index, soldier.Position, Soldiers[target].Position);
                    currentTargetChecked = hasScheduledTargetScan || distance <= attackRangeSquared;
                    currentTargetLineOfFire = targetLineOfFire;
                    var weight = RecoilCombatTargetWeight.Calculate(soldier.Position, Soldiers[target].Position,
                        flatOwnerForward, targetDamagePerSecond, Soldiers[target].Health,
                        distance, attackRange, RecoilCombatTargetWeight.DamageMultiplier(
                            soldier.Stats, Soldiers[target].Stats),
                        RecoilCombatTargetWeight.PriorityMultiplier(soldier, Soldiers[target]));
                    targetWeightEvaluations++;
                    if (targetLineOfFire)
                        RecordAvoidedCategoryCandidate(index, soldier.Stats, Soldiers[target].Stats);
                    FaceEnemy(i, target, ref poses);
                    if (targetLineOfFire)
                    {
                        found = target;
                        nearest = distance;
                        foundPriority = Soldiers[target].Stats.TargetPriority;
                        foundAvoidedCategory = RecoilCombatTargetWeight.IsAvoidedTargetCategory(
                            soldier.Stats, Soldiers[target].Stats);
                        foundWeight = weight;
                        if (distance <= attackRangeSquared)
                        {
                            FaceAtContact(i, target, ref poses);
                            Fire(i, index, target, ref states);
                            if (Attacks.IsCreated)
                            {
                                var diagnostic = Attacks[index];
                                diagnostic.TargetWeightEvaluations = targetWeightEvaluations;
                                diagnostic.TargetPriorityWeightSkips = targetPriorityWeightSkips;
                                Attacks[index] = diagnostic;
                            }
                            HaltAtContact(i, ref navigations, ref paths); continue;
                        }
                    }
                }
                if (!hasScheduledTargetScan)
                {
                    if (found < 0)
                    {
                        ContinueRangedSalvoThroughObstruction(i, index, target, currentTargetValid,
                            currentTargetChecked, currentTargetLineOfFire, currentTargetDistance, attackRangeSquared,
                            ref states);
                        continue;
                    }
                    RecordAvoidedCategorySelection(index, soldier.Stats, Soldiers[found].Stats);
                    var currentState = states[i];
                    currentState.Target = SnapshotEntities[found];
                    states[i] = currentState;
                    var currentApproach = navigations[i];
                    currentApproach.CombatApproach = 1;
                    navigations[i] = currentApproach;
                    FaceEnemy(i, found, ref poses);
                    if (nearest <= attackRangeSquared &&
                        HasLineOfFire(index, soldier.Position, Soldiers[found].Position))
                    {
                        FaceAtContact(i, found, ref poses);
                        Fire(i, index, found, ref states);
                        HaltAtContact(i, ref navigations, ref paths);
                    }
                    continue;
                }
                var cell = (int2)math.floor(soldier.Position.xz / CellSize);
                var radius = (int)math.ceil(math.sqrt(searchRangeSquared) / CellSize);
                var mayHaveEnemy = RecoilCombatQuadOccupancy.MayHaveEnemyUnits(
                    TeamCellCounts, cell, radius, soldier.Team == 0 ? 1 : 0);
                if (Attacks.IsCreated)
                {
                    var diagnostic = Attacks[index];
                    diagnostic.SearchAttempted = 1;
                    diagnostic.EmptyRegionSkipped = mayHaveEnemy ? 0 : 1;
                    diagnostic.TargetWeightEvaluations = targetWeightEvaluations;
                    diagnostic.TargetPriorityWeightSkips = targetPriorityWeightSkips;
                    Attacks[index] = diagnostic;
                }
                if (!mayHaveEnemy) continue;
                var checks = 0;
                // Search only cells intersecting this unit's weapon range, but inspect every
                // candidate in those cells. Recoil ranks the complete weapon target set; a fixed
                // candidate cap can hide a higher-priority target behind a crowded grid bucket.
                for (var ring = 0; ring <= radius; ring++)
                for (var x = -ring; x <= ring; x++)
                for (var z = -ring; z <= ring; z++)
                {
                    if (math.max(math.abs(x), math.abs(z)) != ring) continue;
                    if (!Cells.TryGetFirstValue(new int3(cell.x + x, cell.y + z, soldier.Team == 0 ? 1 : 0),
                        out var candidate, out var cursor)) continue;
                    do
                    {
                        checks++;
                        // The retained target was already range/LOS validated and weighted above.
                        // Count its grid visit against the bounded scan, but do not repeat the costly score.
                        if (candidate == target && currentTargetChecked && found == target) continue;
                        var enemy = Soldiers[candidate];
                        if (enemy.Active == 0 || enemy.Health <= 0 || enemy.Team == soldier.Team ||
                            !IsAllowedWeaponTarget(soldier, enemy)) continue;
                        var distance = math.distancesq(soldier.Position.xz, enemy.Position.xz);
                        if (distance > searchRangeSquared) continue;
                        if (enemy.Stats.TargetPriority > foundPriority)
                        {
                            targetPriorityWeightSkips++;
                            continue;
                        }
                        if (candidate == target && currentTargetChecked)
                        {
                            if (!currentTargetLineOfFire) continue;
                        }
                        else if (!HasLineOfFire(index, soldier.Position, enemy.Position)) continue;
                        var weight = RecoilCombatTargetWeight.Calculate(soldier.Position, enemy.Position,
                            flatOwnerForward, targetDamagePerSecond, enemy.Health, distance, attackRange,
                            RecoilCombatTargetWeight.DamageMultiplier(soldier.Stats, enemy.Stats),
                            RecoilCombatTargetWeight.PriorityMultiplier(soldier, enemy));
                        targetWeightEvaluations++;
                        RecordAvoidedCategoryCandidate(index, soldier.Stats, enemy.Stats);
                        var avoidedCategory = RecoilCombatTargetWeight.IsAvoidedTargetCategory(
                            soldier.Stats, enemy.Stats);
                        if (RecoilCombatTargetWeight.IsPreferredCandidate(enemy.Stats.TargetPriority,
                                avoidedCategory, weight, distance, foundPriority, foundAvoidedCategory,
                                foundWeight, nearest))
                        {
                            nearest = distance;
                            foundPriority = enemy.Stats.TargetPriority;
                            foundAvoidedCategory = avoidedCategory;
                            foundWeight = weight;
                            found = candidate;
                        }
                    } while (Cells.TryGetNextValue(out candidate, ref cursor));
                }
                if (Attacks.IsCreated)
                {
                    var diagnostic = Attacks[index];
                    diagnostic.CandidateChecks = checks;
                    diagnostic.TargetWeightEvaluations = targetWeightEvaluations;
                    diagnostic.TargetPriorityWeightSkips = targetPriorityWeightSkips;
                    Attacks[index] = diagnostic;
                }
                if (found < 0)
                {
                    ContinueRangedSalvoThroughObstruction(i, index, target, currentTargetValid,
                        currentTargetChecked, currentTargetLineOfFire, currentTargetDistance, attackRangeSquared,
                        ref states);
                    continue;
                }
                RecordAvoidedCategorySelection(index, soldier.Stats, Soldiers[found].Stats);
                var state = states[i];
                state.Target = SnapshotEntities[found];
                states[i] = state;
                var approach = navigations[i];
                approach.CombatApproach = 1;
                navigations[i] = approach;
                FaceEnemy(i, found, ref poses);
                if (nearest <= attackRangeSquared &&
                    HasLineOfFire(index, soldier.Position, Soldiers[found].Position))
                {
                    FaceAtContact(i, found, ref poses);
                    Fire(i, index, found, ref states);
                    HaltAtContact(i, ref navigations, ref paths);
                }
            }
        }

        private void FaceEnemy(int index, int target, ref NativeArray<RecoilUnitPose> poses)
        {
            var pose = poses[index];
            RecoilCombatFacing.FaceTarget(ref pose, Soldiers[target].Position);
            poses[index] = pose;
        }

        private void ContinueRangedSalvoThroughObstruction(int local, int index, int target,
            bool targetValid, bool targetChecked, bool targetLineOfFire, float targetDistance,
            float attackRangeSquared, ref NativeArray<RecoilBattleEcsState> states)
        {
            if (!targetValid || !targetChecked || targetLineOfFire || target < 0 ||
                targetDistance > attackRangeSquared || !RangedUnits.IsCreated || RangedUnits[index] == 0)
                return;
            var state = states[local];
            if (state.SalvoShotsRemaining <= 0 || state.SalvoCooldown > 0f) return;

            // Recoil's default burstControlWhenOutOfArc is zero: rounds already in a salvo
            // continue firing. Keep the current ranged target and let projectile/terrain
            // collision decide whether the shot reaches it.
            Fire(local, index, target, ref states);
        }

        private void FaceAtContact(int index, int target, ref NativeArray<RecoilUnitPose> poses)
        {
            var pose = poses[index];
            RecoilCombatFacing.FaceTarget(ref pose, Soldiers[target].Position);
            pose.Speed = 0f;
            pose.Velocity = float3.zero;
            poses[index] = pose;
        }

        private bool HasLineOfFire(int index, float3 start, float3 end)
        {
            var arcHeight = RangedUnits.IsCreated && RangedUnits[index] != 0
                ? RecoilCombatProjectileTrajectory.ArcHeight(start.xz, end.xz)
                : 0f;
            var clear = Heights.HasLineOfFire(start, end, arcHeight);
            if (Attacks.IsCreated)
            {
                var diagnostic = Attacks[index];
                diagnostic.LineOfFireChecks++;
                if (!clear) diagnostic.LineOfFireBlocked++;
                Attacks[index] = diagnostic;
            }
            return clear;
        }

        private void RecordAvoidedCategoryCandidate(int index, RecoilUnitCombatStats attacker,
            RecoilUnitCombatStats target)
        {
            if (!Attacks.IsCreated || !RecoilCombatTargetWeight.IsAvoidedTargetCategory(attacker, target)) return;
            var diagnostic = Attacks[index];
            diagnostic.AvoidedCategoryCandidates++;
            Attacks[index] = diagnostic;
        }

        private void RecordAvoidedCategorySelection(int index, RecoilUnitCombatStats attacker,
            RecoilUnitCombatStats target)
        {
            if (!Attacks.IsCreated || !RecoilCombatTargetWeight.IsAvoidedTargetCategory(attacker, target)) return;
            var diagnostic = Attacks[index];
            diagnostic.AvoidedCategoryTargetSelected = 1;
            Attacks[index] = diagnostic;
        }

        private float GetAttackRangeSquared(byte unitType)
        {
            if (unitType == 2)
                return SiegeAttackRangeSquared;
            return unitType == 1 ? ArcherAttackRangeSquared : MeleeAttackRangeSquared;
        }

        private static bool IsAllowedWeaponTarget(RecoilBattleCombatSnapshot attacker,
            RecoilBattleCombatSnapshot target)
        {
            if (attacker.PreferredHeroTarget != Entity.Null)
                return target.Entity == attacker.PreferredHeroTarget;
            if (!attacker.Fight.Allows(attacker.Position, target.Position, attacker.Stats.Range)) return false;
            var mask = attacker.Stats.OnlyTargetCategoryMask;
            return mask == 0 || (target.Stats.TargetCategoryMask & mask) != 0;
        }

        private void Fire(int local, int index, int target, ref NativeArray<RecoilBattleEcsState> states)
        {
            if (!Attacks.IsCreated) return;
            var state = states[local];
            if (state.SalvoShotsRemaining > 0)
            {
                if (state.SalvoCooldown > 0f) return;
                state.SalvoShotsRemaining--;
                state.SalvoCooldown = state.SalvoShotsRemaining > 0
                    ? math.max(0.01f, state.Stats.SalvoDelay)
                    : 0f;
            }
            else
            {
                if (state.AttackCooldown > 0f) return;
                if (state.WindupPending == 0 && state.Stats.Windup > 0f)
                {
                    state.WindupPending = 1;
                    state.WindupCooldown = state.Stats.Windup;
                    states[local] = state;
                    return;
                }
                if (state.WindupPending != 0 && state.WindupCooldown > 0f) return;
                state.WindupPending = 0;
                state.SalvoShotsRemaining = math.max(1, state.Stats.SalvoSize) - 1;
                state.AttackCooldown = math.max(0.01f,
                    state.Stats.IsConfigured ? state.Stats.AttackInterval : AttackInterval);
                state.SalvoCooldown = state.SalvoShotsRemaining > 0
                    ? math.max(0.01f, state.Stats.SalvoDelay)
                    : 0f;
            }
            var attack = Attacks[index];
            attack.TargetIndex = target;
            attack.Damage = state.Stats.IsConfigured ? state.Stats.Damage : AttackDamage;
            attack.Projectile = (byte)(RangedUnits.IsCreated && RangedUnits[index] != 0 ? 1 : 0);
            Attacks[index] = attack;
            states[local] = state;
        }

        private static void HaltAtContact(int i, ref NativeArray<RecoilUnitNavigation> navigations,
            ref BufferAccessor<RecoilUnitWaypoint> paths)
        {
            var navigation = navigations[i];
            var path = paths[i];
            navigation.CombatApproach = 1;
            navigations[i] = navigation;
            if (navigation.HasDestination == 0 && navigation.PathPending == 0 && path.Length == 0) return;
            navigation.HasDestination = 0; navigation.PathPending = 0; navigation.WaypointIndex = 0; navigation.HasPathEnd = 0;
            navigation.RequestVersion++; navigation.MovementVersion++;
            path.Clear();
            navigations[i] = navigation;
        }
    }

    [BurstCompile]
    internal struct RecoilBuildCombatSnapshotJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        [ReadOnly] public ComponentTypeHandle<RecoilBattleEcsState> States;
        [ReadOnly] public ComponentTypeHandle<RecoilUnitPose> Poses;
        [ReadOnly] public ComponentTypeHandle<RecoilUnitCombatTiming> Timings;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [NativeDisableParallelForRestriction] public NativeArray<RecoilBattleCombatSnapshot> Snapshots;
        public NativeParallelMultiHashMap<int3, int>.ParallelWriter Cells;
        public float Now;
        public float DeltaTime;
        public float InitialDelta;
        public float CellSize;
        public byte SkipGridBuild;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var poses = chunk.GetNativeArray(ref Poses);
            var timings = chunk.GetNativeArray(ref Timings);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index)) continue;
                var state = states[i];
                var timing = timings[i];
                var hasTarget = state.Target != Entity.Null && Indices.TryGetValue(state.Target, out _);
                var target = -1;
                if (hasTarget) Indices.TryGetValue(state.Target, out target);
                Snapshots[index] = new RecoilBattleCombatSnapshot
                {
                    Entity = entities[i], LastAttacker = state.LastAttacker, Fight = timing.Fight,
                    PreferredHeroTarget = state.PreferredHeroTarget,
                    RecentAttackerValid = (byte)(RecoilCombatTargetWeight.IsRecentAttacker(
                        state.LastAttacker, state.LastAttackedTime, Now) ? 1 : 0),
                    Position = poses[i].Position, PreviousPosition = poses[i].PreviousPosition,
                    Velocity = poses[i].Velocity, Rotation = poses[i].Rotation,
                    Team = state.Team, Health = state.Health, Stats = state.Stats,
                    CurrentTargetIndex = target, AttackCooldown = state.AttackCooldown, Active = state.Active,
                    DeltaTime = timing.LastCombatUpdateTime > 0f ? math.max(DeltaTime, Now - timing.LastCombatUpdateTime) : InitialDelta,
                    NextTargetSearchTime = timing.NextTargetSearchTime,
                    SearchTarget = (byte)(!hasTarget && Now >= timing.NextTargetSearchTime ? 1 : 0)
                };
                if (SkipGridBuild != 0 || state.Active == 0 || state.Health <= 0) continue;
                var cell = (int2)math.floor(poses[i].Position.xz / CellSize);
                Cells.Add(new int3(cell.x, cell.y, state.Team), index);
            }
        }
    }

    [BurstCompile]
    internal struct RecoilBuildProjectileSnapshotJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        [ReadOnly] public ComponentTypeHandle<RecoilBattleEcsState> States;
        [ReadOnly] public ComponentTypeHandle<RecoilUnitPose> Poses;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [NativeDisableParallelForRestriction] public NativeArray<RecoilBattleCombatSnapshot> Snapshots;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
            in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var poses = chunk.GetNativeArray(ref Poses);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index)) continue;
                var snapshot = Snapshots[index];
                var state = states[i];
                var pose = poses[i];
                snapshot.Entity = entities[i];
                snapshot.Position = pose.Position;
                snapshot.PreviousPosition = pose.PreviousPosition;
                snapshot.Team = state.Team;
                snapshot.Health = state.Health;
                snapshot.Active = state.Active;
                Snapshots[index] = snapshot;
            }
        }
    }

    [BurstCompile]
    internal struct RecoilUpdateCombatGridJob : IJob
    {
        public NativeParallelHashSet<int3> ChangedCells;
        public NativeList<int> Survivors;
        public NativeArray<byte> ChangedIndices;
        public NativeParallelHashMap<int3, int> TeamCellCounts;
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Snapshots;
        public NativeParallelMultiHashMap<int3, int> Cells;
        public NativeArray<int3> PreviousCells;
        public NativeArray<byte> PreviousActive;
        // [0] = previous snapshot count; [1] = number of removals/additions in this update.
        public NativeArray<int> Metadata;
        public int Count;
        public float CellSize;

        public void Execute()
        {
            ChangedCells.Clear();
            var mutations = 0;
            var length = math.max(Count, Metadata[0]);
            for (var i = 0; i < length; i++)
            {
                var active = i < Count && Snapshots[i].Active != 0 && Snapshots[i].Health > 0;
                var next = int3.zero;
                if (active)
                {
                    var snapshot = Snapshots[i];
                    var cell = (int2)math.floor(snapshot.Position.xz / CellSize);
                    next = new int3(cell.x, cell.y, snapshot.Team);
                }
                var wasActive = PreviousActive[i] != 0;
                ChangedIndices[i] = 0;
                if (active && wasActive && math.all(PreviousCells[i] == next)) continue;
                ChangedIndices[i] = 1;
                if (wasActive)
                {
                    ChangedCells.Add(PreviousCells[i]);
                    RecoilCombatQuadOccupancy.EraseTeamUnit(ref TeamCellCounts, PreviousCells[i]);
                    mutations++;
                }
            }
            // Remove each affected key once, instead of rescanning its entire hash
            // bucket for every departing soldier. Restore survivors in reverse
            // insertion order so capped searches see exactly the original order.
            var scanned = 0;
            foreach (var key in ChangedCells)
            {
                Survivors.Clear();
                if (Cells.TryGetFirstValue(key, out var value, out var iterator))
                {
                    do
                    {
                        scanned++;
                        if (ChangedIndices[value] == 0) Survivors.AddNoResize(value);
                    } while (Cells.TryGetNextValue(out value, ref iterator));
                }
                Cells.Remove(key);
                for (var j = Survivors.Length - 1; j >= 0; j--) Cells.Add(key, Survivors[j]);
            }
            for (var i = 0; i < length; i++)
            {
                if (ChangedIndices[i] == 0) continue;
                var active = i < Count && Snapshots[i].Active != 0 && Snapshots[i].Health > 0;
                if (active)
                {
                    var snapshot = Snapshots[i];
                    var cell = (int2)math.floor(snapshot.Position.xz / CellSize);
                    var next = new int3(cell.x, cell.y, snapshot.Team);
                    Cells.Add(next, i);
                    RecoilCombatQuadOccupancy.AddTeamUnit(ref TeamCellCounts, next);
                    mutations++;
                    PreviousCells[i] = next;
                }
                PreviousActive[i] = (byte)(active ? 1 : 0);
            }
            Metadata[0] = Count;
            Metadata[1] = mutations;
            Metadata[2] = ChangedCells.Count();
            Metadata[3] = scanned;
        }
    }

    public struct RecoilBattleEcsState : IComponentData
    {
        public byte Airborne;
        public int Team;
        public int Health;
        public RecoilUnitCombatStats Stats;
        public float AttackCooldown;
        public float SalvoCooldown;
        public float WindupCooldown;
        public int SalvoShotsRemaining;
        public byte Active;
        public byte AttackingCastle;
        public byte WindupPending;
        public Entity Target;
        public Entity LastAttacker;
        public Entity PreferredHeroTarget;
        public float LastAttackedTime;
    }

    [BurstCompile]
    internal struct RecoilBattleCombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeParallelHashMap<int3, int> TeamCellCounts;
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        [ReadOnly] public NativeArray<int> ScheduledSoldiers;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> Cells;
        [ReadOnly] public NativeArray<byte> RangedUnits;
        public RecoilTerrainHeightField Heights;
        [WriteOnly] public NativeArray<RecoilBattleCombatResult> Results;
        public int MinCellX;
        public int MaxCellX;
        public int MinCellZ;
        public int MaxCellZ;
        public float CellSize;
        public float DetectionRangeSquared;
        public float AttackRangeSquared;
        public float AttackInterval;
        public int AttackDamage;
        public int BlueCount;
        public int RedCount;

        public void Execute(int index)
        {
            var soldierIndex = ScheduledSoldiers[index];
            var soldier = Soldiers[soldierIndex];
            var targetIndex = soldier.CurrentTargetIndex;
            var candidateChecks = 0;
            var targetWeightEvaluations = 0;
            var targetPriorityWeightSkips = 0;
            var avoidedCategoryCandidates = 0;
            var lineOfFireChecks = 0;
            var lineOfFireBlocked = 0;
            byte targetSearchPerformed = 0;
            var targetDistanceSquared = -1f;
            var enemyCount = soldier.Team == 0 ? RedCount : BlueCount;
            if (enemyCount == 0)
            {
                targetIndex = -1;
            }
            else
            {
                if (!IsValidEnemy(soldier, targetIndex, out var currentTarget, out targetDistanceSquared) ||
                    (targetIndex >= 0 && soldier.PreferredHeroTarget == Entity.Null && (!HasLineOfFire(soldierIndex, soldier, currentTarget,
                        ref lineOfFireChecks, ref lineOfFireBlocked))))
                {
                    targetIndex = -1;
                    targetDistanceSquared = -1f;
                }
                if (soldier.SearchTarget != 0 && soldier.PreferredHeroTarget == Entity.Null)
                {
                    targetSearchPerformed = 1;
                    var attackRange = soldier.Stats.IsConfigured
                        ? soldier.Stats.Range
                        : math.sqrt(math.max(0f, DetectionRangeSquared));
                    var fallbackDamagePerSecond = AttackDamage / math.max(0.01f, AttackInterval);
                    var targetDamagePerSecond = RecoilCombatTargetWeight.DamagePerSecond(
                        soldier.Stats, fallbackDamagePerSecond);
                    var flatOwnerForward = RecoilCombatFacing.FlatForward(soldier.Rotation);
                    var selectedTargetIndex = FindNearestEnemy(soldierIndex, soldier, flatOwnerForward, attackRange, targetDamagePerSecond,
                        out candidateChecks, out targetWeightEvaluations, out targetPriorityWeightSkips,
                        out avoidedCategoryCandidates, ref lineOfFireChecks, ref lineOfFireBlocked);
                    if (selectedTargetIndex >= 0)
                    {
                        targetIndex = selectedTargetIndex;
                        targetDistanceSquared = math.distancesq(
                            Soldiers[targetIndex].Position.xz, soldier.Position.xz);
                    }
                    else if (targetIndex < 0 || !IsAllowedWeaponTarget(soldier, Soldiers[targetIndex]))
                    {
                        targetIndex = -1;
                        targetDistanceSquared = -1f;
                    }
                }
            }

            var cooldown = math.max(0f, soldier.AttackCooldown - soldier.DeltaTime);
            var damageTargetIndex = -1;
            var damage = 0;
            if (targetIndex >= 0)
            {
                if (targetDistanceSquared < 0f)
                {
                    var offset = Soldiers[targetIndex].Position - soldier.Position;
                    offset.y = 0f;
                    targetDistanceSquared = math.lengthsq(offset);
                }
                if (targetDistanceSquared <= AttackRangeSquared && cooldown <= 0f)
                {
                    damageTargetIndex = targetIndex;
                    damage = AttackDamage;
                    cooldown = AttackInterval;
                }
            }

            Results[index] = new RecoilBattleCombatResult
            {
                TargetIndex = targetIndex,
                DamageTargetIndex = damageTargetIndex,
                Cooldown = cooldown,
                Damage = damage,
                CandidateChecks = candidateChecks,
                TargetWeightEvaluations = targetWeightEvaluations,
                TargetPriorityWeightSkips = targetPriorityWeightSkips,
                AvoidedCategoryCandidates = avoidedCategoryCandidates,
                AvoidedCategoryTargetSelected = targetIndex >= 0 && targetIndex < Soldiers.Length &&
                    RecoilCombatTargetWeight.IsAvoidedTargetCategory(soldier.Stats, Soldiers[targetIndex].Stats) ? 1 : 0,
                LineOfFireChecks = lineOfFireChecks,
                LineOfFireBlocked = lineOfFireBlocked,
                TargetSearchPerformed = targetSearchPerformed
            };
        }

        private int FindNearestEnemy(int soldierIndex, RecoilBattleCombatSnapshot soldier, float3 flatOwnerForward, float attackRange,
            float targetDamagePerSecond, out int candidateChecks, out int targetWeightEvaluations,
            out int targetPriorityWeightSkips, out int avoidedCategoryCandidates,
            ref int lineOfFireChecks, ref int lineOfFireBlocked)
        {
            candidateChecks = 0;
            targetWeightEvaluations = 0;
            targetPriorityWeightSkips = 0;
            avoidedCategoryCandidates = 0;
            var cell = ToCell(soldier.Position);
            var maxRadius = (int)math.ceil(math.sqrt(DetectionRangeSquared) / CellSize);
            if (!RecoilCombatQuadOccupancy.MayHaveEnemyUnits(TeamCellCounts, cell, maxRadius,
                soldier.Team == 0 ? 1 : 0)) return -1;
            var nearestIndex = -1;
            var nearestDistanceSquared = DetectionRangeSquared;
            var nearestPriority = int.MaxValue;
            var nearestAvoidedCategory = false;
            var nearestWeight = float.PositiveInfinity;

            for (var radius = 0; radius <= maxRadius; radius++)
            {
                var minX = cell.x - radius;
                var maxX = cell.x + radius;
                var minZ = cell.y - radius;
                var maxZ = cell.y + radius;
                if (minX > maxX || minZ > maxZ)
                    continue;

                for (var x = minX; x <= maxX; x++)
                {
                    CheckCell(soldierIndex, soldier, new int2(x, minZ), ref nearestIndex, ref nearestDistanceSquared,
                        ref nearestPriority, ref nearestWeight, flatOwnerForward, attackRange, targetDamagePerSecond, ref candidateChecks,
                        ref nearestAvoidedCategory,
                        ref targetWeightEvaluations, ref targetPriorityWeightSkips, ref avoidedCategoryCandidates, ref lineOfFireChecks,
                        ref lineOfFireBlocked);
                    if (maxZ != minZ)
                        CheckCell(soldierIndex, soldier, new int2(x, maxZ), ref nearestIndex, ref nearestDistanceSquared,
                            ref nearestPriority, ref nearestWeight, flatOwnerForward, attackRange, targetDamagePerSecond, ref candidateChecks,
                            ref nearestAvoidedCategory,
                            ref targetWeightEvaluations, ref targetPriorityWeightSkips, ref avoidedCategoryCandidates, ref lineOfFireChecks,
                            ref lineOfFireBlocked);
                }

                for (var z = minZ + 1; z < maxZ; z++)
                {
                    CheckCell(soldierIndex, soldier, new int2(minX, z), ref nearestIndex, ref nearestDistanceSquared,
                        ref nearestPriority, ref nearestWeight, flatOwnerForward, attackRange, targetDamagePerSecond, ref candidateChecks,
                        ref nearestAvoidedCategory,
                        ref targetWeightEvaluations, ref targetPriorityWeightSkips, ref avoidedCategoryCandidates, ref lineOfFireChecks,
                        ref lineOfFireBlocked);
                    if (maxX != minX)
                        CheckCell(soldierIndex, soldier, new int2(maxX, z), ref nearestIndex, ref nearestDistanceSquared,
                            ref nearestPriority, ref nearestWeight, flatOwnerForward, attackRange, targetDamagePerSecond, ref candidateChecks,
                            ref nearestAvoidedCategory,
                        ref targetWeightEvaluations, ref targetPriorityWeightSkips, ref avoidedCategoryCandidates, ref lineOfFireChecks,
                        ref lineOfFireBlocked);
                }

            }

            return nearestIndex;
        }

        private void CheckCell(
            int soldierIndex,
            RecoilBattleCombatSnapshot soldier,
            int2 cell,
            ref int nearestIndex,
            ref float nearestDistanceSquared,
            ref int nearestPriority,
            ref float nearestWeight,
            float3 flatOwnerForward,
            float attackRange,
            float targetDamagePerSecond,
            ref int candidateChecks,
            ref bool nearestAvoidedCategory,
            ref int targetWeightEvaluations,
            ref int targetPriorityWeightSkips,
            ref int avoidedCategoryCandidates,
            ref int lineOfFireChecks,
            ref int lineOfFireBlocked)
        {
            if (!IsCellInsideBounds(cell)) return;
            // Recoil's coarse QuadField occupancy is a conservative broad phase. Reject
            // fine cells in an empty 4x4 region before doing distance math and probing the
            // fine-grid multi-hash map; the count cannot hide a candidate because it covers
            // every fine cell in this coarse region for the opposing team.
            if (TeamCellCounts.IsCreated)
            {
                var enemyTeam = soldier.Team == 0 ? 1 : 0;
                var coarseCell = RecoilCombatQuadOccupancy.CoarseCell(
                    new int3(cell.x, cell.y, enemyTeam));
                if (!TeamCellCounts.TryGetValue(coarseCell, out var enemyCount) || enemyCount <= 0)
                    return;
            }
            var cellMin = new float2(cell.x, cell.y) * CellSize;
            var cellMax = cellMin + new float2(CellSize);
            var closestPoint = math.max(cellMin, math.min(soldier.Position.xz, cellMax));
            if (math.lengthsq(closestPoint - soldier.Position.xz) > DetectionRangeSquared)
                return;
            var enemyCell = new int3(cell.x, cell.y, soldier.Team == 0 ? 1 : 0);
            if (!Cells.TryGetFirstValue(enemyCell, out var candidateIndex, out var iterator))
                return;

            do
            {
                candidateChecks++;
                if (!IsValidEnemy(soldier, candidateIndex, out var candidate, out var distanceSquared)) continue;
                if (candidate.Stats.TargetPriority > nearestPriority)
                {
                    targetPriorityWeightSkips++;
                    continue;
                }
                var candidateAvoidedCategory = RecoilCombatTargetWeight.IsAvoidedTargetCategory(
                    soldier.Stats, candidate.Stats);
                // Once an eligible target at this priority exists, an avoided-category
                // candidate cannot replace it. Reject it before the terrain LOS traversal.
                if (candidate.Stats.TargetPriority == nearestPriority &&
                    nearestIndex >= 0 && candidateAvoidedCategory && !nearestAvoidedCategory)
                    continue;
                var candidateWeight = RecoilCombatTargetWeight.Calculate(
                    soldier.Position, candidate.Position, flatOwnerForward, targetDamagePerSecond,
                    candidate.Health, distanceSquared, attackRange,
                    RecoilCombatTargetWeight.DamageMultiplier(soldier.Stats, candidate.Stats),
                    RecoilCombatTargetWeight.PriorityMultiplier(soldier, candidate));
                targetWeightEvaluations++;
                if (!RecoilCombatTargetWeight.IsPreferredCandidate(candidate.Stats.TargetPriority,
                        candidateAvoidedCategory, candidateWeight, distanceSquared, nearestPriority,
                        nearestAvoidedCategory, nearestWeight, nearestDistanceSquared))
                    continue;
                if (!HasLineOfFire(soldierIndex, soldier, candidate, ref lineOfFireChecks, ref lineOfFireBlocked))
                    continue;
                if (candidateAvoidedCategory)
                    avoidedCategoryCandidates++;
                if (RecoilCombatTargetWeight.IsPreferredCandidate(candidate.Stats.TargetPriority,
                        candidateAvoidedCategory, candidateWeight, distanceSquared, nearestPriority,
                        nearestAvoidedCategory, nearestWeight, nearestDistanceSquared))
                {
                    nearestIndex = candidateIndex;
                    nearestDistanceSquared = distanceSquared;
                    nearestPriority = candidate.Stats.TargetPriority;
                    nearestAvoidedCategory = candidateAvoidedCategory;
                    nearestWeight = candidateWeight;
                }
            }
            while (Cells.TryGetNextValue(out candidateIndex, ref iterator));
        }

        private bool IsValidEnemy(RecoilBattleCombatSnapshot soldier, int candidateIndex,
            out RecoilBattleCombatSnapshot candidate, out float distanceSquared)
        {
            candidate = default;
            distanceSquared = float.PositiveInfinity;
            if (candidateIndex < 0 || candidateIndex >= Soldiers.Length)
                return false;

            candidate = Soldiers[candidateIndex];
            if (candidate.Active == 0 || candidate.Health <= 0 || candidate.Team == soldier.Team ||
                !IsAllowedWeaponTarget(soldier, candidate)) return false;
            distanceSquared = math.lengthsq(candidate.Position.xz - soldier.Position.xz);
            return soldier.PreferredHeroTarget == candidate.Entity || distanceSquared <= DetectionRangeSquared;
        }

        private bool HasLineOfFire(int soldierIndex, RecoilBattleCombatSnapshot attacker,
            RecoilBattleCombatSnapshot target, ref int lineOfFireChecks, ref int lineOfFireBlocked)
        {
            var arcHeight = RangedUnits.IsCreated && soldierIndex >= 0 && soldierIndex < RangedUnits.Length &&
                RangedUnits[soldierIndex] != 0
                ? RecoilCombatProjectileTrajectory.ArcHeight(attacker.Position.xz, target.Position.xz)
                : 0f;
            lineOfFireChecks++;
            var clear = Heights.HasLineOfFire(attacker.Position, target.Position, arcHeight);
            if (!clear) lineOfFireBlocked++;
            return clear;
        }

        private static bool IsAllowedWeaponTarget(RecoilBattleCombatSnapshot attacker,
            RecoilBattleCombatSnapshot target)
        {
            if (attacker.PreferredHeroTarget != Entity.Null)
                return target.Entity == attacker.PreferredHeroTarget;
            if (!attacker.Fight.Allows(attacker.Position, target.Position, attacker.Stats.Range)) return false;
            var mask = attacker.Stats.OnlyTargetCategoryMask;
            return mask == 0 || (target.Stats.TargetCategoryMask & mask) != 0;
        }

        private bool IsCellInsideBounds(int2 cell) =>
            cell.x >= MinCellX && cell.x <= MaxCellX &&
            cell.y >= MinCellZ && cell.y <= MaxCellZ;

        private int2 ToCell(float3 position) =>
            (int2)math.floor(position.xz / CellSize);
    }

    internal struct RecoilBattleCombatSnapshot
    {
        public RecoilFightCorridor Fight;
        public Entity Entity;
        public Entity LastAttacker;
        public Entity PreferredHeroTarget;
        public float3 Position;
        public float3 PreviousPosition;
        public float3 Velocity;
        public quaternion Rotation;
        public int Team;
        public int Health;
        public RecoilUnitCombatStats Stats;
        public int CurrentTargetIndex;
        public float AttackCooldown;
        public float DeltaTime;
        public float NextTargetSearchTime;
        public byte Active;
        public byte SearchTarget;
        public byte RecentAttackerValid;
    }

    internal struct RecoilBattleCombatResult
    {
        public int TargetIndex;
        public int DamageTargetIndex;
        public float Cooldown;
        public int Damage;
        public int CandidateChecks;
        public int TargetWeightEvaluations;
        public int TargetPriorityWeightSkips;
        public int AvoidedCategoryCandidates;
        public int AvoidedCategoryTargetSelected;
        public int LineOfFireChecks;
        public int LineOfFireBlocked;
        public byte TargetSearchPerformed;
    }

    public sealed partial class RecoilAutoBattleDemo : MonoBehaviour
    {
        private enum ManualSpawnTeamMode : byte
        {
            Both,
            Blue,
            Red
        }

        [System.Serializable]
        private sealed class LobbyUnitStatsSaveData
        {
            public RecoilUnitCombatStats[] Units;
            public int CastleHealth;
            // Retained for compatibility with JSON saved while the objective was the spawn target.
            public int SpawnTargetHealth;
        }

        private const string LobbyUnitStatsFileName = "recoil_unit_stats.csv";
        private const int DefaultCastleHealth = 100_000_000;
        private const int MinimumCastleHealth = 1_000_000;
        private const int MaximumCastleHealth = 500_000_000;

        private const int UnitsPerSpawnBatch = 500;
        private const int DefaultNewUnitsPerFrame = 4;
        private const int DefaultSpawnQueueRateFrames = 5;
        private const int DefaultMaxSpawnsPerFrame = 128;
        private const int VariantFormationColumns = 5;
        private const int CharacterTypeCount = 8;
        [SerializeField] private bool allowDifferentAlliedTypesToPassThrough = true;
        private const int UnitsPerManualSpawn = 500;
        private static bool NumberKeySpawningEnabled = false;
        private const float SpawnMinX = -310f;
        private const float SpawnMaxX = 320f;
        private const float SpawnMinZ = -250f;
        private const float SpawnMaxZ = 250f;
        private const float TargetGridCellSize = 4f;
        private static readonly UnityEngine.InputSystem.Key[] SpawnHotkeys =
        {
            UnityEngine.InputSystem.Key.Digit1, UnityEngine.InputSystem.Key.Digit2,
            UnityEngine.InputSystem.Key.Digit3, UnityEngine.InputSystem.Key.Digit4,
            UnityEngine.InputSystem.Key.Digit5, UnityEngine.InputSystem.Key.Digit6,
            UnityEngine.InputSystem.Key.Digit7, UnityEngine.InputSystem.Key.Digit8
        };
        private static readonly string[] CharacterTypeLabels =
        {
            "Cung binh",
            "Dân binh",
            "Giáo binh",
            "Kỵ binh",
            "Máy bắn đá",
            "Pháp sư",
            "Voi chiến",
            "Bộ binh"
        };
        private static readonly ProfilerMarker BattleUpdateMarker =
            new ProfilerMarker("RecoilBattle.Update");
        private static readonly ProfilerMarker CombatJobMarker =
            new ProfilerMarker("RecoilBattle.CombatBurstJob");
        private static readonly ProfilerMarker SnapshotBuildMarker =
            new ProfilerMarker("RecoilBattle.BuildSnapshotAndGrid");
        public float LastSnapshotBuildMilliseconds { get; private set; }
        public float PresentationTierUpdateMillisecondsLastFrame { get; private set; }
        private static readonly ProfilerMarker LifecycleMarker = new("RecoilBattle.Lifecycle");
        private static readonly ProfilerMarker RunCombatMarker = new("RecoilBattle.ScheduleCombat");
        private static readonly ProfilerMarker ResolveMarker = new("RecoilBattle.ResolveCombat");
        private static readonly ProfilerMarker PresentationMarker = new("RecoilBattle.Rendering.Prefab");
        private static readonly ProfilerMarker CastleAttacksMarker = new("RecoilBattle.CastleAttacks");
        private static readonly ProfilerMarker DepartureMarker = new("RecoilBattle.ScheduleMovementUpdates");
        private static readonly ProfilerMarker RefreshTargetsMarker = new("RecoilBattle.RefreshTargetsJob");
        private static readonly ProfilerMarker ResolveEcsMarker = new("RecoilBattle.ResolveNavigationJob");
        private static readonly ProfilerMarker ResolvePathsMarker = new("RecoilBattle.DispatchPathRequests");
        private static readonly ProfilerMarker ResolveMetricsMarker = new("RecoilBattle.CollectTargetMetrics");
        private static readonly ProfilerMarker DamageMarker = new("RecoilBattle.ApplyDamageJob");
        private static readonly ProfilerMarker ContactEffectsMarker = new("RecoilBattle.ContactEffects");
        private static readonly ProfilerMarker RosterRefreshMarker = new("RecoilBattle.RefreshRoster");
        private static readonly ProfilerMarker TeamScheduleMarker = new("RecoilBattle.ScheduleTeam");
        private static readonly ProfilerMarker ProjectileResolveMarker = new("RecoilBattle.ResolveProjectiles");
        private static readonly ProfilerMarker RetirementMarker = new("RecoilBattle.CompactRetired");
        [SerializeField] private Terrain terrain = null;
        [SerializeField] private RecoilTerrainMapData terrainMap = null;
        [SerializeField] private RecoilNavMeshPathService pathService = null;
        [SerializeField] private GameObject blueUnitTemplate = null;
        [SerializeField] private GameObject redUnitTemplate = null;
        [SerializeField] private GameObject[] blueUnitVariants = System.Array.Empty<GameObject>();
        [SerializeField] private GameObject[] redUnitVariants = System.Array.Empty<GameObject>();
        [SerializeField] private GameObject[] blueUnitsByType = new GameObject[CharacterTypeCount];
        [SerializeField] private GameObject[] redUnitsByType = new GameObject[CharacterTypeCount];
        [SerializeField] private RecoilUnitCombatStats[] lobbyUnitStats = new RecoilUnitCombatStats[CharacterTypeCount];
        [SerializeField, Min(1)] private int castleHealth = DefaultCastleHealth;
        [SerializeField] private Texture2D lobbyBackgroundImage;
        [SerializeField] private Texture2D lobbyTitleImage;
        [SerializeField] private Texture2D lobbyCursorTexture;
        [SerializeField] private Vector2 lobbyCursorDisplaySize = new(240f, 160f);
        [SerializeField] private Vector2 lobbyCursorSourceHotspot = new(500f, 144f);
        private bool _hideLobbyCursor;
        [SerializeField] private Texture2D battleRedHealthFrameImage;
        [SerializeField] private Texture2D battleBlueHealthFrameImage;
        [SerializeField] private Texture2D[] battleTopBorderImages = new Texture2D[3];
        [SerializeField] private string[] battleRedTopNames = new string[3];
        [SerializeField] private int[] battleRedTopScores = new int[3];
        [SerializeField] private string[] battleBlueTopNames = new string[3];
        [SerializeField] private int[] battleBlueTopScores = new int[3];
        [SerializeField] private Texture2D lobbyStartButtonImage;
        [SerializeField] private Texture2D lobbySettingsButtonImage;
        [SerializeField, Tooltip("Vị trí, scale, xoay của các phần tử ở sảnh (title, nút, đồng hồ, thanh tải). Sửa bằng \"Chỉnh giao diện sảnh\".")]
        private RecoilLobbyLayout lobbyLayout = new();
        [SerializeField, Tooltip("Vị trí, scale của đồng hồ trận và bể điểm. Sửa bằng \"Chỉnh UI thời gian + bể điểm\".")]
        private RecoilBattleHudLayout battleHudLayout = new();
        private GUIStyle _lobbyErrorStyle;
        [SerializeField] private Texture2D lobbyBackButtonImage;
        [SerializeField] private Texture2D[] lobbyUnitImages = new Texture2D[CharacterTypeCount];
        [SerializeField, Min(1)]
        private int maxNewUnitsPerFrame = DefaultNewUnitsPerFrame;
        [SerializeField, Min(1)]
        private int spawnQueueRateFrames = DefaultSpawnQueueRateFrames;
        [SerializeField, Min(1)]
        private int maxSpawnsPerFrame = DefaultMaxSpawnsPerFrame;
        [SerializeField, Min(1)] private int unitsPerVariantAtStart = 50;
        [Header("Render capacity test")]
        [SerializeField] private bool prepareArmyBeforeCombat = true;
        [SerializeField, Min(2)] private int preparedArmyTotal = 4800;
        [SerializeField, Min(0)] private int preparedSiegePerTeam = 10;
        [SerializeField] private bool useFightCorridors = true;
        [SerializeField, Min(0f)] private float fightLeash = 12f;
        [SerializeField, Min(1f)] private float preparedFormationHalfWidth = 120f;
        // Gathering limit: at most this many soldiers (heroes not counted) per side in the gathering before the battle
        // starts; more wait until the battle. The opening army itself is exactly what the formation config says.
        private const int MaximumStagedPerTeam = 1000;
        private int PreparedTypeCount(bool blue, int type) => PreparedTypeCounts(blue)[type];

        /// <summary>Soldiers per type for the opening army (the formation config), within the gathering limit.</summary>
        private int[] PreparedTypeCounts(bool blue)
        {
            var counts = new int[CharacterTypeCount];
            for (var type = 0; type < CharacterTypeCount; type++) counts[type] = RequestedPreparedTypeCount(blue, type);
            return LimitToTeamCapacity(counts);
        }

        /// <summary>
        /// Scales <paramref name="counts"/> to add up to <paramref name="total"/>, keeping their proportions (largest
        /// remainders get the leftover places). Types at 0 stay at 0.
        /// </summary>
        private static int[] ScaleToTotal(int[] counts, int total)
        {
            long sum = 0;
            foreach (var count in counts) sum += Mathf.Max(0, count);
            var result = new int[counts.Length];
            if (sum <= 0 || total <= 0) return result;
            var remainder = new double[counts.Length];
            var used = 0;
            for (var type = 0; type < counts.Length; type++)
            {
                var exact = Mathf.Max(0, counts[type]) * (double)total / sum;
                result[type] = (int)System.Math.Floor(exact);
                remainder[type] = counts[type] > 0 ? exact - result[type] : -1;
                used += result[type];
            }
            while (used < total)
            {
                var best = -1;
                for (var type = 0; type < counts.Length; type++)
                    if (counts[type] > 0 && (best < 0 || remainder[type] > remainder[best])) best = type;
                if (best < 0) break;
                result[best]++;
                remainder[best] -= 1;
                used++;
            }
            return result;
        }

        /// <summary>
        /// Keeps the side within <see cref="MaximumStagedPerTeam"/> soldiers: every type is reduced in proportion
        /// (largest remainders get the leftover places), so the army keeps its mix.
        /// </summary>
        private static int[] LimitToTeamCapacity(int[] counts)
        {
            long total = 0;
            foreach (var count in counts) total += Mathf.Max(0, count);
            return total <= MaximumStagedPerTeam ? counts : ScaleToTotal(counts, MaximumStagedPerTeam);
        }
        private int RequestedPreparedTypeCount(bool blue, int type)
        {
            if (_testHeroesOnly) return 0; // hero test: heroes only, no soldiers
            var customCount = CustomInitialTypeCount(type);
            if (customCount >= 0) return customCount;
            var total = Mathf.Max(2, preparedArmyTotal);
            var team = blue ? total / 2 : total - total / 2;
            var amount = team / CharacterTypeCount + (type < team % CharacterTypeCount ? 1 : 0);
            return type == 4 ? Mathf.Min(amount, Mathf.Max(0, preparedSiegePerTeam)) : amount;
        }
        private int PreparedTeamTarget(bool blue)
        {
            var total = 0;
            foreach (var count in PreparedTypeCounts(blue)) total += count;
            return total;
        }
        private int PreparedArmyTarget => PreparedTeamTarget(true) + PreparedTeamTarget(false);
        [SerializeField] private Transform blueSpawnTarget;
        [SerializeField] private Transform redSpawnTarget;
        [SerializeField] private Vector3 battleCenter = Vector3.zero;
        [SerializeField, Min(1f)] private float teamStartDistance = 18f;
        [SerializeField, Min(0.5f)] private float spawnSpacing = 0.7f;
        [SerializeField, Min(1)] private int spawnColumns = 20;
        [SerializeField, Min(0.5f)] private float attackRange = 1.5f;
        [SerializeField, Min(1f)] private float archerAttackRange = 36f;
        [SerializeField, Min(1f)] private float siegeAttackRange = 54f;
        [SerializeField, Min(0.05f)] private float attackInterval = 0.65f;
        [SerializeField, Min(1)] private int attackDamage = 20;
        [SerializeField, Min(0.1f)] private float retargetDistance = 2f;
        [SerializeField, Min(1f)] private float detectionRange = 24f;
        [SerializeField, Min(0.1f)] private float targetSearchInterval = 0.5f;
        [SerializeField, Min(2)] private int maxTargetSearchesPerFrame = 128;

        private readonly List<Soldier> _blueTeam = new();
        private readonly List<Soldier> _redTeam = new();
        private readonly HashSet<Entity> _scheduledSoldierEntities = new();
        private readonly Dictionary<GameObject, Queue<GameObject>> _blueUnitPools = new();
        private readonly Dictionary<GameObject, Queue<GameObject>> _redUnitPools = new();
        private RecoilVisualProjectilePool _visualProjectilePool;
        private readonly int[] _bluePresentationModelsCreatedByType = new int[CharacterTypeCount];
        private readonly int[] _redPresentationModelsCreatedByType = new int[CharacterTypeCount];
        private bool _templatesValidated;
        private bool _templateValidationFailed;
        private bool _lobbyStarted;
        private bool _lobbyStarting;
        private bool _settingsOpen;
        private bool _matchFinished;
        private int _blueCastleHealthRemaining;
        private int _redCastleHealthRemaining;
        private long _castleAttackCount;
        private long _castleDamageTotal;
        private int _castleAttackersInRangeLastTick;
        private int _castleTargetConflictsLastTick;
        private int _castleLineOfFireBlockedLastTick;
        private int _castleShotsLaunchedLastTick;
        private Texture2D _attributeRadar;
        private readonly float[] _radarValues = new float[8];
        private RecoilUnitCombatStats[] _lobbyStatLimits;
        private readonly Vector2[] _radarVertices = new Vector2[8];
        private readonly Color32[] _radarPixels = new Color32[256 * 256];
        private static readonly string[] RadarLabels =
            { "Máu", "Sát thương", "Tầm đánh", "Tốc đánh", "Di chuyển", "Armor", "P. Armor", "Sát thương lan" };
        private int _selectedLobbyUnitType;
        private string _lobbyError;
        private string _matchResult;
        private int _templatesValidatedCount;
        private int _templateValidationTotal;
        private string _templateValidationError;
        private int _newUnitsCreatedThisFrame;
        private bool _redPresentationFirst;
        [SerializeField]
        private bool useGpuBoneInstancing = false;
        [SerializeField] private bool useVatPresentation = true;
        [SerializeField]
        private bool useVatLod = false;
        private RecoilVatPrefabLibrary _vatLibrary;
        private RecoilVatPrefabLibrary _vatLodLibrary;
        private readonly HashSet<GameObject> _missingVatTemplates = new();
        internal bool UsesGpuBoneInstancing => !useVatPresentation && useGpuBoneInstancing && !RecoilGpuSkinnedCrowdRenderer.SuspendedForProfiling;

        private GameObject ResolvePresentationTemplate(GameObject template)
        {
            if (template != null && template.GetComponent<NhiQuoc.Tools.HeroVatPlayer>() != null) return template;
            if (!useVatPresentation) return template;
            if (useVatLod && RecoilVatPrefabLibrary.LodEnabled)
            {
                if (_vatLodLibrary == null) _vatLodLibrary = Resources.Load<RecoilVatPrefabLibrary>("RecoilVatLodPrefabs");
                var lod = _vatLodLibrary != null ? _vatLodLibrary.Find(template) : null;
                if (lod != null) return lod;
            }
            if (_vatLibrary == null) _vatLibrary = Resources.Load<RecoilVatPrefabLibrary>("RecoilVatPrefabs");
            var vat = _vatLibrary != null ? _vatLibrary.Find(template) : null;
            if (vat != null) return vat;
            if (_missingVatTemplates.Add(template)) Debug.LogWarning("VAT unavailable for " + template.name + "; using original prefab. Update the VAT game library after baking.", this);
            return template;
        }
        [SerializeField]
        private bool useRecoilJobChaining = true;
        [SerializeField]
        private bool suspendAnimatorsForProfiling = false;
        private readonly Dictionary<GameObject, Animator[]> _profilingAnimatorViews = new();
        private readonly Dictionary<Animator, bool> _profilingAnimatorStates = new();
        private RecoilGpuSkinnedCrowdRenderer _gpuSkinnedCrowd;
        private bool _gpuRendererChecked;
        public int GpuSkinnedRenderers => _gpuSkinnedCrowd != null ? _gpuSkinnedCrowd.RegisteredRenderers : 0;
        public int GpuSkinnedInstancesLastFrame => _gpuSkinnedCrowd != null ? _gpuSkinnedCrowd.DrawnInstances : 0;
        public int GpuSkinnedDrawCallsLastFrame => _gpuSkinnedCrowd != null ? _gpuSkinnedCrowd.DrawCalls : 0;
        private int _fullDetailSoldierCount;
        private readonly Queue<SelectedSpawnBatch> _blueSelectedSpawnQueue = new();
        private readonly Queue<SelectedSpawnBatch> _redSelectedSpawnQueue = new();
        private readonly List<Entity> _ownedSoldierEntities = new();
        private readonly List<Soldier> _combatSoldiers = new();
        private readonly List<float> _combatBodyRadiusCache = new();
        private readonly HashSet<long> _projectileVisitedCells = new();
        private readonly List<Soldier> _scheduledSoldiers = new();
        private readonly Dictionary<Entity, int> _combatIndicesByEntity = new();
        private bool _combatRosterDirty = true;
        private int _combatRosterRemovalsSinceBuild;
        private bool _projectileGridStale = true;
        private System.Predicate<Soldier> _deadSoldierPredicate;
        private System.Predicate<Soldier> _retiredSoldierPredicate;
        private bool _hasRetirements;
        private NativeArray<byte> _combatAliveFlags;
        private EntityQuery _aliveQuery;
        private bool _hasAliveQuery;
        private bool _useAliveFlags;
        public float LastRosterPruneMilliseconds { get; private set; }
        public float LastCombatResolveMilliseconds { get; private set; }
        public float ProjectileResolveMillisecondsLastFrame { get; private set; }
        public int ProjectileCandidateChecksLastFrame { get; private set; }
        public int ProjectileGridCellsVisitedLastFrame { get; private set; }
        public int ProjectileLinearSearchesLastFrame { get; private set; }
        public int ProjectileUnitHitsLastFrame { get; private set; }
        public int ProjectileImpactsLastFrame { get; private set; }
        public int ProjectileTerrainImpactsLastFrame { get; private set; }
        public int ProjectileLeadSolutionsLastFrame { get; private set; }
        public int ProjectileLeadFallbacksLastFrame { get; private set; }
        public int TargetWeightEvaluationsLastFrame { get; private set; }
        public int TargetSearchRequestsLastFrame { get; private set; }
        public int TargetSearchesPerformedLastFrame { get; private set; }
        public int TargetSearchesDeferredLastFrame { get; private set; }
        public int TargetPriorityWeightSkipsLastFrame { get; private set; }
        public int AvoidedCategoryCandidatesLastFrame { get; private set; }
        public int AvoidedCategoryTargetsSelectedLastFrame { get; private set; }
        public int StrategicLineOfFireChecksLastFrame { get; private set; }
        public int StrategicLineOfFireBlockedLastFrame { get; private set; }
        public int CloseTargetWeightEvaluationsLastFrame { get; private set; }
        public int CloseTargetPriorityWeightSkipsLastFrame { get; private set; }
        public int LineOfFireChecksLastFrame { get; private set; }
        public int LineOfFireBlockedLastFrame { get; private set; }
        public int PendingCombatProjectileCount => _pendingCombatProjectiles.Count;
        public int PendingSplashDamageCount => _pendingSplashDamageCount;
        public int CombatRosterBuildCount { get; private set; }
        private NativeArray<RecoilBattleCombatSnapshot> _combatSnapshots;
        private NativeArray<byte> _combatRangedUnits;
        private NativeArray<int> _scheduledCombatIndices;
        private NativeArray<int> _combatScheduleGenerations;
        private int _combatScheduleGeneration;
        private NativeList<int> _ecsPathRequests;
        private NativeArray<RecoilBattleCombatResult> _combatResults;
        private NativeReference<RecoilCombatResolveSummary> _resolveSummary;
        private NativeArray<int> _damageBySoldier;
        private NativeArray<int> _lastAttackerByTarget;
        private NativeParallelMultiHashMap<int3, int> _combatCells;
        private NativeParallelHashMap<int3, int> _combatTeamCellCounts;
        private NativeArray<int3> _combatPreviousCells;
        private NativeArray<byte> _combatPreviousActive;
        private NativeArray<int> _combatGridMetadata;
        private NativeParallelHashSet<int3> _combatChangedCells;
        private NativeList<int> _combatGridSurvivors;
        private NativeArray<byte> _combatChangedIndices;
        public int LastCombatGridChangedCells => _combatGridMetadata.IsCreated ? _combatGridMetadata[2] : 0;
        public int LastCombatGridScannedEntries => _combatGridMetadata.IsCreated ? _combatGridMetadata[3] : 0;
        public int LastCombatGridMutations => _combatGridMetadata.IsCreated ? _combatGridMetadata[1] : 0;
        public int CombatGridUnits => _combatSoldiers.Count;
        public int CombatGridRegions => _combatTeamCellCounts.IsCreated ? _combatTeamCellCounts.Count() : 0;
        public int CloseSearchesLastFrame { get; private set; }
        public int CloseEmptySkipsLastFrame { get; private set; }
        public int CloseCandidateChecksLastFrame { get; private set; }
        public int ContactAttacksLastFrame { get; private set; }
        private int _combatDebugFrame = -1;
        private NativeParallelHashMap<Entity, int> _combatEntityIndices;
        private NativeArray<Entity> _combatSnapshotEntities;
        private NativeArray<float> _combatBodyRadii;
        private NativeArray<RecoilContactAttack> _contactAttacks;
        private NativeList<int> _combatDeaths;
        private EntityQuery _damageQuery;
        private EntityQuery _closeTargetQuery;
        private EntityQuery _combatSnapshotQuery;
        private bool _hasCombatSnapshotQuery;
        private EntityQuery _projectileSnapshotQuery;
        private bool _hasProjectileSnapshotQuery;
        private JobHandle _combatJobHandle;
        private int _combatBufferCapacity;
        private int _blueTargetSearchCursor;
        private int _redTargetSearchCursor;
        private int _targetSearchBudgetFrame = -1;
        private int _targetSearchesBudgetedThisFrame;
        private float _combatMaxBodyRadius = 0.5f;
        private int _blueSpawnQueue;
        private int _redSpawnQueue;
        private int _blueSelectedSpawnTotal;
        private int _redSelectedSpawnTotal;
        private int _blueSpawnedTotal;
        private int _redSpawnedTotal;
        private int _blueCombatSpawnSlotIndex;
        private int _redCombatSpawnSlotIndex;
        private double _spawnBlueCredit;
        private bool _spawnFailureReported;
        private int _projectileDiagnosticsFrame = -1;
        private int _projectileLeadDiagnosticsFrame = -1;
        private float _nextSpawnAttemptTime;
        private int _combatUpdatesLastFrame;
        public float MaxMovementCommandAgeLastTick { get; private set; }
        public int MovementCommandOverdueUnitsLastTick { get; private set; }
        private int _targetCandidateChecksLastFrame;
        private bool _combatJobPending;
        private World _world;
        private EntityManager _entityManager;
        private EntityArchetype _soldierArchetype;
        private bool _hasEntityManager;
        private bool _previousCursorVisibility;
        private bool _hasPreviousCursorVisibility;
        private Texture2D _transparentSystemCursor;

        [SerializeField]
        private float heroGroundOffset = .6f;
        private const float HeroOccupiedRadius = 2f;
        private const float HeroHitRadius = 2f;

        private sealed class Soldier
        {
            public int HeroId;
            public string DonorName;
            public string DonorId;
            public NhiQuoc.Tools.HeroVatPlayer HeroVat;
            public float HeroAnimationUntil, NextHeroUlti;
            public int HeroAnimation = -1;
            public bool HeroRooted; // held in place while performing a skill
            public int DiagFired, DiagCasts, DiagWalkIns; // hero diagnostics counters
            public int TotalCasts; // skills cast since spawn (hero test tool)
            public float HeroUltiCooldown;
            public Vector3 FormationDestination;
            public bool FormationAssigned;
            public bool FormationArrived;
            public bool Retired;
            public bool FullDetailCounted;
            public byte CharacterType;
            public byte RangedUnitType;
            public bool IsBlue;
            public float VisualHeight;
            public float BodyRadius;
            public GameObject Template;
            public RecoilNavMeshPathAgent Agent;
            public RecoilCharacterPresentation Presentation;
            public RecoilVatAnimation Vat;
            public Collider[] Colliders;
            public RecoilUnitSimulation Simulation;
            public Entity Entity;
            public float AttackStartedAt = float.NegativeInfinity;
            public Vector3 AttackTargetPosition;
            public Soldier AttackTarget;
            public int CombatSlot = -1;
            public float LastMovementCommandTime;
            public RecoilCharacterPresentation VisualSource;
            public float NavigationVerticalOffset;
            public float VisualGroundCorrection;
        }

        private struct PendingCombatProjectile
        {
            public GameObject HeroVisual;
            public float CollisionRadius;
            public float FlightStartTime;
            /// <summary>True while the attack animation has not thrown the projectile yet (until FlightStartTime).</summary>
            public bool AwaitingRelease;
            public float LastProcessedTime;
            public float TravelDuration;
            public Vector3 FlightStartPoint;
            public float ArcHeight;
            public float ImpactTime;
            public Vector3 ImpactPoint;
            public Entity IntendedTarget;
            public Entity Attacker;
            public int Team;
            public int Damage;
            public float AreaRadius;
            public float AreaEdgeEffectiveness;
            public bool SecondaryDamage;
            public RecoilUnitCombatStats AttackerStats;
            public bool CastleTarget;
            public bool SiegeStone;
            public RecoilCastleTeam CastleTeam;
        }

        private struct PendingSplashDamage
        {
            public Soldier Target;
            public Entity Attacker;
            public long DueTick;
            public int Damage;
        }

        private struct ProjectileGridCellVisitor : IRecoilProjectileGridCellVisitor
        {
            public RecoilAutoBattleDemo Owner;
            public PendingCombatProjectile Projectile;
            public int EnemyTeam;
            public float2 SegmentStart;
            public float2 SegmentDelta;
            public Vector3 WorldStart;
            public Vector3 WorldEnd;
            public float BestT;
            public Soldier Best;
            public Vector3 BestPosition;

            public void VisitCell(int x, int z)
            {
                var key = ((long)x << 32) ^ (uint)z;
                if (!Owner._projectileVisitedCells.Add(key)) return;
                Owner.CheckProjectileGridCell(Projectile, x, z, EnemyTeam, SegmentStart,
                    SegmentDelta, WorldStart, WorldEnd, ref BestT, ref Best, ref BestPosition);
            }
        }

        private readonly List<PendingCombatProjectile> _pendingCombatProjectiles = new();
        private readonly List<PendingSplashDamage>[] _pendingSplashDamageBuckets =
            CreatePendingSplashDamageBuckets();
        private long _lastResolvedSplashDamageTick = long.MinValue;
        private int _pendingSplashDamageCount;
        private readonly List<Soldier> _projectileDeaths = new();

        private sealed class SelectedSpawnBatch
        {
            public int CharacterType;
            public int Remaining;
        }
        public int TargetCandidateChecksLastFrame => _targetCandidateChecksLastFrame;
        public int CombatUpdatesLastFrame => _combatUpdatesLastFrame;
        public int EcsSoldierEntityCount => _ownedSoldierEntities.Count;
        public int AliveSoldierCount => _blueTeam.Count + _redTeam.Count;
        public int BlueSoldierCount => _blueTeam.Count;
        public int RedSoldierCount => _redTeam.Count;
        public int BlueCastleHealthRemaining => _blueCastleHealthRemaining;
        public int RedCastleHealthRemaining => _redCastleHealthRemaining;
        public int FullDetailSoldierCount => _fullDetailSoldierCount;
        public RecoilTerrainMapData TerrainMap => terrainMap;
        internal RecoilTerrainHeightField PresentationHeightField => pathService != null ? pathService.TerrainHeightField : default;
        public bool CanControlCamera => _lobbyStarted && !_initialFormationLoading && !_matchFinished;
        public bool ShouldShowGameplayHud => !CapacityBenchmarkHidesUi && _lobbyStarted && !_initialFormationLoading && !_matchFinished && !_showBattleVitalsHud;

        public void CopyMinimapUnitPositions(List<Vector3> bluePositions, List<Vector3> redPositions)
        {
            if (bluePositions == null) throw new System.ArgumentNullException(nameof(bluePositions));
            if (redPositions == null) throw new System.ArgumentNullException(nameof(redPositions));
            bluePositions.Clear();
            redPositions.Clear();
            CopyMinimapTeamPositions(_blueTeam, bluePositions);
            CopyMinimapTeamPositions(_redTeam, redPositions);
        }

        private static void CopyMinimapTeamPositions(List<Soldier> team, List<Vector3> positions)
        {
            foreach (var soldier in team)
                if (soldier != null && !soldier.Retired &&
                    soldier.Simulation != null && soldier.Simulation.Exists)
                    positions.Add(soldier.Simulation.Position);
        }

        private bool _combatStarted = false;
        private bool _showBattleVitalsHud = true;
        private bool _clockDriven;
        private bool _hasStarted;
        private float _tickDelta = RecoilFixedTickAccumulator.Step;
        private float _tickTime;
        private float SimulationNow => _clockDriven ? _tickTime : Time.time;
        private float SimulationDelta => _clockDriven ? _tickDelta : Time.deltaTime;

        private void Awake()
        {
            BeginMatchScoring();
            DeactivateSceneTemplate(blueUnitTemplate);
            DeactivateSceneTemplate(redUnitTemplate);
            foreach (var template in blueUnitVariants) DeactivateSceneTemplate(template);
            foreach (var template in redUnitVariants) DeactivateSceneTemplate(template);
            foreach (var template in blueUnitsByType) DeactivateSceneTemplate(template);
            foreach (var template in redUnitsByType) DeactivateSceneTemplate(template);
        }

        private void DeactivateSceneTemplate(GameObject template)
        {
            // A scene template is a source, not a combatant. Hide it before agent Start can
            // create a standalone navigation entity with no team/health combat membership.
            if (template == null || !template.scene.IsValid()) return;
            if (template == gameObject || transform.IsChildOf(template.transform))
            {
                Debug.LogError("A soldier template cannot be the battle controller or its parent.", this);
                return;
            }
            template.SetActive(false);
        }

        private void Start()
        {

            if (terrainMap == null && pathService != null)
                terrainMap = pathService.ActiveTerrainMap;
            if (terrainMap != null && !terrainMap.HasValidData)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} has invalid baked map data.", this);
                enabled = false;
                return;
            }
            if (terrain == null && terrainMap == null)
                terrain = FindFirstObjectByType<Terrain>();

            if (terrainMap == null && (terrain == null || terrain.terrainData == null))
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires valid baked map data or a Terrain.", this);
                enabled = false;
                return;
            }

            if (pathService == null || !pathService.IsReady)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires an initialized path service.", this);
                enabled = false;
                return;
            }

            if (blueUnitTemplate == null || redUnitTemplate == null)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires both blue and red unit templates.", this);
                enabled = false;
                return;
            }
            InitializeLobbyUnitStats();
            LoadLobbyUnitStats();
            LoadCastleSettings();
            InitializeCombatRoles();
            LoadHeroSettings();
            RememberCsv(LobbyUnitStatsPath);
            RememberCsv(CastleSettingsPath);
            RememberCsv(HeroSettingsPath);
            RememberCsv(TikTokArmySettingsPath);

            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null || !_world.IsCreated)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires an initialized Entities world.", this);
                enabled = false;
                return;
            }

            _entityManager = _world.EntityManager;
            EnsureSpawnTargets();
            _soldierArchetype = _entityManager.CreateArchetype(
                typeof(RecoilBattleEcsState),
                typeof(RecoilUnitPose),
                typeof(RecoilUnitMovementSettings),
                typeof(RecoilUnitOrder),
                typeof(RecoilUnitNavigation),
                typeof(RecoilUnitCombatTiming),
                typeof(RecoilUnitWaypoint));
            _hasEntityManager = true;
            _combatSnapshotQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RecoilBattleEcsState>(), ComponentType.ReadOnly<RecoilUnitPose>(),
                ComponentType.ReadOnly<RecoilUnitCombatTiming>());
            _hasCombatSnapshotQuery = true;
            CreateProjectileSnapshotQuery();
            CreateCloseTargetQuery();
            _clockDriven = true;
            _hasStarted = true;
            RecoilSimulationClock.Register(this);
            StartCoroutine(ValidateUnitTemplates());
        }

        private void OnEnable()
        {
            TikTokUdpReceiver.OnEvent += HandleTikTokBattleEvent;
            if (Application.isPlaying)
            {
                TikTokUdpReceiver.EnsureListening();
                UpdateSystemCursorVisibility(); // the cursor setup applies to the whole game, battle included
                EnsureBattleAudioSources();
            }
            if (!_hasStarted || _world == null || !_world.IsCreated) return;
            _clockDriven = true;
            RecoilSimulationClock.Register(this);
        }

        private void ApplyLobbyCursor()
        {
            if (!_hasPreviousCursorVisibility)
            {
                _previousCursorVisibility = Cursor.visible;
                _hasPreviousCursorVisibility = true;
            }
            if (_transparentSystemCursor == null)
            {
                _transparentSystemCursor = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    name = "Transparent System Cursor",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _transparentSystemCursor.SetPixel(0, 0, Color.clear);
                _transparentSystemCursor.Apply(false, false);
            }
            Cursor.SetCursor(_transparentSystemCursor, Vector2.zero, CursorMode.Auto);
            Cursor.visible = false;
            _osCursorShown = false;
        }

        private void UpdateSystemCursorVisibility()
        {
            if (Application.isPlaying)
            {
                // Hidden (C key): the pointer is also locked at the screen center, so it cannot wander off the
                // window or stray to the edges; shown again, it is free.
                var lockState = _hideLobbyCursor && Application.isFocused ? CursorLockMode.Locked : CursorLockMode.None;
                if (Cursor.lockState != lockState) Cursor.lockState = lockState;
                // Settings > Setup chuột: the Windows pointer, or the cursor image drawn by Windows, instead of
                // the game-drawn one.
                if (UseOsCursor)
                {
                    ApplyOsCursor();
                    return;
                }
                if (!_hasPreviousCursorVisibility || Cursor.visible)
                    ApplyLobbyCursor();
                return;
            }

            RestoreSystemCursor();
        }

        private void RestoreSystemCursor()
        {
            if (!_hasPreviousCursorVisibility) return;
            Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = _previousCursorVisibility;
            _hasPreviousCursorVisibility = false;
            _osCursorShown = false;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus || !Application.isPlaying || !isActiveAndEnabled) return;
            _osCursorShown = false; // the OS may have reset the cursor while unfocused
            UpdateSystemCursorVisibility();
        }

        private void DrawLobbyCursor()
        {
            if (!Application.isPlaying || _hideLobbyCursor || lobbyCursorTexture == null ||
                Event.current.type != EventType.Repaint)
                return;

            UpdateSystemCursorVisibility();
            if (_osCursorShown) return; // Windows is drawing the cursor
            // Size and click point come from Settings > Setup chuột (defaults: the inspector values).
            var size = LobbyCursorSize;
            var hotspot = Vector2.Scale(LobbyCursorHotspot, size);
            var mouse = Mouse.current;
            if (mouse == null) return;
            var screenPosition = mouse.position.ReadValue();
            var guiPosition = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            var rect = new Rect(guiPosition - hotspot, size);
            var previousColor = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(rect, lobbyCursorTexture, ScaleMode.StretchToFill, true);
            GUI.color = previousColor;
        }

        private void OnDisable()
        {
            RestoreLobbyFramePacing();
            StopBattleAudio();
            if (_battleAudioRoot != null)
            {
                Destroy(_battleAudioRoot);
                _battleAudioRoot = null;
                _battleAmbienceSource = null;
                _armyMovementSource = null;
                _battleEffectsSource = null;
            }
            RestoreSystemCursor();
            ReleaseWindowsCursorImage();
            if (_transparentSystemCursor != null)
            {
                Destroy(_transparentSystemCursor);
                _transparentSystemCursor = null;
            }
            StopHeroIntros(false);
            SaveHeroSettings();
            _startCountdownEndsAt = 0;
            TikTokUdpReceiver.OnEvent -= HandleTikTokBattleEvent;
            RestoreProfilingAnimators();
            ReleaseSiegeImpactEffects();
            ReleaseAdvisorEffects();
            DisposeSpawnJobs();
            CancelPoolPrewarm();
            RecoilSimulationClock.Unregister(this);
            _clockDriven = false;
            DisposeAliveQuery();
            DisposeCombatBuffers();
            if (_hasCombatSnapshotQuery && _world != null && _world.IsCreated)
            {
                _combatSnapshotQuery.Dispose();
                if (_hasProjectileSnapshotQuery) _projectileSnapshotQuery.Dispose();
                _closeTargetQuery.Dispose();
                _damageQuery.Dispose();
            }
            _hasCombatSnapshotQuery = false;
            _hasProjectileSnapshotQuery = false;
        }

        private void OnDestroy()
        {
            ReleaseDonationAvatars();
            RestoreEndgameLighting();
            if (_fantasyLoadingFillMaterial != null) Destroy(_fantasyLoadingFillMaterial);
            if (_heroIdentityBackdrop != null) Destroy(_heroIdentityBackdrop);
            if (_heroIntroEdgeMaterial != null) Destroy(_heroIntroEdgeMaterial);
            if (_endgameNameGradient != null) Destroy(_endgameNameGradient);
            if (rankingFade != null) Destroy(rankingFade);
            if (rankingRowFade != null) Destroy(rankingRowFade);
            if (_endgameCamera != null && _endgameOriginalFar > 0) _endgameCamera.farClipPlane = _endgameOriginalFar;
            ReleaseSiegeImpactEffects();
            ReleaseAdvisorEffects();
            DisposeSpawnJobs();
            if (_attributeRadar != null) Destroy(_attributeRadar);
            DisposeAliveQuery();
            if (_combatJobPending)
            {
                _combatJobHandle.Complete();
                _combatJobPending = false;
            }

            DisposeCombatBuffers();
            if (!_hasEntityManager || _world == null || !_world.IsCreated)
                return;
            if (_hasCombatSnapshotQuery)
            {
                _combatSnapshotQuery.Dispose();
                if (_hasProjectileSnapshotQuery) _projectileSnapshotQuery.Dispose();
                _closeTargetQuery.Dispose();
                _damageQuery.Dispose();
                _hasCombatSnapshotQuery = false;
                _hasProjectileSnapshotQuery = false;
            }

            foreach (var soldier in _blueTeam)
            {
                soldier.Simulation.SetActive(false);
                if (soldier.Agent != null)
                    soldier.Agent.Bind(null);
            }
            foreach (var soldier in _redTeam)
            {
                soldier.Simulation.SetActive(false);
                if (soldier.Agent != null)
                    soldier.Agent.Bind(null);
            }

            for (var i = 0; i < _ownedSoldierEntities.Count; i++)
            {
                var entity = _ownedSoldierEntities[i];
                if (_entityManager.Exists(entity))
                    _entityManager.DestroyEntity(entity);
            }

            _ownedSoldierEntities.Clear();
        }

        private void Update()
        {
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (Application.isFocused && keyboard != null && keyboard.cKey.wasPressedThisFrame)
                _hideLobbyCursor = !_hideLobbyCursor;
            UpdateSystemCursorVisibility();
            RefreshExternalSettings();
            UpdateSiegeImpactEffects();
            UpdateAdvisorEffects();
            UpdateArmyMovementAudio();
            using (BattleUpdateMarker.Auto())
                UpdateBattle();
            UpdateInitialFormationLoading();
            UpdateHeroIntros();
            UpdateLobbyClock();
            UpdateLobbyFramePacing();
        }

        private void UpdateBattle()
        {
            if (!_hasEntityManager || _world == null || !_world.IsCreated)
            {
                Debug.LogError("Battle stopped because its Entities world is no longer available.", this);
                enabled = false;
                return;
            }

            if (!_templatesValidated)
                return;
            if (!_lobbyStarted || _matchFinished)
                return;

            if (!_initialFormationLoading) FlushTikTokReinforcements();
            UpdateStartCountdown();
            UpdateCombatLaunch();
            UpdateMatchTimer();

            var spawnKeyboard = UnityEngine.InputSystem.Keyboard.current;
            if (Application.isFocused && spawnKeyboard != null && spawnKeyboard.tabKey.wasPressedThisFrame)
                _showBattleVitalsHud = !_showBattleVitalsHud;
            // Quick result: R = red wins at once, B = blue wins at once.
            if (Application.isFocused && spawnKeyboard != null)
            {
                if (spawnKeyboard.rKey.wasPressedThisFrame) { FinishMatch("ĐỘI ĐỎ CHIẾN THẮNG", false); return; }
                if (spawnKeyboard.bKey.wasPressedThisFrame) { FinishMatch("ĐỘI XANH CHIẾN THẮNG", true); return; }
            }
            if (!_initialFormationLoading && Application.isFocused && spawnKeyboard != null)
            {
                for (var type = 0; type < CharacterTypeCount; type++)
                    if (NumberKeySpawningEnabled && spawnKeyboard[SpawnHotkeys[type]].wasPressedThisFrame)
                        QueueSelectedTypeSpawn(type);
            }

            if (!_initialFormationLoading && !_combatStarted && _lobbyStarting &&
                HasAliveSoldier(_blueTeam) && HasAliveSoldier(_redTeam))
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard != null && keyboard.lKey.wasPressedThisFrame)
                    TryStartBattle();
            }

            _newUnitsCreatedThisFrame = 0;
            UpdatePresentationTiers();
            _combatUpdatesLastFrame = 0;
            _targetCandidateChecksLastFrame = 0;
            TargetWeightEvaluationsLastFrame = 0;
            TargetSearchRequestsLastFrame = 0;
            TargetSearchesPerformedLastFrame = 0;
            TargetSearchesDeferredLastFrame = 0;
            TargetPriorityWeightSkipsLastFrame = 0;
            AvoidedCategoryCandidatesLastFrame = 0;
            AvoidedCategoryTargetsSelectedLastFrame = 0;
            if (_combatJobPending && !_combatJobHandle.IsCompleted)
                return;

            // The fixed-step clock resolves combat and advances logical projectiles in
            // SimulateFixedTick. Repeating these checks from rendered Update adds work
            // without advancing SimulationNow, and contaminates per-frame phase timing.
            if (!_clockDriven)
            {
                CompletePendingCombatJob();
                ProcessPendingCombatProjectiles();
            }
            if (!_clockDriven) PruneCombatRoster();
            ProcessSpawnQueue();
            if (!_clockDriven && !_combatStarted) UpdateAssembly();
            if (_clockDriven) return;
            if (!_combatStarted) return;
            RefreshCombatRoster();
            _scheduledSoldiers.Clear();
            _scheduledSoldierEntities.Clear();
            UpdateHeroDuelTargets();
            ScheduleTeamUpdates(_blueTeam);
            ScheduleTeamUpdates(_redTeam);
            if (_blueTeam.Count + _redTeam.Count > 0)
                RunCombatJob();

        }

        internal void SimulateFixedTick(float delta, float now)
        {
            if (!_hasEntityManager || _world == null || !_world.IsCreated) return;
            if (!_templatesValidated) return;
            if (!_lobbyStarted || _matchFinished) return;
            _tickDelta = delta;
            _tickTime = now;
            CompletePendingCombatJob();
            ProcessPendingCombatProjectiles();
            if (!_combatStarted)
            {
                if (!_lobbyStarting) return;
                UpdateAssembly();
                return;
            }
            using (LifecycleMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.Lifecycle)) PruneCombatRoster();
            RefreshCombatRoster();
            _scheduledSoldiers.Clear();
            _scheduledSoldierEntities.Clear();
            MaxMovementCommandAgeLastTick = 0f;
            MovementCommandOverdueUnitsLastTick = 0;
            UpdateHeroDuelTargets();
            ScheduleTeamUpdates(_blueTeam);
            ScheduleTeamUpdates(_redTeam);
            using (RunCombatMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.ScheduleCombat))
                if (_blueTeam.Count + _redTeam.Count > 0) RunCombatJob();
            // Resolve damage before the next fixed step, so all phases observe the same tick state.
            using (ResolveMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.ResolveCombat)) CompletePendingCombatJob();
            ProcessCastleAttacks();
            if (_matchFinished) return;
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.RetireCombat)) CompactRetiredSoldiers();
        }

        private void InitializeCastleCombatState()
        {
            ClearHeroSkillCasts();
            if (_heroPrefabs == null) _heroPrefabs = Resources.Load<RecoilHeroPrefabCatalog>("RecoilHeroPrefabs");
            _heroSkills = _heroPrefabs != null ? _heroPrefabs.Skills : null;
            _heroSkillsLoaded = true;
            _blueCastleHealthRemaining = castleHealth;
            _redCastleHealthRemaining = castleHealth;
            SetCastleVisualBurning(RecoilCastleTeam.Blue, false);
            SetCastleVisualBurning(RecoilCastleTeam.Red, false);
            _castleAttackCount = 0;
            _castleDamageTotal = 0;
            _pendingCombatProjectiles.Clear();
            for (var i = 0; i < _pendingSplashDamageBuckets.Length; i++)
                _pendingSplashDamageBuckets[i].Clear();
            _lastResolvedSplashDamageTick = long.MinValue;
            _pendingSplashDamageCount = 0;
            _projectileDeaths.Clear();
        }

        private void ProcessCastleAttacks()
        {
            using var castleMeasure = CastleAttacksMarker.Auto();
            _castleAttackersInRangeLastTick = 0;
            _castleTargetConflictsLastTick = 0;
            _castleLineOfFireBlockedLastTick = 0;
            _castleShotsLaunchedLastTick = 0;
            var blueMayAttackCastle = !HasAliveSoldier(_redTeam);
            var redMayAttackCastle = !HasAliveSoldier(_blueTeam);
            if (!blueMayAttackCastle && !redMayAttackCastle) return;
            var hasBlueCastle = TryGetCastleBounds(RecoilCastleTeam.Blue, out var blueCastleBounds);
            var hasRedCastle = TryGetCastleBounds(RecoilCastleTeam.Red, out var redCastleBounds);
            foreach (var soldier in _scheduledSoldiers)
            {
                var blue = soldier.IsBlue;
                if (blue ? !blueMayAttackCastle : !redMayAttackCastle) continue;
                var castleTeam = blue ? RecoilCastleTeam.Red : RecoilCastleTeam.Blue;
                if ((blue ? !hasRedCastle : !hasBlueCastle) ||
                    soldier.Simulation == null ||
                    soldier.Entity == Entity.Null) continue;
                var castleBounds = blue ? redCastleBounds : blueCastleBounds;
                // The snapshot was built after movement for this tick; combat resolve changes
                // facing/orders, not position. Far-away soldiers cannot hit the castle, so
                // avoid probing multiple EntityManager components for every marching unit.
                if (TryGetCombatSnapshot(soldier, out _, out var castleSnapshot))
                {
                    var range = castleSnapshot.Stats.IsConfigured ? castleSnapshot.Stats.Range :
                        soldier.RangedUnitType == 2 ? siegeAttackRange :
                        soldier.RangedUnitType == 1 ? archerAttackRange : attackRange;
                    var snapshotDistance = RecoilCastleMarch.DistanceToBounds(castleSnapshot.Position.xz,
                        new float2(castleBounds.center.x, castleBounds.center.z),
                        new float2(castleBounds.extents.x, castleBounds.extents.z));
                    if (snapshotDistance > CastleAttackRange(soldier, range)) continue;
                }
                // Combat resolution may retire an entity after this soldier was copied into
                // the scheduled list. Validate both ECS handles before reading its pose.
                if (!soldier.Simulation.Exists ||
                    !_entityManager.Exists(soldier.Entity) ||
                    !_entityManager.HasComponent<RecoilBattleEcsState>(soldier.Entity) ||
                    !soldier.Simulation.TryGetPose(out var castleAttackPose))
                {
                    soldier.Retired = true;
                    _hasRetirements = true;
                    continue;
                }
                var position = (Vector3)castleAttackPose.Position;
                var distance = RecoilCastleMarch.DistanceToBounds(
                    new float2(position.x, position.z),
                    new float2(castleBounds.center.x, castleBounds.center.z),
                    new float2(castleBounds.extents.x, castleBounds.extents.z));
                if (distance > CastleAttackRange(soldier, GetAttackRange(soldier))) continue;
                _castleAttackersInRangeLastTick++;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                if (state.Active == 0 || state.Health <= 0 || state.Airborne != 0) continue;
                if (state.Target != Entity.Null && _entityManager.Exists(state.Target) &&
                    _entityManager.GetComponentData<RecoilBattleEcsState>(state.Target).Health > 0)
                {
                    _castleTargetConflictsLastTick++;
                    continue;
                }
                if (state.AttackingCastle == 0)
                {
                    state.AttackingCastle = 1;
                    _entityManager.SetComponentData(soldier.Entity, state);
                    soldier.Simulation.StopMovement();
                    var order = _entityManager.GetComponentData<RecoilUnitOrder>(soldier.Entity);
                    order.Kind = RecoilUnitOrderKind.Attack;
                    order.Target = Entity.Null;
                    order.Destination = castleBounds.ClosestPoint(position);
                    _entityManager.SetComponentData(soldier.Entity, order);
                }

                // Skip terrain line tests while the weapon is reloading or winding up.
                // Recoil only needs a clear firing solution when a weapon is ready to fire.
                if (state.SalvoShotsRemaining > 0)
                {
                    if (state.SalvoCooldown > 0f)
                    {
                        continue;
                    }
                }
                else
                {
                    if (state.AttackCooldown > 0f)
                    {
                        continue;
                    }
                    if (state.WindupPending == 0 && state.Stats.Windup > 0f)
                    {
                        state.WindupPending = 1;
                        state.WindupCooldown = state.Stats.Windup;
                        _entityManager.SetComponentData(soldier.Entity, state);
                        continue;
                    }
                    if (state.WindupPending != 0 && state.WindupCooldown > 0f)
                    {
                        continue;
                    }
                }

                var targetPoint = castleBounds.ClosestPoint(position);
                targetPoint.y = castleBounds.center.y;
                var shotOrigin = soldier.RangedUnitType != 0
                    ? (float3)GetProjectileOrigin(soldier, (float3)position)
                    : (float3)position;
                var arcHeight = soldier.RangedUnitType != 0
                    ? RecoilCombatProjectileTrajectory.ArcHeight(
                        new float2(shotOrigin.x, shotOrigin.z), new float2(targetPoint.x, targetPoint.z))
                    : 0f;
                // Only shots need a clear line: a melee blow on the wall is not stopped by the ground's slope
                // (the line from its feet to the castle's middle height often runs into a raised castle base).
                if (soldier.RangedUnitType != 0 && !HasCombatLineOfFire(shotOrigin, targetPoint, arcHeight))
                {
                    _castleLineOfFireBlockedLastTick++;
                    continue;
                }
                var damage = state.Stats.IsConfigured ? state.Stats.Damage : attackDamage;
                if (soldier.HeroVat != null && _heroSkills != null && _heroSkills.Find(soldier.HeroId) != null)
                {
                    if (SimulationNow >= soldier.HeroAnimationUntil)
                        BeginHeroSkill(soldier, targetPoint, state.Stats, true, castleBounds);
                    continue;
                }
                var interval = state.Stats.IsConfigured ? state.Stats.AttackInterval : attackInterval;
                if (state.SalvoShotsRemaining > 0)
                {
                    state.SalvoShotsRemaining--;
                    state.SalvoCooldown = state.SalvoShotsRemaining > 0
                        ? Mathf.Max(0.01f, state.Stats.SalvoDelay)
                        : 0f;
                }
                else
                {
                    state.WindupPending = 0;
                    state.SalvoShotsRemaining = Mathf.Max(1, state.Stats.SalvoSize) - 1;
                    state.SalvoCooldown = state.SalvoShotsRemaining > 0
                        ? Mathf.Max(0.01f, state.Stats.SalvoDelay)
                        : 0f;
                    state.AttackCooldown = interval;
                }
                _entityManager.SetComponentData(soldier.Entity, state);
                _castleShotsLaunchedLastTick++;
                if (soldier.RangedUnitType != 0)
                {
                    var projectileOrigin = GetProjectileOrigin(soldier, (float3)position);
                    var speed = soldier.Presentation != null ? soldier.Presentation.ProjectileSpeed :
                        soldier.Vat != null ? soldier.Vat.ProjectileSpeed : 24f;
                    var horizontalDistance = Vector2.Distance(
                        new Vector2(projectileOrigin.x, projectileOrigin.z), new Vector2(targetPoint.x, targetPoint.z));
                    var travelTime = RecoilCombatProjectileTrajectory.TravelDuration(horizontalDistance, speed);
                    // Leaves when the animation throws it, like the stone / arrow that is drawn.
                    var launchTime = SimulationNow + ProjectileReleaseDelay(soldier);
                    _pendingCombatProjectiles.Add(new PendingCombatProjectile
                    {
                        FlightStartTime = launchTime,
                        LastProcessedTime = launchTime,
                        AwaitingRelease = launchTime > SimulationNow,
                        TravelDuration = travelTime,
                        FlightStartPoint = projectileOrigin,
                        ArcHeight = RecoilCombatProjectileTrajectory.ArcHeight(
                            new float2(projectileOrigin.x, projectileOrigin.z), new float2(targetPoint.x, targetPoint.z)),
                        ImpactTime = launchTime + travelTime,
                        ImpactPoint = targetPoint,
                        Attacker = soldier.Entity,
                        Team = blue ? 0 : 1,
                        Damage = damage,
                        AreaRadius = state.Stats.IsConfigured ? state.Stats.AreaRadius : 0f,
                        AreaEdgeEffectiveness = state.Stats.IsConfigured ? state.Stats.AreaEdgeEffectiveness : 0f,
                        SecondaryDamage = RecoilUnitDamage.UsesRangedArmor(state.Stats),
                        AttackerStats = state.Stats,
                        CastleTarget = true,
                        SiegeStone = soldier.RangedUnitType == 2,
                        CastleTeam = castleTeam
                    });
                }
                else
                {
                    if (blue) _redCastleHealthRemaining = Mathf.Max(0, _redCastleHealthRemaining - damage);
                    else _blueCastleHealthRemaining = Mathf.Max(0, _blueCastleHealthRemaining - damage);
                    _castleAttackCount++;
                    _castleDamageTotal += damage;
                }
                var pose = _entityManager.GetComponentData<RecoilUnitPose>(soldier.Entity);
                RecoilCombatFacing.FaceTarget(ref pose, targetPoint);
                _entityManager.SetComponentData(soldier.Entity, pose);
                PlayUnitAttackSound(soldier);
                // A Quân sư casting on the castle: same cast, orbs and burn as on a unit, aimed at the wall.
                if (damage > 0) OnAdvisorAttack(soldier, null, targetPoint, damage, castleTeam);
                soldier.Presentation?.PlayAttack(null, targetPoint);
                soldier.Vat?.RequestAttack(targetPoint);
                PlayHeroAttack(soldier, targetPoint);
            }
            if (_blueCastleHealthRemaining <= 0)
                FinishMatch("THÀNH XANH BỊ PHÁ — ĐỘI ĐỎ CHIẾN THẮNG", false);
            else if (_redCastleHealthRemaining <= 0)
                FinishMatch("THÀNH ĐỎ BỊ PHÁ — ĐỘI XANH CHIẾN THẮNG", true);
        }

        [SerializeField, Min(-1f), Tooltip("Tầm với thêm (m) của quân cận chiến khi đánh thành, cộng vào tầm đánh và bán kính " +
                                           "thân. -1 = tự tính theo ô bản đồ (đất đi được dừng cách tường thành tới khoảng 1 ô).")]
        private float castleMeleeExtraReach = -1f;

        /// <summary>
        /// Reach on a castle, from the soldier's centre to the castle footprint: its weapon range plus its body (it
        /// stands with its body against the wall), and for melee the strip of unwalkable ground left around the castle by
        /// the map's coarse cells, which a melee unit can never cross.
        /// </summary>
        private float CastleAttackRange(Soldier soldier, float range)
        {
            var body = soldier.Simulation != null && soldier.Simulation.Exists ? soldier.Simulation.Settings.BodyRadius : .5f;
            var reach = range + Mathf.Max(0f, body);
            if (soldier.RangedUnitType != 0) return reach;
            var extra = castleMeleeExtraReach >= 0f ? castleMeleeExtraReach
                : terrainMap != null ? Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) + .5f : 1.5f;
            return reach + extra;
        }

        private bool TryGetCastleBounds(RecoilCastleTeam team, out Bounds bounds)
        {
            for (var i = 0; terrainMap != null && i < terrainMap.CastleBlockCount; i++)
            {
                var castle = terrainMap.GetCastleBlock(i);
                if (castle.Team != team) continue;
                bounds = castle.WorldBounds;
                return true;
            }
            bounds = default;
            return false;
        }

        private void InitializeLobbyUnitStats()
        {
            System.Array.Resize(ref lobbyUnitStats, CharacterTypeCount);
            System.Array.Resize(ref lobbyUnitImages, CharacterTypeCount);
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                if (lobbyUnitStats[type].IsConfigured) continue;
                var blueTemplate = FindTemplateForType(true, type);
                var redTemplate = FindTemplateForType(false, type);
                var blueStats = blueTemplate != null ? blueTemplate.GetComponent<RecoilUnitStats>() : null;
                var redStats = redTemplate != null ? redTemplate.GetComponent<RecoilUnitStats>() : null;
                if (blueStats != null && blueStats.Stats.IsConfigured)
                    lobbyUnitStats[type] = blueStats.Stats;
                else if (redStats != null && redStats.Stats.IsConfigured)
                    lobbyUnitStats[type] = redStats.Stats;
                else
                    lobbyUnitStats[type] = new RecoilUnitCombatStats
                    {
                        MaxHealth = 100,
                        Damage = 20,
                        // Configured stats override the range in both combat and navigation.
                        // Preserve the unit type's range when generating default lobby stats.
                        Range = type == 4 ? siegeAttackRange : type == 0 ? archerAttackRange : attackRange,
                        AttackSpeed = 1f,
                        MoveSpeed = 3.5f
                    };
            }
        }

        private string LobbyUnitStatsPath => Path.Combine(Application.streamingAssetsPath, LobbyUnitStatsFileName);

        private void LoadLobbyUnitStats()
        {
            var path = LobbyUnitStatsPath;

            try
            {
                if (!File.Exists(path))
                {
                    var defaultPath = Path.Combine(Application.persistentDataPath, LobbyUnitStatsFileName);
                    if (File.Exists(defaultPath))
                    {
                        Directory.CreateDirectory(Application.streamingAssetsPath);
                        File.Copy(defaultPath, path, false);
                    }
                }
                LobbyUnitStatsSaveData save;
                if (File.Exists(path))
                {
                    var units = RecoilSettingsCsv.ReadUnits(File.ReadAllText(path), out var savedHealth, out var spawnHealth, lobbyUnitStats);
                    save = new LobbyUnitStatsSaveData { Units = units, CastleHealth = savedHealth, SpawnTargetHealth = spawnHealth };
                }
                else
                {
                    SaveLobbyUnitStats();
                    return;
                }
                if (save?.Units == null) return;
                var count = Mathf.Min(lobbyUnitStats.Length, save.Units.Length);
                for (var i = 0; i < count; i++)
                    if (save.Units[i].IsConfigured)
                        lobbyUnitStats[i] = save.Units[i];
                var savedCastleHealth = save.CastleHealth > 0 ? save.CastleHealth : save.SpawnTargetHealth;
                if (savedCastleHealth > 0)
                    castleHealth = Mathf.Clamp(savedCastleHealth, MinimumCastleHealth, MaximumCastleHealth);
                //Debug.Log($"Loaded unit settings from {path}", this);
                if (!File.Exists(path)) SaveLobbyUnitStats();
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"Could not load unit settings from {path}: {exception.Message}", this);
            }
        }

        private string _unitSettingsStatus = "";
        private void SaveLobbyUnitStats()
        {
            var path = LobbyUnitStatsPath;
            try
            {
                if (RefreshExternalCsv(LobbyUnitStatsPath) | RefreshExternalCsv(CastleSettingsPath)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                RecoilSettingsCsv.Save(path, RecoilSettingsCsv.WriteUnits(lobbyUnitStats));
                SaveCastleSettings();
                RememberCsv(LobbyUnitStatsPath);
                RememberCsv(CastleSettingsPath);
                _unitSettingsStatus = "Đã lưu cấu hình lính và máu thành.";
                Debug.Log($"Saved unit settings to {path}", this);
            }
            catch (System.Exception exception)
            {
                _unitSettingsStatus = "Không lưu được: " + exception.Message;
                Debug.LogError($"Could not save unit settings to {path}: {exception.Message}", this);
            }
        }

        private RecoilUnitCombatStats GetLobbyStatLimits(int type)
        {
            if (_lobbyStatLimits == null)
            {
                _lobbyStatLimits = new RecoilUnitCombatStats[CharacterTypeCount];
                for (var i = 0; i < CharacterTypeCount; i++)
                {
                    var initial = lobbyUnitStats[i];
                    // Freeze the scale before editing, so dragging never moves the maximum.
                    _lobbyStatLimits[i] = new RecoilUnitCombatStats
                    {
                        MaxHealth = Mathf.Max(2, initial.MaxHealth * 2),
                        Damage = Mathf.Max(10, initial.Damage * 2),
                        Range = Mathf.Max(1f, initial.Range * 2f),
                        AttackSpeed = Mathf.Max(1f, initial.AttackSpeed * 2f),
                        MoveSpeed = Mathf.Max(1f, initial.MoveSpeed * 2f),
                        Armor = 1f, SecondaryArmor = 1f,
                        AreaRadius = Mathf.Max(5f, initial.AreaRadius * 2f)
                    };
                }
            }
            return _lobbyStatLimits[type];
        }

        private void ScheduleTeamUpdates(List<Soldier> team)
        {
            using var measure = TeamScheduleMarker.Auto();
            using var departuresMeasure = DepartureMarker.Auto();
            var now = SimulationNow;
            var count = team.Count;
            for (var i = 0; i < count; i++)
            {
                var soldier = team[i];
                if (soldier == null || soldier.Retired || soldier.Entity == Entity.Null)
                    continue;
                var age = Mathf.Max(0f, now - soldier.LastMovementCommandTime);
                MaxMovementCommandAgeLastTick = Mathf.Max(MaxMovementCommandAgeLastTick, age);
                if (age > 2f)
                    MovementCommandOverdueUnitsLastTick++;
                // Like Recoil's movement update pass, do not hold units behind a
                // per-team departure/combat rotation. Burst work still batches execution.
                ScheduleMovementUpdate(soldier);
            }

        }

        private void ScheduleMovementUpdate(Soldier soldier)
        {
            if (soldier == null || soldier.Retired || soldier.Entity == Entity.Null ||
                !_scheduledSoldierEntities.Add(soldier.Entity))
                return;
            _scheduledSoldiers.Add(soldier);
            _combatUpdatesLastFrame++;
            soldier.LastMovementCommandTime = SimulationNow;
        }

        private void RunCombatJob()
        {
            if (_combatDebugFrame != Time.frameCount)
            {
                _combatDebugFrame = Time.frameCount;
                CloseSearchesLastFrame = CloseEmptySkipsLastFrame = CloseCandidateChecksLastFrame = ContactAttacksLastFrame = 0;
                CloseTargetWeightEvaluationsLastFrame = CloseTargetPriorityWeightSkipsLastFrame = 0;
                LineOfFireChecksLastFrame = LineOfFireBlockedLastFrame = 0;
                StrategicLineOfFireChecksLastFrame = StrategicLineOfFireBlockedLastFrame = 0;
            }
            if (!_hasCombatSnapshotQuery)
            {
                _combatSnapshotQuery = _entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<RecoilBattleEcsState>(), ComponentType.ReadOnly<RecoilUnitPose>(),
                    ComponentType.ReadOnly<RecoilUnitCombatTiming>());
                _hasCombatSnapshotQuery = true;
                CreateCloseTargetQuery();
            }
            RefreshCombatRoster();
            if (_combatSoldiers.Count == 0)
                return;
            // Chunk iteration reads each component column directly and builds the spatial grid in parallel.
            var snapshotJob = new RecoilBuildCombatSnapshotJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                Poses = _entityManager.GetComponentTypeHandle<RecoilUnitPose>(true),
                Timings = _entityManager.GetComponentTypeHandle<RecoilUnitCombatTiming>(true),
                Indices = _combatEntityIndices, Snapshots = _combatSnapshots, Cells = _combatCells.AsParallelWriter(),
                Now = SimulationNow, DeltaTime = SimulationDelta,
                InitialDelta = SimulationDelta, CellSize = TargetGridCellSize,
                SkipGridBuild = 1
            };
            var snapshotStart = RecoilCpuPhases.Timestamp();
            using (SnapshotBuildMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.SnapshotCombat))
            {
                var resetSnapshots = new RecoilResetCombatSnapshotsJob { Snapshots = _combatSnapshots }
                    .Schedule(_combatSoldiers.Count, 64);
                var snapshotHandle = snapshotJob.ScheduleParallel(_combatSnapshotQuery, resetSnapshots);
                if (!useRecoilJobChaining)
                    using (RecoilCpuPhases.Measure(RecoilCpuPhase.CaptureCombat)) snapshotHandle.Complete();
                var gridJob = new RecoilUpdateCombatGridJob
                {
                    ChangedCells = _combatChangedCells, Survivors = _combatGridSurvivors,
                    ChangedIndices = _combatChangedIndices,
                    TeamCellCounts = _combatTeamCellCounts,
                    Snapshots = _combatSnapshots, Cells = _combatCells, PreviousCells = _combatPreviousCells,
                    PreviousActive = _combatPreviousActive, Metadata = _combatGridMetadata,
                    Count = _combatSoldiers.Count, CellSize = TargetGridCellSize
                };
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.GridCombat))
                    gridJob.Schedule(useRecoilJobChaining ? snapshotHandle : default).Complete();
                _projectileGridStale = false;
            }
            var scheduledCount = 0;
            var slowPhase = (int)(GetSimulationTick() % RecoilCloseTargetJob.SlowTargetUpdateRate);
            var scheduleGeneration = NextCombatScheduleGeneration();
            for (var i = 0; i < _scheduledSoldiers.Count; i++)
            {
                var soldier = _scheduledSoldiers[i];
                var index = soldier.CombatSlot;
                if (index < 0 || index >= _combatSoldiers.Count || _combatSnapshotEntities[index] != soldier.Entity)
                    if (!_combatIndicesByEntity.TryGetValue(soldier.Entity, out index))
                    {
                        _scheduledSoldiers.RemoveAt(i--);
                        continue;
                    }
                _scheduledCombatIndices[scheduledCount++] = index;
                if (soldier.Entity.Index % RecoilCloseTargetJob.SlowTargetUpdateRate == slowPhase)
                    _combatScheduleGenerations[index] = scheduleGeneration;
            }
            var closeTargets = new RecoilCloseTargetJob
            {
                TeamCellCounts = _combatTeamCellCounts,
                Entities = _entityManager.GetEntityTypeHandle(),
                Poses = _entityManager.GetComponentTypeHandle<RecoilUnitPose>(false),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Navigations = _entityManager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Waypoints = _entityManager.GetBufferTypeHandle<RecoilUnitWaypoint>(false),
                Indices = _combatEntityIndices, SnapshotEntities = _combatSnapshotEntities,
                Soldiers = _combatSnapshots, ScheduledGenerations = _combatScheduleGenerations,
                ScheduleGeneration = scheduleGeneration,
                Cells = _combatCells, Heights = pathService.TerrainHeightField, CellSize = TargetGridCellSize,
                RangeSquared = Mathf.Max(attackRange, 4f) * Mathf.Max(attackRange, 4f),
                MeleeAttackRangeSquared = attackRange * attackRange,
                ArcherAttackRangeSquared = archerAttackRange * archerAttackRange,
                SiegeAttackRangeSquared = siegeAttackRange * siegeAttackRange,
                RangedUnits = _combatRangedUnits,
                Attacks = _contactAttacks, CombatDelta = SimulationDelta,
                AttackInterval = attackInterval, AttackDamage = attackDamage
            };
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.CloseCombat))
                closeTargets.ScheduleParallel(_closeTargetQuery, default).Complete();
            PrepareHeroSkillAttacks();

            // Preserve close-target acquisition without reading ECS once per soldier on the main thread.
            using (RefreshTargetsMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.RefreshCombat))
                _world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>().ScheduleRefreshTargets(
                    new RecoilRefreshCombatTargetsJob
                    {
                        Entities = _combatSnapshotEntities, Scheduled = _scheduledCombatIndices,
                        Indices = _combatEntityIndices, Snapshots = _combatSnapshots,
                        Count = scheduledCount, SlowPhase = slowPhase, Now = SimulationNow
                    }).Complete();
            LastSnapshotBuildMilliseconds = (float)((RecoilCpuPhases.Timestamp() - snapshotStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));

            // Preserve the configured cap and rotate the starting point so soldiers later
            // in the roster are not perpetually deferred when demand exceeds the budget.
            if (_targetSearchBudgetFrame != Time.frameCount)
            {
                _targetSearchBudgetFrame = Time.frameCount;
                _targetSearchesBudgetedThisFrame = 0;
            }
            var searchBudget = Mathf.Max(2, maxTargetSearchesPerFrame) - _targetSearchesBudgetedThisFrame;
            searchBudget = Mathf.Max(0, searchBudget);
            var totalSearchRequests = 0;
            for (var i = 0; i < scheduledCount; i++)
                if (_combatSnapshots[_scheduledCombatIndices[i]].SearchTarget != 0)
                    totalSearchRequests++;
            TargetSearchRequestsLastFrame += totalSearchRequests;
            var deferredSearches = RecoilTargetSearchBudget.Apply(_combatSnapshots,
                _scheduledCombatIndices, scheduledCount, searchBudget,
                ref _blueTargetSearchCursor, ref _redTargetSearchCursor);
            TargetSearchesDeferredLastFrame += deferredSearches;
            _targetSearchesBudgetedThisFrame += totalSearchRequests - deferredSearches;

            var combatJob = new RecoilBattleCombatJob
            {
                TeamCellCounts = _combatTeamCellCounts,
                Soldiers = _combatSnapshots,
                ScheduledSoldiers = _scheduledCombatIndices,
                Cells = _combatCells,
                RangedUnits = _combatRangedUnits,
                Heights = pathService.TerrainHeightField,
                Results = _combatResults,
                MinCellX = -1000000000,
                MaxCellX = 1000000000,
                MinCellZ = -1000000000,
                MaxCellZ = 1000000000,
                CellSize = TargetGridCellSize,
                DetectionRangeSquared = Mathf.Max(
                    detectionRange, Mathf.Max(archerAttackRange, siegeAttackRange)) *
                    Mathf.Max(detectionRange, Mathf.Max(archerAttackRange, siegeAttackRange)),
                AttackRangeSquared = attackRange * attackRange,
                AttackInterval = attackInterval,
                AttackDamage = 0, // Contact damage has its own all-unit pass.
                BlueCount = _blueTeam.Count,
                RedCount = _redTeam.Count
            };
            // Finish every main-thread snapshot edit before either reader is scheduled.
            var aggregate = new RecoilAggregateDamageJob
            {
                Attacks = _contactAttacks, Damage = _damageBySoldier,
                LastAttackerByTarget = _lastAttackerByTarget, Count = _combatSoldiers.Count,
                Soldiers = _combatSnapshots, Cells = _combatCells, CellSize = TargetGridCellSize
            }.Schedule();
            _combatJobHandle = aggregate;
            _combatJobPending = true;
            using (CombatJobMarker.Auto())
                _combatJobHandle = scheduledCount > 0
                    ? combatJob.Schedule(scheduledCount, 32, aggregate) : aggregate;
        }

        private void CreateCloseTargetQuery()
        {
            _damageQuery = _entityManager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>());
            _closeTargetQuery = _entityManager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>(),
                ComponentType.ReadWrite<RecoilUnitNavigation>(), ComponentType.ReadWrite<RecoilUnitWaypoint>(), ComponentType.ReadWrite<RecoilUnitPose>());
        }

        private void CompletePendingCombatJob()
        {
            if (!_combatJobPending) return;
            var start = RecoilCpuPhases.Timestamp();
            try { CompletePendingCombatJobCore(); }
            finally
            {
                LastCombatResolveMilliseconds = (float)((RecoilCpuPhases.Timestamp() - start) *
                    (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            }
        }

        private void CompletePendingCombatJobCore()
        {
            if (!_combatJobPending)
                return;

            _combatJobHandle.Complete();
            _combatJobPending = false;

            if (pathService.UsingTerrainMap)
            {
                _ecsPathRequests.Clear();
                var resolve = new RecoilResolveCombatJob
                {
                    Entities = _combatSnapshotEntities, Scheduled = _scheduledCombatIndices,
                    Snapshots = _combatSnapshots, Results = _combatResults,
                    Heights = pathService.TerrainHeightField, PathRequests = _ecsPathRequests.AsParallelWriter(),
                    Now = SimulationNow, SearchInterval = targetSearchInterval,
                    AttackRange = attackRange, ArcherAttackRange = archerAttackRange,
                    SiegeAttackRange = siegeAttackRange, RangedUnits = _combatRangedUnits,
                    RetargetDistance = retargetDistance, BlueGoal = GetCastleTargetCenter(true),
                    BlueMarchTarget = GetCastleMarchTarget(true), RedMarchTarget = GetCastleMarchTarget(false),
                    RedGoal = GetCastleTargetCenter(false), SoldierCount = _combatSoldiers.Count
                };
                if (!_resolveSummary.IsCreated) _resolveSummary = new NativeReference<RecoilCombatResolveSummary>(Allocator.Persistent);
                new RecoilBuildResolveSummaryJob { Soldiers = _combatSnapshots, Count = _combatSoldiers.Count,
                    Output = _resolveSummary }.Schedule().Complete();
                var summary = _resolveSummary.Value;
                resolve.RegionTargets = summary.RegionTargets;
                resolve.BlueAliveCount = summary.BlueAliveCount; resolve.RedAliveCount = summary.RedAliveCount;
                resolve.BlueRepresentative = summary.BlueRepresentative; resolve.RedRepresentative = summary.RedRepresentative;
                using (ResolveEcsMarker.Auto())
                _world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>()
                    .ScheduleResolve(resolve, _scheduledSoldiers.Count).Complete();
                if (RecoilCpuPhases.Enabled)
                using (ResolveMetricsMarker.Auto())
                for (var i = 0; i < _scheduledSoldiers.Count; i++)
                {
                    _targetCandidateChecksLastFrame += _combatResults[i].CandidateChecks;
                    TargetSearchesPerformedLastFrame += _combatResults[i].TargetSearchPerformed;
                    var result = _combatResults[i];
                    TargetWeightEvaluationsLastFrame += result.TargetWeightEvaluations;
                    TargetPriorityWeightSkipsLastFrame += result.TargetPriorityWeightSkips;
                    AvoidedCategoryCandidatesLastFrame += result.AvoidedCategoryCandidates;
                    AvoidedCategoryTargetsSelectedLastFrame += result.AvoidedCategoryTargetSelected;
                    StrategicLineOfFireChecksLastFrame += result.LineOfFireChecks;
                    StrategicLineOfFireBlockedLastFrame += result.LineOfFireBlocked;
                    LineOfFireChecksLastFrame += result.LineOfFireChecks;
                    LineOfFireBlockedLastFrame += result.LineOfFireBlocked;
                }
                // The callback-based path service is still a managed boundary. Only new
                // path requests cross it; state, orders and stop decisions were applied in ECS.
                using (ResolvePathsMarker.Auto())
                for (var i = 0; i < _ecsPathRequests.Length; i++)
                    _combatSoldiers[_ecsPathRequests[i]].Simulation.RequestPathFromEcs();
            }
            else for (var i = 0; i < _scheduledSoldiers.Count; i++)
            {
                var combatIndex = _scheduledCombatIndices[i];
                var soldier = _combatSoldiers[combatIndex];
                var result = _combatResults[i];
                if (RecoilCpuPhases.Enabled)
                {
                    _targetCandidateChecksLastFrame += result.CandidateChecks;
                    TargetSearchesPerformedLastFrame += result.TargetSearchPerformed;
                    TargetWeightEvaluationsLastFrame += result.TargetWeightEvaluations;
                    TargetPriorityWeightSkipsLastFrame += result.TargetPriorityWeightSkips;
                    AvoidedCategoryCandidatesLastFrame += result.AvoidedCategoryCandidates;
                    AvoidedCategoryTargetsSelectedLastFrame += result.AvoidedCategoryTargetSelected;
                    StrategicLineOfFireChecksLastFrame += result.LineOfFireChecks;
                    StrategicLineOfFireBlockedLastFrame += result.LineOfFireBlocked;
                    LineOfFireChecksLastFrame += result.LineOfFireChecks;
                    LineOfFireBlockedLastFrame += result.LineOfFireBlocked;
                }
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
                timing.LastCombatUpdateTime = SimulationNow;
                var target = result.TargetIndex >= 0
                    ? _combatSoldiers[result.TargetIndex]
                    : null;

                state.Target = target != null ? target.Entity : Entity.Null;
                _entityManager.SetComponentData(soldier.Entity, state);

                if (_combatSnapshots[combatIndex].SearchTarget != 0)
                    timing.NextTargetSearchTime = SimulationNow + targetSearchInterval;
                _entityManager.SetComponentData(soldier.Entity, timing);
                UpdateNavigationBridge(soldier, target);

            }

            _combatDeaths.Clear();
            using (DamageMarker.Auto())
            new RecoilApplyDamageJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Indices = _combatEntityIndices, Damage = _damageBySoldier,
                Deaths = _combatDeaths.AsParallelWriter(), LastAttackerByTarget = _lastAttackerByTarget,
                SnapshotEntities = _combatSnapshotEntities,
                Now = SimulationNow
            }.ScheduleParallel(_damageQuery, default).Complete();
            using (ContactEffectsMarker.Auto())
            for (var i = 0; i < _combatSoldiers.Count; i++)
            {
                var attack = _contactAttacks[i];
                if (RecoilCpuPhases.Enabled)
                {
                    CloseSearchesLastFrame += attack.SearchAttempted;
                    CloseEmptySkipsLastFrame += attack.EmptyRegionSkipped;
                    CloseCandidateChecksLastFrame += attack.CandidateChecks;
                    CloseTargetWeightEvaluationsLastFrame += attack.TargetWeightEvaluations;
                    CloseTargetPriorityWeightSkipsLastFrame += attack.TargetPriorityWeightSkips;
                    AvoidedCategoryCandidatesLastFrame += attack.AvoidedCategoryCandidates;
                    AvoidedCategoryTargetsSelectedLastFrame += attack.AvoidedCategoryTargetSelected;
                    LineOfFireChecksLastFrame += attack.LineOfFireChecks;
                    LineOfFireBlockedLastFrame += attack.LineOfFireBlocked;
                }
                if (attack.Damage > 0) ContactAttacksLastFrame++;
                if (attack.TargetIndex < 0 || attack.TargetIndex >= _combatSoldiers.Count)
                    continue;

                var attacker = _combatSoldiers[i];
                var target = _combatSoldiers[attack.TargetIndex];
                attacker.AttackStartedAt = SimulationNow;
                attacker.AttackTarget = target;
                attacker.AttackTargetPosition = _combatSnapshots[attack.TargetIndex].Position;
                if (attack.Damage > 0) OnAdvisorAttack(attacker, target, attacker.AttackTargetPosition, attack.Damage);
                var presentationTargetPosition = attacker.AttackTargetPosition;
                if (attack.Projectile != 0 && attack.Damage > 0)
                {
                    var origin = GetProjectileOrigin(attacker, _combatSnapshots[i].Position);
                    var speed = attacker.Presentation != null ? attacker.Presentation.ProjectileSpeed :
                        attacker.Vat != null ? attacker.Vat.ProjectileSpeed :
                        attacker.RangedUnitType == 2 ? 18f : 24f;
                    var targetSnapshot = _combatSnapshots[attack.TargetIndex];
                    // The projectile leaves when the animation throws it, like the stone / arrow that is drawn.
                    var releaseDelay = ProjectileReleaseDelay(attacker);
                    var targetAtRelease = targetSnapshot.Position + targetSnapshot.Velocity * releaseDelay;
                    Vector3 targetPosition;
                    if (RecoilCombatProjectileTrajectory.TryGetInterceptTime((float3)origin,
                            targetAtRelease, targetSnapshot.Velocity, speed, out var leadTime))
                    {
                        targetPosition = (Vector3)(targetAtRelease + targetSnapshot.Velocity * leadTime);
                        RecordProjectileLead(true);
                    }
                    else
                    {
                        targetPosition = target.Agent != null
                            ? target.Agent.transform.position
                            : (Vector3)targetSnapshot.Position;
                        RecordProjectileLead(false);
                    }
                    presentationTargetPosition = targetPosition;
                    var horizontalDistance = Vector2.Distance(
                        new Vector2(origin.x, origin.z), new Vector2(targetPosition.x, targetPosition.z));
                    var travelTime = RecoilCombatProjectileTrajectory.TravelDuration(horizontalDistance, speed);
                    var launchTime = SimulationNow + releaseDelay;
                    _pendingCombatProjectiles.Add(new PendingCombatProjectile
                    {
                        FlightStartTime = launchTime,
                        LastProcessedTime = launchTime,
                        AwaitingRelease = launchTime > SimulationNow,
                        TravelDuration = travelTime,
                        FlightStartPoint = origin,
                        ArcHeight = RecoilCombatProjectileTrajectory.ArcHeight(
                            new float2(origin.x, origin.z), new float2(targetPosition.x, targetPosition.z)),
                        ImpactTime = launchTime + travelTime,
                        ImpactPoint = targetPosition,
                        IntendedTarget = target.Entity,
                        SiegeStone = attacker.RangedUnitType == 2,
                        Attacker = attacker.Entity,
                        Team = attacker.IsBlue ? 0 : 1,
                        Damage = attack.Damage,
                        AreaRadius = _combatSnapshots[i].Stats.IsConfigured ? _combatSnapshots[i].Stats.AreaRadius : 0f,
                        AreaEdgeEffectiveness = _combatSnapshots[i].Stats.IsConfigured
                            ? _combatSnapshots[i].Stats.AreaEdgeEffectiveness : 0f,
                        SecondaryDamage = RecoilUnitDamage.UsesRangedArmor(_combatSnapshots[i].Stats),
                        AttackerStats = _combatSnapshots[i].Stats
                    });
                }
                if (attacker.Agent != null)
                {
                    PlayUnitAttackSound(attacker);
                    attacker.Presentation?.PlayAttack(target.Agent != null ? target.Agent.transform : null,
                        presentationTargetPosition);
                    attacker.Vat?.RequestAttack(presentationTargetPosition);
                    // Ground-targeted Ulti must not inherit a projectile's predicted intercept point.
                    PlayHeroAttack(attacker, target.Agent != null ? target.Agent.transform.position : target.Simulation.Position);
                }
            }
            for (var i = 0; i < _combatDeaths.Length; i++) KillSoldier(_combatSoldiers[_combatDeaths[i]]);
            ProcessHeroSkillHits();
        }

        private void ProcessPendingCombatProjectiles()
        {
            using var measure = ProjectileResolveMarker.Auto();
            if (_projectileDiagnosticsFrame != Time.frameCount)
            {
                _projectileDiagnosticsFrame = Time.frameCount;
                ProjectileResolveMillisecondsLastFrame = 0f;
                ProjectileCandidateChecksLastFrame = 0;
                ProjectileGridCellsVisitedLastFrame = 0;
                ProjectileLinearSearchesLastFrame = 0;
                ProjectileUnitHitsLastFrame = 0;
                ProjectileImpactsLastFrame = 0;
                ProjectileTerrainImpactsLastFrame = 0;
            }
            var resolveStart = RecoilCpuPhases.Timestamp();
            using var projectilePhase = RecoilCpuPhases.Measure(RecoilCpuPhase.ProjectileResolve);
            _projectileDeaths.Clear();
            ResolvePendingSplashDamages();
            if (_pendingCombatProjectiles.Count == 0)
            {
                foreach (var dead in _projectileDeaths) KillSoldier(dead);
                ProjectileResolveMillisecondsLastFrame += (float)((RecoilCpuPhases.Timestamp() - resolveStart) *
                    (1000.0 / System.Diagnostics.Stopwatch.Frequency));
                return;
            }
            if (_combatRosterDirty)
                RefreshCombatRoster();
            var projectileCount = _pendingCombatProjectiles.Count;
            var canRefreshProjectileSnapshot = _combatSnapshots.IsCreated &&
                _combatEntityIndices.IsCreated && _combatSnapshotEntities.IsCreated &&
                _combatCells.IsCreated && _combatTeamCellCounts.IsCreated &&
                _combatPreviousCells.IsCreated && _combatPreviousActive.IsCreated &&
                _combatGridMetadata.IsCreated && _combatChangedCells.IsCreated &&
                _combatChangedIndices.IsCreated && _combatGridSurvivors.IsCreated;
            if (projectileCount > 0 && _combatSoldiers.Count > 0 && canRefreshProjectileSnapshot)
                // Projectile sweeps need current unit cells and poses. Refresh both once per
                // projectile phase so the candidate grid cannot lag behind moving units.
                RefreshCombatSpatialGrid();
            var projectileGridStale = _projectileGridStale;
            var retainedProjectiles = 0;
            for (var projectileIndex = 0; projectileIndex < projectileCount; projectileIndex++)
            {
                var projectile = _pendingCombatProjectiles[projectileIndex];
                if (projectile.AwaitingRelease)
                {
                    if (SimulationNow < projectile.FlightStartTime)
                    {
                        // Still in the attacker's hands.
                        if (retainedProjectiles != projectileIndex)
                            _pendingCombatProjectiles[retainedProjectiles] = projectile;
                        retainedProjectiles++;
                        continue;
                    }
                    // An attacker killed before throwing never throws (no stone is drawn either).
                    if (!ProjectileAttackerAlive(projectile.Attacker)) continue;
                    projectile.AwaitingRelease = false;
                }
                if (projectileGridStale)
                    ProjectileLinearSearchesLastFrame++;
                var previousT = Mathf.Clamp01((projectile.LastProcessedTime - projectile.FlightStartTime) /
                    Mathf.Max(0.02f, projectile.TravelDuration));
                var currentT = Mathf.Clamp01((SimulationNow - projectile.FlightStartTime) /
                    Mathf.Max(0.02f, projectile.TravelDuration));
                var segmentStart = EvaluateProjectilePosition(projectile, previousT);
                var terrainImpactTime = currentT;
                var terrainImpact = pathService != null && pathService.TerrainHeightField.TryFindProjectileTerrainHitTime(
                    projectile.FlightStartPoint, projectile.ImpactPoint, projectile.ArcHeight,
                    previousT, currentT, out terrainImpactTime);
                var collisionEndT = terrainImpact ? terrainImpactTime : currentT;
                var segmentEnd = EvaluateProjectilePosition(projectile, collisionEndT);
                var hitPosition = segmentEnd;
                var hitUnit = FindProjectileUnitHit(projectile, segmentStart, segmentEnd,
                    projectileGridStale, out hitPosition);
                if (hitUnit != null)
                {
                    if (projectile.HeroVisual != null) GetOrCreateVisualProjectilePool().Despawn(projectile.HeroVisual);
                    ProjectileUnitHitsLastFrame++;
                    ProjectileImpactsLastFrame++;
                    projectile.ImpactPoint = hitPosition;
                    PlaySiegeImpact(projectile, hitPosition);
                    if (projectile.AreaRadius > 0f)
                        ApplyProjectileSplash(projectile, hitUnit, projectileGridStale);
                    else if (_entityManager.Exists(hitUnit.Entity))
                    {
                        var hitState = _entityManager.GetComponentData<RecoilBattleEcsState>(hitUnit.Entity);
                        ApplyProjectileDamage(hitUnit, hitState, projectile, _projectileDeaths);
                    }
                    continue;
                }

                if (terrainImpact)
                {
                    if (projectile.HeroVisual != null) GetOrCreateVisualProjectilePool().Despawn(projectile.HeroVisual);
                    ProjectileTerrainImpactsLastFrame++;
                    ProjectileImpactsLastFrame++;
                    projectile.ImpactPoint = segmentEnd;
                    PlaySiegeImpact(projectile, segmentEnd);
                    if (projectile.AreaRadius > 0f)
                        ApplyProjectileSplash(projectile, null, projectileGridStale);
                    continue;
                }

                projectile.LastProcessedTime = SimulationNow;
                if (SimulationNow >= projectile.ImpactTime)
                {
                    if (projectile.HeroVisual != null) GetOrCreateVisualProjectilePool().Despawn(projectile.HeroVisual);
                    PlaySiegeImpact(projectile, segmentEnd);
                    ProjectileImpactsLastFrame++;
                    if (projectile.CastleTarget)
                    {
                        if (projectile.AreaRadius > 0f)
                        {
                            projectile.ImpactPoint = segmentEnd;
                            ApplyProjectileSplash(projectile, null, projectileGridStale);
                        }
                        SetCastleVisualBurning(projectile.CastleTeam, true);
                        if (HasAliveSoldier(projectile.CastleTeam == RecoilCastleTeam.Blue ? _blueTeam : _redTeam)) continue;
                        if (projectile.CastleTeam == RecoilCastleTeam.Blue)
                            _blueCastleHealthRemaining = Mathf.Max(0, _blueCastleHealthRemaining - projectile.Damage);
                        else
                            _redCastleHealthRemaining = Mathf.Max(0, _redCastleHealthRemaining - projectile.Damage);
                        _castleAttackCount++;
                        _castleDamageTotal += projectile.Damage;
                    }
                    else if (projectile.AreaRadius > 0f)
                    {
                        projectile.ImpactPoint = segmentEnd;
                        ApplyProjectileSplash(projectile, null, projectileGridStale);
                    }
                    continue;
                }

                if (retainedProjectiles != projectileIndex)
                    _pendingCombatProjectiles[retainedProjectiles] = projectile;
                retainedProjectiles++;
            }
            if (retainedProjectiles < projectileCount)
                _pendingCombatProjectiles.RemoveRange(retainedProjectiles, projectileCount - retainedProjectiles);
            foreach (var dead in _projectileDeaths) KillSoldier(dead);
            ProjectileResolveMillisecondsLastFrame += (float)((RecoilCpuPhases.Timestamp() - resolveStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            if (_blueCastleHealthRemaining <= 0)
                FinishMatch("THÀNH XANH BỊ PHÁ — ĐỘI ĐỎ CHIẾN THẮNG", false);
            else if (_redCastleHealthRemaining <= 0)
                FinishMatch("THÀNH ĐỎ BỊ PHÁ — ĐỘI XANH CHIẾN THẮNG", true);
        }

        private void RefreshCombatSpatialGrid()
        {
            if (!_hasCombatSnapshotQuery)
            {
                _combatSnapshotQuery = _entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<RecoilBattleEcsState>(), ComponentType.ReadOnly<RecoilUnitPose>(),
                    ComponentType.ReadOnly<RecoilUnitCombatTiming>());
                _hasCombatSnapshotQuery = true;
                CreateCloseTargetQuery();
            }
            CreateProjectileSnapshotQuery();

            var snapshotStart = RecoilCpuPhases.Timestamp();
            var snapshotJob = new RecoilBuildProjectileSnapshotJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                Poses = _entityManager.GetComponentTypeHandle<RecoilUnitPose>(true),
                Indices = _combatEntityIndices, Snapshots = _combatSnapshots
            };
            using (SnapshotBuildMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.SnapshotCombat))
            {
                var snapshotHandle = snapshotJob.ScheduleParallel(_projectileSnapshotQuery, default);
                if (!useRecoilJobChaining) snapshotHandle.Complete();
                var gridJob = new RecoilUpdateCombatGridJob
                {
                    ChangedCells = _combatChangedCells, Survivors = _combatGridSurvivors,
                    ChangedIndices = _combatChangedIndices, TeamCellCounts = _combatTeamCellCounts,
                    Snapshots = _combatSnapshots, Cells = _combatCells,
                    PreviousCells = _combatPreviousCells, PreviousActive = _combatPreviousActive,
                    Metadata = _combatGridMetadata, Count = _combatSoldiers.Count,
                    CellSize = TargetGridCellSize
                };
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.GridCombat))
                    gridJob.Schedule(useRecoilJobChaining ? snapshotHandle : default).Complete();
                _projectileGridStale = false;
            }
            LastSnapshotBuildMilliseconds = (float)((RecoilCpuPhases.Timestamp() - snapshotStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        private void CreateProjectileSnapshotQuery()
        {
            if (_hasProjectileSnapshotQuery || !_hasEntityManager || _world == null || !_world.IsCreated)
                return;
            _projectileSnapshotQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RecoilBattleEcsState>(), ComponentType.ReadOnly<RecoilUnitPose>());
            _hasProjectileSnapshotQuery = true;
        }

        private void RecordProjectileLead(bool solved)
        {
            if (_projectileLeadDiagnosticsFrame != Time.frameCount)
            {
                _projectileLeadDiagnosticsFrame = Time.frameCount;
                ProjectileLeadSolutionsLastFrame = 0;
                ProjectileLeadFallbacksLastFrame = 0;
            }
            if (solved) ProjectileLeadSolutionsLastFrame++;
            else ProjectileLeadFallbacksLastFrame++;
        }

        private static Vector3 EvaluateProjectilePosition(PendingCombatProjectile projectile, float t)
        {
            return RecoilCombatProjectileTrajectory.Position(
                (float3)projectile.FlightStartPoint, (float3)projectile.ImpactPoint,
                t, projectile.ArcHeight);
        }

        /// <summary>Seconds from the start of <paramref name="soldier"/>'s attack until its projectile leaves.</summary>
        private static float ProjectileReleaseDelay(Soldier soldier)
        {
            if (soldier == null) return 0f;
            if (soldier.Vat != null) return Mathf.Max(0f, soldier.Vat.ProjectileReleaseDelay);
            if (soldier.Presentation != null) return Mathf.Max(0f, soldier.Presentation.ProjectileReleaseDelay);
            return 0f;
        }

        private bool ProjectileAttackerAlive(Entity attacker)
        {
            if (attacker == Entity.Null || !_entityManager.Exists(attacker) ||
                !_entityManager.HasComponent<RecoilBattleEcsState>(attacker)) return false;
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(attacker);
            return state.Active != 0 && state.Health > 0;
        }

        private Vector3 GetProjectileOrigin(Soldier soldier, float3 fallbackPosition)
        {
            if (soldier != null && soldier.Vat != null) return soldier.Vat.ProjectileOrigin;
            var spawnPoint = soldier != null && soldier.Presentation != null
                ? soldier.Presentation.ProjectileSpawnPoint
                : null;
            var unitPosition = soldier != null && soldier.Simulation != null && soldier.Simulation.Exists
                ? soldier.Simulation.Position
                : (Vector3)fallbackPosition;
            var muzzlePosition = spawnPoint != null ? (float3)spawnPoint.position : default;
            var hasMuzzle = spawnPoint != null;
            return (Vector3)RecoilCombatProjectileTrajectory.SelectOrigin(
                (float3)unitPosition, muzzlePosition, hasMuzzle);
        }

        private Soldier FindProjectileUnitHit(PendingCombatProjectile projectile,
            Vector3 segmentStart, Vector3 segmentEnd, bool forceLinearSearch, out Vector3 hitPosition)
        {
            var bestPosition = segmentEnd;
            var start = new float2(segmentStart.x, segmentStart.z);
            var end = new float2(segmentEnd.x, segmentEnd.z);
            var delta = end - start;
            var minX = Mathf.Min(segmentStart.x, segmentEnd.x);
            var maxX = Mathf.Max(segmentStart.x, segmentEnd.x);
            var minZ = Mathf.Min(segmentStart.z, segmentEnd.z);
            var maxZ = Mathf.Max(segmentStart.z, segmentEnd.z);
            var searchPadding = _combatMaxBodyRadius + Mathf.Max(.1f, projectile.CollisionRadius);
            var bestT = float.PositiveInfinity;
            Soldier best = null;

            if (!forceLinearSearch && _combatCells.IsCreated && TargetGridCellSize > 0f)
            {
                var minCellX = Mathf.FloorToInt((minX - searchPadding) / TargetGridCellSize);
                var maxCellX = Mathf.FloorToInt((maxX + searchPadding) / TargetGridCellSize);
                var minCellZ = Mathf.FloorToInt((minZ - searchPadding) / TargetGridCellSize);
                var maxCellZ = Mathf.FloorToInt((maxZ + searchPadding) / TargetGridCellSize);
                var enemyTeam = projectile.Team == 0 ? 1 : 0;
                var rectangleCellCount = (long)(maxCellX - minCellX + 1) * (maxCellZ - minCellZ + 1);
                var startCellX = Mathf.FloorToInt(start.x / TargetGridCellSize);
                var startCellZ = Mathf.FloorToInt(start.y / TargetGridCellSize);
                var endCellX = Mathf.FloorToInt(end.x / TargetGridCellSize);
                var endCellZ = Mathf.FloorToInt(end.y / TargetGridCellSize);
                var neighborExtent = Mathf.CeilToInt(searchPadding / TargetGridCellSize);
                var neighborhoodWidth = neighborExtent * 2 + 1;
                var ddaCellEstimate = ((long)Mathf.Abs(endCellX - startCellX) +
                    Mathf.Abs(endCellZ - startCellZ) + 1L) * neighborhoodWidth * neighborhoodWidth;

                if (ddaCellEstimate < rectangleCellCount)
                {
                    _projectileVisitedCells.Clear();
                    var visitor = new ProjectileGridCellVisitor
                    {
                        Owner = this, Projectile = projectile, EnemyTeam = enemyTeam,
                        SegmentStart = start, SegmentDelta = delta,
                        WorldStart = segmentStart, WorldEnd = segmentEnd,
                        BestT = bestT, Best = best, BestPosition = bestPosition
                    };
                    RecoilProjectileGridTraversal.Visit(start, end, TargetGridCellSize,
                        neighborExtent, ref visitor);
                    bestT = visitor.BestT;
                    best = visitor.Best;
                    bestPosition = visitor.BestPosition;
                }
                else
                {
                    for (var x = minCellX; x <= maxCellX; x++)
                    for (var z = minCellZ; z <= maxCellZ; z++)
                        CheckProjectileGridCell(projectile, x, z, enemyTeam, start, delta,
                            segmentStart, segmentEnd, ref bestT, ref best, ref bestPosition);
                }
            }
            else
                for (var i = 0; i < _combatSoldiers.Count; i++)
                    CheckProjectileSegmentCandidate(projectile, i, start, delta,
                        segmentStart, segmentEnd, ref bestT, ref best, ref bestPosition);

            hitPosition = bestPosition;
            return best;
        }

        private void CheckProjectileGridCell(PendingCombatProjectile projectile, int cellX, int cellZ,
            int enemyTeam, float2 start, float2 delta, Vector3 worldStart, Vector3 worldEnd,
            ref float bestT, ref Soldier best, ref Vector3 bestPosition)
        {
            ProjectileGridCellsVisitedLastFrame++;
            if (!_combatCells.TryGetFirstValue(new int3(cellX, cellZ, enemyTeam), out var index, out var iterator))
                return;
            do
            {
                CheckProjectileSegmentCandidate(projectile, index, start, delta,
                    worldStart, worldEnd, ref bestT, ref best, ref bestPosition);
            }
            while (_combatCells.TryGetNextValue(out index, ref iterator));
        }

        private void CheckProjectileSegmentCandidate(PendingCombatProjectile projectile, int index,
            float2 segmentStart, float2 segmentDelta,
            Vector3 worldStart, Vector3 worldEnd, ref float bestT, ref Soldier best,
            ref Vector3 bestPosition)
        {
            ProjectileCandidateChecksLastFrame++;
            if (index < 0 || index >= _combatSoldiers.Count) return;
            var candidate = _combatSoldiers[index];
            if (candidate == null || candidate.Retired || candidate.Entity == Entity.Null) return;
            var position = Vector3.zero;
            var previousPosition = float3.zero;
            var radius = 0.5f;
            if (_combatSnapshots.IsCreated && index < _combatSnapshots.Length &&
                _combatSnapshots[index].Entity == candidate.Entity)
            {
                var snapshot = _combatSnapshots[index];
                if (snapshot.Active == 0 || snapshot.Health <= 0 || snapshot.Team == projectile.Team)
                    return;
                position = snapshot.Position;
                previousPosition = snapshot.PreviousPosition;
                if (_combatBodyRadii.IsCreated && index < _combatBodyRadii.Length)
                    radius = _combatBodyRadii[index];
            }
            else
            {
                if (candidate.IsBlue == (projectile.Team == 0) ||
                    !_entityManager.Exists(candidate.Entity) ||
                    !_entityManager.HasComponent<RecoilBattleEcsState>(candidate.Entity) ||
                    !_entityManager.HasComponent<RecoilUnitPose>(candidate.Entity) ||
                    (candidate.Simulation != null && candidate.Simulation.SimulationWorld != _world)) return;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(candidate.Entity);
                if (state.Active == 0 || state.Health <= 0) return;
                var pose = _entityManager.GetComponentData<RecoilUnitPose>(candidate.Entity);
                position = pose.Position;
                previousPosition = pose.PreviousPosition;
                radius = _combatBodyRadii.IsCreated && index < _combatBodyRadii.Length
                    ? _combatBodyRadii[index]
                    : 0.5f;
            }
            var bodyHalfHeight = Mathf.Max(0.75f, radius * 2f);
            // Sweep in target-relative space so units moving across the projectile path
            // during this fixed tick cannot pass through the projectile between snapshots.
            var relativeStart = (float3)worldStart - previousPosition;
            var relativeEnd = (float3)worldEnd - (float3)position;
            if (!RecoilProjectileSweep.TryGetBodyHitTime(relativeStart, relativeEnd,
                float3.zero, radius, bodyHalfHeight, projectile.CollisionRadius > 0 ? projectile.CollisionRadius : .08f, out var t) || t >= bestT) return;
            bestT = t;
            best = candidate;
            bestPosition = Vector3.Lerp(worldStart, worldEnd, t);
        }

        private void ApplyProjectileSplash(PendingCombatProjectile projectile, Soldier directHit,
            bool forceLinearSearch)
        {
            if (projectile.AreaRadius <= 0f) return;
            var cellSize = Mathf.Max(0.01f, TargetGridCellSize);
            var cell = (int2)math.floor(new float2(projectile.ImpactPoint.x, projectile.ImpactPoint.z) /
                cellSize);
            var extent = Mathf.CeilToInt((projectile.AreaRadius + _combatMaxBodyRadius) / cellSize);
            var enemyTeam = projectile.Team == 0 ? 1 : 0;
            if (!forceLinearSearch && _combatCells.IsCreated && TargetGridCellSize > 0f)
            {
                for (var x = -extent; x <= extent; x++)
                for (var z = -extent; z <= extent; z++)
                {
                    if (!_combatCells.TryGetFirstValue(new int3(cell.x + x, cell.y + z, enemyTeam),
                        out var index, out var iterator)) continue;
                    do { ApplySplashCandidate(index, projectile, directHit); }
                    while (_combatCells.TryGetNextValue(out index, ref iterator));
                }
            }
            else
                for (var i = 0; i < _combatSoldiers.Count; i++)
                    ApplySplashCandidate(i, projectile, directHit);

            // A direct body collision always damages that unit, even when its center lies
            // outside the splash radius. It is excluded from the radial pass to avoid double damage.
            if (directHit != null)
            {
                var hasSnapshot = TryGetCombatSnapshot(directHit, out var directIndex,
                    out var directSnapshot);
                var directPosition = Vector3.zero;
                RecoilBattleEcsState directState;
                if (hasSnapshot)
                {
                    if (directSnapshot.Active == 0 || directSnapshot.Health <= 0) return;
                    directPosition = directSnapshot.Position;
                    directState = _entityManager.GetComponentData<RecoilBattleEcsState>(directHit.Entity);
                }
                else if (directHit.Entity != Entity.Null && _entityManager.Exists(directHit.Entity) &&
                         _entityManager.HasComponent<RecoilBattleEcsState>(directHit.Entity) &&
                         _entityManager.HasComponent<RecoilUnitPose>(directHit.Entity))
                {
                    directState = _entityManager.GetComponentData<RecoilBattleEcsState>(directHit.Entity);
                    if (directState.Active == 0 || directState.Health <= 0) return;
                    directPosition = _entityManager.GetComponentData<RecoilUnitPose>(directHit.Entity).Position;
                    directIndex = -1;
                }
                else return;

                var bodyRadius = _combatBodyRadii.IsCreated && directIndex >= 0 &&
                    directIndex < _combatBodyRadii.Length
                    ? _combatBodyRadii[directIndex]
                    : GetCombatBodyRadius(directHit);
                var surfaceDistance = Mathf.Max(0f,
                    Vector3.Distance(directPosition, projectile.ImpactPoint) - bodyRadius);
                QueueOrApplySplashDamage(directHit, directState, projectile, surfaceDistance);
                TossFromSiegeBlast(directHit, directPosition, projectile, surfaceDistance);
            }
        }

        private void ApplySplashCandidate(int index, PendingCombatProjectile projectile, Soldier directHit)
        {
            if (index < 0 || index >= _combatSoldiers.Count) return;
            var target = _combatSoldiers[index];
            if (target == null || target.Retired || target.Entity == Entity.Null || target == directHit) return;
            var snapshotIndex = -1;
            var position = Vector3.zero;
            RecoilBattleEcsState state;
            if (TryGetCombatSnapshot(target, out snapshotIndex, out var snapshot))
            {
                if (snapshot.Team == projectile.Team || snapshot.Active == 0 || snapshot.Health <= 0) return;
                position = snapshot.Position;
                state = _entityManager.GetComponentData<RecoilBattleEcsState>(target.Entity);
            }
            else
            {
                if (!RecoilProjectileSweep.CanDamageSplashTeam(projectile.Team, target.IsBlue ? 0 : 1) ||
                    !_entityManager.Exists(target.Entity) ||
                    !_entityManager.HasComponent<RecoilBattleEcsState>(target.Entity) ||
                    !_entityManager.HasComponent<RecoilUnitPose>(target.Entity) ||
                    (target.Simulation != null && target.Simulation.SimulationWorld != _world)) return;
                state = _entityManager.GetComponentData<RecoilBattleEcsState>(target.Entity);
                if (state.Active == 0 || state.Health <= 0) return;
                position = _entityManager.GetComponentData<RecoilUnitPose>(target.Entity).Position;
            }
            var bodyRadius = _combatBodyRadii.IsCreated && snapshotIndex >= 0 &&
                snapshotIndex < _combatBodyRadii.Length
                ? _combatBodyRadii[snapshotIndex]
                : GetCombatBodyRadius(target);
            var surfaceDistance = Mathf.Max(0f, Vector3.Distance(position, projectile.ImpactPoint) - bodyRadius);
            QueueOrApplySplashDamage(target, state, projectile, surfaceDistance);
            TossFromSiegeBlast(target, position, projectile, surfaceDistance);
        }

        private void QueueOrApplySplashDamage(Soldier target, RecoilBattleEcsState state,
            PendingCombatProjectile projectile, float surfaceDistance)
        {
            var damageScale = RecoilProjectileSweep.SplashDamageMultiplier(surfaceDistance,
                projectile.AreaRadius, projectile.AreaEdgeEffectiveness);
            if (damageScale <= 0f) return;
            var damage = RecoilUnitDamage.Calculate(projectile.Damage * damageScale,
                projectile.AttackerStats, state.Stats);
            if (damage <= 0) return;

            var speed = RecoilCombatProjectileTrajectory.DefaultExplosionSpeed(projectile.Damage);
            var delayFrames = RecoilCombatProjectileTrajectory.SplashDamageDelayFrames(surfaceDistance, speed);
            if (delayFrames <= 0)
            {
                ApplyProjectileDamage(target, state, projectile, _projectileDeaths, damageScale);
                return;
            }
            var dueTick = GetSimulationTick() + delayFrames;
            var bucket = RecoilExplosionDamageSchedule.BucketIndex(dueTick);
            _pendingSplashDamageBuckets[bucket].Add(new PendingSplashDamage
            {
                Target = target,
                Attacker = projectile.Attacker,
                DueTick = dueTick,
                Damage = damage
            });
            _pendingSplashDamageCount++;
        }

        private void ResolvePendingSplashDamages()
        {
            var currentTick = GetSimulationTick();
            if (_lastResolvedSplashDamageTick == long.MinValue)
                _lastResolvedSplashDamageTick = currentTick - 1;
            if (currentTick <= _lastResolvedSplashDamageTick) return;

            var firstTick = RecoilExplosionDamageSchedule.FirstTickToProcess(
                _lastResolvedSplashDamageTick, currentTick);
            for (var tick = firstTick; tick <= currentTick; tick++)
            {
                var bucket = _pendingSplashDamageBuckets[RecoilExplosionDamageSchedule.BucketIndex(tick)];
                var retained = 0;
                var count = bucket.Count;
                for (var i = 0; i < count; i++)
                {
                    var pending = bucket[i];
                    if (pending.DueTick > currentTick)
                    {
                        if (retained != i) bucket[retained] = pending;
                        retained++;
                        continue;
                    }
                    _pendingSplashDamageCount = Mathf.Max(0, _pendingSplashDamageCount - 1);

                    var target = pending.Target;
                    if (target == null || target.Retired || target.Entity == Entity.Null ||
                        !_entityManager.Exists(target.Entity) ||
                        !_entityManager.HasComponent<RecoilBattleEcsState>(target.Entity)) continue;
                    var state = _entityManager.GetComponentData<RecoilBattleEcsState>(target.Entity);
                    if (state.Active == 0 || state.Health <= 0) continue;
                    state.Health = Mathf.Max(0, state.Health - pending.Damage);
                    if (pending.Attacker != Entity.Null)
                    {
                        state.LastAttacker = pending.Attacker;
                        state.LastAttackedTime = SimulationNow;
                    }
                    if (state.Health == 0)
                    {
                        state.Active = 0;
                        state.Target = Entity.Null;
                        _projectileDeaths.Add(target);
                    }
                    _entityManager.SetComponentData(target.Entity, state);
                }
                if (retained < count) bucket.RemoveRange(retained, count - retained);
            }
            _lastResolvedSplashDamageTick = currentTick;
        }

        private long GetSimulationTick() =>
            (long)System.Math.Floor(SimulationNow * 30.0 + 0.001);

        private bool IsSlowTargetUpdateDue(Entity entity)
        {
            if (entity == Entity.Null)
                return false;
            var phase = (int)(GetSimulationTick() % RecoilCloseTargetJob.SlowTargetUpdateRate);
            return entity.Index % RecoilCloseTargetJob.SlowTargetUpdateRate == phase;
        }

        private static List<PendingSplashDamage>[] CreatePendingSplashDamageBuckets()
        {
            var buckets = new List<PendingSplashDamage>[RecoilExplosionDamageSchedule.BucketCount];
            for (var i = 0; i < buckets.Length; i++) buckets[i] = new List<PendingSplashDamage>();
            return buckets;
        }

        private float GetCombatBodyRadius(Soldier soldier)
        {
            if (soldier != null && soldier.HeroId > 0) return HeroHitRadius;
            if (soldier != null && _combatIndicesByEntity.TryGetValue(soldier.Entity, out var index) &&
                index >= 0 && index < _combatBodyRadiusCache.Count)
                return _combatBodyRadiusCache[index];
            return 0.5f;
        }

        private float3 GetSoldierPosition(Soldier soldier)
        {
            if (soldier?.Simulation != null && soldier.Simulation.SimulationWorld == _world &&
                soldier.Simulation.Exists)
                return soldier.Simulation.Position;
            return _entityManager.GetComponentData<RecoilUnitPose>(soldier.Entity).Position;
        }

        private void ApplyProjectileDamage(Soldier target, RecoilBattleEcsState state,
            PendingCombatProjectile projectile, List<Soldier> deaths, float damageScale = 1f)
        {
            var damage = RecoilUnitDamage.Calculate(projectile.Damage * Mathf.Clamp01(damageScale),
                projectile.AttackerStats, state.Stats);
            if (damage <= 0) return;
            state.Health = Mathf.Max(0, state.Health - damage);
            if (projectile.Attacker != Entity.Null)
            {
                state.LastAttacker = projectile.Attacker;
                state.LastAttackedTime = SimulationNow;
            }
            if (state.Health == 0)
            {
                state.Active = 0;
                state.Target = Entity.Null;
                deaths.Add(target);
            }
            _entityManager.SetComponentData(target.Entity, state);
            if (TryGetCombatSnapshot(target, out var snapshotIndex, out var snapshot))
            {
                snapshot.Health = state.Health;
                snapshot.Active = state.Active;
                _combatSnapshots[snapshotIndex] = snapshot;
            }
        }

        private bool TryGetCombatSnapshot(Soldier soldier, out int index,
            out RecoilBattleCombatSnapshot snapshot)
        {
            if (soldier == null || !_combatSnapshots.IsCreated)
            {
                index = -1;
                snapshot = default;
                return false;
            }
            index = soldier != null ? soldier.CombatSlot : -1;
            if (index < 0 || index >= _combatSnapshots.Length ||
                _combatSnapshots[index].Entity != soldier.Entity)
            {
                if (soldier == null || !_combatIndicesByEntity.TryGetValue(soldier.Entity, out index))
                {
                    snapshot = default;
                    return false;
                }
            }

            if (index < 0 || index >= _combatSnapshots.Length)
            {
                snapshot = default;
                return false;
            }
            snapshot = _combatSnapshots[index];
            return snapshot.Entity == soldier.Entity;
        }

        private void AddCombatSoldiers(List<Soldier> team)
        {
            for (var i = 0; i < team.Count; i++)
            {
                var soldier = team[i];
                var valid = false;
                try
                {
                    // Soldier wrappers are created with the full archetype. Reuse their cached
                    // body radius and avoid probing every required component through EntityManager.
                    valid = soldier != null && !soldier.Retired && soldier.Entity != Entity.Null &&
                        _entityManager.Exists(soldier.Entity) &&
                        _entityManager.HasComponent<RecoilBattleEcsState>(soldier.Entity);
                    if (valid && soldier.Simulation != null)
                        valid = soldier.Simulation.Entity == soldier.Entity &&
                            soldier.Simulation.SimulationWorld == _world &&
                            _entityManager.HasComponent<RecoilUnitMovementSettings>(soldier.Entity);
                }
                catch (System.ArgumentException)
                {
                    // EntityManager may reject a stale wrapper while a roster rebuild is in flight.
                    valid = false;
                }
                catch (System.InvalidOperationException)
                {
                    valid = false;
                }

                if (!valid)
                {
                    if (soldier != null)
                    {
                        soldier.CombatSlot = -1;
                        soldier.Retired = true;
                    }
                    continue;
                }
                soldier.CombatSlot = _combatSoldiers.Count;
                _combatIndicesByEntity.Add(soldier.Entity, _combatSoldiers.Count);
                _combatSoldiers.Add(soldier);
                var bodyRadius = soldier.HeroId > 0 ? HeroHitRadius :
                    soldier.BodyRadius > 0f ? Mathf.Max(0.15f, soldier.BodyRadius) : 0.5f;
                _combatMaxBodyRadius = Mathf.Max(_combatMaxBodyRadius, bodyRadius);
                _combatBodyRadiusCache.Add(bodyRadius);
            }
        }

        private bool TryAppendCombatSoldierToRoster(Soldier soldier)
        {
            if (_combatRosterDirty || soldier == null || soldier.Retired ||
                soldier.Entity == Entity.Null || !_combatSnapshots.IsCreated ||
                !_combatEntityIndices.IsCreated || !_combatSnapshotEntities.IsCreated ||
                _combatSoldiers.Count >= _combatBufferCapacity)
                return false;

            try
            {
                if (!_entityManager.Exists(soldier.Entity) ||
                    !_entityManager.HasComponent<RecoilBattleEcsState>(soldier.Entity) ||
                    soldier.Simulation == null || soldier.Simulation.Entity != soldier.Entity ||
                    soldier.Simulation.SimulationWorld != _world ||
                    !_entityManager.HasComponent<RecoilUnitMovementSettings>(soldier.Entity))
                    return false;

                var index = _combatSoldiers.Count;
                var bodyRadius = soldier.HeroId > 0 ? HeroHitRadius :
                    soldier.BodyRadius > 0f ? Mathf.Max(0.15f, soldier.BodyRadius) : 0.5f;
                _combatIndicesByEntity.Add(soldier.Entity, index);
                _combatEntityIndices.Add(soldier.Entity, index);
                _combatSoldiers.Add(soldier);
                _combatBodyRadiusCache.Add(bodyRadius);
                _combatMaxBodyRadius = Mathf.Max(_combatMaxBodyRadius, bodyRadius);
                _combatSnapshotEntities[index] = soldier.Entity;
                _combatRangedUnits[index] = soldier.RangedUnitType;
                _combatBodyRadii[index] = bodyRadius;
                _combatSnapshots[index] = default;
                _combatPreviousCells[index] = default;
                _combatPreviousActive[index] = 0;
                _combatScheduleGenerations[index] = 0;
                soldier.CombatSlot = index;
                _projectileGridStale = true;
                return true;
            }
            catch (System.ArgumentException)
            {
                return false;
            }
            catch (System.InvalidOperationException)
            {
                return false;
            }
        }

        private void RefreshCombatRoster()
        {
            using var measure = RosterRefreshMarker.Auto();
            if (!_combatRosterDirty) return;
            using var rosterMeasure = RecoilCpuPhases.Measure(RecoilCpuPhase.RosterBuild);
            _combatSoldiers.Clear();
            _combatBodyRadiusCache.Clear();
            _combatIndicesByEntity.Clear();
            _combatMaxBodyRadius = 0.15f;
            AddCombatSoldiers(_blueTeam);
            AddCombatSoldiers(_redTeam);
            if (_combatSoldiers.Count > 0)
            {
                EnsureCombatBuffers(_combatSoldiers.Count);
                for (var i = 0; i < _combatSoldiers.Count; i++)
                {
                    var soldier = _combatSoldiers[i];
                    _combatSnapshotEntities[i] = soldier.Entity;
                    _combatRangedUnits[i] = soldier.RangedUnitType;
                    _combatBodyRadii[i] = _combatBodyRadiusCache[i];
                }
                new RecoilBuildCombatIndicesJob
                {
                    Entities = _combatSnapshotEntities, Indices = _combatEntityIndices,
                    Count = _combatSoldiers.Count
                }.Schedule().Complete();
            }
            _combatRosterDirty = false;
            _combatRosterRemovalsSinceBuild = 0;
            _projectileGridStale = true;
            CombatRosterBuildCount++;
        }

        private void EnsureCombatBuffers(int soldierCount)
        {
            if (soldierCount <= _combatBufferCapacity)
                return;

            DisposeCombatBuffers();
            _combatBufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(4, soldierCount));
            _ecsPathRequests = new NativeList<int>(_combatBufferCapacity, Allocator.Persistent);
            _combatSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _combatRangedUnits = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _scheduledCombatIndices = new NativeArray<int>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _combatResults = new NativeArray<RecoilBattleCombatResult>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _damageBySoldier = new NativeArray<int>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _lastAttackerByTarget = new NativeArray<int>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _combatCells = new NativeParallelMultiHashMap<int3, int>(
                _combatBufferCapacity,
                Allocator.Persistent);
            _combatPreviousCells = new NativeArray<int3>(_combatBufferCapacity, Allocator.Persistent);
            _combatTeamCellCounts = new NativeParallelHashMap<int3, int>(_combatBufferCapacity, Allocator.Persistent);
            _combatPreviousActive = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _combatGridMetadata = new NativeArray<int>(4, Allocator.Persistent);
            _combatChangedCells = new NativeParallelHashSet<int3>(_combatBufferCapacity, Allocator.Persistent);
            _combatGridSurvivors = new NativeList<int>(_combatBufferCapacity, Allocator.Persistent);
            _combatChangedIndices = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _combatEntityIndices = new NativeParallelHashMap<Entity, int>(_combatBufferCapacity, Allocator.Persistent);
            _combatSnapshotEntities = new NativeArray<Entity>(_combatBufferCapacity, Allocator.Persistent);
            _combatScheduleGenerations = new NativeArray<int>(_combatBufferCapacity, Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _combatBodyRadii = new NativeArray<float>(_combatBufferCapacity, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _contactAttacks = new NativeArray<RecoilContactAttack>(_combatBufferCapacity, Allocator.Persistent);
            _combatAliveFlags = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _combatDeaths = new NativeList<int>(_combatBufferCapacity, Allocator.Persistent);
        }

        private int NextCombatScheduleGeneration()
        {
            if (_combatScheduleGeneration == int.MaxValue)
            {
                for (var i = 0; i < _combatSoldiers.Count; i++)
                    _combatScheduleGenerations[i] = 0;
                _combatScheduleGeneration = 0;
            }
            return ++_combatScheduleGeneration;
        }

        private void DisposeCombatBuffers()
        {
            if (_resolveSummary.IsCreated) _resolveSummary.Dispose();
            _combatRosterDirty = true;
            if (_combatJobPending)
            {
                _combatJobHandle.Complete();
                _combatJobPending = false;
            }

            if (_combatSnapshots.IsCreated)
                _combatSnapshots.Dispose();
            if (_combatRangedUnits.IsCreated)
                _combatRangedUnits.Dispose();
            if (_scheduledCombatIndices.IsCreated)
                _scheduledCombatIndices.Dispose();
            if (_combatScheduleGenerations.IsCreated) _combatScheduleGenerations.Dispose();
            if (_ecsPathRequests.IsCreated) _ecsPathRequests.Dispose();
            if (_combatResults.IsCreated)
                _combatResults.Dispose();
            if (_damageBySoldier.IsCreated)
                _damageBySoldier.Dispose();
            if (_lastAttackerByTarget.IsCreated)
                _lastAttackerByTarget.Dispose();
            if (_combatCells.IsCreated)
                _combatCells.Dispose();
            if (_combatPreviousCells.IsCreated) _combatPreviousCells.Dispose();
            if (_combatTeamCellCounts.IsCreated) _combatTeamCellCounts.Dispose();
            if (_combatPreviousActive.IsCreated) _combatPreviousActive.Dispose();
            if (_combatGridMetadata.IsCreated) _combatGridMetadata.Dispose();
            if (_combatChangedCells.IsCreated) _combatChangedCells.Dispose();
            if (_combatGridSurvivors.IsCreated) _combatGridSurvivors.Dispose();
            if (_combatChangedIndices.IsCreated) _combatChangedIndices.Dispose();
            if (_combatEntityIndices.IsCreated)
                _combatEntityIndices.Dispose();
            if (_combatSnapshotEntities.IsCreated) _combatSnapshotEntities.Dispose();
            if (_combatBodyRadii.IsCreated) _combatBodyRadii.Dispose();
            if (_contactAttacks.IsCreated) _contactAttacks.Dispose();
            if (_combatAliveFlags.IsCreated) _combatAliveFlags.Dispose();
            if (_combatDeaths.IsCreated) _combatDeaths.Dispose();

            _combatBufferCapacity = 0;
        }

        private bool HasVariantTeams =>
            blueUnitVariants != null && blueUnitVariants.Length > 0 &&
            redUnitVariants != null && redUnitVariants.Length > 0;

        private void ProcessSpawnQueue()
        {
            using var spawnMarker = SpawnQueueMarker.Auto();
            if (!_initialFormationLoading) FlushTikTokHeroes();
            if (Time.time < _nextSpawnAttemptTime)
                return;

            var backlog = _blueSpawnQueue + _redSpawnQueue +
                _blueSelectedSpawnTotal + _redSelectedSpawnTotal;
            if (backlog <= 0)
            {
                _spawnBlueCredit = 0;
                return;
            }

            // Select one team for this frame. Re-evaluate living army sizes next frame;
            // only fall back to the other team when this team has no eligible spawns.
            bool bluePending = _blueSpawnQueue > 0 || _blueSelectedSpawnTotal > 0;
            bool redPending = _redSpawnQueue > 0 || _redSelectedSpawnTotal > 0;
            bool preferredBlue;
            if (bluePending && redPending)
                preferredBlue = RecoilBattleScheduling.TakeBalancedSpawnFrame(
                    CountLivingSpawnArmy(_blueTeam), CountLivingSpawnArmy(_redTeam),
                    smallerArmySpawnFrames, largerArmySpawnFrames, ref _spawnBlueCredit);
            else
            {
                preferredBlue = bluePending;
                _spawnBlueCredit = 0;
            }
            var spawnsThisFrame = Mathf.Min(maxSpawnsPerFrame, backlog / Mathf.Max(1, spawnQueueRateFrames) + 1);
            var blueBlockedThisFrame = false;
            var redBlockedThisFrame = false;
            var spawnStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var spawnBudgetTicks = System.Math.Max(0.1, spawnWorkBudgetMilliseconds) *
                (System.Diagnostics.Stopwatch.Frequency / 1000.0);
            for (var i = 0; i < spawnsThisFrame && HasQueuedSpawns; i++)
            {
                // Finish the current spawn atomically, then yield the remaining queue to a later frame.
                if (i > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - spawnStart >= spawnBudgetTicks) break;
                var hasBlue = !blueBlockedThisFrame && (_blueSpawnQueue > 0 || _blueSelectedSpawnTotal > 0);
                var hasRed = !redBlockedThisFrame && (_redSpawnQueue > 0 || _redSelectedSpawnTotal > 0);
                if (!hasBlue && !hasRed) break;
                var spawnBlue = preferredBlue ? hasBlue : !hasRed;
                var selectedQueue = spawnBlue ? _blueSelectedSpawnQueue : _redSelectedSpawnQueue;
                var activeRow = spawnBlue ? _blueSpawnRow : _redSpawnRow;
                var hasSelectedType = activeRow.Count > 0 ? activeRow.Selected : selectedQueue.Count > 0;
                var requestedType = activeRow.Count > 0 ? activeRow.Type : hasSelectedType ? selectedQueue.Peek().CharacterType :
                    ResolveCharacterType(GetUnitTemplate(spawnBlue, activeRow.Ordinal));
                if (activeRow.Count == 0) { activeRow.Type = requestedType; activeRow.Selected = hasSelectedType; }
                var spawnIndex = spawnBlue ? _blueSpawnedTotal : _redSpawnedTotal;
                var template = requestedType >= 0
                    ? FindTemplateForType(spawnBlue, requestedType)
                    : GetUnitTemplate(spawnBlue, spawnIndex);
                if (!SpawnUnit(spawnBlue, requestedType))
                {
                    FinishUnavailableSpawnRow(spawnBlue, selectedQueue);
                    if (_spawnExitBlocked)
                    {
                        if (spawnBlue) blueBlockedThisFrame = true;
                        else redBlockedThisFrame = true;
                        // A busy spawn lane is normal backpressure; keep both queues moving.
                        _nextSpawnAttemptTime = Time.time + 0.025f;
                        continue;
                    }
                    _nextSpawnAttemptTime = Time.time + 1f;
                    if (!_spawnFailureReported)
                    {
                        Debug.LogWarning(
                            "Lính tạm dừng spawn: không tìm thấy vị trí spawn hợp lệ quanh điểm xuất quân. " +
                            "Đơn vị đang chờ vẫn được giữ lại; hãy kiểm tra map bake và vùng walkable.",
                            this);
                        Debug.LogWarning($"[Spawn diagnostics] Team={(spawnBlue ? "Blue" : "Red")}, index={spawnIndex}, " +
                            $"anchor={GetSpawnTargetPosition(spawnBlue)}, bounds=X[{SpawnMinX},{SpawnMaxX}] Z[{SpawnMinZ},{SpawnMaxZ}], " +
                            $"backend={(pathService != null ? pathService.SurfaceBackend : "missing")}, " +
                            $"terrainMap={(terrainMap != null ? terrainMap.name : pathService != null && pathService.ActiveTerrainMap != null ? pathService.ActiveTerrainMap.name : "missing")}", this);
                        _spawnFailureReported = true;
                    }

                    return;
                }

                _spawnFailureReported = false;
                if (spawnBlue)
                {
                    if (hasSelectedType)
                    {
                        if (prepareArmyBeforeCombat && !_combatStarted && _lastSpawnWasPrepared) ConsumeSpawnType(selectedQueue, false);
                        else ConsumeSpawnRow(true, selectedQueue);
                        _blueSelectedSpawnTotal--;
                    }
                    else
                    {
                        _blueSpawnQueue--;
                        FinishDefaultSpawnRow(true);
                    }
                }
                else
                {
                    if (hasSelectedType)
                    {
                        if (prepareArmyBeforeCombat && !_combatStarted && _lastSpawnWasPrepared) ConsumeSpawnType(selectedQueue, false);
                        else ConsumeSpawnRow(false, selectedQueue);
                        _redSelectedSpawnTotal--;
                    }
                    else
                    {
                        _redSpawnQueue--;
                        FinishDefaultSpawnRow(false);
                    }
                }

                if (!prepareArmyBeforeCombat && !_combatStarted && _lobbyStarting &&
                    HasAliveSoldier(_blueTeam) && HasAliveSoldier(_redTeam))
                    TryStartBattle();
            }

        }

        private IEnumerator ValidateUnitTemplates()
        {
            _templateValidationTotal = CharacterTypeCount * 2;
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                if (FindTemplateForType(true, type) == null || FindTemplateForType(false, type) == null)
                {
                    _templateValidationError =
                        $"Thiếu mẫu lính loại {CharacterTypeLabels[type]} cho một trong hai phe. " +
                        "Hãy gán đủ 7 loại lính cho cả hai phe trong Game Tools/Công Thành/Set Up.";
                    _templateValidationFailed = true;
                    Debug.LogError(_templateValidationError, this);
                    yield break;
                }
                _templatesValidatedCount += 2;
            }

            _templatesValidated = true;
            RecoilSimulationClock.Register(this);
            yield break;
        }

        private bool HasQueuedSpawns =>
            _blueSpawnQueue > 0 || _redSpawnQueue > 0 ||
            _blueSelectedSpawnTotal > 0 || _redSelectedSpawnTotal > 0;

        private bool StartMatchFromLobby()
        {
            if (!_templatesValidated || _templateValidationFailed || _lobbyStarted || _poolPrewarmRunning)
                return false;
            CaptureMatchTimeLimit();

            for (var type = 0; type < CharacterTypeCount; type++)
            {
                if (FindTemplateForType(true, type) == null || FindTemplateForType(false, type) == null)
                {
                    _lobbyError = $"Thiếu prefab cho loại quân {CharacterTypeLabels[type]} ở một trong hai phe.";
                    return false;
                }
            }

            if (prewarmSoldierPools && (prewarmPerTypePerTeam > 0 || prewarmHeroesPerTypePerTeam > 0) && !_poolPrewarmFinished)
            {
                BeginPoolPrewarm();
                return false;
            }
            if (prepareArmyBeforeCombat && !_initialLayoutPrepared)
            {
                if (!_initialFormationLoading)
                {
                    _initialFormationLoading = true;
                    StartCoroutine(PrepareInitialLayoutAndStart());
                }
                return false;
            }
            _lobbyError = null;
            _lobbyStarted = true;
            BeginBattleMovementAudio();
            _settingsOpen = false;
            _blueCombatSpawnSlotIndex = 0;
            _redCombatSpawnSlotIndex = 0;
            if (prepareArmyBeforeCombat)
            {
                if (!_initialArmy.Enabled)
                {
                    _preparedPositionsBuilt = false;
                    _preparedBluePositions.Clear();
                    _preparedRedPositions.Clear();
                }
                var bluePrepared = PreparedTypeCounts(true);
                var redPrepared = PreparedTypeCounts(false);
                for (var type = 0; type < CharacterTypeCount; type++)
                {
                    var blueAmount = bluePrepared[type];
                    var redAmount = redPrepared[type];
                    _waitingDeferred[0, type] += Mathf.Max(0, RequestedPreparedTypeCount(true, type) - blueAmount);
                    _waitingDeferred[1, type] += Mathf.Max(0, RequestedPreparedTypeCount(false, type) - redAmount);
                    if (blueAmount > 0) EnqueueSpawnType(_blueSelectedSpawnQueue, type, blueAmount);
                    if (redAmount > 0) EnqueueSpawnType(_redSelectedSpawnQueue, type, redAmount);
                    _blueSelectedSpawnTotal += blueAmount;
                    _redSelectedSpawnTotal += redAmount;
                }
                _lobbyStarting = true;
                InvalidateSpawnFormationPlans();
                InvalidateAssembly();
            }
            return true;
        }

        private void FinishMatch(string result, bool blueWins)
        {
            if (_matchFinished) return;
            _matchFinished = true;
            StopBattleAudio();
            _matchResult = result;
            SettleMatchScoring(blueWins);
            StopHeroIntros(false);
            CompletePendingCombatJob();
            if (pathService != null) pathService.SuspendForEndgame();

            foreach (var soldier in _blueTeam)
                if (soldier != null && soldier.Simulation != null)
                    soldier.Simulation.SetActive(false);
            foreach (var soldier in _redTeam)
                if (soldier != null && soldier.Simulation != null)
                    soldier.Simulation.SetActive(false);
            _blueSelectedSpawnQueue.Clear();
            _redSelectedSpawnQueue.Clear();
            _blueSpawnQueue = _redSpawnQueue = _blueSelectedSpawnTotal = _redSelectedSpawnTotal = 0;
            _pendingCombatProjectiles.Clear();
            BeginEndgamePresentation(blueWins);
        }

        private void QueueSelectedTypeSpawn(int characterType,
            ManualSpawnTeamMode teamMode = ManualSpawnTeamMode.Both)
        {

            if (characterType < 0 || characterType >= CharacterTypeCount)
                return;
            var spawnBlue = teamMode != ManualSpawnTeamMode.Red;
            var spawnRed = teamMode != ManualSpawnTeamMode.Blue;
            if ((spawnBlue && FindTemplateForType(true, characterType) == null) ||
                (spawnRed && FindTemplateForType(false, characterType) == null))
            {
                Debug.LogError(
                    $"Chưa gán mẫu lính {CharacterTypeLabels[characterType]} cho đủ hai phe. Mở Game Tools/Công Thành/Set Up để gán.",
                    this);
                return;
            }

            if (prepareArmyBeforeCombat && !_combatStarted)
            {
                DeferWaitingArmyAddition(characterType,
                    spawnBlue ? UnitsPerManualSpawn : 0, spawnRed ? UnitsPerManualSpawn : 0);
                return;
            }
            if (spawnBlue)
            {
                EnqueueSpawnType(_blueSelectedSpawnQueue, characterType, UnitsPerManualSpawn);
                _blueSelectedSpawnTotal += UnitsPerManualSpawn;
            }
            if (spawnRed)
            {
                EnqueueSpawnType(_redSelectedSpawnQueue, characterType, UnitsPerManualSpawn);
                _redSelectedSpawnTotal += UnitsPerManualSpawn;
            }
            if (!_combatStarted)
            {
                InvalidateSpawnFormationPlans();
                _lobbyStarting = true;
                InvalidateAssembly();
            }
        }

        private bool SpawnUnit(bool isBlue, int requestedType = -1, RecoilHeroConfig hero = null, GameObject heroPrefab = null)
        {
            var preparedSpawn = hero == null && prepareArmyBeforeCombat && !_combatStarted &&
                (isBlue ? _initialBlueSoldiersSpawned : _initialRedSoldiersSpawned) < PreparedTeamTarget(isBlue);
            var team = isBlue ? _blueTeam : _redTeam;
            var spawnIndex = isBlue ? _blueSpawnedTotal : _redSpawnedTotal;
            var template = heroPrefab != null ? heroPrefab : requestedType >= 0
                ? FindTemplateForType(isBlue, requestedType)
                : GetUnitTemplate(isBlue, spawnIndex);
            if (template == null)
            {
                Debug.LogError(
                    $"No character prefab is configured for the {(isBlue ? "blue" : "red")} team.",
                    this);
                return false;
            }
            var characterType = (byte)Mathf.Clamp(requestedType >= 0 ? requestedType : ResolveCharacterType(template), 0, CharacterTypeCount - 1);
            var spawn = default(NavMeshHit);
            var templateAgent = template.GetComponent<RecoilNavMeshPathAgent>();
            var hasSpawnPosition = preparedSpawn
                ? TryGetPreparedSpawnPosition(isBlue, out spawn)
                : hero != null
                    ? TryFindHeroSpawnPosition(isBlue, out spawn)
                    : TryFindStreamSpawnPosition(isBlue,
                        templateAgent != null ? Mathf.Max(0.15f, templateAgent.MovementSettings.BodyRadius) : 0.5f, out spawn);
            if (!hasSpawnPosition || !IsWithinSpawnBounds(spawn.position))
                return false;

            var rotation = Quaternion.LookRotation(BattleAxis() * (isBlue ? 1f : -1f));
            var rangedUnitType = characterType == 4 ? (byte)2 : characterType == 0 ? (byte)1 : (byte)0;
            var configuredPresentation = template.GetComponent<RecoilCharacterPresentation>();
            if (configuredPresentation != null && configuredPresentation.HasRangedAttackOverride)
                rangedUnitType = configuredPresentation.IsRangedAttack
                    ? (characterType == 4 ? (byte)2 : (byte)1) : (byte)0;
            var statsSource = template.GetComponent<RecoilUnitStats>();
            var stats = statsSource != null ? statsSource.Stats : default;
            if (characterType < lobbyUnitStats.Length && lobbyUnitStats[characterType].IsConfigured)
                stats = lobbyUnitStats[characterType];
            if (hero != null)
            {
                stats = hero.Stats;
                stats.CombatRole = RecoilCombatRole.Unknown;
                rangedUnitType = stats.AttackType == RecoilAttackType.Ranged ? (byte)1 : (byte)0;
            }
            if (stats.TargetCategoryMask == 0)
                stats.TargetCategoryMask = 1 << characterType;
            var movementSettings = templateAgent != null
                ? templateAgent.MovementSettings
                : new RecoilUnitMovementSettings
                {
                    MaxSpeed = 3.5f,
                    Acceleration = 12f,
                    TurnSpeedDegrees = 540f,
                    WaypointTolerance = 0.25f,
                    AvoidanceRadius = 1.2f,
                    AvoidanceStrength = 1f,
                    BodyRadius = 0.5f,
                    Mass = 1f,
                    VerticalOffset = 1f,
                    AreaMask = NavMesh.AllAreas
                };
            if (hero != null)
            {
                movementSettings.BodyRadius = HeroOccupiedRadius;
                movementSettings.AvoidanceRadius = Mathf.Max(movementSettings.AvoidanceRadius, HeroOccupiedRadius);
            }
            // Recoil's collision weighting uses unit mass. Scale it by occupied-circle area
            // (pi cancels in relative ratios); the prefab Mass remains a per-unit multiplier.
            movementSettings.Mass = RecoilMovementMass.FromOccupiedCircle(
                movementSettings.Mass, movementSettings.BodyRadius);
            movementSettings.AllyPassThroughGroup = allowDifferentAlliedTypesToPassThrough ? characterType + 1 : 0;
            if (hero != null)
            {
                movementSettings.PassThroughUnits = RecoilUnitMovementSettings.PassThroughOrdinaryUnits;
                movementSettings.AvoidanceStrength = 0;
                movementSettings.VerticalOffset = heroGroundOffset;
            }
            if (stats.IsConfigured) movementSettings.MaxSpeed = stats.MoveSpeed;
            if (pathService != null && pathService.UsingTerrainMap)
                movementSettings.AreaMask = isBlue ? 1 : 2;

            var entity = _entityManager.CreateEntity(_soldierArchetype);
            _ownedSoldierEntities.Add(entity);
            _entityManager.SetComponentData(entity, new RecoilBattleEcsState
            {
                Team = isBlue ? 0 : 1,
                Health = stats.IsConfigured ? stats.MaxHealth : 100,
                Stats = stats,
                AttackCooldown = 0f,
                Active = 1,
                Target = Entity.Null
            });

            var simulation = new RecoilUnitSimulation(_world, entity, pathService,
                spawn.position + Vector3.up*movementSettings.VerticalOffset, rotation, movementSettings);
            simulation.SetAssemblyMovement(false);
            simulation.SetPresentationFacing(!_combatStarted, rotation);
            if (useFightCorridors)
            {
                var lateral = Vector3.Cross(Vector3.up, BattleAxis());
                var lane = Vector3.Dot(simulation.Position - GetSpawnTargetPosition(isBlue), lateral);
                var end = GetSpawnTargetPosition(!isBlue) + lateral * lane;
                end.x = Mathf.Clamp(end.x, SpawnMinX, SpawnMaxX);
                end.z = Mathf.Clamp(end.z, SpawnMinZ, SpawnMaxZ);
                if (TryGetGroundHeight(end, out var laneHeight)) end.y = laneHeight;
                var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(entity);
                timing.Fight = new RecoilFightCorridor { Start = simulation.Position, End = end,
                    Leash = fightLeash, Enabled = 1 };
                _entityManager.SetComponentData(entity, timing);
            }
            var soldier = new Soldier
            {
                HeroId = hero != null ? hero.Id : 0,
                HeroUltiCooldown = hero != null ? hero.UltiCooldown : 0,
                NextHeroUlti = SimulationNow + (hero != null ? hero.UltiCooldown : 0),
                Simulation = simulation,
                Entity = entity,
                IsBlue = isBlue,
                Template = template,
                VisualSource = configuredPresentation,
                NavigationVerticalOffset = movementSettings.VerticalOffset,
                VisualHeight = hero != null ? HeroBodyHeight(template) : configuredPresentation != null
                    ? configuredPresentation.NormalizedVisualHeight : 3f,
                BodyRadius = Mathf.Max(0.15f, movementSettings.BodyRadius),
                CharacterType = characterType,
                RangedUnitType = rangedUnitType,
                LastMovementCommandTime = SimulationNow
            };
            team.Add(soldier);
            if (hero != null) _heroDuelists.Add(soldier);
            _lastSpawnWasPrepared = preparedSpawn;
            if (preparedSpawn)
            {
                if (isBlue) _initialBlueSoldiersSpawned++;
                else _initialRedSoldiersSpawned++;
            }
            if (hero != null && !_combatStarted && prepareArmyBeforeCombat)
            {
                if (!_waitingFormationActive) _waitingFormationDirty = true;
                _waitingFormationActive = _waitingHeroesDirty = true;

            }
            if (!preparedSpawn) RecordStreamSpawn(soldier);
            if (!preparedSpawn && hero == null) OnAdvisorSummoned(soldier);
            if (!preparedSpawn && !_combatStarted && _waitingFormationActive && !_waitingFormationDirty)
                AssignWaitingArmySlot(soldier);
            if (!_combatStarted)
            {
                var formationIndices = isBlue
                    ? _blueSpawnFormationIndices
                    : _redSpawnFormationIndices;
                if (formationIndices != null)
                    formationIndices[characterType]++;
                InvalidateAssembly();
            }
            if (_combatStarted)
            {
                soldier.LastMovementCommandTime = SimulationNow;
                UpdateNavigationBridge(soldier, null);
            }
            if (!_combatStarted || !TryAppendCombatSoldierToRoster(soldier))
                _combatRosterDirty = true;
            if (isBlue)
                _blueSpawnedTotal++;
            else
                _redSpawnedTotal++;
            return true;
        }

        private void UpdatePresentationTiers()
        {
            if (!suspendAnimatorsForProfiling && _profilingAnimatorViews.Count > 0)
                RestoreProfilingAnimators();
            var presentationStart = RecoilCpuPhases.Timestamp();
            if (_gpuSkinnedCrowd == null && !_gpuRendererChecked)
            {
                _gpuRendererChecked = true;
                _gpuSkinnedCrowd = GetComponent<RecoilGpuSkinnedCrowdRenderer>();
            }
            if (UsesGpuBoneInstancing && _gpuSkinnedCrowd == null)
                _gpuSkinnedCrowd = GetComponent<RecoilGpuSkinnedCrowdRenderer>() ?? gameObject.AddComponent<RecoilGpuSkinnedCrowdRenderer>();
            if (_gpuSkinnedCrowd != null && _gpuSkinnedCrowd.enabled != UsesGpuBoneInstancing)
                _gpuSkinnedCrowd.SetRenderingEnabled(UsesGpuBoneInstancing);
            using (PresentationMarker.Auto())
            {
                // Both teams share the creation budget. Alternate priority so a large
                // backlog on either side cannot prevent the other side from getting models.
                if (_redPresentationFirst)
                {
                    UpdatePresentationTeam(_redTeam);
                    UpdatePresentationTeam(_blueTeam);
                }
                else
                {
                    UpdatePresentationTeam(_blueTeam);
                    UpdatePresentationTeam(_redTeam);
                }
                _redPresentationFirst = !_redPresentationFirst;
            }
            PresentationTierUpdateMillisecondsLastFrame = (float)(
                (RecoilCpuPhases.Timestamp() - presentationStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        private void UpdatePresentationTeam(List<Soldier> team)
        {
            for (var i = 0; i < team.Count; i++)
            {
                var soldier = team[i];
                if (soldier == null || soldier.Retired ||
                    !TryGetPresentationPose(soldier, out _))
                    continue;

                if (soldier.Agent == null)
                    TryAttachPrefabView(soldier);
                UpdateHeroAnimation(soldier);
                if (suspendAnimatorsForProfiling && soldier.Agent != null)
                {
                    var view = soldier.Agent.gameObject;
                    if (!_profilingAnimatorViews.TryGetValue(view, out var animators))
                    {
                        animators = view.GetComponentsInChildren<Animator>(true);
                        _profilingAnimatorViews.Add(view, animators);
                        foreach (var animator in animators)
                            if (animator != null) _profilingAnimatorStates.TryAdd(animator, animator.enabled);
                    }
                    foreach (var animator in animators)
                        if (animator != null && animator.enabled) animator.enabled = false;
                }
                if (UsesGpuBoneInstancing && soldier.Agent != null)
                    _gpuSkinnedCrowd.Register(soldier.Agent.gameObject);
            }
        }

        private void RestoreProfilingAnimators()
        {
            foreach (var state in _profilingAnimatorStates)
                if (state.Key != null) state.Key.enabled = state.Value;
            _profilingAnimatorStates.Clear();
            _profilingAnimatorViews.Clear();
        }

        private bool TryGetPresentationPose(Soldier soldier, out RecoilUnitPose pose)
        {
            var index = soldier.CombatSlot;
            var slotMatches = index >= 0 && index < _combatSnapshots.Length &&
                _combatSnapshots[index].Entity == soldier.Entity;
            if (!slotMatches && !_combatIndicesByEntity.TryGetValue(soldier.Entity, out index))
                index = -1;

            if (index >= 0 && index < _combatSnapshots.Length)
            {
                var snapshot = _combatSnapshots[index];
                if (snapshot.Entity == soldier.Entity)
                {
                    if (snapshot.Active == 0 || snapshot.Health <= 0)
                    {
                        pose = default;
                        return false;
                    }
                    pose = new RecoilUnitPose { Position = snapshot.Position, PreviousPosition = snapshot.PreviousPosition,
                        Velocity = snapshot.Velocity, Rotation = snapshot.Rotation };
                    return true;
                }
            }

            if (soldier.Simulation != null && soldier.Simulation.TryGetPose(out pose))
            {
                return true;
            }

            pose = default;
            return false;
        }

        private bool TryAttachPrefabView(Soldier soldier)
        {
            var unitObject = GetOrCreatePooledUnit(soldier.Template, soldier.IsBlue);
            if (unitObject == null)
                return false;

            var agent = unitObject.GetComponent<RecoilNavMeshPathAgent>();
            if (agent == null)
            {
                agent = unitObject.AddComponent<RecoilNavMeshPathAgent>();
                if (pathService != null)
                    agent.ConfigurePathService(pathService);
            }
            if (agent == null)
            {
                Debug.LogError($"Prefab '{unitObject.name}' could not be given a {nameof(RecoilNavMeshPathAgent)}.", unitObject);
                GetUnitPool(soldier.IsBlue, soldier.Template).Enqueue(unitObject);
                return false;
            }

            unitObject.transform.SetPositionAndRotation(soldier.Simulation.Position, soldier.Simulation.Rotation);
            unitObject.name = $"{(soldier.IsBlue ? "Blue" : "Red")} Soldier {soldier.Entity.Index}";
            unitObject.SetActive(true);
            var presentations = unitObject.GetComponentsInChildren<RecoilCharacterPresentation>(true);
            foreach (var presentation in presentations)
            {
                presentation.SetGroundCorrection(0f);
                presentation.ResetForReuse();
                presentation.ConfigureVisualProjectilePool(GetOrCreateVisualProjectilePool());
            }
            var colliders = unitObject.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
                colliders[i].enabled = soldier.HeroId == 0;

            agent.Bind(soldier.Simulation);
            soldier.Agent = agent;
            _prefabSoldierOwners[unitObject] = soldier;
            soldier.HeroVat = unitObject.GetComponent<NhiQuoc.Tools.HeroVatPlayer>();
            if (soldier.HeroVat != null)
            {
                soldier.HeroVat.keyboardPreview = false;
                soldier.HeroAnimation = -1;
                unitObject.name = $"{(soldier.IsBlue ? "Blue" : "Red")} Hero {soldier.HeroId}";
            }
            soldier.Presentation = unitObject.GetComponent<RecoilCharacterPresentation>() ??
                (presentations.Length > 0 ? presentations[0] : null);
            soldier.Vat = unitObject.GetComponent<RecoilVatAnimation>();
            if (soldier.Vat != null)
                soldier.Vat.BindToEcs(_world, soldier.Entity, soldier.VisualSource, GetOrCreateVisualProjectilePool());
            soldier.Colliders = colliders;
            if (!soldier.FullDetailCounted)
            {
                soldier.FullDetailCounted = true;
                _fullDetailSoldierCount++;
            }

            var attackClip = soldier.Presentation != null ? soldier.Presentation.AttackClip : null;
            var attackDuration = attackClip != null ? attackClip.length : attackInterval;
            if (soldier.Presentation != null &&
                SimulationNow - soldier.AttackStartedAt < attackDuration)
            {
                var attackTarget = soldier.AttackTarget != null &&
                    soldier.AttackTarget.Simulation != null && soldier.AttackTarget.Simulation.Exists &&
                    soldier.AttackTarget.Agent != null
                    ? soldier.AttackTarget.Agent.transform
                    : null;
                soldier.Presentation.PlayAttack(attackTarget,
                    soldier.AttackTargetPosition);
            }
            if (soldier.Vat != null && soldier.AttackTarget != null &&
                SimulationNow - soldier.AttackStartedAt < soldier.Vat.AttackDuration)
                soldier.Vat.RequestAttack(soldier.AttackTargetPosition);
            return true;
        }

        private RecoilVisualProjectilePool GetOrCreateVisualProjectilePool()
        {
            if (_visualProjectilePool != null)
                return _visualProjectilePool;
            _visualProjectilePool = GetComponent<RecoilVisualProjectilePool>();
            if (_visualProjectilePool == null)
                _visualProjectilePool = gameObject.AddComponent<RecoilVisualProjectilePool>();
            return _visualProjectilePool;
        }

        private void ReturnPrefabViewToPool(Soldier soldier)
        {
            var agent = soldier.Agent;
            if (agent == null)
                return;

            var unitObject = agent.gameObject;
            agent.Bind(null);
            soldier.Agent = null;
            soldier.Presentation = null;
            soldier.Vat = null;
            soldier.Colliders = null;
            if (soldier.FullDetailCounted)
            {
                soldier.FullDetailCounted = false;
                _fullDetailSoldierCount = Mathf.Max(0, _fullDetailSoldierCount - 1);
            }

            if (_gpuSkinnedCrowd != null) _gpuSkinnedCrowd.Unregister(unitObject);
            unitObject.SetActive(false);
            unitObject.transform.SetParent(transform, true);
            GetUnitPool(soldier.IsBlue, soldier.Template).Enqueue(unitObject);
        }

        private GameObject GetOrCreatePooledUnit(GameObject template, bool isBlue)
        {
            var pool = GetUnitPool(isBlue, template);
            var typeIndex = Mathf.Clamp(GetCharacterType(template), 0, CharacterTypeCount - 1);
            while (pool.Count > 0)
            {
                var unit = pool.Dequeue();
                if (unit == null)
                {
                    DecrementPresentationModelCount(isBlue, typeIndex);
                    continue;
                }
                unit.transform.SetParent(transform, true);
                return unit;
            }

            if (_newUnitsCreatedThisFrame >= Mathf.Max(1, maxNewUnitsPerFrame))
                return null;

            var created = CreateInactivePooledUnit(template, isBlue);
            created.transform.SetParent(transform, true);
            _newUnitsCreatedThisFrame++;
            return created;
        }

        private void DecrementPresentationModelCount(bool isBlue, int typeIndex)
        {
            var counts = isBlue
                ? _bluePresentationModelsCreatedByType
                : _redPresentationModelsCreatedByType;
            counts[typeIndex] = Mathf.Max(0, counts[typeIndex] - 1);
        }

        private Queue<GameObject> GetUnitPool(bool isBlue, GameObject template)
        {
            if (template == null)
                throw new System.ArgumentNullException(nameof(template));
            var pools = isBlue ? _blueUnitPools : _redUnitPools;
            if (!pools.TryGetValue(template, out var pool))
            {
                pool = new Queue<GameObject>();
                pools.Add(template, pool);
            }
            return pool;
        }

        private static byte GetRangedUnitType(GameObject unit)
        {
            var characterType = GetCharacterType(unit);
            return characterType == 4 ? (byte)2 : characterType == 0 ? (byte)1 : (byte)0;
        }

        private static int GetCharacterType(GameObject unit)
        {
            if (unit == null)
                return -1;
            var name = unit.name.Replace("_", "").Replace(" ", "").Replace("-", "");
            if (name.IndexOf("BOBINH", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 7;
            if (name.IndexOf("CUNGKY", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (name.IndexOf("DAOBINH", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("DANBINH", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            if (name.IndexOf("GIAOBINH", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            if (name.IndexOf("KYBINH", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 3;
            if (name.IndexOf("MAYDA", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MAYBANDA", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 4;
            if (name.IndexOf("QUANSU", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("QUAN_SU", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 5;
            if (name.IndexOf("VOI", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 6;
            return -1;
        }

        private GameObject FindTemplateForType(bool isBlue, int characterType)
        {
            var assigned = isBlue ? blueUnitsByType : redUnitsByType;
            if (assigned != null && characterType >= 0 && characterType < assigned.Length && assigned[characterType] != null)
                return assigned[characterType];
            var variants = isBlue ? blueUnitVariants : redUnitVariants;
            if (variants == null)
                return null;
            for (var i = 0; i < variants.Length; i++)
                if (GetCharacterType(variants[i]) == characterType)
                    return variants[i];
            var fallback = isBlue ? blueUnitTemplate : redUnitTemplate;
            return GetCharacterType(fallback) == characterType ? fallback : null;
        }

        public GameObject GetTemplateForType(bool isBlue, int characterType) => FindTemplateForType(isBlue, characterType);

        private int ResolveCharacterType(GameObject template)
        {
            for (var i = 0; i < CharacterTypeCount; i++)
            {
                if ((blueUnitsByType != null && i < blueUnitsByType.Length && blueUnitsByType[i] == template) ||
                    (redUnitsByType != null && i < redUnitsByType.Length && redUnitsByType[i] == template)) return i;
            }
            return GetCharacterType(template);
        }

        private GameObject GetUnitTemplate(bool isBlue, int spawnIndex)
        {
            var variants = isBlue ? blueUnitVariants : redUnitVariants;
            if (variants == null || variants.Length == 0)
                return isBlue ? blueUnitTemplate : redUnitTemplate;

            var initialFormationSize = Mathf.Max(1, unitsPerVariantAtStart);
            var variantIndex = (spawnIndex / initialFormationSize) % variants.Length;
            var template = variants[variantIndex];
            return template != null
                ? template
                : (isBlue ? blueUnitTemplate : redUnitTemplate);
        }

        private bool TryFindSpawnPosition(bool isBlue, int spawnIndex, out NavMeshHit spawn)
        {
            spawn = default;
            var variants = isBlue ? blueUnitVariants : redUnitVariants;
            if (variants != null && variants.Length > 0)
                return TryFindVariantSpawnPosition(isBlue, spawnIndex, variants.Length, out spawn);
            if (terrainMap != null && terrainMap.HasValidData)
                return TryFindMapSpawnPosition(isBlue, spawnIndex, out spawn);

            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;
            var spacing = Mathf.Max(0.5f, spawnSpacing);
            var margin = Mathf.Min(spacing, Mathf.Min(size.x, size.z) * 0.1f);
            var minX = origin.x + margin;
            var maxX = origin.x + size.x - margin;
            var minZ = origin.z + margin;
            var maxZ = origin.z + size.z - margin;
            var side = isBlue ? -1f : 1f;
            var anchor = GetSpawnTargetPosition(isBlue);
            var startX = Mathf.Clamp(anchor.x, minX, maxX);
            var availableDepth = isBlue ? startX - minX : maxX - startX;
            var rows = Mathf.Max(1, Mathf.FloorToInt(availableDepth / spacing) + 1);
            var columns = Mathf.Clamp(Mathf.FloorToInt((maxZ - minZ) / spacing) + 1, 1, Mathf.Max(1, spawnColumns));
            var startZ = Mathf.Clamp(anchor.z - (columns - 1) * spacing * 0.5f,
                minZ, Mathf.Max(minZ, maxZ - (columns - 1) * spacing));
            var capacity = (long)rows * columns;
            // Reuse the finite deployment zone instead of extending rows beyond the terrain forever.
            // Try alternate slots when a hole or obstacle makes the preferred slot unwalkable.
            var attempts = (int)System.Math.Min(64L, capacity);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var slot = ((long)spawnIndex + attempt) % capacity;
                // Fill from the middle outward, alternating both flanks before adding another row.
                var ordinal = (int)(slot % columns);
                var column = (columns - 1) / 2 + (ordinal % 2 == 1 ? (ordinal + 1) / 2 : -ordinal / 2);
                var desired = new Vector3(startX + side * (slot / columns) * spacing, 0f,
                    startZ + column * spacing);
                if (!IsWithinSpawnBounds(desired)) continue;
                desired.y = terrain.SampleHeight(desired) + origin.y;
                if (pathService.TrySamplePosition(desired, 5f, NavMesh.AllAreas, out var position))
                {
                    spawn = new NavMeshHit { position = position };
                    return true;
                }
            }
            return false;
        }

        private bool TryFindSelectedTypeSpawnPosition(bool isBlue, out NavMeshHit spawn)
        {
            return TryFindSpawnInFixedArea(isBlue, out spawn);
        }

        private bool TryFindSpawnInFixedArea(bool isBlue, out NavMeshHit spawn)
        {
            spawn = default;
            var spacing = Mathf.Max(1f, spawnSpacing);
            var axis = BattleAxis();
            var splitOnX = Mathf.Abs(axis.x) >= Mathf.Abs(axis.z);
            var blueOnLowSide = splitOnX ? axis.x >= 0f : axis.z >= 0f;
            var ownLowSide = isBlue ? blueOnLowSide : !blueOnLowSide;
            var anchor = GetSpawnTargetPosition(isBlue);
            anchor.x = Mathf.Clamp(anchor.x, SpawnMinX, SpawnMaxX);
            anchor.z = Mathf.Clamp(anchor.z, SpawnMinZ, SpawnMaxZ);

            // Keep one fixed rectangular staging area per team: its depth is spawnColumns
            // rows from the anchor toward the center, and its width spans the configured
            // cross-map bounds. Slots wrap inside this rectangle instead of marching onward.
            var crossMin = splitOnX ? SpawnMinZ : SpawnMinX;
            var crossMax = splitOnX ? SpawnMaxZ : SpawnMaxX;
            var crossSlots = Mathf.Max(1, Mathf.FloorToInt((crossMax - crossMin) / spacing));
            var crossStart = crossMin + spacing * 0.5f;
            var crossCenter = splitOnX ? anchor.z : anchor.x;
            var centerCrossIndex = Mathf.Clamp(
                Mathf.RoundToInt((crossCenter - crossStart) / spacing), 0, crossSlots - 1);
            var depthSlots = Mathf.Max(1, spawnColumns);
            var slotCount = crossSlots * depthSlots;
            var slotIndex = isBlue ? _blueCombatSpawnSlotIndex : _redCombatSpawnSlotIndex;
            var spawnAreaMask = GetSpawnAreaMask(isBlue);
            var cellSize = terrainMap != null && terrainMap.HasValidData
                ? Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) : 1f;
            var projectionDistance = Mathf.Max(0.5f, cellSize * 0.5f);

            for (var attempt = 0; attempt < slotCount; attempt++)
            {
                var slot = (slotIndex + attempt) % slotCount;
                // Fill the configured depth before widening the formation. The old
                // cross-first ordering spread hundreds of units across the map before
                // advancing even one depth row, so small batches appeared only 1-2 rows deep.
                var crossOrdinal = slot / depthSlots;
                var depth = slot % depthSlots;
                // Start beside the spawn target's center and fan out left/right by columns.
                // The former crossMin-based coordinate started every army at one map edge.
                var crossOffset = crossOrdinal == 0 ? 0 :
                    (crossOrdinal % 2 == 1 ? (crossOrdinal + 1) / 2 : -crossOrdinal / 2);
                var cross = centerCrossIndex + crossOffset;
                var offsetFromCenter = (depth + 0.5f) * spacing;
                var desired = splitOnX
                    ? new Vector3(anchor.x + (ownLowSide ? offsetFromCenter : -offsetFromCenter),
                        anchor.y, crossStart + cross * spacing)
                    : new Vector3(crossStart + cross * spacing, anchor.y,
                        anchor.z + (ownLowSide ? offsetFromCenter : -offsetFromCenter));
                if (!IsWithinSpawnBounds(desired) || !TryGetGroundHeight(desired, out var groundHeight))
                    continue;
                desired.y = groundHeight;
                if (terrainMap != null && terrainMap.HasValidData &&
                    terrainMap.TrySample(desired, out var mapHeight, out _))
                    desired.y = mapHeight;
                if (pathService == null || !pathService.TrySamplePosition(
                        desired, projectionDistance, spawnAreaMask, out var position) ||
                    HorizontalDistance(desired, position) > projectionDistance || !IsWithinSpawnBounds(position) ||
                    !IsTerrainPositionWalkableForTeam(position, isBlue))
                    continue;

                var nextSlot = (slot + 1) % slotCount;
                if (isBlue) _blueCombatSpawnSlotIndex = nextSlot;
                else _redCombatSpawnSlotIndex = nextSlot;
                spawn = new NavMeshHit { position = position };
                return true;
            }
            return false;
        }

        private static bool IsWithinSpawnBounds(Vector3 position) =>
            position.x >= SpawnMinX && position.x <= SpawnMaxX &&
            position.z >= SpawnMinZ && position.z <= SpawnMaxZ;

        private bool TryFindVariantSpawnPosition(
            bool isBlue,
            int spawnIndex,
            int variantCount,
            out NavMeshHit spawn)
        {
            spawn = default;
            var unitsPerVariant = Mathf.Max(1, unitsPerVariantAtStart);
            var variantIndex = (spawnIndex / unitsPerVariant) % variantCount;
            var unitIndex = spawnIndex % unitsPerVariant;
            var columnCount = Mathf.Min(VariantFormationColumns, unitsPerVariant);
            var rowCount = Mathf.CeilToInt((float)unitsPerVariant / columnCount);
            var variantColumns = Mathf.CeilToInt(Mathf.Sqrt(variantCount));
            var variantRow = variantIndex / variantColumns;
            var variantColumn = variantIndex % variantColumns;

            var blueAnchor = GetSpawnTargetPosition(true);
            var redAnchor = GetSpawnTargetPosition(false);
            var axis = redAnchor - blueAnchor;
            axis.y = 0f;
            if (axis.sqrMagnitude < 0.001f)
                axis = Vector3.right;
            else
                axis.Normalize();
            var forward = isBlue ? axis : -axis;
            var lateral = new Vector3(-axis.z, 0f, axis.x);
            var anchor = isBlue ? blueAnchor : redAnchor;
            var spacing = Mathf.Max(1f, spawnSpacing);
            var formationWidth = columnCount * spacing + spacing;
            var formationDepth = rowCount * spacing + spacing;
            var lateralBlockOffset =
                (variantColumn - (variantColumns - 1) * 0.5f) * formationWidth;
            var forwardBlockOffset = 10f + variantRow * formationDepth;
            var row = unitIndex / columnCount;
            var column = unitIndex % columnCount;
            var desired = anchor +
                          forward * (forwardBlockOffset +
                                     (row - (rowCount - 1) * 0.5f) * spacing) +
                          lateral * (lateralBlockOffset +
                                     (column - (columnCount - 1) * 0.5f) * spacing);
            if (!IsWithinSpawnBounds(desired)) return false;

            if (terrainMap != null && terrainMap.HasValidData)
            {
                return TryFindWalkableTerrainSpawn(desired, isBlue, out spawn);
            }
            else if (!TryGetGroundHeight(desired, out var groundHeight))
            {
                return false;
            }
            else
            {
                desired.y = groundHeight;
            }

            var projectionDistance = terrainMap != null
                ? Mathf.Max(3f, Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * 2f)
                : 5f;
            if (!pathService.TrySamplePosition(
                    desired, projectionDistance, GetSpawnAreaMask(isBlue), out var position))
                return false;
            spawn = new NavMeshHit { position = position };
            return true;
        }

        private bool TryFindWalkableTerrainSpawn(Vector3 desired, bool isBlue, out NavMeshHit spawn)
        {
            spawn = default;
            var cellSize = terrainMap.CellSize;
            var centerX = Mathf.Clamp(
                Mathf.FloorToInt((desired.x - terrainMap.WorldOrigin.x) / cellSize.x),
                0, terrainMap.Columns - 2);
            var centerZ = Mathf.Clamp(
                Mathf.FloorToInt((desired.z - terrainMap.WorldOrigin.z) / cellSize.y),
                0, terrainMap.Rows - 2);
            var maxRing = Mathf.Max(terrainMap.Columns, terrainMap.Rows);
            var projectionDistance = Mathf.Max(3f, Mathf.Max(cellSize.x, cellSize.y) * 2f);

            for (var ring = 0; ring <= maxRing; ring++)
            {
                var nearestDistance = float.PositiveInfinity;
                var nearestPosition = Vector3.zero;
                var found = false;
                for (var dz = -ring; dz <= ring; dz++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring)
                        continue;

                    var cellX = centerX + dx;
                    var cellZ = centerZ + dz;
                    var team = isBlue ? RecoilCastleTeam.Blue : RecoilCastleTeam.Red;
                    if (!terrainMap.IsWalkableForTeam(cellX, cellZ, team))
                        continue;

                    var candidate = new Vector3(
                        terrainMap.WorldOrigin.x + (cellX + 0.5f) * cellSize.x,
                        desired.y,
                        terrainMap.WorldOrigin.z + (cellZ + 0.5f) * cellSize.y);
                    if (!IsWithinSpawnBounds(candidate)) continue;
                    var distance = (candidate - desired).sqrMagnitude;
                    if (distance >= nearestDistance ||
                        !terrainMap.TrySample(candidate, out var height, out var walkable) || !walkable)
                        continue;
                    candidate.y = height;
                    if (!pathService.TrySamplePosition(
                            candidate, projectionDistance, GetSpawnAreaMask(isBlue), out var projected) ||
                        !IsTerrainPositionWalkableForTeam(projected, isBlue))
                        continue;

                    nearestDistance = distance;
                    nearestPosition = projected;
                    found = true;
                }

                if (!found)
                    continue;

                spawn = new NavMeshHit { position = nearestPosition };
                return true;
            }

            return false;
        }

        private bool TryFindMapSpawnPosition(bool isBlue, int spawnIndex, out NavMeshHit spawn)
        {
            spawn = default;
            var mapSize = terrainMap.WorldSize;
            var spacing = Mathf.Max(0.5f, spawnSpacing);
            var blueAnchor = GetSpawnTargetPosition(true);
            var redAnchor = GetSpawnTargetPosition(false);
            var axis = redAnchor - blueAnchor;
            axis.y = 0f;
            if (axis.sqrMagnitude < 0.001f)
                axis = Vector3.right;
            else
                axis.Normalize();
            var forward = isBlue ? axis : -axis;
            var lateral = new Vector3(-axis.z, 0f, axis.x);
            var anchor = isBlue ? blueAnchor : redAnchor;
            var centerDistance = Mathf.Abs(Vector3.Dot(battleCenter - anchor, forward));
            var rows = Mathf.Max(1, Mathf.FloorToInt(centerDistance / spacing));
            var lateralSize = Mathf.Abs(axis.x) * mapSize.y + Mathf.Abs(axis.z) * mapSize.x;
            var columns = Mathf.Clamp(
                Mathf.FloorToInt(lateralSize / spacing) + 1,
                1,
                Mathf.Max(1, spawnColumns));
            var capacity = (long)rows * columns;
            if (capacity <= 0)
                return false;

            var attempts = (int)System.Math.Min(64L, capacity);
            var projectionDistance = Mathf.Max(
                spacing,
                Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * 2f);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var slot = ((long)spawnIndex + attempt) % capacity;
                var ordinal = (int)(slot % columns);
                var column = (columns - 1) / 2 +
                             (ordinal % 2 == 1 ? (ordinal + 1) / 2 : -ordinal / 2);
                var lateralOffset = column * spacing;
                var desired = anchor + forward * (slot / columns * spacing) +
                              lateral * lateralOffset;
                if (!IsWithinSpawnBounds(desired)) continue;
                if (!IsTerrainPositionWalkableForTeam(desired, isBlue) ||
                    !terrainMap.TrySample(desired, out var height, out var walkable) || !walkable)
                    continue;
                desired.y = height;
                if (pathService.TrySamplePosition(desired, projectionDistance, GetSpawnAreaMask(isBlue), out var position) &&
                    IsTerrainPositionWalkableForTeam(position, isBlue))
                {
                    spawn = new NavMeshHit { position = position };
                    return true;
                }
            }
            return false;
        }

        private Vector3 GetSpawnTargetPosition(bool isBlue)
        {
            var target = isBlue ? blueSpawnTarget : redSpawnTarget;
            return target != null ? target.position : battleCenter +
                Vector3.right * ((isBlue ? -1f : 1f) * teamStartDistance);
        }

        private void EnsureSpawnTargets()
        {
            if (blueSpawnTarget == null) blueSpawnTarget = CreateSpawnTarget(true);
            if (redSpawnTarget == null) redSpawnTarget = CreateSpawnTarget(false);
        }

        private Transform CreateSpawnTarget(bool isBlue)
        {
            var position = GetSpawnTargetPosition(isBlue);
            if (TryGetGroundHeight(position, out var height))
                position.y = height;
            var projectionDistance = terrainMap != null
                ? Mathf.Max(5f, Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * 4f)
                : 5f;
            if (pathService.TrySamplePosition(position, projectionDistance, GetSpawnAreaMask(isBlue), out var projected) &&
                IsTerrainPositionWalkableForTeam(projected, isBlue))
                position = projected;
            var target = new GameObject(isBlue ? "Blue Spawn Target" : "Red Spawn Target").transform;
            target.SetParent(transform, false);
            target.position = position;
            return target;
        }

        private int GetSpawnAreaMask(bool isBlue) => pathService != null && pathService.UsingTerrainMap
            ? (isBlue ? 1 : 2)
            : NavMesh.AllAreas;

        private bool IsTerrainPositionWalkableForTeam(Vector3 position, bool isBlue)
        {
            if (terrainMap == null || !terrainMap.HasValidData) return true;
            var size = terrainMap.CellSize;
            var x = Mathf.FloorToInt((position.x - terrainMap.WorldOrigin.x) / size.x);
            var z = Mathf.FloorToInt((position.z - terrainMap.WorldOrigin.z) / size.y);
            return terrainMap.IsWalkableForTeam(x, z,
                isBlue ? RecoilCastleTeam.Blue : RecoilCastleTeam.Red);
        }

        private bool TryGetGroundHeight(Vector3 position, out float height)
        {
            if (pathService != null && pathService.UsingTerrainMap &&
                pathService.TerrainHeightField.TryHeight(new float2(position.x, position.z), out height))
                return true;
            if (terrainMap != null && terrainMap.TrySample(position, out height, out _))
                return true;
            if (terrain != null && terrain.terrainData != null)
            {
                height = terrain.SampleHeight(position) + terrain.transform.position.y;
                return true;
            }
            height = 0f;
            return false;
        }

        private void UpdateNavigationBridge(Soldier soldier, Soldier target)
        {
            if (target == null)
            {
                var enemies = soldier.IsBlue ? _redTeam : _blueTeam;
                foreach (var enemy in enemies)
                    if (enemy != null && !enemy.Retired && enemy.Simulation != null && IsAlive(enemy))
                    { target = enemy; break; }
            }
            var simulation = soldier.Simulation;
            if (!TryGetMovementSettings(soldier, out var movementSettings))
            {
                soldier.Retired = true;
                _hasRetirements = true;
                return;
            }
            simulation.SetCombatApproach(target != null);
            var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
            var targetEntity = target != null ? target.Entity : Entity.Null;
            if (target != null)
            {
                var targetPosition = target.Simulation.Position;
                var targetInRange = HorizontalDistance(simulation.Position, targetPosition) <=
                                    GetAttackRange(soldier) &&
                                    HasCombatLineOfFire(simulation.Position, targetPosition,
                                        soldier.RangedUnitType != 0
                                            ? RecoilCombatProjectileTrajectory.ArcHeight(new float2(
                                                simulation.Position.x, simulation.Position.z),
                                                new float2(targetPosition.x, targetPosition.z))
                                            : 0f);
                if (targetInRange)
                {
                    simulation.StopMovement();
                    var facingPose = simulation.Pose;
                    RecoilCombatFacing.FaceTarget(ref facingPose, targetPosition);
                    _entityManager.SetComponentData(soldier.Entity, facingPose);
                    timing.NavigationTarget = targetEntity;
                    _entityManager.SetComponentData(soldier.Entity, timing);
                    return;
                }

                var targetMoved = timing.NavigationTarget != targetEntity ||
                    HorizontalDistance(timing.LastDestination, targetPosition) >= retargetDistance;
                if (targetMoved || (!simulation.HasPath && !simulation.IsPathPending))
                {
                    timing.NavigationTarget = targetEntity;
                    timing.LastDestination = targetPosition;
                    var destination = (float3)targetPosition;
                    if (pathService.TerrainHeightField.TryFindUnitApproach(simulation.Position, destination,
                        movementSettings.AreaMask, out var approach, movementSettings.BodyRadius + 0.05f)) destination = approach;
                    simulation.SetDestination(destination);
                }
                _entityManager.SetComponentData(soldier.Entity, timing);
                return;
            }
            _entityManager.SetComponentData(soldier.Entity, new RecoilUnitOrder
            {
                Kind = target != null ? RecoilUnitOrderKind.Attack : RecoilUnitOrderKind.Stop,
                Target = targetEntity,
                Destination = target != null ? target.Simulation.Pose.Position : simulation.Pose.Position
            });
            if (target == null)
            {
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                var destination = GetCastleTargetCenter(state.Team == 0);
                var bodyRadius = movementSettings.BodyRadius;
                var navigation = _entityManager.GetComponentData<RecoilUnitNavigation>(soldier.Entity);
                if (navigation.CastleApproach == 0 && RecoilCastleMarch.TryStraightDestination(
                    simulation.Position, destination, GetCastleMarchTarget(state.Team == 0), out var straight, movementSettings.WaypointTolerance))
                    destination = straight;
                else
                {
                    navigation.CastleApproach = 1;
                    _entityManager.SetComponentData(soldier.Entity, navigation);
                    var marchTarget = GetCastleMarchTarget(state.Team == 0);
                    if (marchTarget.HasCastleBounds != 0)
                    {
                        var padding = terrainMap != null
                            ? Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) + 0.25f
                            : 1f;
                        padding = RecoilCastleMarch.AttackApproachPadding(
                            bodyRadius, padding, GetAttackRange(soldier));
                        destination = (Vector3)RecoilCastleMarch.AttackApproachPoint(
                            simulation.Pose.Position, marchTarget, padding);
                    }
                }
                if (TryGetGroundHeight(destination, out var destinationHeight))
                    destination.y = destinationHeight;
                _entityManager.SetComponentData(soldier.Entity, new RecoilUnitOrder
                { Kind = RecoilUnitOrderKind.Move, Destination = destination });
                var destinationChanged = HorizontalDistance(timing.LastDestination, destination) >= retargetDistance;
                if (HorizontalDistance(simulation.Position, destination) <= Mathf.Max(0.5f, bodyRadius))
                    simulation.StopMovement();
                else if (destinationChanged || (!simulation.HasPath && !simulation.IsPathPending))
                {
                    simulation.SetDestination(destination);
                    timing.LastDestination = destination;
                }
                timing.NavigationTarget = Entity.Null;
            }
            _entityManager.SetComponentData(soldier.Entity, timing);
        }

        private float GetAttackRange(Soldier soldier)
        {
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            if (state.Stats.IsConfigured) return state.Stats.Range;
            if (soldier.RangedUnitType == 2) return siegeAttackRange;
            return soldier.RangedUnitType == 1 ? archerAttackRange : attackRange;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private bool HasCombatLineOfFire(Vector3 start, Vector3 end, float arcHeight) =>
            pathService == null || pathService.TerrainHeightField.HasLineOfFire(start, end, arcHeight);

        private void KillSoldier(Soldier soldier)
        {
            if (soldier == null || soldier.Retired) return;
            soldier.Retired = true;
            PlayUnitDeathSound(soldier);
            ScheduleHeroRespawn(soldier);
            _hasRetirements = true;
            if (soldier.Entity != Entity.Null && _entityManager.Exists(soldier.Entity))
            {
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                state.Health = 0;
                state.Active = 0;
                state.Target = Entity.Null;
                _entityManager.SetComponentData(soldier.Entity, state);
            }

            soldier.Simulation.SetActive(false);
            if (soldier.Agent != null)
            {
                if (soldier.Colliders != null)
                    foreach (var collider in soldier.Colliders)
                        if (collider != null) collider.enabled = false;

                var unitObject = soldier.Agent.gameObject;
                // The impostor atlas has no death bank. Stop its GPU draws immediately,
                // then let the native low-detail mesh play the death/sinking presentation.
                if (_gpuSkinnedCrowd != null) _gpuSkinnedCrowd.Unregister(unitObject);
                foreach (var group in unitObject.GetComponentsInChildren<LODGroup>(true))
                    group.ForceLOD(Mathf.Min(2, group.lodCount - 1));
                if (soldier.Presentation != null &&
                    soldier.Presentation.PlayDeath(() => CompletePrefabSoldierRetirement(soldier, unitObject)))
                    return;
                if (soldier.HeroVat != null)
                {
                    soldier.HeroVat.Play(3);
                    StartCoroutine(RetireHeroAfterDeath(soldier, unitObject));
                    return;
                }
                if (soldier.Vat != null)
                {
                    soldier.Vat.BeginDeath(() => CompletePrefabSoldierRetirement(soldier, unitObject));
                    return;
                }

                CompletePrefabSoldierRetirement(soldier, unitObject);
            }
            else
            {
                if (_entityManager.Exists(soldier.Entity))
                    _entityManager.DestroyEntity(soldier.Entity);
                _ownedSoldierEntities.Remove(soldier.Entity);
            }
        }

        private void CompletePrefabSoldierRetirement(Soldier soldier, GameObject unitObject)
        {
            if (unitObject != null)
            {
                if (!_prefabSoldierOwners.TryGetValue(unitObject, out var owner) || !ReferenceEquals(owner, soldier)) return;
                _prefabSoldierOwners.Remove(unitObject);
            }
            ReleaseFullDetailCount(soldier);
            if (soldier.Agent != null)
                soldier.Agent.Bind(null);
            soldier.Agent = null;
            soldier.Presentation = null;
            soldier.Vat = null;
            soldier.Colliders = null;
            if (_entityManager.Exists(soldier.Entity))
                _entityManager.DestroyEntity(soldier.Entity);
            _ownedSoldierEntities.Remove(soldier.Entity);
            if (unitObject == null)
                return;
            if (_gpuSkinnedCrowd != null) _gpuSkinnedCrowd.Unregister(unitObject);
            foreach (var group in unitObject.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(-1);
            unitObject.SetActive(false);
            unitObject.transform.SetParent(transform, true);
            GetUnitPool(soldier.IsBlue, soldier.Template).Enqueue(unitObject);
        }

        private void ReleaseFullDetailCount(Soldier soldier)
        {
            if (!soldier.FullDetailCounted)
                return;
            soldier.FullDetailCounted = false;
            _fullDetailSoldierCount = Mathf.Max(0, _fullDetailSoldierCount - 1);
        }

        private bool IsAlive(Soldier soldier)
        {
            if (soldier == null || soldier.Entity == Entity.Null ||
                _world == null || !_world.IsCreated ||
                !_entityManager.Exists(soldier.Entity) ||
                !_entityManager.HasComponent<RecoilBattleEcsState>(soldier.Entity))
                return false;

            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            return state.Active != 0 && state.Health > 0;
        }

        private bool TryGetMovementSettings(Soldier soldier, out RecoilUnitMovementSettings settings)
        {
            settings = default;
            if (soldier?.Simulation == null || soldier.Entity == Entity.Null ||
                soldier.Simulation.Entity != soldier.Entity ||
                soldier.Simulation.SimulationWorld != _world ||
                !soldier.Simulation.TryGetSettings(out settings))
                return false;
            return true;
        }

        private void RemoveDestroyedSoldiers(List<Soldier> team)
        {
            _deadSoldierPredicate ??= IsDeadSoldier;
            // Compact once instead of repeatedly shifting the list for every death.
            // Keep combat slots stable. Snapshot capture clears missing entities and the
            // spatial-grid update removes their cells without rebuilding the full roster.
            RecordCombatRosterRemovals(team.RemoveAll(_deadSoldierPredicate));
        }

        private void RecordCombatRosterRemovals(int removed)
        {
            if (removed <= 0 || _combatSoldiers.Count == 0)
                return;
            _combatRosterRemovalsSinceBuild += removed;
            var compactThreshold = Mathf.Min(_combatSoldiers.Count,
                Mathf.Max(512, _combatSoldiers.Count / 4));
            if (_combatRosterRemovalsSinceBuild >= compactThreshold)
                _combatRosterDirty = true;
        }

        private bool IsDeadSoldier(Soldier soldier)
        {
            if (soldier == null || soldier.Retired) return true;
            if (!_useAliveFlags) return !IsAlive(soldier);
            return soldier.CombatSlot < 0 ||
                soldier.CombatSlot >= _combatSoldiers.Count ||
                _combatAliveFlags[soldier.CombatSlot] == 0;
        }

        private void CompactRetiredSoldiers()
        {
            using var measure = RetirementMarker.Auto();
            if (!_hasRetirements) return;
            _retiredSoldierPredicate ??= soldier => soldier.Retired;
            var removed = _blueTeam.RemoveAll(_retiredSoldierPredicate) +
                          _redTeam.RemoveAll(_retiredSoldierPredicate);
            RecordCombatRosterRemovals(removed);
            _hasRetirements = false;
        }

        private void PruneCombatRoster()
        {
            var start = RecoilCpuPhases.Timestamp();
            RefreshCombatRoster();
            if (_combatSoldiers.Count == 0)
            {
                // No valid snapshot slots exist for the fast alive-flag path. Use guarded
                // EntityManager checks once to compact stale team references instead.
                _useAliveFlags = false;
                RemoveDestroyedSoldiers(_blueTeam);
                RemoveDestroyedSoldiers(_redTeam);
                LastRosterPruneMilliseconds = (float)((RecoilCpuPhases.Timestamp() - start) *
                    (1000.0 / System.Diagnostics.Stopwatch.Frequency));
                return;
            }
            if (!_hasAliveQuery)
            {
                _aliveQuery = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<RecoilBattleEcsState>());
                _hasAliveQuery = true;
            }
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.AliveCapture))
            {
            var reset = new RecoilResetAliveFlagsJob { Alive = _combatAliveFlags }.Schedule(_combatSoldiers.Count, 64);
            new RecoilCaptureAliveJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                Indices = _combatEntityIndices, Alive = _combatAliveFlags
            }.ScheduleParallel(_aliveQuery, reset).Complete();
            }
            _useAliveFlags = true;
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.RosterCompact))
            {
            try
            {
                RemoveDestroyedSoldiers(_blueTeam);
                RemoveDestroyedSoldiers(_redTeam);
            }
            finally { _useAliveFlags = false; }
            }
            LastRosterPruneMilliseconds = (float)((RecoilCpuPhases.Timestamp() - start) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        private void DisposeAliveQuery()
        {
            if (_hasAliveQuery && _world != null && _world.IsCreated) _aliveQuery.Dispose();
            _hasAliveQuery = false;
        }

        private void OnGUI()
        {
            TextOnly(GUI.skin.label);
            var previousDepth = GUI.depth;
            GUI.depth = -100;
            try
            {
                DrawBattleScreenGui();
                DrawLobbyCursor();
            }
            finally { GUI.depth = previousDepth; }
        }

        private void DrawBattleScreenGui()
        {
            if (CapacityBenchmarkHidesUi) return;
            if (_battlefieldClearing)
            {
                DrawBattlefieldClearing();
                return;
            }
            if (_startCountdownEndsAt > 0 && !_combatStarted && !_matchFinished)
            {
                DrawHeroHealthBars();
                DrawBattleVitalsHud();
                DrawStartCountdown();
                DrawHeroIntro(); // summon intros keep playing through the countdown
                return;
            }
            if (!_templatesValidated)
            {
                DrawTemplateValidationScreen();
                return;
            }

            if (_matchFinished)
            {
                DrawMatchResult();
                return;
            }
            if (_initialFormationLoading || _poolPrewarmRunning)
            {
                DrawInitialFormationLoading();
                return;
            }
            if (!_lobbyStarted)
            {
                DrawLobby();
                return;
            }
            DrawHeroHealthBars();
            DrawBattleHud();
            DrawBattleLeaderboardButton(); // bottom-left; the hero intro videos may cover it while they play
            DrawHeroIntro();
        }

        private GUIStyle _heroHealthLabel;
        private void DrawHeroHealthBars()
        {
            var previousDepth = GUI.depth;
            GUI.depth = 100;
            try { DrawHeroHealthBarsBehindHud(); }
            finally { GUI.depth = previousDepth; }
        }

        private void DrawHeroHealthBarsBehindHud()
        {
            if (Event.current.type != EventType.Repaint || !_hasEntityManager || _world == null || !_world.IsCreated) return;
            var view = Camera.main;
            if (view == null) return;
            _heroHealthLabel ??= new GUIStyle(GUI.skin.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = 13, fontStyle = FontStyle.Bold, padding = new RectOffset() };
            var oldColor = GUI.color;
            var oldContent = GUI.contentColor;
            GUI.color = GUI.contentColor = Color.white;
            DrawHeroTeamHealthBars(_blueTeam, view);
            DrawHeroTeamHealthBars(_redTeam, view);
            GUI.color = oldColor;
            GUI.contentColor = oldContent;
        }

        private void DrawHeroTeamHealthBars(List<Soldier> team, Camera view)
        {
            foreach (var hero in team)
            {
                if (hero == null || hero.HeroId <= 0 || hero.Retired || hero.Agent == null ||
                    !hero.Agent.gameObject.activeInHierarchy || !_entityManager.Exists(hero.Entity)) continue;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(hero.Entity);
                if (state.Active == 0 || state.Health <= 0) continue;
                var screen = view.WorldToScreenPoint(hero.Agent.transform.position + Vector3.up * (hero.VisualHeight + 3f));
                if (screen.z <= 0 || !view.pixelRect.Contains(new Vector2(screen.x, screen.y))) continue;
                var bar = new Rect(screen.x - 110, Screen.height - screen.y, 220, 20);
                const float avatarSize = 72;
                const float avatarOverlap = avatarSize * .5f;
                var avatar = new Rect(bar.x - avatarOverlap, bar.center.y - avatarOverlap, avatarSize, avatarSize);
                GUI.color = Color.white;
                GUI.DrawTexture(bar, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                    new Color(.18f, .18f, .18f, 1), 0, bar.height * .5f);
                float fraction = Mathf.Clamp01(state.Health / (float)Mathf.Max(1, state.Stats.MaxHealth));
                float visibleWidth = bar.width - avatarOverlap;
                var fill = new Rect(bar.x, bar.y, avatarOverlap + visibleWidth * fraction, bar.height);
                GUI.DrawTexture(fill, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                    hero.IsBlue ? new Color(.1f, .45f, 1, 1) : new Color(.9f, .15f, .12f, 1), 0, bar.height * .5f);
                // Approximate HP units: 30 HP = 2 segments, 300 HP = 20.
                // Bound the density so the marks remain readable at screen resolution.
                int segments = Mathf.Clamp(Mathf.CeilToInt(state.Stats.MaxHealth / 15f), 2, 40);
                GUI.color = new Color(0, 0, 0, .65f);
                for (int mark = 1; mark < segments; mark++)
                {
                    float x = bar.x + avatarOverlap + visibleWidth * mark / segments;
                    GUI.DrawTexture(new Rect(x, bar.y + 3, 1, bar.height - 6), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
                // The donor portrait overlays the left end of the health bar.
                GUI.DrawTexture(avatar, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                    Color.black, 0, avatarOverlap);
                if (!string.IsNullOrEmpty(hero.DonorId) && _donationTopUsers.TryGetValue(hero.DonorId, out var portrait) && portrait.Avatar != null)
                    GUI.DrawTexture(avatar, portrait.Avatar, ScaleMode.StretchToFill, true);
                var label = new Rect(bar.x + avatarOverlap + 6, bar.y - 23, visibleWidth - 6, 20);
                string title = lobbyHeroStats != null ? System.Array.Find(lobbyHeroStats, h => h.Id == hero.HeroId)?.Name : null;
                string text = string.IsNullOrEmpty(title) ? "Tướng " + hero.HeroId : title;
                var donor = string.IsNullOrWhiteSpace(hero.DonorName) ? "Người ủng hộ" : hero.DonorName;
                DrawHeroIdentityLine(label, donor, UserNameColor(donor, hero.IsBlue));
                DrawHeroIdentityLine(new Rect(label.x, bar.yMax + 3, label.width, bar.height), text, Color.white);
            }
        }

        private void DrawLobby()
        {
            if (!_settingsOpen) DrawLobbyBackdrop();
            else DrawSettingsBackdrop();
            var width = Mathf.Min(650f, Screen.width - 32f);
            var height = Mathf.Min(_settingsOpen ? 590f : 460f, Screen.height - 24f);
            var panel = new Rect((Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f, width, height);
            var layout = lobbyLayout;
            var screenW = Screen.width;
            var screenH = Screen.height;
            var titleAspect = lobbyTitleImage != null && lobbyTitleImage.height > 0
                ? (float)lobbyTitleImage.width / lobbyTitleImage.height : 3f;
            var titleRect = RecoilLobbyLayout.GuiRect(layout.title,
                RecoilLobbyLayout.TitleBounds(titleAspect), screenW, screenH);
            if (!_settingsOpen)
            {
                var oldMatrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(RecoilLobbyLayout.GuiAngle(layout.title.rotation), titleRect.center);
                DrawLobbyImage(titleRect, lobbyTitleImage, ScaleMode.ScaleToFit);
                GUI.matrix = oldMatrix;
            }

            if (_settingsOpen)
            {
                DrawLobbySettings(panel);
                return;
            }

            var startButton = FitLobbyButtonImage(
                RecoilLobbyLayout.GuiRect(layout.startButton, RecoilLobbyLayout.StartButtonBounds, screenW, screenH),
                lobbyStartButtonImage);
            var settingsButton = FitLobbyButtonImage(
                RecoilLobbyLayout.GuiRect(layout.settingsButton, RecoilLobbyLayout.SettingsButtonBounds, screenW, screenH),
                lobbySettingsButtonImage);
            // Loading after Start: the buttons fly off (Start down, Settings right; the clock goes left on its own
            // canvas) and only the title and the loading bar stay.
            var exit = LobbyExitEased;
            if (exit != 0f)
            {
                startButton.y += (Screen.height + 20f - startButton.yMin) * exit;
                settingsButton.x += (Screen.width + 20f - settingsButton.xMin) * exit;
            }
            var buttonsGone = LobbyExitProgress >= 1f;
            if (!buttonsGone && LobbyImageButton(settingsButton, lobbySettingsButtonImage, layout.settingsButton.rotation))
                _settingsOpen = true;
            if (!buttonsGone && LobbyImageButton(startButton, lobbyStartButtonImage, layout.startButton.rotation))
                StartMatchFromLobby();
            var loadingRect = RecoilLobbyLayout.GuiRect(layout.loadingBar, RecoilLobbyLayout.LoadingBarBounds, screenW, screenH);
            var errorRect = RecoilLobbyLayout.GuiRect(layout.errorText, RecoilLobbyLayout.ErrorTextBounds, screenW, screenH);
            if (_poolPrewarmRunning || _initialFormationLoading)
            {
                // Buttons fly off first, a short beat, then the bar rises into place.
                var barIn = LobbyLoadingBarIn;
                if (barIn >= 0f)
                {
                    loadingRect.y += (1f - barIn) * 28f * RecoilLobbyLayout.FitScale(Screen.width, Screen.height);
                    DrawPoolLoadingBar(loadingRect);
                }
            }
            else if (!string.IsNullOrEmpty(_lobbyError))
            {
                _lobbyErrorStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(errorRect, _lobbyError, _lobbyErrorStyle);
            }
            DrawLobbyClockOnTop(); // over the title and buttons; the cursor is drawn after this
        }

        private static Rect FitLobbyButtonImage(Rect bounds, Texture2D image)
        {
            if (image == null || image.width <= 0 || image.height <= 0) return bounds;
            var fit = Mathf.Min(bounds.width / image.width, bounds.height / image.height);
            var size = new Vector2(image.width, image.height) * fit;
            return new Rect(bounds.center - size * 0.5f, size);
        }

        private bool LobbyImageButton(Rect rect, Texture2D image, float rotation = 0f)
        {
            var hover = SettingsButtonHover(rect);
            var oldMatrix = GUI.matrix;
            try
            {
                GUIUtility.RotateAroundPivot(RecoilLobbyLayout.GuiAngle(rotation), rect.center);
                GUIUtility.ScaleAroundPivot(Vector2.one * (1f + .055f * hover), rect.center);
                GUI.matrix = Matrix4x4.Translate(new Vector3(0f, -4f * hover, 0f)) * GUI.matrix;
                // A click on the lobby clock drawn over this button belongs to the clock.
                var clicked = GUI.Button(rect, GUIContent.none, GUIStyle.none) &&
                              !LobbyClockCovers(Event.current.mousePosition);
                DrawLobbyImage(rect, image, ScaleMode.ScaleToFit);
                return clicked;
            }
            finally { GUI.matrix = oldMatrix; }
        }

        private void DrawLobbySettings(Rect panel)
        {
            var settings = GUI.skin.settings;
            var previousCursor = settings.cursorColor;
            var previousSelection = settings.selectionColor;
            var previousFlash = settings.cursorFlashSpeed;
            settings.cursorColor = ThemeGoldBright;
            settings.selectionColor = Alpha(ThemeGold, .35f);
            settings.cursorFlashSpeed = 1f;
            ApplySettingsSkin(); // themed look; only real buttons react to hovering in the settings
            try
            {
            var wasInitialArmySettingsOpen = _initialArmySettingsOpen;
            DrawSetupNavigation();
            if (wasInitialArmySettingsOpen && (!_initialArmySettingsOpen || !_settingsOpen))
                CancelUnassignedInitialArmyBlocks();
            if (_initialArmySettingsOpen)
            {
                DrawInitialArmySettings();
                return;
            }
            if (_cursorSettingsOpen)
            {
                DrawCursorSettings();
                return;
            }
            if (_heroSettingsOpen)
            {
                DrawCombatSettings(true);
                return;
            }
            if (_tikTokSettingsOpen)
            {
                DrawTikTokArmySettings();
                return;
            }
            DrawCombatSettings(false);
            }
            finally
            {
                settings.cursorColor = previousCursor;
                settings.selectionColor = previousSelection;
                settings.cursorFlashSpeed = previousFlash;
                RestoreSettingsSkin();
            }
        }

        private void DrawAttributeRadar(Rect bounds, RecoilUnitCombatStats stats, RecoilUnitCombatStats? configuredLimits = null)
        {
            var limits = configuredLimits ?? GetLobbyStatLimits(_selectedLobbyUnitType);
            var changed = _attributeRadar == null;
            for (var i = 0; i < 8; i++)
            {
                var value = i == 0 ? (float)stats.MaxHealth / limits.MaxHealth : i == 1 ? (float)stats.Damage / limits.Damage :
                    i == 2 ? stats.Range / limits.Range : i == 3 ? stats.AttackSpeed / limits.AttackSpeed :
                    i == 4 ? stats.MoveSpeed / limits.MoveSpeed : i == 5 ? stats.Armor :
                    i == 6 ? stats.SecondaryArmor : stats.AreaRadius / limits.AreaRadius;
                value = Mathf.Clamp01(value);
                changed |= _radarValues[i] != value;
                _radarValues[i] = value;
            }
            if (_attributeRadar == null)
                _attributeRadar = new Texture2D(256, 256, TextureFormat.RGBA32, false)
                { name = "Unit Attribute Radar", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            if (changed)
            {
                System.Array.Clear(_radarPixels, 0, _radarPixels.Length);
                for (var i = 0; i < 8; i++)
                {
                    var angle = (-90f + i * 45f) * Mathf.Deg2Rad;
                    _radarVertices[i] = new Vector2(128f, 128f) +
                        new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (124f * _radarValues[i]);
                }
                for (var y = 0; y < 256; y++)
                for (var x = 0; x < 256; x++)
                {
                    var inside = false;
                    for (int i = 0, j = 7; i < 8; j = i++)
                    {
                        var a = _radarVertices[i];
                        var b = _radarVertices[j];
                        if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x)
                            inside = !inside;
                    }
                    if (inside) _radarPixels[(255 - y) * 256 + x] = new Color32(226, 185, 92, 78);
                }
                _attributeRadar.SetPixels32(_radarPixels);
                _attributeRadar.Apply(false, false);
            }
            var radius = Mathf.Min(bounds.width * 0.43f, bounds.height * 0.40f);
            var center = bounds.center;
            var chart = new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f);
            GUI.DrawTexture(chart, _attributeRadar);
            for (var i = 0; i < 8; i++)
            {
                var angle = (-90f + i * 45f) * Mathf.Deg2Rad;
                var nextAngle = angle + Mathf.PI / 4f;
                var axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                var nextAxis = new Vector2(Mathf.Cos(nextAngle), Mathf.Sin(nextAngle));
                DrawRadarLine(center, center + axis * radius, Alpha(Color.white, .2f));
                for (var ring = 1; ring <= 5; ring++)
                    DrawRadarLine(center + axis * (radius * ring / 5f),
                        center + nextAxis * (radius * ring / 5f), Alpha(Color.white, ring == 5 ? .22f : .1f));
                DrawRadarLine(center + axis * (radius * _radarValues[i]),
                    center + nextAxis * (radius * _radarValues[(i + 1) % 8]), ThemeGoldBright);
                var labelPoint = center + axis * (radius + 26f);
                GUI.Label(new Rect(labelPoint.x - 54f, labelPoint.y - 16f, 108f, 36f),
                    RadarLabels[i], RadarLabelStyle);
            }
        }

        private GUIStyle _radarLabelStyle;
        private GUIStyle RadarLabelStyle => _radarLabelStyle ??= new GUIStyle(GUIStyle.none)
        {
            font = SettingsFont, alignment = TextAnchor.MiddleCenter, fontSize = 15, fontStyle = FontStyle.Bold,
            wordWrap = true, normal = { textColor = ThemeTextDim }, hover = { textColor = ThemeTextDim }
        };

        private static void DrawRadarLine(Vector2 start, Vector2 end, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            var matrix = GUI.matrix;
            var previousColor = GUI.color;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(end.y - start.y, end.x - start.x) * Mathf.Rad2Deg, start);
            GUI.DrawTexture(new Rect(start.x, start.y, Vector2.Distance(start, end), 1.5f), Texture2D.whiteTexture);
            GUI.matrix = matrix;
            GUI.color = previousColor;
        }

        private GUIStyle _settingsSliderStyle, _settingsThumbStyle;
        private GUIStyle _settingsValueInputStyle;
        private readonly System.Collections.Generic.Dictionary<int, float> _settingsButtonHover = new();
        private readonly Dictionary<string, string> _settingsValueInputs = new();
        private readonly HashSet<string> _focusedSettingsValueInputs = new();
        private GUIStyle _settingsAttributeLabelStyle;
        private GUIStyle _settingsUnitNameStyle;
        private GUIStyle SettingsUnitNameStyle => _settingsUnitNameStyle ??= new GUIStyle(GUIStyle.none)
        {
            font = SettingsFont, fontSize = 34, fontStyle = FontStyle.Bold, padding = new RectOffset(2, 16, 4, 4),
            normal = { textColor = ThemeGoldBright }, hover = { textColor = ThemeGoldBright }
        };
        private GUIStyle SettingsAttributeLabelStyle => _settingsAttributeLabelStyle ??= new GUIStyle(GUIStyle.none)
        {
            font = SettingsFont, fontSize = 19, fontStyle = FontStyle.Bold, wordWrap = true,
            alignment = TextAnchor.MiddleLeft, normal = { textColor = ThemeText }, hover = { textColor = ThemeText }
        };

        private float SettingsButtonHover(Rect rect)
        {
            var id = rect.GetHashCode();
            var hoverRect = rect;
            hoverRect.xMin -= 6f;
            hoverRect.yMin -= 6f;
            hoverRect.xMax += 6f;
            hoverRect.yMax += 6f;
            var target = GUI.enabled && hoverRect.Contains(Event.current.mousePosition) ? 1f : 0f;
            _settingsButtonHover.TryGetValue(id, out var hover);
            if (Event.current.type == EventType.Repaint)
            {
                hover = Mathf.Lerp(hover, target, 1f - Mathf.Exp(-16f * Time.unscaledDeltaTime));
                _settingsButtonHover[id] = hover;
            }
            return hover;
        }

        private bool SettingsButton(Rect rect, GUIContent content, GUIStyle style)
        {
            var hover = SettingsButtonHover(rect);
            var oldMatrix = GUI.matrix;
            try
            {
                GUIUtility.ScaleAroundPivot(Vector2.one * (1f + .055f * hover), rect.center);
                GUI.matrix = Matrix4x4.Translate(new Vector3(0f, -4f * hover, 0f)) * GUI.matrix;
                return GUI.Button(rect, content, style);
            }
            finally { GUI.matrix = oldMatrix; }
        }

        /// <summary>A selector (e.g. the hero list): no hover effect, only real buttons react to hovering.</summary>
        private bool SettingsToggleButton(Rect rect, bool active, GUIContent content, GUIStyle style) =>
            GUI.Toggle(rect, active, content, WithoutHover(style));

        private bool SettingsGUILayoutButton(GUIContent content, params GUILayoutOption[] options)
        {
            var style = GUI.skin.button;
            var rect = GUILayoutUtility.GetRect(content, style, options);
            return SettingsButton(rect, content, style);
        }

        private bool DrawSecondaryDamageCheckbox(Rect rect, bool value)
        {
            if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) value = !value;
            var box = new Rect(rect.x, rect.y + 12f, 28f, 28f);
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(box, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, ThemeSurfaceRaised, 0, 7f);
                GUI.DrawTexture(box, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                    value ? ThemeGold : ThemeLineStrong, 1.5f, 7f);
                if (value)
                    GUI.DrawTexture(new Rect(box.x + 7f, box.y + 7f, 14f, 14f), Texture2D.whiteTexture,
                        ScaleMode.StretchToFill, true, 0, ThemeGold, 0, 4f);
            }
            GUI.Label(new Rect(rect.x + 52f, rect.y + 14f, Mathf.Max(0f, rect.width - 52f), 32f),
                "Gây sát thương phép", SettingsAttributeLabelStyle);
            return value;
        }
        private float DrawStatSlider(
            float x, float y, float width, string label, float value, float minimum, float maximum,
            string format)
        {
            var labelWidth = Mathf.Min(210f, width * 0.44f);
            var wideValue = format == "N0W";
            var valueWidth = Mathf.Min(wideValue ? 160f : 74f, width * (wideValue ? 0.34f : 0.2f));
            GUI.Label(new Rect(x, y, labelWidth, 32f), label, SettingsAttributeLabelStyle);
            if (_settingsSliderStyle == null)
            {
                _settingsSliderStyle = new GUIStyle(GUI.skin.horizontalSlider)
                { fixedHeight = 18f, padding = new RectOffset(0, 0, 0, 0), border = new RectOffset() };
                _settingsThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb)
                { fixedWidth = 18f, fixedHeight = 18f, border = new RectOffset(), margin = new RectOffset(), overflow = new RectOffset() };
                foreach (var state in new[] { _settingsSliderStyle.normal, _settingsSliderStyle.hover,
                    _settingsSliderStyle.active, _settingsSliderStyle.focused }) state.background = null;
                // Round cream thumb with a gold ring.
                var thumb = RoundedTexture(ThemeText, ThemeGold, 8, 1.5f);
                foreach (var state in new[] { _settingsThumbStyle.normal, _settingsThumbStyle.hover,
                    _settingsThumbStyle.active, _settingsThumbStyle.focused })
                {
                    state.background = thumb;
                    state.scaledBackgrounds = null;
                }
            }
            const float textGap = 20f;
            var slider = new Rect(x + labelWidth + textGap, y + 3f,
                Mathf.Max(20f, width - labelWidth - valueWidth - textGap), 20f);
            if (Event.current.type == EventType.Repaint)
            {
                // Slim rounded track; the filled part in gold.
                GUI.DrawTexture(new Rect(slider.x, slider.center.y - 3f, slider.width, 6f), Texture2D.whiteTexture,
                    ScaleMode.StretchToFill, true, 0, Alpha(Color.white, .13f), 0, 3f);
                var filled = slider.width * Mathf.InverseLerp(minimum, maximum, value);
                if (filled > .5f)
                    GUI.DrawTexture(new Rect(slider.x, slider.center.y - 3f, filled, 6f), Texture2D.whiteTexture,
                        ScaleMode.StretchToFill, true, 0, ThemeGold, 0, 3f);
            }
            var previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = Color.white;
            float beforeSlider = value;
            value = GUI.HorizontalSlider(slider, value, minimum, maximum, _settingsSliderStyle, _settingsThumbStyle);
            if (value != beforeSlider)
            {
                int decimals = format == "0.##" || format == "0.00" || format == "P0" ? 2 : format == "0.0" ? 1 : 0;
                value = Mathf.Clamp((float)System.Math.Round(value, decimals), minimum, maximum);
                _settingsValueInputs.Remove(label);
            }
            GUI.backgroundColor = previousBackground;

            var controlName = $"RecoilSettingValue_{label}";
            GUI.SetNextControlName(controlName);
            _settingsValueInputStyle ??= SettingsTextBoxStyle();
            var inputRect = new Rect(x + width - valueWidth, y, valueWidth, 28f);
            var displayFormat = format == "P0" ? "P0" : wideValue ? "N0" : format;
            if (!_settingsValueInputs.ContainsKey(label))
                _settingsValueInputs[label] = value.ToString(displayFormat, CultureInfo.CurrentCulture);
            var previousValue = value;
            var input = GUI.TextField(inputRect, _settingsValueInputs[label], _settingsValueInputStyle);
            bool inputChanged = input != _settingsValueInputs[label];
            if (inputChanged)
                _settingsValueInputs[label] = input;

            var wasFocused = _focusedSettingsValueInputs.Contains(label);
            var isFocused = GUI.GetNameOfFocusedControl() == controlName;
            if (isFocused)
            {
                _focusedSettingsValueInputs.Add(label);
                if (inputChanged && TryParseSettingValue(input, format == "P0", out var parsed))
                    value = Mathf.Clamp(parsed, minimum, maximum);
            }
            else
            {
                _focusedSettingsValueInputs.Remove(label);
                if (inputChanged && TryParseSettingValue(input, format == "P0", out var parsed))
                    value = Mathf.Clamp(parsed, minimum, maximum);

                if (wasFocused || !Mathf.Approximately(previousValue, value))
                    _settingsValueInputs[label] = value.ToString(displayFormat, CultureInfo.CurrentCulture);
            }
            return value;
        }

        private static bool TryParseSettingValue(string input, bool isPercentage, out float value)
        {
            var hasPercentSymbol = input.IndexOf('%') >= 0;
            var numericText = hasPercentSymbol ? input.Replace("%", string.Empty).Trim() : input;
            if (!float.TryParse(numericText, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture, out value))
                return false;
            if (isPercentage) value *= 0.01f;
            return true;
        }

        private void DrawLobbyBackdrop()
        {
            if (LobbyClockDrawsBackdrop) return; // the lobby clock canvas draws it, under the clock
            var oldColor = GUI.color;
            if (Event.current.type == EventType.Repaint)
            {
                if (lobbyBackgroundImage != null)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height),
                        lobbyBackgroundImage, ScaleMode.ScaleAndCrop, true);
                }
                else
                {
                    GUI.color = new Color(0.025f, 0.04f, 0.07f, 0.92f);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                }
            }
            GUI.color = oldColor;
        }

        private static void DrawLobbyImage(Rect rect, Texture2D image, ScaleMode scaleMode)
        {
            if (Event.current.type == EventType.Repaint && image != null)
                GUI.DrawTexture(rect, image, scaleMode, true);
        }

        private void DrawBattleHud()
        {
            if (_showBattleVitalsHud)
            {
                DrawBattleVitalsHud();
                return;
            }

            GUI.Box(new Rect(6f, 6f, 738f, 244f), GUIContent.none);
            var status = _combatStarted
                ? "ĐANG GIAO CHIẾN"
                : _lobbyStarting
                    ? HasAliveSoldier(_blueTeam) && HasAliveSoldier(_redTeam)
                        ? "LÍNH ĐANG ĐI RA — NHẤN L ĐỂ GIAO CHIẾN"
                        : $"ĐANG XUẤT QUÂN — BLUE: {_blueTeam.Count}  RED: {_redTeam.Count}"
                    : "CHƯA CÓ QUÂN — BẤM NÚT LOẠI QUÂN ĐỂ GỌI";
            GUI.Label(new Rect(12f, 12f, 480f, 24f), status);
            GUI.Label(new Rect(12f, 38f, 720f, 24f),
                $"ALIVE  BLUE: {_blueTeam.Count}   RED: {_redTeam.Count}   TOTAL: {_blueTeam.Count + _redTeam.Count}");
            GUI.Label(new Rect(12f, 62f, 720f, 24f),
                $"SPAWNED  BLUE: {_blueSpawnedTotal}   RED: {_redSpawnedTotal}   TOTAL: {_blueSpawnedTotal + _redSpawnedTotal}");
            GUI.Label(new Rect(12f, 86f, 720f, 24f),
                $"QUEUED  BLUE: {_blueSpawnQueue + _blueSelectedSpawnTotal}   RED: {_redSpawnQueue + _redSelectedSpawnTotal}" +
                (_combatStarted ? "  |  bấm nút loại quân để gọi thêm" : string.Empty));
            if (_combatStarted)
                GUI.Label(new Rect(12f, 108f, 720f, 20f),
                    $"CASTLE HP  BLUE: {_blueCastleHealthRemaining:N0}   RED: {_redCastleHealthRemaining:N0}" +
                    $"   HITS: {_castleAttackCount:N0}  DMG: {_castleDamageTotal:N0}");
            if (_combatStarted)
                GUI.Label(new Rect(12f, 128f, 720f, 20f),
                    $"CASTLE TICK  SCHEDULED IN RANGE: {_castleAttackersInRangeLastTick}" +
                    $"  TARGET BLOCK: {_castleTargetConflictsLastTick}  TERRAIN BLOCK: {_castleLineOfFireBlockedLastTick}" +
                    $"  FIRED: {_castleShotsLaunchedLastTick}");
            GUI.Label(new Rect(12f, 150f, 900f, 20f),
                $"W/S: TIẾN/LÙI | A/D: TRÁI/PHẢI | Q/E: LÊN/XUỐNG | SHIFT: TĂNG TỐC | GIỮ CHUỘT TRÁI/PHẢI: XOAY GÓC NHÌN");
            for (var i = 0; i < CharacterTypeCount; i++)
            {
                var row = i / 4;
                var column = i % 4;
                var rect = new Rect(12f + column * 178f, 174f + row * 30f, 170f, 26f);
                if (GUI.Button(rect, lobbyUnitImages[i] == null
                        ? new GUIContent($"[{i + 1}] {CharacterTypeLabels[i]}")
                        : GUIContent.none))
                    QueueSelectedTypeSpawn(i);
                if (lobbyUnitImages[i] != null)
                {
                    DrawLobbyImage(new Rect(rect.x + 2f, rect.y + 2f, 22f, 22f),
                        lobbyUnitImages[i], ScaleMode.ScaleToFit);
                    GUI.Label(new Rect(rect.x + 28f, rect.y, rect.width - 30f, rect.height),
                        $"[{i + 1}] {CharacterTypeLabels[i]}");
                }
            }
            GUI.Label(new Rect(600f, 210f, 136f, 18f), "TAB: HUD CHI TIẾT");
        }

        private void DrawBattleVitalsHud()
        {
            var scale = Mathf.Clamp(Screen.width / 1920f, 0.62f, 1f);
            var width = Mathf.Min(Screen.width - 20f, 1810f * scale);
            var height = 128f * scale;
            var top = 25f * scale;
            var centerX = Screen.width * 0.5f;
            var centerHudWidth = 250f * scale;
            var gap = 18f * scale;
            var panelLeft = (Screen.width - width) * 0.5f;
            var leftBar = new Rect(panelLeft + 10f * scale, top + 30f * scale,
                (width - centerHudWidth) * 0.5f - 10f * scale - gap, 66f * scale);
            var rightBar = new Rect(centerX + centerHudWidth * 0.5f + gap, top + 30f * scale,
                (width - centerHudWidth) * 0.5f - 10f * scale - gap, 66f * scale);

            var currentBlueHp = _combatStarted ? _blueCastleHealthRemaining : castleHealth;
            var currentRedHp = _combatStarted ? _redCastleHealthRemaining : castleHealth;
            var redFrameHeight = GetFrameHeight(leftBar, battleRedHealthFrameImage);
            var blueFrameHeight = GetFrameHeight(rightBar, battleBlueHealthFrameImage);
            var sharedFrameHeight = redFrameHeight > 0f && blueFrameHeight > 0f
                ? Mathf.Min(redFrameHeight, blueFrameHeight)
                : Mathf.Max(redFrameHeight, blueFrameHeight);
            var redDisplayedHeight = redFrameHeight > 0f ?
                (blueFrameHeight > 0f ? sharedFrameHeight : redFrameHeight) * 0.85f * 0.7f : 0f;
            var blueDisplayedHeight = blueFrameHeight > 0f ?
                (redFrameHeight > 0f ? sharedFrameHeight : blueFrameHeight) * 0.7f : 0f;
            if (Event.current.type == EventType.Repaint)
            {
                DrawHealthFill(leftBar, currentRedHp, castleHealth,
                    new Color(0.72f, 0.09f, 0.055f, 1f), true, scale);
                DrawHealthFill(rightBar, currentBlueHp, castleHealth,
                    new Color(0.08f, 0.22f, 0.78f, 1f), false, scale, 0.9f);
                if (battleRedHealthFrameImage != null)
                    DrawFrameScaledToWidth(leftBar, battleRedHealthFrameImage, false, redDisplayedHeight);
                if (battleBlueHealthFrameImage != null)
                    DrawFrameScaledToWidth(rightBar, battleBlueHealthFrameImage, true, blueDisplayedHeight);
                var bordersTop = Mathf.Max(leftBar.yMax, rightBar.yMax,
                    GetFrameRect(leftBar, redDisplayedHeight).yMax,
                    GetFrameRect(rightBar, blueDisplayedHeight).yMax) + 8f * scale;
                DrawBattleTopBorders(bordersTop, scale);
            }
            DrawHealthValue(GetFrameRect(leftBar, redDisplayedHeight), currentRedHp, scale, 55f);
            DrawHealthValue(GetFrameRect(rightBar, blueDisplayedHeight), currentBlueHp, scale, 73f);

            // Timer and score pool: placed by battleHudLayout (the battle HUD tool), on the same frame as this HUD.
            var hud = battleHudLayout ??= new RecoilBattleHudLayout();
            var poolStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = RecoilBattleHudLayout.FontSize(hud.poolTitle, BattleHudPieceId.PoolTitle, Screen.width),
                fontStyle = FontStyle.Bold
            };
            SetLabelTextColor(poolStyle, Color.white);
            DrawHighlightedLabel(RecoilBattleHudLayout.GuiRect(hud.poolTitle, BattleHudPieceId.PoolTitle, Screen.width),
                "BỂ ĐIỂM", poolStyle, scale * hud.poolTitle.scale);
            poolStyle.fontSize = RecoilBattleHudLayout.FontSize(hud.poolScore, BattleHudPieceId.PoolScore, Screen.width);
            SetLabelTextColor(poolStyle, new Color(1f, 0.8f, 0.3f, 1f));
            DrawHighlightedLabel(RecoilBattleHudLayout.GuiRect(hud.poolScore, BattleHudPieceId.PoolScore, Screen.width),
                (matchScoring?.TotalFixedPoints ?? 0m).ToString("N0"), poolStyle, scale * hud.poolScore.scale);
            DrawMatchTimer(RecoilBattleHudLayout.GuiRect(hud.timer, BattleHudPieceId.Timer, Screen.width),
                poolStyle, scale * hud.poolScore.scale);
        }

        /// <summary>A hero's body height (set when its VAT is baked / resized); 6 when unknown.</summary>
        private static float HeroBodyHeight(GameObject template)
        {
            var player = template != null ? template.GetComponent<NhiQuoc.Tools.HeroVatPlayer>() : null;
            return player != null && player.targetBodyHeight > .01f ? player.targetBodyHeight : 6f;
        }

        private static Texture2D _healthFillRoundTexture;

        private void DrawBattleTopBorders(float top, float scale)
        {
            if (battleTopBorderImages == null) return;
            var width = 80f * scale;
            var margin = 10f * scale;
            var y = top - 40f;
            _battleTopsTop = y; // the leaderboard opens in this column's place
            for (var i = 0; i < battleTopBorderImages.Length; i++)
            {
                var image = battleTopBorderImages[i];
                if (image == null) continue;
                // The images are square but the top 2 / 3 rings are painted wider than tall: narrowed so they are round.
                var height = width * image.height / Mathf.Max(1f, image.width);
                var borderWidth = width * BattleTopFrameWidthScale(i);
                var leftBorder = new Rect(margin, y, borderWidth, height);
                var rightBorder = new Rect(Screen.width - margin - borderWidth, y, borderWidth, height);
                DrawAnimatedBattleTop(leftBorder, image, false, i, scale);
                DrawAnimatedBattleTop(rightBorder, image, true, i, scale);
                y += height + 1f;
            }
        }

        /// <summary>
        /// The ring of each top frame (top1/2/3Boder.png), measured from the images: an ellipse fitted to the sides and
        /// bottom of the transparent opening (the crown covers its top). x, y (from the top), width, height as
        /// fractions of the image, slightly enlarged so no gap shows; the crown hides what is above the opening.
        /// </summary>
        private static readonly Rect[] BattleTopAvatarOpenings =
        {
            new Rect(.142f, .167f, .716f, .718f),
            new Rect(.124f, .232f, .752f, .650f),
            new Rect(.127f, .224f, .746f, .658f),
        };

        /// <summary>
        /// Drawn width / height of each top frame so its ring is a circle: the ring's height over its width
        /// (top 1 is round already; the top 2 / 3 art is about 14% / 12% too wide).
        /// </summary>
        private static float BattleTopFrameWidthScale(int rank) => rank == 1 ? .860f : rank == 2 ? .878f : 1f;

        /// <summary>Where the avatar goes inside the top <paramref name="rank"/> frame drawn in <paramref name="frame"/>.</summary>
        private static Rect BattleTopAvatarRect(Rect frame, int rank)
        {
            var opening = BattleTopAvatarOpenings[Mathf.Clamp(rank, 0, BattleTopAvatarOpenings.Length - 1)];
            return new Rect(frame.x + frame.width * opening.x, frame.y + frame.height * opening.y,
                frame.width * opening.width, frame.height * opening.height);
        }

        private static void DrawBattleTopAvatarBackground(Rect avatar)
        {
            EnsureHealthFillRoundTexture();
            var oldColor = GUI.color;
            GUI.color = new Color(0, 0, 0, oldColor.a);
            // Elliptical placeholder beneath the frame's transparent avatar opening.
            GUI.DrawTexture(avatar, _healthFillRoundTexture, ScaleMode.StretchToFill, true);
            GUI.color = oldColor;
        }
        private static Texture2D _battleTopScoreGradient;

        private static void EnsureBattleTopScoreGradient()
        {
            if (_battleTopScoreGradient != null) return;
            const int width = 256;
            _battleTopScoreGradient = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                name = "Top Score Gradient",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color[width];
            for (var i = 0; i < width; i++)
                pixels[i] = new Color(0.025f, 0.03f, 0.045f, 0.85f * (1f - i / (width - 1f)));
            _battleTopScoreGradient.SetPixels(pixels);
            _battleTopScoreGradient.Apply(false, true);
        }
        private void DrawBattleTopScore(Rect border, bool rightSide, int rank, float scale)
        {
            var barWidth = Mathf.Min(310f * scale, Screen.width * 0.5f - border.width - 20f * scale);
            var overlap = border.width * 0.5f;
            var bar = new Rect(rightSide ? border.x + overlap - barWidth : border.xMax - overlap,
                border.center.y + border.height * 0.075f - border.height * 0.855f * 0.5f - 1f,
                barWidth, border.height * 0.855f);
            var oldColor = GUI.color;
            EnsureBattleTopScoreGradient();
            GUI.color = oldColor;
            GUI.DrawTextureWithTexCoords(bar, _battleTopScoreGradient,
                rightSide ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f), true);
            GUI.color = oldColor;

            var names = rightSide ? battleBlueTopNames : battleRedTopNames;
            var scores = rightSide ? battleBlueTopScores : battleRedTopScores;
            var playerName = names != null && rank < names.Length && !string.IsNullOrWhiteSpace(names[rank])
                ? names[rank] : "Tên";
            var score = scores != null && rank < scores.Length ? scores[rank] : 0;
            var textInset = overlap + 12f * scale;
            var text = new Rect(bar.x + (rightSide ? 12f * scale : textInset), bar.y,
                Mathf.Max(0f, bar.width - textInset - 12f * scale), bar.height * 0.5f);
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = rightSide ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft,
                wordWrap = false,
                richText = false,
                fontSize = Mathf.RoundToInt(20f * scale),
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 0, 0, 0)
            };
            var nameHeight = style.lineHeight;
            var scoreStyle = new GUIStyle(style) { fontSize = Mathf.RoundToInt(18f * scale) };
            var scoreHeight = scoreStyle.lineHeight;
            text.y = bar.center.y - (nameHeight + 1f + scoreHeight) * 0.5f;
            text.height = nameHeight;
            SetLabelTextColor(style, UserNameColor(playerName, rightSide));
            GUI.Label(text, FitTopName(playerName, style, text.width), style);
            text.y += nameHeight + 1f;
            text.height = scoreHeight;
            style.fontSize = Mathf.RoundToInt(18f * scale);
            SetLabelTextColor(style, new Color(1f, 0.8f, 0.3f, 1f));
            GUI.Label(text, score.ToString("N0"), style);
        }
        private static void DrawHealthFill(Rect rect, int current, int maximum,
            Color teamColor, bool fillFromLeft, float scale, float heightScale = 1f)
        {
            var horizontalInset = Mathf.Max(10f * scale, rect.width * 0.03f);
            var barHeight = 49f * scale * heightScale;
            var bar = new Rect(rect.x + horizontalInset, rect.center.y - barHeight * 0.5f,
                rect.width - horizontalInset * 2f, barHeight);
            var anchorShift = bar.width * 0.14f;
            if (fillFromLeft)
                bar.x += anchorShift;
            bar.width -= anchorShift;
            var oldColor = GUI.color;
            var fillAmount = Mathf.Clamp01((float)Mathf.Max(0, current) / Mathf.Max(1, maximum));
            var fillWidth = bar.width * fillAmount;
            GUI.color = teamColor;
            DrawRoundedHealthFill(new Rect(fillFromLeft ? bar.x : bar.xMax - fillWidth,
                bar.y, fillWidth, bar.height), 24f * scale);
            GUI.color = oldColor;
        }

        private static void DrawRoundedHealthFill(Rect rect, float radius)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;
            var teamColor = GUI.color;
            radius = Mathf.Min(radius, rect.height * 0.5f, rect.width * 0.5f);
            EnsureHealthFillRoundTexture();
            var diameter = radius * 2f;
            GUI.DrawTexture(new Rect(rect.x + radius, rect.y, rect.width - diameter, rect.height),
                Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y + radius, rect.width, rect.height - diameter),
                Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, diameter, diameter), _healthFillRoundTexture);
            GUI.DrawTexture(new Rect(rect.xMax - diameter, rect.y, diameter, diameter), _healthFillRoundTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - diameter, diameter, diameter), _healthFillRoundTexture);
            GUI.DrawTexture(new Rect(rect.xMax - diameter, rect.yMax - diameter, diameter, diameter),
                _healthFillRoundTexture);

            // Paint the gradient across the full rounded silhouette so the
            // base team color cannot show through as an extra band at the ends.
            const int gradientSteps = 96;
            var rowHeight = rect.height / gradientSteps;
            for (var i = 0; i < gradientSteps; i++)
            {
                var y = rect.y + i * rowHeight;
                var midY = y + rowHeight * 0.5f;
                var inset = 0f;
                if (midY < rect.y + radius)
                {
                    var dy = radius - (midY - rect.y);
                    inset = radius - Mathf.Sqrt(Mathf.Max(0f, radius * radius - dy * dy));
                }
                else if (midY > rect.yMax - radius)
                {
                    var dy = radius - (rect.yMax - midY);
                    inset = radius - Mathf.Sqrt(Mathf.Max(0f, radius * radius - dy * dy));
                }

                var t = (i + 0.5f) / gradientSteps;
                if (t < 0.25f)
                    GUI.color = Color.Lerp(Color.black, teamColor, t / 0.25f);
                else if (t < 0.75f)
                    GUI.color = teamColor;
                else
                    GUI.color = Color.Lerp(teamColor, Color.black, (t - 0.75f) / 0.25f);
                GUI.DrawTexture(new Rect(rect.x + inset, y,
                    Mathf.Max(0f, rect.width - inset * 2f), rowHeight + 0.25f), Texture2D.whiteTexture);
            }
            GUI.color = teamColor;
        }

        private static void EnsureHealthFillRoundTexture()
        {
            if (_healthFillRoundTexture != null) return;
            const int size = 32;
            _healthFillRoundTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Health Fill Rounded Corner",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color[size * size];
            var center = (size - 1) * 0.5f;
            var edge = center - 0.5f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                var alpha = Mathf.Clamp01(edge + 0.75f - distance);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
            _healthFillRoundTexture.SetPixels(pixels);
            _healthFillRoundTexture.Apply(false, true);
        }

        private static void DrawHealthValue(Rect frameRect, int current, float scale, float verticalOffset)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.RoundToInt(34f * scale),
                fontStyle = FontStyle.Bold
            };
            SetLabelTextColor(style, new Color(1f, 0.68f, 0.12f, 1f));
            var labelHeight = 38f * scale;
            var labelRect = new Rect(frameRect.x, frameRect.yMax - labelHeight - verticalOffset * scale,
                frameRect.width, labelHeight);
            DrawHighlightedLabel(labelRect, Mathf.Max(0, current).ToString("N0"), style, scale);
        }

        private static float GetFrameHeight(Rect target, Texture2D frame) =>
            frame != null && frame.width > 0 ? target.width * frame.height / frame.width : 0f;

        private static Rect GetFrameRect(Rect target, float height)
        {
            if (height <= 0f) return target;
            return new Rect(target.x, target.center.y - height * 0.5f, target.width, height);
        }

        private static void DrawHighlightedLabel(Rect rect, string text, GUIStyle style, float scale)
        {
            var outline = new GUIStyle(style);
            SetLabelTextColor(outline, new Color(0f, 0f, 0f, 1f));
            var offset = Mathf.Max(1.5f, 3f * scale);
            GUI.Label(new Rect(rect.x - offset, rect.y, rect.width, rect.height), text, outline);
            GUI.Label(new Rect(rect.x + offset, rect.y, rect.width, rect.height), text, outline);
            GUI.Label(new Rect(rect.x, rect.y - offset, rect.width, rect.height), text, outline);
            GUI.Label(new Rect(rect.x, rect.y + offset, rect.width, rect.height), text, outline);
            GUI.Label(new Rect(rect.x - offset, rect.y - offset, rect.width, rect.height), text, outline);
            GUI.Label(new Rect(rect.x + offset, rect.y - offset, rect.width, rect.height), text, outline);
            GUI.Label(new Rect(rect.x - offset, rect.y + offset, rect.width, rect.height), text, outline);
            GUI.Label(new Rect(rect.x + offset, rect.y + offset, rect.width, rect.height), text, outline);

            // Static warm glow behind the gold lettering.
            var glow = new GUIStyle(style);
            SetLabelTextColor(glow, new Color(1f, 0.48f, 0.04f, 0.3f));
            var glowOffset = Mathf.Max(1f, 1.5f * scale);
            GUI.Label(new Rect(rect.x - glowOffset, rect.y, rect.width, rect.height), text, glow);
            GUI.Label(new Rect(rect.x + glowOffset, rect.y, rect.width, rect.height), text, glow);
            GUI.Label(new Rect(rect.x, rect.y - glowOffset, rect.width, rect.height), text, glow);
            GUI.Label(new Rect(rect.x, rect.y + glowOffset, rect.width, rect.height), text, glow);

            var goldShadow = new GUIStyle(style);
            SetLabelTextColor(goldShadow, new Color(0.62f, 0.25f, 0.015f, 1f));
            GUI.Label(new Rect(rect.x, rect.y + Mathf.Max(1f, scale), rect.width, rect.height), text, goldShadow);
            DrawGoldWhiteGradientLabel(rect, text, style);
        }

        private static void DrawGoldWhiteGradientLabel(Rect rect, string text, GUIStyle baseStyle)
        {
            const int slices = 64;
            var leftColor = new Color(1f, 0.98f, 0.82f, 1f);
            var rightColor = new Color(1f, 0.62f, 0.08f, 1f);
            var gradientStyle = new GUIStyle(baseStyle);
            for (var i = 0; i < slices; i++)
            {
                var t = (i + 0.5f) / slices;
                SetLabelTextColor(gradientStyle, Color.Lerp(leftColor, rightColor, t));
                var x = rect.width * i / slices;
                var nextX = rect.width * (i + 1) / slices;
                GUI.BeginGroup(new Rect(rect.x + x, rect.y, Mathf.Max(1f, nextX - x + 0.5f), rect.height));
                GUI.Label(new Rect(-x, 0f, rect.width, rect.height), text, gradientStyle);
                GUI.EndGroup();
            }
        }

        /// <summary>Sets the text colour of every state, so the label looks the same under the mouse (no hover on text).</summary>
        private static void SetLabelTextColor(GUIStyle style, Color color)
        {
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = color;
            style.onNormal.textColor = style.onHover.textColor = style.onActive.textColor = style.onFocused.textColor = color;
        }

        /// <summary>A text-only style: its hover / active / focus states look exactly like its normal state.</summary>
        private static GUIStyle TextOnly(GUIStyle style)
        {
            SetLabelTextColor(style, style.normal.textColor);
            style.hover.background = style.active.background = style.focused.background = style.normal.background;
            return style;
        }

        /// <summary>
        /// A viewer's name: the same dark outline as the gold labels, but the name's own (team) colour instead of the
        /// gold gradient.
        /// </summary>
        private static void DrawOutlinedNameLabel(Rect rect, string text, GUIStyle style, float scale, Color color)
        {
            var outline = new GUIStyle(style);
            SetLabelTextColor(outline, Color.black);
            var offset = Mathf.Max(1.5f, 3f * scale);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    GUI.Label(new Rect(rect.x + dx * offset, rect.y + dy * offset, rect.width, rect.height), text, outline);
            var fill = new GUIStyle(style);
            SetLabelTextColor(fill, color);
            GUI.Label(rect, text, fill);
        }

        private static void DrawFrameScaledToWidth(Rect target, Texture2D frame,
            bool flipHorizontal, float sharedHeight)
        {
            if (frame == null || frame.width <= 0 || frame.height <= 0) return;
            var drawHeight = sharedHeight > 0f ? sharedHeight : GetFrameHeight(target, frame);
            var drawRect = GetFrameRect(target, drawHeight);
            if (flipHorizontal)
                GUIUtility.ScaleAroundPivot(new Vector2(-1f, 1f), drawRect.center);
            GUI.DrawTexture(drawRect, frame, ScaleMode.StretchToFill, true);
            if (flipHorizontal)
                GUIUtility.ScaleAroundPivot(new Vector2(-1f, 1f), drawRect.center);
        }

        private void DrawMatchResult()
        {
            DrawEndgamePresentation();
        }

        private void DrawTemplateValidationScreen()
        {
            var screen = new Rect(0f, 0f, Screen.width, Screen.height);
            GUI.color = new Color(0.035f, 0.045f, 0.065f, 0.98f);
            GUI.DrawTexture(screen, Texture2D.whiteTexture);
            GUI.color = Color.white;

            var centerX = Screen.width * 0.5f;
            var centerY = Screen.height * 0.5f;
            GUI.Label(new Rect(centerX - 240f, centerY - 76f, 480f, 36f),
                _templateValidationFailed ? "KHÔNG THỂ NẠP QUÂN" : "ĐANG CHUẨN BỊ TRẬN ĐẤU",
                GUI.skin.label);
            GUI.Label(new Rect(centerX - 240f, centerY - 32f, 480f, 28f),
                _templateValidationFailed
                    ? _templateValidationError
                    : "Đang tải…",
                GUI.skin.label);

            var bar = new Rect(centerX - 240f, centerY + 8f, 480f, 22f);
            GUI.color = new Color(0.18f, 0.2f, 0.24f, 1f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            if (!_templateValidationFailed && _templateValidationTotal > 0)
            {
                var progress = Mathf.Clamp01((float)_templatesValidatedCount / _templateValidationTotal);
                GUI.color = new Color(0.2f, 0.62f, 0.95f, 1f);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * progress, bar.height),
                    Texture2D.whiteTexture);
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = Color.white;
                if (_templateValidationFailed)
                    GUI.Label(new Rect(centerX - 240f, centerY + 38f, 480f, 24f), "Hãy sửa cấu hình mẫu lính rồi tải lại scene.");
            }
            GUI.color = Color.white;
        }

    }
}
