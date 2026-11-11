using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private bool _lastSpawnWasPrepared;

        private bool AssignWaitingHeroRows()
        {
            var middle = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
            var axis = BattleAxis();
            var lateral = Vector3.Cross(Vector3.up, axis);
            var assignments = new List<(Soldier Hero, Vector3 Position)>();
            for (var side = 0; side < 2; side++)
            {
                var team = side == 0 ? _blueTeam : _redTeam;
                var forward = side == 0 ? axis : -axis;
                var heroes = new List<Soldier>();
                var rear = 8f;
                var armyRadius = .5f;
                var heroRadius = .5f;
                foreach (var soldier in team)
                {
                    if (soldier == null || soldier.Retired || !IsAlive(soldier)) continue;
                    if (soldier.HeroId != 0)
                    { heroes.Add(soldier); heroRadius = Mathf.Max(heroRadius, soldier.BodyRadius); }
                    else armyRadius = Mathf.Max(armyRadius, soldier.BodyRadius);
                }
                if (heroes.Count == 0) continue;
                foreach (var slots in _waitingSlots[side])
                    if (slots != null)
                        foreach (var slot in slots)
                            rear = Mathf.Max(rear, -Vector3.Dot(slot - middle, forward) + armyRadius);
                var pitch = heroRadius * 2f + 1f;
                var columns = Mathf.Max(1, Mathf.Min(heroes.Count,
                    Mathf.FloorToInt(preparedFormationHalfWidth * 2f / pitch)));
                var found = false;
                var positions = new List<Vector3>();
                for (var shift = 0f; shift <= 128f; shift += Mathf.Max(2f, pitch))
                {
                    positions.Clear();
                    for (var i = 0; i < heroes.Count; i++)
                    {
                        var row = i / columns;
                        var rowCount = Mathf.Min(columns, heroes.Count - row * columns);
                        var cross = (i % columns - (rowCount - 1) * .5f) * pitch;
                        var depth = rear + heroRadius + 2f + shift + row * pitch;
                        var point = middle + lateral * cross - forward * depth;
                        if (!TryProjectInitialArmyPoint(point, side == 0, heroes[i].BodyRadius, out var projected)) break;
                        positions.Add(projected);
                    }
                    if (positions.Count != heroes.Count) continue;
                    found = true;
                    break;
                }
                if (!found)
                {
                    _nextHeroFormationAttempt = Time.unscaledTime + 1f;
                    _assemblyStatus = "Chưa tìm được vùng đi được cho hàng tướng phía sau đội quân.";
                    return false;
                }
                for (var i = 0; i < heroes.Count; i++) assignments.Add((heroes[i], positions[i]));
            }
            foreach (var assignment in assignments)
            {
                var hero = assignment.Hero;
                hero.FormationDestination = assignment.Position;
                hero.FormationAssigned = true;
                hero.FormationArrived = false;
                hero.Simulation.SetAssemblyMovement(true);
                hero.Simulation.SetDestination(assignment.Position);
                if (useFightCorridors)
                {
                    var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(hero.Entity);
                    timing.Fight.Start = assignment.Position;
                    timing.Fight.End = GetSpawnTargetPosition(!hero.IsBlue);
                    _entityManager.SetComponentData(hero.Entity, timing);
                }
            }
            _waitingHeroesDirty = false;
            return true;
        }
    }
}
