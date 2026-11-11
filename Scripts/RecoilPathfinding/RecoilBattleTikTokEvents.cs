using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private readonly Dictionary<TikTokArmyRule, Dictionary<string, long>> _tikTokProgress = new();
        private readonly TikTokGiftNormalizer _giftNormalizer = new();
        private readonly int[,] _tikTokPendingUnits = new int[2, CharacterTypeCount];
        private readonly Dictionary<string, bool> _tikTokUserTeams = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TikEvent>> _tikTokUnassignedEvents = new(StringComparer.Ordinal);
        private readonly Dictionary<TikTokArmyRule, Dictionary<string, long>> _tikTokHeroProgress = new();
        private sealed class HeroReward
        {
            public string User;
            public string DisplayName;
            public TikTokArmyRule Rule;
            public bool Blue;
            public int Id;
            public Soldier Active;
            public bool Pending;
            public float RespawnAt;
        }
        private readonly List<HeroReward> _heroRewards = new();
        private RecoilHeroPrefabCatalog _heroPrefabs;

        private void AccumulateTikTokHero(TikTokArmyRule rule, string user, bool blue, long amount, string displayName)
        {
            int threshold = blue ? rule.BlueAccumulation : rule.RedAccumulation;
            int id = blue ? rule.BlueHeroId : rule.RedHeroId;
            if (threshold <= 0 || id <= 0) return;
            if (!_tikTokHeroProgress.TryGetValue(rule, out var users))
                _tikTokHeroProgress[rule] = users = new Dictionary<string, long>();
            users.TryGetValue(user, out long previous);
            long total = previous + amount;
            users[user] = total % threshold;
            if (total < threshold) return;
            var reward = _heroRewards.Find(r => r.User == user && ReferenceEquals(r.Rule, rule));
            if (reward == null)
            {
                reward = new HeroReward { User = user, Rule = rule, Blue = blue, Id = id };
                _heroRewards.Add(reward);
            }
            reward.DisplayName = string.IsNullOrWhiteSpace(displayName) ? user : displayName;
            if (!_donationTopUsers.TryGetValue(user, out var portrait))
            {
                portrait = new DonationTopUser { Id = user, Name = reward.DisplayName, Blue = blue, Order = _donationTopUsers.Count };
                _donationTopUsers.Add(user, portrait);
            }
            UpdateDonationAvatar(portrait, null);
            if (reward.Active != null) reward.Active.DonorName = reward.DisplayName;
            if (reward.Active == null || reward.Active.Retired) reward.Pending = true;
        }

        private void FlushTikTokHeroes()
        {
            if (_matchFinished || !_lobbyStarted || !_heroRewards.Exists(r => r.Pending && SimulationNow >= r.RespawnAt)) return;
            if (_heroPrefabs == null) _heroPrefabs = Resources.Load<RecoilHeroPrefabCatalog>("RecoilHeroPrefabs");
            int budget = 1; // Load one hero at a time to avoid a large VAT/FX allocation spike.
            foreach (var reward in _heroRewards)
            {
                if (!reward.Pending || SimulationNow < reward.RespawnAt || budget <= 0) continue;
                if (lobbyHeroStats == null) LoadHeroSettings();
                var config = Array.Find(lobbyHeroStats, h => h.Id == reward.Id);
                var prefab = _heroPrefabs != null ? _heroPrefabs.Find(reward.Id) : null;
                if (config == null || prefab == null)
                {
                    Debug.LogError($"TikTok: thiếu cấu hình/prefab tướng ID {reward.Id}.", this);
                    reward.Pending = false;
                    continue;
                }
                int formationType = config.Stats.AttackType == RecoilAttackType.Ranged ? 0 : 7;
                if (!SpawnUnit(reward.Blue, formationType, config, prefab)) continue;
                var team = reward.Blue ? _blueTeam : _redTeam;
                reward.Active = team[team.Count - 1];
                reward.Active.DonorName = reward.DisplayName ?? reward.User;
                reward.Active.DonorId = reward.User;
                if (!_testSkipIntroUsers.Contains(reward.User))
                    QueueHeroSummonIntro(reward.User, reward.Active.DonorName, reward.Id, reward.Blue);
                reward.Pending = false;
                budget--;
                Debug.Log($"TikTok: {reward.User} triệu hồi {config.Name} (ID {config.Id}) → {(reward.Blue ? "Xanh" : "Đỏ")}.", this);
            }
        }

        private void ScheduleHeroRespawn(Soldier soldier)
        {
            if (soldier.HeroId <= 0 || _matchFinished) return;
            var config = lobbyHeroStats != null ? Array.Find(lobbyHeroStats, h => h.Id == soldier.HeroId) : null;
            if (config == null) return;
            var reward = _heroRewards.Find(r => ReferenceEquals(r.Active, soldier));
            if (reward == null)
            {
                reward = new HeroReward { Blue = soldier.IsBlue, Id = soldier.HeroId, Active = soldier, DisplayName = soldier.DonorName };
                _heroRewards.Add(reward);
            }
            reward.RespawnAt = SimulationNow + Mathf.Max(0f, config.RespawnSeconds);
            reward.Pending = true;
        }
        private void UpdateHeroAnimation(Soldier soldier)
        {
            if (soldier.HeroVat == null || soldier.Retired || SimulationNow < soldier.HeroAnimationUntil) return;
            int state = soldier.Simulation.CurrentSpeed > .05f ? 1 : 0;
            if (state == soldier.HeroAnimation) return;
            soldier.HeroVat.Play(state);
            soldier.HeroAnimation = state;
        }

        private void PlayHeroAttack(Soldier soldier, Vector3 targetPosition)
        {
            if (soldier.HeroVat == null || soldier.Retired) return;
            if (soldier.HeroAnimation == 4 && SimulationNow < soldier.HeroAnimationUntil) return;
            int clip = 2;
            if (soldier.HeroUltiCooldown > 0 && SimulationNow >= soldier.NextHeroUlti)
            {
                clip = 4;
                soldier.NextHeroUlti = SimulationNow + soldier.HeroUltiCooldown;
            }
            soldier.HeroVat.SetAttackTarget(targetPosition,
                soldier.AttackTarget != null && !soldier.AttackTarget.Retired && soldier.AttackTarget.Agent != null ? soldier.AttackTarget.Agent.transform : null);
            soldier.HeroVat.Play(clip);
            clip = soldier.HeroVat.ActiveClipIndex;
            soldier.HeroAnimation = clip;
            var clips = soldier.HeroVat.clips;
            soldier.HeroAnimationUntil = SimulationNow + (clips != null && clips.Length > clip ? clips[clip].duration : .5f);
        }

        private System.Collections.IEnumerator RetireHeroAfterDeath(Soldier soldier, GameObject instance)
        {
            var clips = soldier.HeroVat.clips;
            float duration = clips != null && clips.Length > 3 ? clips[3].duration : 1f;
            yield return new WaitForSeconds(Mathf.Max(.1f, duration));
            if (instance != null) CompletePrefabSoldierRetirement(soldier, instance);
        }

        private void HandleTikTokBattleEvent(TikEvent message)
        {
            if (message == null || _matchFinished) return;
            if (_tikTokArmySettings == null) LoadTikTokArmySettings();
            var kind = (message.type ?? "").ToLowerInvariant();
            if (kind == "gift_tick" || kind == "gift_final")
            {
                if (!_giftNormalizer.TryNormalize(message, Time.unscaledTime, out var giftEvent)) return;
                message = giftEvent;
                kind = "gift";
            }
            if (Array.IndexOf(TikTokEventTypes, kind) < 0) return;
            var user = message.user?.user_id;
            if (string.IsNullOrEmpty(user)) user = message.user?.unique_id;
            if (string.IsNullOrWhiteSpace(user)) return;
            if (!string.IsNullOrWhiteSpace(message.user?.avatar_url)) _donationAvatarUrls[user] = message.user.avatar_url;
            if (_donationTopUsers.TryGetValue(user, out var avatarDonor))
            {
                UpdateDonationAvatar(avatarDonor, message.user);
                if (!string.IsNullOrWhiteSpace(message.user?.nickname))
                {
                    avatarDonor.Name = message.user.nickname;
                    foreach (var reward in _heroRewards)
                        if (reward.User == user)
                        {
                            reward.DisplayName = message.user.nickname;
                            if (reward.Active != null) reward.Active.DonorName = reward.DisplayName;
                        }
                }
            }
            if (kind == "comment")
            {
                var command = message.comment?.Trim();
                if (command == "1" || command == "2")
                {
                    var requestedBlue = command == "2";
                    if (_tikTokUserTeams.TryGetValue(user, out var selectedBlue))
                    {
                        if (selectedBlue != requestedBlue)
                            Debug.Log($"TikTok: {user} đã chọn phe {(selectedBlue ? "Xanh" : "Đỏ")}, không được đổi phe.", this);
                    }
                    else
                    {
                        _tikTokUserTeams.Add(user, requestedBlue);
                        Debug.Log($"TikTok: {user} gia nhập phe {(requestedBlue ? "Xanh" : "Đỏ")}.", this);
                        if (_tikTokUnassignedEvents.TryGetValue(user, out var pendingEvents))
                        {
                            _tikTokUnassignedEvents.Remove(user);
                            foreach (var pendingEvent in pendingEvents) HandleTikTokBattleEvent(pendingEvent);
                            Debug.Log($"TikTok: đã chuyển {pendingEvents.Count} sự kiện đang dồn của {user} sang phe {(requestedBlue ? "Xanh" : "Đỏ")}.", this);
                        }
                    }
                    JoinMatchScoring(user, message.user?.nickname ?? message.user?.unique_id ?? user, _tikTokUserTeams[user]);
                    return; // Team-selection comments are commands, not reward events.
                }
            }
            if (!_tikTokUserTeams.ContainsKey(user) && TiktokEventTestPopup.RandomTeamIfUnassigned)
            {
                bool randomBlue = UnityEngine.Random.value < .5f;
                _tikTokUserTeams.Add(user, randomBlue);
                Debug.Log($"TikTok: userId={user} được chọn ngẫu nhiên phe {(randomBlue ? "Xanh" : "Đỏ")}.", this);
                if (_tikTokUnassignedEvents.TryGetValue(user, out var waiting))
                {
                    _tikTokUnassignedEvents.Remove(user);
                    foreach (var pending in waiting) HandleTikTokBattleEvent(pending);
                }
            }
            if (!_tikTokUserTeams.TryGetValue(user, out var blue))
            {
                if (!_tikTokUnassignedEvents.TryGetValue(user, out var pendingEvents))
                    _tikTokUnassignedEvents[user] = pendingEvents = new List<TikEvent>();
                pendingEvents.Add(message);
                if (kind == "gift")
                    Debug.Log($"TikTok: quà {message.gift?.name} của userId={user} đang chờ chính user này bình luận 1 (Đỏ) hoặc 2 (Xanh) để nhận quân.", this);
                return;
            }
            JoinMatchScoring(user, message.user?.nickname ?? message.user?.unique_id ?? user, blue);
            if (kind == "gift") RecordDonationTop(message, user, blue);
            foreach (var rule in _tikTokArmySettings.Rules)
            {
                if (!rule.Enabled || !rule.MatchesEvent(kind)) continue;
                if (kind == "gift" && !TikTokGiftMatches(rule, message.gift)) continue;
                if (kind == "comment" && !string.Equals(rule.Comment?.Trim(), message.comment?.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var amount = kind == "like" ? Math.Max(0, message.like_count) :
                    kind == "gift" ? Math.Max(0, message.delta > 0 ? message.delta : message.count) : 1;
                if (amount == 0) continue;
                AccumulateTikTokHero(rule, user, blue, amount,
                    !string.IsNullOrWhiteSpace(message.user?.nickname) ? message.user.nickname : message.user?.unique_id ?? user);
                if (!_tikTokProgress.TryGetValue(rule, out var users)) _tikTokProgress[rule] = users = new Dictionary<string, long>();
                users.TryGetValue(user, out var previous);
                var total = previous + amount;
                var threshold = Math.Max(1, rule.Threshold);
                var rolls = total / threshold;
                users[user] = total % threshold;
                if (rolls == 0) continue;
                if (rule.RandomRewards != null && rule.RandomRewards.Count > 0)
                {
                    // Roll once for each earned reward, preserving the configured probabilities.
                    for (long roll = 0; roll < rolls; roll++) QueueTikTokRandomReward(rule, blue);
                }
                else QueueTikTokReward(rule, blue, rolls, 1f);
            }
        }

        private bool TikTokGiftMatches(TikTokArmyRule rule, TikGift gift)
        {
            return ReferenceEquals(rule, RecoilTikTokGiftMapping.FindReward(_tikTokArmySettings?.Rules, gift));
        }

        private void QueueTikTokRandomReward(TikTokArmyRule source, bool blue)
        {
            long weightTotal = 0;
            foreach (var choice in source.RandomRewards) if (choice != null) weightTotal += Math.Max(0, choice.Weight);
            if (weightTotal <= 0) return;
            var pick = UnityEngine.Random.value * weightTotal;
            foreach (var choice in source.RandomRewards)
            {
                if (choice == null || choice.Weight <= 0) continue;
                pick -= choice.Weight;
                if (pick > 0) continue;
                var reward = _tikTokArmySettings.Rules.Find(r => r.Enabled && r.EventType == "gift" && r.Name == choice.GiftName);
                if (reward != null) QueueTikTokReward(reward, blue, 1, choice.CountMultiplier);
                return;
            }
        }

        private void QueueTikTokReward(TikTokArmyRule rule, bool blue, long rolls, float multiplier)
        {
            if (rule.UnitCount <= 0) return;
            if (rule.UnitType < 0 || rule.UnitType >= CharacterTypeCount || FindTemplateForType(blue, rule.UnitType) == null)
            {
                Debug.LogWarning($"TikTok: chưa gán loại quân hợp lệ cho {rule.Name}. Không spawn thay bằng loại khác.", this);
                return;
            }
            var perRoll = Math.Max(0, (long)Math.Floor(rule.UnitCount * (double)Math.Max(0f, multiplier)));
            var team = blue ? 0 : 1;
            var available = int.MaxValue - (long)_tikTokPendingUnits[team, rule.UnitType];
            var amount = perRoll == 0 ? 0 : Math.Min(available, rolls > available / perRoll ? available : rolls * perRoll);
            _tikTokPendingUnits[team, rule.UnitType] += (int)amount;
            if (amount > 0) Debug.Log($"TikTok {rule.Name}: +{amount} {CharacterTypeLabels[rule.UnitType]} → {(blue ? "Xanh" : "Đỏ")}." +
                (!_combatStarted ? " Chờ bắt đầu trận bằng L." : ""), this);
        }

        private void FlushTikTokReinforcements()
        {
            if (!_lobbyStarted || _matchFinished) return;
            if (!_combatStarted && (!prepareArmyBeforeCombat || !InitialWaitingArmySpawned)) return;
            for (var team = 0; team < 2; team++)
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                var pending = _tikTokPendingUnits[team, type];
                if (pending <= 0) continue;
                var total = team == 0 ? _blueSelectedSpawnTotal : _redSelectedSpawnTotal;
                var amount = Math.Min(pending, int.MaxValue - total);
                if (!_combatStarted) amount = Math.Min(amount, RemainingStagingCapacity(team == 0, type));
                if (amount <= 0) continue;
                if (!_combatStarted && !PrepareWaitingReinforcement(type, team == 0 ? amount : 0, team == 1 ? amount : 0)) continue;
                EnqueueSpawnType(team == 0 ? _blueSelectedSpawnQueue : _redSelectedSpawnQueue, type, amount);
                if (team == 0) _blueSelectedSpawnTotal += amount;
                else _redSelectedSpawnTotal += amount;
                _tikTokPendingUnits[team, type] -= amount;
            }
        }
    }
}
