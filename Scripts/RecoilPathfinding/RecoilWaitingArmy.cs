using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private bool _waitingFormationActive, _waitingFormationDirty;
        private int _initialBlueSoldiersSpawned, _initialRedSoldiersSpawned;
        private bool _waitingHeroesDirty;
        private float _nextHeroFormationAttempt;
        private readonly int[,] _waitingDeferred = new int[2, CharacterTypeCount];
        private float _nextWaitingPlanAttempt;
        private bool InitialWaitingArmySpawned => _initialBlueSoldiersSpawned >= PreparedTeamTarget(true) &&
            _initialRedSoldiersSpawned >= PreparedTeamTarget(false);

        private bool PrepareWaitingReinforcement(int type, int blueAmount, int redAmount)
        {
            if (!InitialWaitingArmySpawned || Time.unscaledTime < _nextWaitingPlanAttempt) return false;
            if (!ReplanWaitingArmy(type, blueAmount, redAmount))
            { _nextWaitingPlanAttempt = Time.unscaledTime + 1f; return false; }
            _waitingFormationActive = true;
            _formationReady = false;

            return true;
        }

        private void DeferWaitingArmyAddition(int type, int blue, int red)
        {
            _waitingDeferred[0, type] += blue;
            _waitingDeferred[1, type] += red;
            if (!_waitingFormationActive) _waitingFormationDirty = true;
            _waitingFormationActive = true;
        }

        private void ReleaseDeferredWaitingArmy()
        {
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                var blue = _waitingDeferred[0, type];
                var red = _waitingDeferred[1, type];
                if (blue > 0) EnqueueSpawnType(_blueSelectedSpawnQueue, type, blue);
                if (red > 0) EnqueueSpawnType(_redSelectedSpawnQueue, type, red);
                _blueSelectedSpawnTotal += blue;
                _redSelectedSpawnTotal += red;
                _waitingDeferred[0, type] = _waitingDeferred[1, type] = 0;
            }
        }
        private float _nextStagingQueueCheck;

        private int RemainingStagingCapacity(bool blue, int type)
        {
            var used = 0;
            foreach (var soldier in blue ? _blueTeam : _redTeam)
                if (soldier != null && !soldier.Retired && soldier.HeroId == 0 && soldier.CharacterType == type && IsAlive(soldier)) used++;
            foreach (var batch in blue ? _blueSelectedSpawnQueue : _redSelectedSpawnQueue)
                if (batch.CharacterType == type) used += batch.Remaining;
            return Mathf.Max(0, MaximumStagedPerTypePerTeam - used);
        }
        private void FlushDeferredWaitingArmy()
        {
            if (Time.unscaledTime < _nextStagingQueueCheck) return;
            _nextStagingQueueCheck = Time.unscaledTime + .25f;
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                var blue = Mathf.Min(_waitingDeferred[0, type], RemainingStagingCapacity(true, type));
                var red = Mathf.Min(_waitingDeferred[1, type], RemainingStagingCapacity(false, type));
                if (blue + red <= 0) continue;
                if (Time.unscaledTime < _nextWaitingPlanAttempt) return;
                if (!PrepareWaitingReinforcement(type, blue, red))
                {
                    // Keep the batch at spawn until a formation fits or L starts combat.
                    continue;
                }
                if (blue > 0) EnqueueSpawnType(_blueSelectedSpawnQueue, type, blue);
                if (red > 0) EnqueueSpawnType(_redSelectedSpawnQueue, type, red);
                _blueSelectedSpawnTotal += blue;
                _redSelectedSpawnTotal += red;
                _waitingDeferred[0, type] -= blue;
                _waitingDeferred[1, type] -= red;
            }
        }
        private readonly List<int> _waitingPositionTypes = new();
        private readonly List<Vector3>[][] _waitingSlots = { new List<Vector3>[CharacterTypeCount], new List<Vector3>[CharacterTypeCount] };
        private readonly int[][] _waitingAssigned = { new int[CharacterTypeCount], new int[CharacterTypeCount] };

        private List<RecoilInitialArmyPlanner.Block> BuildWaitingArmyBlocks(
            List<RecoilInitialArmyPlanner.Block> source, int[] counts)
        {
            var radii = new float[CharacterTypeCount];
            var fallback = new List<RecoilInitialArmyPlanner.Block>();
            foreach (var block in CreateDefaultInitialArmyLayout().Blocks)
                fallback.Add(new RecoilInitialArmyPlanner.Block
                    { Type = block.Type, X = block.X, Y = block.Y, Count = block.Count });
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                radii[type] = .5f;
                for (var team = 0; team < 2; team++)
                {
                    var template = FindTemplateForType(team == 0, type);
                    if (template != null && template.TryGetComponent<RecoilNavMeshPathAgent>(out var agent))
                        radii[type] = Mathf.Max(radii[type], agent.MovementSettings.BodyRadius);
                }
            }
            return RecoilInitialArmyPlanner.Resize(source, counts, fallback, radii);
        }
        private bool ReplanWaitingArmy(int addedType = -1, int blueAdded = 0, int redAdded = 0)
        {
            var blue = new int[CharacterTypeCount];
            var red = new int[CharacterTypeCount];
            CountWaitingArmy(_blueTeam, _blueSelectedSpawnQueue, blue);
            CountWaitingArmy(_redTeam, _redSelectedSpawnQueue, red);
            if (addedType >= 0) { blue[addedType] += blueAdded; red[addedType] += redAdded; }
            var candidateSlots = new List<Vector3>[2][];
            for (var team = 0; team < 2; team++)
            {
                var counts = team == 0 ? blue : red;
                var total = 0;
                foreach (var count in counts) total += count;
                if (total > 10000) { Debug.LogWarning("Tối đa 10000 quân mỗi bên trước giao chiến.", this); return false; }
                if (!TryBuildCustomInitialArmy(out var error, counts))
                { _assemblyStatus = error; Debug.LogWarning(error, this); return false; }
                candidateSlots[team] = new List<Vector3>[CharacterTypeCount];
                for (var type = 0; type < CharacterTypeCount; type++)
                    candidateSlots[team][type] = new List<Vector3>();
                var positions = team == 0 ? _preparedBluePositions : _preparedRedPositions;
                for (var i = 0; i < positions.Count; i++)
                    candidateSlots[team][_waitingPositionTypes[i]].Add(positions[i]);
            }
            for (var team = 0; team < 2; team++)
            {
                for (var type = 0; type < CharacterTypeCount; type++)
                {
                    _waitingSlots[team][type] = candidateSlots[team][type];
                    _waitingAssigned[team][type] = 0;
                }
                foreach (var soldier in team == 0 ? _blueTeam : _redTeam)
                    AssignWaitingArmySlot(soldier);
            }
            _waitingHeroesDirty = true;
            _waitingFormationDirty = false;
            return true;
        }

        private void CountWaitingArmy(List<Soldier> team, Queue<SelectedSpawnBatch> queue, int[] counts)
        {
            foreach (var soldier in team)
                if (soldier != null && !soldier.Retired && soldier.HeroId == 0 && IsAlive(soldier))
                    counts[soldier.CharacterType]++;
            foreach (var batch in queue) counts[batch.CharacterType] += batch.Remaining;
        }

        private void AssignWaitingArmySlot(Soldier soldier)
        {
            if (soldier == null || soldier.Retired || soldier.HeroId != 0 || !IsAlive(soldier)) return;
            var team = soldier.IsBlue ? 0 : 1;
            var type = soldier.CharacterType;
            var slots = _waitingSlots[team][type];
            var index = _waitingAssigned[team][type];
            if (slots == null || index >= slots.Count) return;
            _waitingAssigned[team][type]++;
            soldier.FormationDestination = slots[index];
            soldier.FormationAssigned = true;
            soldier.FormationArrived = false;
            soldier.Simulation.SetAssemblyMovement(true);
            soldier.Simulation.SetDestination(slots[index]);
            if (useFightCorridors)
            {
                var lateral = Vector3.Cross(Vector3.up, BattleAxis());
                var lane = Vector3.Dot(slots[index] - GetSpawnTargetPosition(soldier.IsBlue), lateral);
                var end = GetSpawnTargetPosition(!soldier.IsBlue) + lateral * lane;
                end.x = Mathf.Clamp(end.x, SpawnMinX, SpawnMaxX);
                end.z = Mathf.Clamp(end.z, SpawnMinZ, SpawnMaxZ);
                if (TryGetGroundHeight(end, out var height)) end.y = height;
                var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
                timing.Fight = new RecoilFightCorridor
                    { Start = slots[index], End = end, Leash = fightLeash, Enabled = 1 };
                _entityManager.SetComponentData(soldier.Entity, timing);
            }
        }

        private bool UpdateWaitingArmyAssembly()
        {
            if (!_waitingFormationActive) return false;
            if (!InitialWaitingArmySpawned)
            { _formationReady = false; return true; }
            FlushDeferredWaitingArmy();
            if (_waitingFormationDirty && !ReplanWaitingArmy()) { _formationReady = false; return true; }
            if (_waitingHeroesDirty)
            {
                if (Time.unscaledTime < _nextHeroFormationAttempt || !AssignWaitingHeroRows())
                { _formationReady = false; return true; }
            }
            var remaining = 0;
            foreach (var team in new[] { _blueTeam, _redTeam })
            foreach (var soldier in team)
            {
                if (soldier == null || soldier.Retired || !IsAlive(soldier)) continue;
                if (!soldier.FormationAssigned) { remaining++; continue; }
                var delta = soldier.Simulation.Position - soldier.FormationDestination;
                delta.y = 0;
                soldier.FormationArrived = delta.sqrMagnitude <= .75f * .75f;
                if (!soldier.FormationArrived) remaining++;
            }
            _formationReady = !HasQueuedSpawns && remaining == 0 && IsPreparedArmyRendered();
            _assemblyStatus = _formationReady ? "Đã vào hàng — bấm L để giao chiến"
                : "Đang xuất quân và vào hàng: " + remaining + " quân đang di chuyển";
            return true;
        }
    }
}
