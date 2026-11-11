using System.Text;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Temporary hero combat diagnostics: every few seconds one "[HeroDiag]" line per hero in the Console / Editor.log,
    /// to see why a hero does not attack (target, distance, line of fire, skill reach, cooldown, movement).
    /// Turn off with "Log Hero Diagnostics" on RecoilAutoBattleDemo.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        [Header("Chẩn đoán tướng (tạm)")]
        [SerializeField, Tooltip("Ghi một dòng [HeroDiag] cho mỗi tướng mỗi vài giây vào Console để tìm lỗi tướng không đánh.")]
        private bool logHeroDiagnostics = true;
        [SerializeField, Min(.5f)] private float heroDiagnosticsInterval = 2f;

        private float _nextHeroDiagnostics;
        private readonly StringBuilder _heroDiagnostics = new();

        private void LogHeroDiagnostics()
        {
            if (!logHeroDiagnostics || !_combatStarted || _matchFinished || SimulationNow < _nextHeroDiagnostics) return;
            _nextHeroDiagnostics = SimulationNow + Mathf.Max(.5f, heroDiagnosticsInterval);
            for (int i = 0; i < _combatSoldiers.Count; i++)
            {
                var hero = _combatSoldiers[i];
                if (hero == null || hero.HeroId <= 0 || hero.Retired || hero.Simulation == null || !hero.Simulation.Exists) continue;
                var b = _heroDiagnostics.Clear();
                var config = lobbyHeroStats != null ? System.Array.Find(lobbyHeroStats, h => h.Id == hero.HeroId) : null;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(hero.Entity);
                var navigation = _entityManager.GetComponentData<RecoilUnitNavigation>(hero.Entity);
                var pose = hero.Simulation.Pose;
                float configured = config != null ? config.Stats.Range : -1f;
                b.Append("[HeroDiag] t=").Append(SimulationNow.ToString("0.0"))
                    .Append(' ').Append(config != null ? config.Name : "hero " + hero.HeroId)
                    .Append(hero.IsBlue ? " (xanh)" : " (đỏ)")
                    .Append(" vat=").Append(hero.HeroVat != null ? "có" : "KHÔNG")
                    .Append(" ranged=").Append(hero.RangedUnitType)
                    .Append(" range=").Append(state.Stats.Range.ToString("0.0")).Append("/cfg ").Append(configured.ToString("0.0"))
                    .Append(" cd=").Append(state.AttackCooldown.ToString("0.00"))
                    .Append(" active=").Append(state.Active)
                    .Append(" castle=").Append(state.AttackingCastle);

                var targetEntity = state.Target;
                Soldier target = null;
                if (targetEntity != Entity.Null && _combatIndicesByEntity.TryGetValue(targetEntity, out var targetIndex) &&
                    targetIndex >= 0 && targetIndex < _combatSoldiers.Count)
                    target = _combatSoldiers[targetIndex];
                b.Append(" | target=");
                if (target == null) b.Append(targetEntity == Entity.Null ? "không" : "?(ngoài roster)");
                else
                {
                    var targetConfig = target.HeroId > 0 && lobbyHeroStats != null
                        ? System.Array.Find(lobbyHeroStats, h => h.Id == target.HeroId) : null;
                    b.Append(target.HeroId > 0 ? "TƯỚNG " + (targetConfig != null ? targetConfig.Name : target.HeroId.ToString()) : "lính")
                        .Append(HeroSkillAlive(target) ? "" : "(chết)");
                    var from = (float3)hero.Simulation.Position;
                    var to = (float3)target.Simulation.Position;
                    float distance = math.distance(from.xz, to.xz);
                    bool lineOfFire = HasCombatLineOfFire(from, to, hero.RangedUnitType != 0
                        ? RecoilCombatProjectileTrajectory.ArcHeight(from.xz, to.xz) : 0f);
                    b.Append(" dist=").Append(distance.ToString("0.0"))
                        .Append(" LOF=").Append(lineOfFire ? "thông" : "BỊ CHẶN")
                        .Append(" dy=").Append((to.y - from.y).ToString("0.0"));
                    if (hero.HeroVat != null)
                    {
                        HeroSkillCandidates(hero, _heroSkillCandidates);
                        b.Append(" skills=");
                        foreach (var clip in _heroSkillCandidates)
                            b.Append(clip).Append(':').Append(HeroSkillReach(hero, clip, configured > 0 ? configured : state.Stats.Range).ToString("0.0")).Append(' ');
                        b.Append("chọn=").Append(ChooseHeroSkill(hero, distance, configured > 0 ? configured : state.Stats.Range));
                    }
                }
                b.Append(" preferredHero=").Append(state.PreferredHeroTarget != Entity.Null ? "có" : "không");
                b.Append(" | anim=").Append(hero.HeroAnimation)
                    .Append(SimulationNow < hero.HeroAnimationUntil ? " còn " + (hero.HeroAnimationUntil - SimulationNow).ToString("0.0") + "s" : "")
                    .Append(" rooted=").Append(navigation.Rooted)
                    .Append(" dest=").Append(navigation.HasDestination)
                    .Append(" speed=").Append(pose.Speed.ToString("0.0"))
                    .Append(" | fired=").Append(hero.DiagFired)
                    .Append(" casts=").Append(hero.DiagCasts)
                    .Append(" walkIn=").Append(hero.DiagWalkIns);
                hero.DiagFired = hero.DiagCasts = hero.DiagWalkIns = 0;
                Debug.Log(b.ToString(), this);
            }
        }
    }
}
