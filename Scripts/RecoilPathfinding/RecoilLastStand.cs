using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Last Stand ("phản công"): when a side's castle first drops to 30 % or less after combat has started, every
    /// living unit of that side fights at twice its strength for 20 s — HP, damage, attack speed, move speed and
    /// defence (damage taken halved); range is left alone. Once per side per match. Units that arrive during those
    /// 20 s get it too. When it ends each unit keeps its health percentage (100/200 becomes 50/100).
    /// The HUD shows which side it is, the seconds left, the castle bar burning and a warning glow on that side's
    /// screen edge (<see cref="DrawLastStandHud"/>).
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        [Header("Phản công (Last Stand)")]
        [SerializeField, Range(.05f, .9f), Tooltip("Máu thành (phần của máu tối đa) từ đó phe giữ thành phản công.")]
        private float lastStandCastleFraction = .3f;
        [SerializeField, Min(1f), Tooltip("Số giây phản công.")] private float lastStandSeconds = 20f;
        [SerializeField, Min(1f), Tooltip("Hệ số nhân HP / sát thương / tốc độ đánh / tốc độ chạy / phòng thủ.")]
        private float lastStandMultiplier = 2f;

        private struct LastStandTeamState
        {
            public bool Used, Active;
            public float StartedAt, EndsAt; // simulation seconds
            public float ShownAt;           // Time.unscaledTime, for the HUD's flash
        }

        private LastStandTeamState _blueLastStand, _redLastStand;

        private bool LastStandActive(bool blue) => (blue ? _blueLastStand : _redLastStand).Active;

        private float LastStandSecondsLeft(bool blue)
        {
            var state = blue ? _blueLastStand : _redLastStand;
            return state.Active ? Mathf.Max(0f, state.EndsAt - SimulationNow) : 0f;
        }

        /// <summary>A new match: neither side has had its Last Stand.</summary>
        private void ResetLastStand()
        {
            _blueLastStand = default;
            _redLastStand = default;
        }

        /// <summary>Each simulation tick, after the castles took this tick's damage.</summary>
        private void UpdateLastStand()
        {
            if (!_combatStarted || _matchFinished) return;
            UpdateLastStand(true, ref _blueLastStand, _blueCastleHealthRemaining, _blueTeam);
            UpdateLastStand(false, ref _redLastStand, _redCastleHealthRemaining, _redTeam);
        }

        private void UpdateLastStand(bool blue, ref LastStandTeamState state, int castleHp, List<Soldier> team)
        {
            var now = SimulationNow;
            if (state.Active)
            {
                if (now < state.EndsAt) return;
                state.Active = false;
                foreach (var soldier in team) RemoveLastStandBuff(soldier);
                return;
            }
            // Once per match, only while the castle still stands (a razed castle ends the match instead).
            if (state.Used || castleHp <= 0 || castleHp > castleHealth * lastStandCastleFraction) return;
            state.Used = state.Active = true;
            state.StartedAt = now;
            state.EndsAt = now + lastStandSeconds;
            state.ShownAt = Time.unscaledTime;
            foreach (var soldier in team) ApplyLastStandBuff(soldier);
            PlayCountdownWarDrum();
        }

        /// <summary>Doubles a living unit (nothing if it already is, is dead, or has no stats to double).</summary>
        private void ApplyLastStandBuff(Soldier soldier)
        {
            if (soldier == null || soldier.Retired || soldier.LastStandBuffed || soldier.Entity == Entity.Null ||
                !_entityManager.Exists(soldier.Entity) || !_entityManager.HasComponent<RecoilBattleEcsState>(soldier.Entity))
                return;
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            if (state.Active == 0 || state.Health <= 0 || !state.Stats.IsConfigured) return;
            var multiplier = lastStandMultiplier;
            soldier.LastStandBaseStats = state.Stats;
            state.Stats = RecoilLastStandRules.Buffed(state.Stats, multiplier);
            state.Health = RecoilLastStandRules.ScaledHealth(state.Health, multiplier);
            state.AttackCooldown /= multiplier; // the faster rate is felt at once
            _entityManager.SetComponentData(soldier.Entity, state);
            soldier.LastStandBaseSpeed = 0f;
            if (soldier.Simulation != null && soldier.Simulation.TryGetSettings(out var settings) && settings.MaxSpeed > 0f)
            {
                soldier.LastStandBaseSpeed = settings.MaxSpeed;
                settings.MaxSpeed *= multiplier;
                _entityManager.SetComponentData(soldier.Entity, settings);
            }
            soldier.LastStandBuffed = true;
        }

        /// <summary>Back to its own stats, keeping its health percentage.</summary>
        private void RemoveLastStandBuff(Soldier soldier)
        {
            if (soldier == null || !soldier.LastStandBuffed) return;
            soldier.LastStandBuffed = false;
            if (soldier.Entity == Entity.Null || !_entityManager.Exists(soldier.Entity) ||
                !_entityManager.HasComponent<RecoilBattleEcsState>(soldier.Entity))
                return;
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            var buffedMax = state.Stats.MaxHealth;
            state.Stats = soldier.LastStandBaseStats;
            if (state.Health > 0)
                state.Health = RecoilLastStandRules.RestoredHealth(state.Health, buffedMax, state.Stats.MaxHealth);
            state.AttackCooldown *= lastStandMultiplier;
            _entityManager.SetComponentData(soldier.Entity, state);
            if (soldier.LastStandBaseSpeed > 0f && soldier.Simulation != null && soldier.Simulation.TryGetSettings(out var settings))
            {
                settings.MaxSpeed = soldier.LastStandBaseSpeed;
                _entityManager.SetComponentData(soldier.Entity, settings);
            }
        }

        // ---------------------------------------------------------------- HUD

        private static readonly Color LastStandFire = new(1f, .45f, .05f, 1f);
        private static readonly Color LastStandFlash = new(1f, .85f, .35f, 1f);

        /// <summary>0..1 flicker shared by the Last Stand visuals (two sines, so it does not look mechanical).</summary>
        private static float LastStandPulse(float speed = 1f)
        {
            var t = Time.unscaledTime * speed;
            return Mathf.Clamp01(.5f + .3f * Mathf.Sin(t * 9.5f) + .2f * Mathf.Sin(t * 23f + 1.3f));
        }

        /// <summary>
        /// Below the castle bars: "ĐỘI ĐỎ PHẢN CÔNG!" with the seconds left, and the warning glow on that side's
        /// screen edge. Both sides may be in it at once; the banners then stack.
        /// </summary>
        private void DrawLastStandHud(float scale, float top)
        {
            if (Event.current.type != EventType.Repaint) return;
            var y = top;
            for (var side = 0; side < 2; side++)
            {
                var blue = side == 1;
                var state = blue ? _blueLastStand : _redLastStand;
                if (!state.Active) continue;
                var team = blue ? new Color(.25f, .5f, 1f, 1f) : new Color(1f, .22f, .15f, 1f);
                var pulse = LastStandPulse();
                var sinceShown = Time.unscaledTime - state.ShownAt;

                // The side's screen edge glows (red bar is on the left, blue on the right), hard for the first second.
                var edgeAlpha = (.18f + .14f * pulse) * Mathf.Clamp01(sinceShown * 3f) + .45f * Mathf.Clamp01(1f - sinceShown);
                var edgeWidth = Mathf.Min(Screen.width * .22f, 420f * scale);
                var old = GUI.color;
                GUI.color = new Color(team.r, team.g, team.b, edgeAlpha);
                if (blue)
                    GUI.DrawTextureWithTexCoords(new Rect(Screen.width - edgeWidth, 0f, edgeWidth, Screen.height),
                        LeaderboardFadeRight, new Rect(1f, 0f, -1f, 1f), true);
                else
                    GUI.DrawTexture(new Rect(0f, 0f, edgeWidth, Screen.height), LeaderboardFadeRight, ScaleMode.StretchToFill, true);
                GUI.color = old;

                // Banner: name of the side, seconds left, what the buff is.
                EnsureLeaderboardStyles();
                var width = Mathf.Min(Screen.width - 40f, 760f * scale);
                var grow = 1f + .06f * pulse + .35f * Mathf.Clamp01(1f - sinceShown * 2f);
                var title = new Rect(Screen.width * .5f - width * .5f, y, width, 52f * scale);
                _leaderboardRankStyle.fontSize = Mathf.RoundToInt(44f * scale * grow);
                ShadowLabel(title, (blue ? "ĐỘI XANH" : "ĐỘI ĐỎ") + " PHẢN CÔNG!", _leaderboardRankStyle,
                    Color.Lerp(team, LastStandFlash, .35f * pulse), scale);
                var seconds = Mathf.CeilToInt(LastStandSecondsLeft(blue));
                var timer = new Rect(title.x, title.yMax + 2f * scale, width, 58f * scale);
                _leaderboardRankStyle.fontSize = Mathf.RoundToInt((seconds <= 5 ? 56f : 48f) * scale * (seconds <= 5 ? 1f + .12f * pulse : 1f));
                ShadowLabel(timer, seconds.ToString("0") + " s", _leaderboardRankStyle, Color.Lerp(LastStandFire, LastStandFlash, pulse), scale);
                var note = new Rect(title.x, timer.yMax, width, 30f * scale);
                _leaderboardRankStyle.fontSize = Mathf.RoundToInt(22f * scale);
                ShadowLabel(note, $"×{lastStandMultiplier:0.#} MÁU · SÁT THƯƠNG · TỐC ĐỘ · PHÒNG THỦ", _leaderboardRankStyle,
                    new Color(1f, 1f, 1f, .9f), scale);
                y = note.yMax + 10f * scale;
            }
        }

        /// <summary>A burning castle bar: a flickering fire glow around and over the filled part.</summary>
        private static void DrawHealthFillBurning(Rect fill, float scale)
        {
            if (fill.width <= 0f || fill.height <= 0f) return;
            var pulse = LastStandPulse();
            var old = GUI.color;
            var spread = (6f + 6f * pulse) * scale;
            GUI.color = new Color(LastStandFire.r, LastStandFire.g, LastStandFire.b, .28f + .22f * pulse);
            DrawRoundedHealthFill(new Rect(fill.x - spread, fill.y - spread, fill.width + spread * 2f, fill.height + spread * 2f), 30f * scale);
            GUI.color = new Color(LastStandFlash.r, LastStandFlash.g, LastStandFlash.b, .25f + .35f * pulse);
            DrawRoundedHealthFill(fill, 24f * scale);
            GUI.color = old;
        }
    }

    /// <summary>The Last Stand arithmetic, on its own so it can be checked without a battle.</summary>
    public static class RecoilLastStandRules
    {
        public static RecoilUnitCombatStats Buffed(RecoilUnitCombatStats stats, float multiplier)
        {
            stats.MaxHealth = ScaledHealth(stats.MaxHealth, multiplier);
            stats.Damage = (int)Mathf.Min(int.MaxValue / 2f, Mathf.Round(stats.Damage * multiplier));
            stats.AttackSpeed *= multiplier;
            stats.MoveSpeed *= multiplier;
            // Defence × m: the damage that gets through is divided by m. Range is not touched.
            stats.Armor = 1f - (1f - Mathf.Clamp01(stats.Armor)) / multiplier;
            stats.SecondaryArmor = 1f - (1f - Mathf.Clamp01(stats.SecondaryArmor)) / multiplier;
            return stats;
        }

        public static int ScaledHealth(int health, float multiplier) =>
            (int)Mathf.Min(int.MaxValue / 2f, Mathf.Round(health * multiplier));

        /// <summary>The same percentage of the unit's own maximum (100/200 → 50/100), never below 1.</summary>
        public static int RestoredHealth(int health, int buffedMax, int baseMax) =>
            Mathf.Max(1, Mathf.RoundToInt(baseMax * Mathf.Clamp01(health / (float)Mathf.Max(1, buffedMax))));
    }
}
