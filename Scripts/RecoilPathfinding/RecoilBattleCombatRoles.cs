using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private static readonly RecoilCombatRole[] DefaultCombatRoles =
        {
            RecoilCombatRole.Archer, RecoilCombatRole.Militia, RecoilCombatRole.Spearman,
            RecoilCombatRole.Cavalry, RecoilCombatRole.Siege, RecoilCombatRole.Mage,
            RecoilCombatRole.Elephant, RecoilCombatRole.Infantry
        };

        private void InitializeCombatRoles()
        {
            for (var type = 0; type < lobbyUnitStats.Length; type++)
            {
                var stats = lobbyUnitStats[type];
                if (type < DefaultCombatRoles.Length)
                    stats.CombatRole = DefaultCombatRoles[type];
                if (stats.AttackType == RecoilAttackType.Unspecified)
                    stats.AttackType = stats.CombatRole == RecoilCombatRole.Archer || stats.CombatRole == RecoilCombatRole.Siege ||
                        stats.CombatRole == RecoilCombatRole.Mage ? RecoilAttackType.Ranged : RecoilAttackType.Melee;
                stats.SecondaryDamage = stats.AttackType == RecoilAttackType.Ranged;
                // This ruleset applies the same raw damage throughout the AoE.
                stats.AreaEdgeEffectiveness = 1f;
                lobbyUnitStats[type] = stats;
            }
        }

        private void DrawUnitAttackTypeControl(Rect rect, ref RecoilUnitCombatStats stats)
        {
            if (GUI.Button(rect,
                stats.AttackType == RecoilAttackType.Ranged ? "Đánh xa" : "Đánh gần"))
                stats.AttackType = stats.AttackType == RecoilAttackType.Ranged ? RecoilAttackType.Melee : RecoilAttackType.Ranged;
            stats.SecondaryDamage = stats.AttackType == RecoilAttackType.Ranged;
        }
    }
}
