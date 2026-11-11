using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Quick hero test (Game Tools > Công Thành > Battle > "Test 8 tướng"): starts a match from the lobby, optionally
    /// without soldiers, summons the chosen heroes exactly like TikTok gift rewards (spawned one by one, respawning
    /// when killed), then starts the fight once they are all on the field.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        public struct TestHeroInfo
        {
            public int Id;
            public string Name;
            public bool Blue;
            public bool Alive;
            public bool Waiting; // queued, or waiting to respawn
            public float Health01;
            public int Casts;
            public int Damage; // damage dealt (hero arena only)
        }

        private const string TestUserPrefix = "test:";
        private bool _testHeroesOnly;
        private readonly HashSet<string> _testSkipIntroUsers = new();
        private Coroutine _testRun;
        private int _testSummonCounter;

        public string TestStatus { get; private set; } = "";
        public bool TestMatchRunning => _lobbyStarted && !_matchFinished;

        /// <summary>Lobby → match (no soldiers if asked) → heroes as TikTok rewards → fight.</summary>
        public void TestRunHeroBattle(IReadOnlyList<int> ids, IReadOnlyList<bool> blue, bool heroesOnly, bool skipIntro)
        {
            if (_testRun != null) StopCoroutine(_testRun);
            _testRun = StartCoroutine(RunHeroTest(new List<int>(ids), new List<bool>(blue), heroesOnly, skipIntro));
        }

        private IEnumerator RunHeroTest(List<int> ids, List<bool> blue, bool heroesOnly, bool skipIntro)
        {
            if (!_lobbyStarted)
            {
                _testHeroesOnly = heroesOnly; // read when the initial army is counted
                TestStatus = "Đang vào trận…";
                var giveUp = Time.realtimeSinceStartup + 180f;
                while (!_lobbyStarted && Time.realtimeSinceStartup < giveUp)
                {
                    // Same as pressing Start; while the pools load or the army forms up, just wait.
                    if (_templatesValidated && !_matchFinished && !_poolPrewarmRunning && !_initialFormationLoading)
                        StartMatchFromLobby();
                    yield return null;
                }
                if (!_lobbyStarted)
                {
                    TestStatus = "Không vào được trận" + (string.IsNullOrEmpty(_lobbyError) ? " (xem Console)." : ": " + _lobbyError);
                    _testRun = null;
                    yield break;
                }
            }

            TestSummonHeroes(ids, blue, skipIntro);
            TestStatus = "Đang gọi tướng…";
            var waitHeroes = Time.realtimeSinceStartup + 90f;
            while (!_matchFinished && Time.realtimeSinceStartup < waitHeroes &&
                   _heroRewards.Exists(r => r.Pending && IsTestUser(r.User)))
                yield return null;
            if (!_combatStarted && !_matchFinished)
            {
                TestStatus = "Đếm ngược giao chiến…";
                TryStartBattle();
                while (!_combatStarted && !_matchFinished && _startCountdownEndsAt > 0) yield return null;
            }
            TestStatus = _matchFinished ? "Trận đã kết thúc." : "Đang đánh.";
            _testRun = null;
        }

        /// <summary>Queues heroes the way a TikTok gift does: FlushTikTokHeroes spawns them one at a time.</summary>
        public void TestSummonHeroes(IReadOnlyList<int> ids, IReadOnlyList<bool> blue, bool skipIntro)
        {
            if (lobbyHeroStats == null) LoadHeroSettings();
            for (var i = 0; i < ids.Count; i++)
            {
                var user = $"{TestUserPrefix}{ids[i]}:{(blue[i] ? "xanh" : "do")}:{++_testSummonCounter}";
                var config = lobbyHeroStats != null ? System.Array.Find(lobbyHeroStats, h => h.Id == ids[i]) : null;
                if (skipIntro) _testSkipIntroUsers.Add(user);
                _heroRewards.Add(new HeroReward
                {
                    User = user, DisplayName = "Test " + (config != null ? config.Name : "#" + ids[i]),
                    Blue = blue[i], Id = ids[i], Pending = true, RespawnAt = 0f
                });
            }
        }

        /// <summary>Starts the fight now (the 3-2-1 countdown), like pressing L.</summary>
        public void TestStartCombat()
        {
            if (!_combatStarted) TryStartBattle();
        }

        /// <summary>Every test hero: on the field (health, skills cast so far) or waiting to spawn / respawn.</summary>
        public void TestGetHeroes(List<TestHeroInfo> into)
        {
            into.Clear();
            foreach (var reward in _heroRewards)
            {
                if (!IsTestUser(reward.User)) continue;
                var info = new TestHeroInfo { Id = reward.Id, Name = reward.DisplayName, Blue = reward.Blue };
                var hero = reward.Active;
                if (hero != null && !hero.Retired && IsAlive(hero))
                {
                    var state = _entityManager.GetComponentData<RecoilBattleEcsState>(hero.Entity);
                    info.Alive = true;
                    info.Health01 = Mathf.Clamp01(state.Health / (float)Mathf.Max(1, state.Stats.MaxHealth));
                }
                else info.Waiting = reward.Pending;
                info.Casts = hero != null ? hero.TotalCasts : 0;
                into.Add(info);
            }
        }

        private static bool IsTestUser(string user) => user != null && user.StartsWith(TestUserPrefix, System.StringComparison.Ordinal);
    }
}
