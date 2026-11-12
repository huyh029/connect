using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// The army setting off when the countdown ends, without one heavy frame:
    /// <list type="bullet">
    /// <item>During the countdown each soldier's way to the enemy is worked out a little every frame (front rows
    /// first), within <see cref="combatLaunchBudgetMs"/>.</item>
    /// <item>At 0 the army leaves in a wave, front rows first, the wave moving back through the army at
    /// <see cref="combatLaunchWaveSpeed"/>. A soldier not worked out yet (the countdown was too short) is worked out
    /// when the wave reaches it, still within the frame budget, so a big army simply leaves over a few more frames.</item>
    /// </list>
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        [SerializeField, Min(1f), Tooltip("Khi hết đếm ngược, quân xuất phát thành sóng từ hàng đầu về sau. Sóng lan về " +
                                          "phía sau đội hình với tốc độ này (m/giây); lớn = gần như cùng lúc.")]
        private float combatLaunchWaveSpeed = 25f;
        [SerializeField, Range(.5f, 8f), Tooltip("Thời gian tối đa mỗi khung hình (ms) để tính đường đi của lính, trong lúc " +
                                                 "đếm ngược và lúc quân xuất phát.")]
        private float combatLaunchBudgetMs = 2f;

        private sealed class CombatLaunch
        {
            public Soldier Soldier;
            public float Depth;          // behind the middle line; front rows have the smallest
            public bool Ready, HasDestination;
            public float3 Destination;
        }

        private readonly List<CombatLaunch> _combatLaunches = new();
        private readonly HashSet<Soldier> _combatLaunchPlanned = new();
        private int _combatLaunchReleased;   // entries before this index have set off (the list is front first)
        private int _combatLaunchPrepared;   // search start for entries still to work out
        private float _combatLaunchStartedAt = -1f, _combatLaunchFront;
        private double _combatLaunchNextPlan;
        private readonly Stopwatch _combatLaunchWatch = new();

        private void ResetCombatLaunch()
        {
            _combatLaunches.Clear();
            _combatLaunchPlanned.Clear();
            _combatLaunchReleased = _combatLaunchPrepared = 0;
            _combatLaunchStartedAt = -1f;
            _combatLaunchNextPlan = 0d;
        }

        private static readonly IComparer<CombatLaunch> CombatLaunchFrontFirst =
            Comparer<CombatLaunch>.Create((a, b) => a.Depth.CompareTo(b.Depth));

        /// <summary>Adds the soldiers not planned yet (new arrivals) and keeps those still waiting front first.</summary>
        private void PlanCombatLaunch()
        {
            var middle = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
            var axis = BattleAxis();
            var added = false;
            for (var side = 0; side < 2; side++)
            {
                var forward = side == 0 ? axis : -axis;
                foreach (var soldier in side == 0 ? _blueTeam : _redTeam)
                {
                    if (!CombatLaunchValid(soldier) || !_combatLaunchPlanned.Add(soldier)) continue;
                    _combatLaunches.Add(new CombatLaunch
                    {
                        Soldier = soldier,
                        Depth = -Vector3.Dot(soldier.Simulation.Position - middle, forward)
                    });
                    added = true;
                }
            }
            if (!added) return;
            _combatLaunches.Sort(_combatLaunchReleased, _combatLaunches.Count - _combatLaunchReleased, CombatLaunchFrontFirst);
            _combatLaunchPrepared = _combatLaunchReleased;
        }

        private bool CombatLaunchValid(Soldier soldier) =>
            soldier != null && !soldier.Retired && soldier.Simulation != null && soldier.Simulation.Exists && IsAlive(soldier);

        /// <summary>Works out where one soldier heads when it sets off (its fight corridor's far end).</summary>
        private void PrepareCombatLaunch(CombatLaunch entry)
        {
            entry.Ready = true;
            var soldier = entry.Soldier;
            if (!CombatLaunchValid(soldier) || !useFightCorridors || pathService == null || !pathService.UsingTerrainMap) return;
            var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
            if (timing.Fight.Enabled == 0) return;
            var settings = soldier.Simulation.Settings;
            entry.HasDestination = pathService.TerrainHeightField.TryFindUnitApproach(soldier.Simulation.Position,
                timing.Fight.End, settings.AreaMask, out entry.Destination, settings.BodyRadius + .05f);
        }

        /// <summary>The soldier leaves its place in the formation and heads for the enemy.</summary>
        private void ReleaseCombatLaunch(CombatLaunch entry)
        {
            var soldier = entry.Soldier;
            if (!CombatLaunchValid(soldier)) return;
            soldier.FormationAssigned = soldier.FormationArrived = false;
            soldier.Simulation.SetAssemblyMovement(false);
            soldier.Simulation.SetPresentationFacing(false, Quaternion.identity);
            soldier.LastMovementCommandTime = SimulationNow;
            if (useFightCorridors && pathService != null && pathService.UsingTerrainMap)
            {
                var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
                if (timing.Fight.Enabled != 0)
                {
                    if (entry.HasDestination) soldier.Simulation.SetDestination(entry.Destination);
                    return;
                }
            }
            UpdateNavigationBridge(soldier, null);
        }

        private bool CombatLaunchBudgetLeft => _combatLaunchWatch.Elapsed.TotalMilliseconds < combatLaunchBudgetMs;

        /// <summary>Works out the next soldiers (front first) while the frame budget lasts.</summary>
        private void PrepareCombatLaunchesAhead()
        {
            for (var i = Mathf.Max(_combatLaunchPrepared, _combatLaunchReleased); i < _combatLaunches.Count; i++)
            {
                if (!CombatLaunchBudgetLeft) { _combatLaunchPrepared = i; return; }
                if (!_combatLaunches[i].Ready) PrepareCombatLaunch(_combatLaunches[i]);
            }
            _combatLaunchPrepared = _combatLaunches.Count;
        }

        /// <summary>Countdown started: plan the army and start working out its ways a little every frame.</summary>
        private void BeginCombatLaunchPlanning()
        {
            ResetCombatLaunch();
            PlanCombatLaunch();
        }

        /// <summary>The countdown is over: the wave starts from the front rows.</summary>
        private void StartCombatLaunch()
        {
            PlanCombatLaunch(); // soldiers that arrived during the countdown (or no countdown at all)
            _combatLaunchStartedAt = SimulationNow;
            _combatLaunchFront = _combatLaunches.Count > _combatLaunchReleased
                ? _combatLaunches[_combatLaunchReleased].Depth : 0f;
        }

        private void UpdateCombatLaunch()
        {
            if (_combatLaunches.Count == 0) return;
            if (_matchFinished) { ResetCombatLaunch(); return; }
            _combatLaunchWatch.Restart();
            if (!_combatStarted)
            {
                if (_startCountdownEndsAt <= 0) return;
                // New soldiers can still arrive during the countdown.
                if (Time.realtimeSinceStartupAsDouble >= _combatLaunchNextPlan)
                {
                    _combatLaunchNextPlan = Time.realtimeSinceStartupAsDouble + .5d;
                    PlanCombatLaunch();
                }
                PrepareCombatLaunchesAhead();
                return;
            }
            if (_combatLaunchStartedAt < 0f) return;

            var reach = _combatLaunchFront + (SimulationNow - _combatLaunchStartedAt) * combatLaunchWaveSpeed;
            var released = 0;
            while (_combatLaunchReleased < _combatLaunches.Count)
            {
                var entry = _combatLaunches[_combatLaunchReleased];
                if (entry.Depth > reach) break;
                // Over budget: the rest of the wave leaves on the next frames.
                if (released > 0 && !CombatLaunchBudgetLeft) break;
                if (!entry.Ready) PrepareCombatLaunch(entry);
                ReleaseCombatLaunch(entry);
                _combatLaunchReleased++;
                released++;
            }
            if (_combatLaunchReleased >= _combatLaunches.Count) { ResetCombatLaunch(); return; }
            PrepareCombatLaunchesAhead(); // what is left of the budget: those the wave reaches next
        }
    }
}
