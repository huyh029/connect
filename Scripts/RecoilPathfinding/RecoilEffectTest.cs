using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Play-mode hooks for the effect test tool (Game Tools > Công Thành > Battle > Test hiệu ứng): summon soldiers of a
    /// type, play the Quân sư effects on a live advisor, drop a test siege blast on an army.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        public const int TestAdvisorType = AdvisorCharacterType;
        public const int TestSiegeType = 4;

        private readonly List<Soldier> _testPicks = new();

        /// <summary>Live (non-hero) soldiers of a side, of <paramref name="type"/> or any type when -1.</summary>
        public int TestAliveCount(bool blue, int type)
        {
            var count = 0;
            foreach (var soldier in blue ? _blueTeam : _redTeam)
                if (soldier != null && !soldier.Retired && soldier.HeroId == 0 && (type < 0 || soldier.CharacterType == type))
                    count++;
            return count;
        }

        /// <summary>Queues <paramref name="amount"/> soldiers of <paramref name="type"/>, the way a TikTok gift does.</summary>
        public void TestSummonSoldiers(bool blue, int type, int amount)
        {
            if (amount <= 0) return;
            EnqueueSpawnType(blue ? _blueSelectedSpawnQueue : _redSelectedSpawnQueue, type, amount);
        }

        private Soldier TestPick(bool blue, int type)
        {
            _testPicks.Clear();
            foreach (var soldier in blue ? _blueTeam : _redTeam)
                if (soldier != null && !soldier.Retired && soldier.HeroId == 0 && soldier.Agent != null &&
                    (type < 0 || soldier.CharacterType == type) && HeroSkillAlive(soldier))
                    _testPicks.Add(soldier);
            return _testPicks.Count > 0 ? _testPicks[Random.Range(0, _testPicks.Count)] : null;
        }

        /// <summary>
        /// Plays a Quân sư effect on a random live advisor of a side: 0 = summon (follows it), 1 = cast + hit (on its
        /// last target, else 3 m in front of it). False when that side has no advisor on screen.
        /// </summary>
        public bool TestPlayAdvisorEffect(bool blue, int kind)
        {
            var advisor = TestPick(blue, AdvisorCharacterType);
            if (advisor == null) return false;
            if (kind == 0) { PlayAdvisorEffect(0, AdvisorFeet(advisor), advisor); return true; }
            var target = advisor.AttackTarget != null && !advisor.AttackTarget.Retired
                ? advisor.AttackTargetPosition
                : AdvisorFeet(advisor) + advisor.Agent.transform.forward * 3f;
            PlayAdvisorEffect(1, AdvisorFeet(advisor));
            PlayAdvisorEffect(2, target);
            return true;
        }

        /// <summary>
        /// A siege stone explosion on a random soldier of the side <paramref name="hitBlue"/> (explosion effect, toss,
        /// and <paramref name="damage"/> at the centre; 0 = toss only). False when that side has nobody on screen.
        /// </summary>
        public bool TestSiegeBlast(bool hitBlue, float radius, int damage)
        {
            var target = TestPick(hitBlue, -1);
            if (target == null) return false;
            var point = AdvisorFeet(target);
            var projectile = new PendingCombatProjectile
            {
                FlightStartTime = SimulationNow, LastProcessedTime = SimulationNow, ImpactTime = SimulationNow,
                FlightStartPoint = point, ImpactPoint = point,
                Team = hitBlue ? 1 : 0, Damage = Mathf.Max(0, damage),
                AreaRadius = Mathf.Max(.5f, radius), AreaEdgeEffectiveness = .3f,
                SiegeStone = true
            };
            PlaySiegeImpact(projectile, point);
            _projectileDeaths.Clear();
            ApplyProjectileSplash(projectile, null, true);
            foreach (var dead in _projectileDeaths) KillSoldier(dead);
            _projectileDeaths.Clear();
            return true;
        }

        // ---------------------------------------------------------------- watching one Quân sư

        public struct TestAttackEvent
        {
            public float Time;      // battle seconds
            public bool Landed;     // false = strike started, true = the blow landed
            public string Target;
            public int Damage;
            public float Distance;
            public float HitDelay;
            public float TargetHealth01;
        }

        private static readonly string[] TestTypeNames =
            { "Cung kỵ", "Đao binh", "Giáo binh", "Kỵ binh", "Máy bắn đá", "Quân sư", "Voi chiến", "Bộ binh" };

        private Soldier _testWatched;
        private readonly List<TestAttackEvent> _testWatchLog = new();
        public IReadOnlyList<TestAttackEvent> TestWatchLog => _testWatchLog;
        public float TestBattleTime => SimulationNow;

        /// <summary>Picks an advisor of the side to follow: one that is fighting if any. False when there is none.</summary>
        public bool TestWatchAdvisor(bool blue)
        {
            Soldier best = null;
            foreach (var soldier in blue ? _blueTeam : _redTeam)
            {
                if (!IsAdvisor(soldier) || soldier.Retired || soldier.Agent == null || !HeroSkillAlive(soldier)) continue;
                var fighting = soldier.AttackTarget != null && !soldier.AttackTarget.Retired;
                if (best == null || fighting && (best.AttackTarget == null || best.AttackTarget.Retired) ||
                    fighting && soldier.AttackStartedAt > best.AttackStartedAt) best = soldier;
            }
            _testWatched = best;
            _testWatchLog.Clear();
            return best != null;
        }

        public void TestStopWatching() { _testWatched = null; _testWatchLog.Clear(); }

        /// <summary>
        /// The followed advisor: where it is, its current target (if any) and what it is doing.
        /// False when nothing is followed (or it died: <paramref name="state"/> says so).
        /// </summary>
        public bool TestWatchedInfo(out Vector3 position, out Vector3 target, out bool hasTarget, out string state)
        {
            position = target = Vector3.zero;
            hasTarget = false;
            state = "";
            var advisor = _testWatched;
            if (advisor == null) return false;
            if (advisor.Retired || advisor.Agent == null || !HeroSkillAlive(advisor))
            {
                state = "Quân sư đang theo dõi đã chết.";
                return false;
            }
            position = advisor.Agent.transform.position;
            var health = _entityManager.GetComponentData<RecoilBattleEcsState>(advisor.Entity);
            var hp = $"máu {health.Health}/{Mathf.Max(1, health.Stats.MaxHealth)}";
            var victim = advisor.AttackTarget;
            if (victim != null && !victim.Retired && victim.Agent != null)
            {
                hasTarget = true;
                target = victim.Agent.transform.position;
                var since = SimulationNow - advisor.AttackStartedAt;
                state = $"Đang đánh {TestUnitName(victim)} — cách {Vector3.Distance(position, target):0.0} m, " +
                        $"đòn gần nhất {since:0.0} s trước · {hp}";
            }
            else state = "Đang di chuyển / tìm mục tiêu · " + hp;
            return true;
        }

        private string TestUnitName(Soldier soldier)
        {
            if (soldier == null) return "?";
            if (soldier.HeroId > 0) return "tướng " + (string.IsNullOrEmpty(soldier.DonorName) ? "#" + soldier.HeroId : soldier.DonorName);
            var name = soldier.CharacterType < TestTypeNames.Length ? TestTypeNames[soldier.CharacterType] : "lính";
            return name + (soldier.IsBlue ? " xanh" : " đỏ");
        }

        private float TestHealth01(Soldier soldier)
        {
            if (soldier == null || soldier.Retired || !HeroSkillAlive(soldier)) return 0f;
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            return Mathf.Clamp01(state.Health / (float)Mathf.Max(1, state.Stats.MaxHealth));
        }

        private void AddWatchEvent(TestAttackEvent entry)
        {
            _testWatchLog.Add(entry);
            if (_testWatchLog.Count > 30) _testWatchLog.RemoveAt(0);
        }

        private void RecordWatchedAttack(Soldier attacker, Soldier target, Vector3 targetPosition, int damage, float hitDelay)
        {
            if (attacker == null || !ReferenceEquals(attacker, _testWatched)) return;
            AddWatchEvent(new TestAttackEvent
            {
                Time = SimulationNow, Target = TestUnitName(target), Damage = damage, HitDelay = hitDelay,
                Distance = Vector3.Distance(AdvisorFeet(attacker), targetPosition), TargetHealth01 = TestHealth01(target)
            });
        }

        private void RecordWatchedHit(Soldier attacker, Soldier target)
        {
            if (attacker == null || !ReferenceEquals(attacker, _testWatched)) return;
            AddWatchEvent(new TestAttackEvent
                { Time = SimulationNow, Landed = true, Target = TestUnitName(target), TargetHealth01 = TestHealth01(target) });
        }

        /// <summary>Siege blast toss settings, for the tool to show and change.</summary>
        public bool TestTossEnabled
        {
            get => RecoilBattleEffects.Load() is { SiegeBlastToss: true };
            set { var settings = RecoilBattleEffects.Load(); if (settings != null) settings.SiegeBlastToss = value; }
        }
        public bool TestAdvisorEffectsEnabled { get => advisorEffects; set => advisorEffects = value; }
    }
}
