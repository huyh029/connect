using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [Serializable]
        private sealed class UnitAudioBank
        {
            public AudioClip[] attacks = Array.Empty<AudioClip>();
            public AudioClip[] deaths = Array.Empty<AudioClip>();
            public AudioClip[] movement = Array.Empty<AudioClip>();
        }

        [SerializeField] private AudioClip battleAmbienceClip;
        [SerializeField] private AudioClip armyMovementClip;
        [SerializeField] private AudioClip warDrumClip;
        [SerializeField] private UnitAudioBank[] unitAudioBanks = Array.Empty<UnitAudioBank>();

        private GameObject _battleAudioRoot;
        private AudioSource _battleAmbienceSource;
        private AudioSource _armyMovementSource;
        private AudioSource _battleEffectsSource;
        private float _nextAttackSoundAt;
        private float _nextDeathSoundAt;
        private float _nextMovementSoundAt;
        private bool _missingBattleAudioLogged;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (Application.isPlaying ||
                (battleAmbienceClip != null && armyMovementClip != null && warDrumClip != null &&
                 unitAudioBanks != null && unitAudioBanks.Length == CharacterTypeCount))
                return;

            try
            {
                PopulateBattleAudioAssetsInEditor();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
#endif

        private void EnsureBattleAudioSources()
        {
            if (!Application.isPlaying) return;
#if UNITY_EDITOR
            if (battleAmbienceClip == null || armyMovementClip == null || warDrumClip == null ||
                unitAudioBanks == null || unitAudioBanks.Length != CharacterTypeCount)
                PopulateBattleAudioAssetsInEditor();
#endif
            if (_battleAudioRoot != null) return;

            _battleAudioRoot = new GameObject("Battle Audio");
            _battleAudioRoot.transform.SetParent(transform, false);
            _battleAmbienceSource = CreateBattleAudioSource("Battle ambience", true);
            _armyMovementSource = CreateBattleAudioSource("Army movement", true);
            _battleEffectsSource = CreateBattleAudioSource("Battle effects", false);
        }

        private AudioSource CreateBattleAudioSource(string sourceName, bool loop)
        {
            var sourceObject = new GameObject(sourceName);
            sourceObject.transform.SetParent(_battleAudioRoot.transform, false);
            var source = sourceObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            return source;
        }

        private void BeginBattleMovementAudio()
        {
            EnsureBattleAudioSources();
            if (_armyMovementSource == null) return;
            if (armyMovementClip == null)
            {
                LogMissingBattleAudio();
                return;
            }

            _armyMovementSource.clip = armyMovementClip;
            _armyMovementSource.volume = 0.25f;
            _armyMovementSource.Play();
        }

        private void PlayCountdownWarDrum()
        {
            EnsureBattleAudioSources();
            if (_battleEffectsSource == null) return;
            if (warDrumClip == null)
            {
                LogMissingBattleAudio();
                return;
            }

            _battleEffectsSource.PlayOneShot(warDrumClip, 0.45f);
        }

        private void BeginCombatAudio()
        {
            EnsureBattleAudioSources();
            if (_battleAmbienceSource == null) return;
            if (battleAmbienceClip == null)
            {
                LogMissingBattleAudio();
                return;
            }

            _battleAmbienceSource.clip = battleAmbienceClip;
            _battleAmbienceSource.volume = 0.32f;
            _battleAmbienceSource.Play();
        }

        private void StopBattleAudio()
        {
            if (_battleAmbienceSource != null) _battleAmbienceSource.Stop();
            if (_armyMovementSource != null) _armyMovementSource.Stop();
            if (_battleEffectsSource != null) _battleEffectsSource.Stop();
        }

        private float _nextSiegeExplosionSoundAt;

        /// <summary>
        /// The explosion sound (Resources/RecoilBattleEffects): a siege stone landing (at most one every
        /// SiegeExplosionMinInterval, so a volley of stones does not stack into noise) or the endgame meteor.
        /// </summary>
        private void PlayExplosionSound(bool endgame)
        {
            var effects = RecoilBattleEffects.Load();
            if (effects == null || effects.ExplosionSound == null) return;
            if (!endgame)
            {
                if (Time.unscaledTime < _nextSiegeExplosionSoundAt) return;
                _nextSiegeExplosionSoundAt = Time.unscaledTime + effects.SiegeExplosionMinInterval;
            }
            EnsureBattleAudioSources();
            if (_battleEffectsSource == null) return;
            _battleEffectsSource.PlayOneShot(effects.ExplosionSound,
                endgame ? effects.EndgameExplosionVolume : effects.SiegeExplosionVolume);
        }

        private void PlayUnitAttackSound(Soldier soldier)
        {
            if (soldier == null || Time.unscaledTime < _nextAttackSoundAt ||
                !TryGetUnitSoundBank(soldier.CharacterType, out var bank))
                return;
            var clip = PickUnitSound(bank.attacks);
            if (clip == null) return;

            EnsureBattleAudioSources();
            if (_battleEffectsSource == null) return;
            _battleEffectsSource.PlayOneShot(clip, 0.2f);
            _nextAttackSoundAt = Time.unscaledTime + 0.14f;
        }

        private void PlayUnitDeathSound(Soldier soldier)
        {
            if (soldier == null || Time.unscaledTime < _nextDeathSoundAt ||
                !TryGetUnitSoundBank(soldier.CharacterType, out var bank))
                return;
            var clip = PickUnitSound(bank.deaths);
            if (clip == null) return;

            EnsureBattleAudioSources();
            if (_battleEffectsSource == null) return;
            _battleEffectsSource.PlayOneShot(clip, 0.3f);
            _nextDeathSoundAt = Time.unscaledTime + 0.28f;
        }

        private void UpdateArmyMovementAudio()
        {
            if (!_combatStarted || Time.unscaledTime < _nextMovementSoundAt ||
                unitAudioBanks == null || unitAudioBanks.Length == 0)
                return;

            var total = _blueTeam.Count + _redTeam.Count;
            if (total == 0) return;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var index = UnityEngine.Random.Range(0, total);
                var soldier = index < _blueTeam.Count
                    ? _blueTeam[index]
                    : _redTeam[index - _blueTeam.Count];
                if (soldier == null || soldier.Retired || soldier.Simulation == null ||
                    !soldier.Simulation.Exists ||
                    soldier.Simulation.CurrentSpeed < 0.5f ||
                    !TryGetUnitSoundBank(soldier.CharacterType, out var bank))
                    continue;

                var clip = PickUnitSound(bank.movement);
                if (clip == null) continue;
                EnsureBattleAudioSources();
                if (_battleEffectsSource == null) return;
                _battleEffectsSource.PlayOneShot(clip, 0.12f);
                _nextMovementSoundAt = Time.unscaledTime + 0.55f;
                return;
            }
        }

        private bool TryGetUnitSoundBank(int characterType, out UnitAudioBank bank)
        {
            bank = null;
            return unitAudioBanks != null && characterType >= 0 &&
                   characterType < unitAudioBanks.Length &&
                   (bank = unitAudioBanks[characterType]) != null;
        }

        private static AudioClip PickUnitSound(AudioClip[] clips)
        {
            if (clips == null || clips.Length == 0) return null;
            var start = UnityEngine.Random.Range(0, clips.Length);
            for (var attempt = 0; attempt < clips.Length; attempt++)
            {
                var clip = clips[(start + attempt) % clips.Length];
                if (clip != null) return clip;
            }
            return null;
        }

        private void LogMissingBattleAudio()
        {
            if (_missingBattleAudioLogged) return;
            _missingBattleAudioLogged = true;
            Debug.LogError("Battle audio clips are not assigned. Populate them from Assets/_Game/sound.", this);
        }

#if UNITY_EDITOR
        public void PopulateBattleAudioAssetsInEditor()
        {
            const string root = "Assets/_Game/sound";
            var clips = new List<AudioClip>();
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:AudioClip", new[] { root }))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null) clips.Add(clip);
            }
            clips.Sort((left, right) => string.CompareOrdinal(left.name, right.name));

            AudioClip FindClip(string exactName) =>
                clips.Find(clip => string.Equals(clip.name, exactName, StringComparison.OrdinalIgnoreCase));
            AudioClip[] FindClips(Func<string, bool> predicate) =>
                clips.FindAll(clip => predicate(clip.name)).ToArray();
            AudioClip[] WithPrefix(string prefix) =>
                FindClips(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            battleAmbienceClip = FindClip("Tiếng Chiến Trận Hỗn Chiến");
            armyMovementClip = FindClip("Bộ Binh + Thương Binh + Cung Binh_ Di Chuyển");
            warDrumClip = FindClip("war-drum-03");
            unitAudioBanks = new UnitAudioBank[CharacterTypeCount];
            for (var i = 0; i < unitAudioBanks.Length; i++)
                unitAudioBanks[i] = new UnitAudioBank();

            var archerAttacks = WithPrefix("archer_shot");
            var infantryAttacks = WithPrefix("shortswordsman_attack");
            var spearmanAttacks = WithPrefix("hoplite_attack");
            var cavalryAttacks = WithPrefix("cavalry_attack");
            var siegeAttacks = WithPrefix("stonethrower_attack");
            var elephantAttacks = WithPrefix("elephant_attack");
            unitAudioBanks[0].attacks = archerAttacks;
            unitAudioBanks[1].attacks = infantryAttacks;
            unitAudioBanks[2].attacks = spearmanAttacks;
            unitAudioBanks[3].attacks = cavalryAttacks;
            unitAudioBanks[4].attacks = siegeAttacks;
            unitAudioBanks[6].attacks = elephantAttacks;
            unitAudioBanks[7].attacks = infantryAttacks;

            unitAudioBanks[3].deaths = WithPrefix("cavalry_die");
            unitAudioBanks[4].deaths = WithPrefix("stonethrower_die");
            unitAudioBanks[6].deaths = WithPrefix("elephant_die");
            unitAudioBanks[3].movement = WithPrefix("cavalry_move");
            unitAudioBanks[6].movement = WithPrefix("elephant_move");

            if (battleAmbienceClip == null || armyMovementClip == null || warDrumClip == null ||
                archerAttacks.Length == 0 || infantryAttacks.Length == 0 ||
                spearmanAttacks.Length == 0 || cavalryAttacks.Length == 0 ||
                siegeAttacks.Length == 0 || elephantAttacks.Length == 0)
                throw new InvalidOperationException("One or more expected audio clips were not found in Assets/_Game/sound.");

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
