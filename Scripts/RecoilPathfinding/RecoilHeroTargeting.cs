using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private readonly List<Soldier> _heroDuelists = new();

        private void UpdateHeroDuelTargets()
        {
            _heroDuelists.RemoveAll(hero => hero == null || hero.Retired ||
                hero.Simulation == null || !hero.Simulation.Exists || !IsAlive(hero));
            foreach (var hero in _heroDuelists)
            {
                Soldier selected = null;
                var best = float.PositiveInfinity;
                foreach (var enemy in _heroDuelists)
                {
                    if (enemy.IsBlue == hero.IsBlue) continue;
                    var offset = enemy.Simulation.Position - hero.Simulation.Position;
                    offset.y = 0;
                    if (offset.sqrMagnitude >= best) continue;
                    best = offset.sqrMagnitude;
                    selected = enemy;
                }
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(hero.Entity);
                state.Stats.Range = HeroEngagementRange(hero, state.Stats.Range);
                var preferred = selected != null ? selected.Entity : Entity.Null;
                if (preferred != Entity.Null)
                {
                    state.Target = preferred;
                    state.AttackingCastle = 0;
                }
                else if (state.PreferredHeroTarget != Entity.Null)
                    state.Target = Entity.Null;
                state.PreferredHeroTarget = preferred;
                _entityManager.SetComponentData(hero.Entity, state);
            }
        }
    }
}
