using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private readonly Dictionary<GameObject, Soldier> _prefabSoldierOwners = new();

        /// <summary>The finished battle is being cleared (Retry / Home): a loading screen covers it.</summary>
        private bool _battlefieldClearing;
        private float _battlefieldClearProgress;
        private float _battlefieldClearStarted;
        private const float MinimumClearingSeconds = .8f; // long enough to read, so it never just flickers

        /// <summary>
        /// Clears the finished match in place, keeping every pooled unit, VAT, effect and warmed render asset:
        /// Retry then starts the next match at once, Home goes back to the lobby (its next Start skips the loading
        /// that was already done).
        /// </summary>
        private void BeginRetryInPlace(bool startNextMatch = true)
        {
            _endgameLeaving = true;
            StopAllCoroutines();
            StartCoroutine(RetryInPlace(startNextMatch));
        }

        private IEnumerator RetryInPlace(bool startNextMatch)
        {
            // A loading screen covers the battlefield while everything goes back to its pool (nothing is destroyed:
            // pools, VAT, effects and warmed render assets stay for the next match).
            _battlefieldClearing = true;
            _battlefieldClearProgress = 0f;
            _battlefieldClearStarted = Time.unscaledTime;
            yield return null;
            StopHeroIntros(true);
            CompletePendingCombatJob();
            if (pathService != null) pathService.SuspendForEndgame();
            RestoreEndgameLighting();

            if (_endgameCamera != null)
            {
                _endgameCamera.transform.SetPositionAndRotation(_endgameStartPosition, _endgameStartRotation);
                _endgameCamera.farClipPlane = _endgameOriginalFar;
                if (_endgameCamera.orthographic) _endgameCamera.orthographicSize = _endgameStartOrtho;
            }
            if (_endgameMeteor != null)
            {
                foreach (var particle in _endgameMeteorParticles)
                    if (particle != null) particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                _endgameMeteor.SetActive(false);
            }
            _endgameExplosionPlayed = false;
            var owners = new List<KeyValuePair<GameObject, Soldier>>(_prefabSoldierOwners);
            for (var i = 0; i < owners.Count; i++)
            {
                var pair = owners[i];
                var soldier = pair.Value;
                soldier.Retired = true;
                if (soldier.Simulation != null && soldier.Simulation.Exists) soldier.Simulation.SetActive(false);
                if (soldier.HeroVat != null) soldier.HeroVat.PrepareForPool();
                CompletePrefabSoldierRetirement(soldier, pair.Key);
                if (i % 64 == 63)
                {
                    _battlefieldClearProgress = .9f * (i + 1) / Mathf.Max(1, owners.Count);
                    yield return null;
                }
            }
            _battlefieldClearProgress = .9f;
            _prefabSoldierOwners.Clear();
            foreach (var entity in _ownedSoldierEntities)
                if (_entityManager.Exists(entity)) _entityManager.DestroyEntity(entity);
            _ownedSoldierEntities.Clear();
            _blueTeam.Clear(); _redTeam.Clear();
            _combatSoldiers.Clear(); _combatIndicesByEntity.Clear(); _combatBodyRadiusCache.Clear();
            _scheduledSoldiers.Clear(); _scheduledSoldierEntities.Clear();
            ResetDonationTop();
            _heroDuelists.Clear(); _heroRewards.Clear(); _tikTokHeroProgress.Clear(); _tikTokHeroCombos.Clear();
            _tikTokProgress.Clear(); _tikTokUserTeams.Clear(); _tikTokUnassignedEvents.Clear();
            _giftNormalizer.Clear();
            System.Array.Clear(_tikTokPendingUnits, 0, _tikTokPendingUnits.Length);
            System.Array.Clear(_waitingDeferred, 0, _waitingDeferred.Length);
            for (var team = 0; team < 2; team++)
            for (var type = 0; type < CharacterTypeCount; type++)
            { _waitingSlots[team][type]?.Clear(); _waitingAssigned[team][type] = 0; }
            _waitingFormationActive = _waitingFormationDirty = _waitingHeroesDirty = false;
            ResetCombatLaunch();
            _nextWaitingPlanAttempt = _nextStagingQueueCheck = _nextHeroFormationAttempt = 0;
            _initialBlueSoldiersSpawned = _initialRedSoldiersSpawned = 0;
            _waitingPositionTypes.Clear();
            _preparedBluePositions.Clear(); _preparedRedPositions.Clear();
            _preparedPositionsBuilt = false;
            _initialLayoutPrepared = _initialFormationLoading = false;
            _initialArmySelected = -1;
            _blueSpawnedTotal = _redSpawnedTotal = _fullDetailSoldierCount = 0;
            _blueSelectedSpawnQueue.Clear(); _redSelectedSpawnQueue.Clear();
            _blueSpawnQueue = _redSpawnQueue = _blueSelectedSpawnTotal = _redSelectedSpawnTotal = 0;
            _blueSpawnRow.Reset(); _redSpawnRow.Reset();
            _spawnBlueCredit = 0;
            _spawnFootprintFrame = -1;
            if (_spawnFootprints.IsCreated) _spawnFootprints.Clear();
            _nextSpawnAttemptTime = 0;
            _spawnFailureReported = _spawnExitBlocked = false;
            _pendingBlueExitLane = _pendingRedExitLane = 0;
            _blueCombatSpawnSlotIndex = _redCombatSpawnSlotIndex = 0;
            if (_visualProjectilePool != null) _visualProjectilePool.ReturnAllToPool();
            foreach (var effect in _siegeImpactEffects)
            {
                if (effect.Object == null) continue;
                foreach (var particle in effect.Particles)
                    if (particle != null) particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                effect.Object.SetActive(false);
            }
            StopAdvisorEffectsForNextMatch(); // before the castles are restored: no strike may re-ignite them
            InitializeCastleCombatState();
            ResetRetryCombatBuffers();
            _hasRetirements = false;
            _combatRosterDirty = _projectileGridStale = true;
            _combatRosterRemovalsSinceBuild = 0;
            _combatScheduleGeneration = 0;
            _blueTargetSearchCursor = _redTargetSearchCursor = 0;
            _targetSearchBudgetFrame = -1; _targetSearchesBudgetedThisFrame = 0;
            _startCountdownEndsAt = 0;
            StopBattleAudio();
            _tickTime = RecoilSimulationClock.SimulationSeconds;
            _combatStarted = _lobbyStarted = _lobbyStarting = false;
            _matchFinished = false; _matchResult = null;
            _settingsOpen = false;
            _formationReady = false;

            _endgameStartedAt = -1f;
            _endgameLeaving = false;
            InvalidateSpawnFormationPlans();
            InvalidateAssembly();
            if (pathService != null) pathService.ResumeAfterEndgame();
            RecoilSimulationClock.Register(this);
            _lobbyError = null;
            // Let the bar finish before the next screen.
            while (Time.unscaledTime - _battlefieldClearStarted < MinimumClearingSeconds)
            {
                _battlefieldClearProgress = Mathf.Max(_battlefieldClearProgress,
                    Mathf.Lerp(.9f, 1f, (Time.unscaledTime - _battlefieldClearStarted) / MinimumClearingSeconds));
                yield return null;
            }
            _battlefieldClearProgress = 1f;
            yield return null;
            // Both castles whole again as the loading screen lifts, whatever landed during the clearing.
            SetCastleVisualBurning(RecoilCastleTeam.Blue, false);
            SetCastleVisualBurning(RecoilCastleTeam.Red, false);
            _battlefieldClearing = false;
            if (startNextMatch) StartMatchFromLobby();
            // Home: nothing else to do; with the match cleared the lobby shows again, ready to Start.
        }


        private void ResetRetryCombatBuffers()
        {
            if (_combatCells.IsCreated) _combatCells.Clear();
            if (_combatTeamCellCounts.IsCreated) _combatTeamCellCounts.Clear();
            if (_combatChangedCells.IsCreated) _combatChangedCells.Clear();
            if (_combatGridSurvivors.IsCreated) _combatGridSurvivors.Clear();
            if (_combatEntityIndices.IsCreated) _combatEntityIndices.Clear();
            if (_combatDeaths.IsCreated) _combatDeaths.Clear();
            for (var i = 0; i < _combatBufferCapacity; i++)
            {
                _combatSnapshots[i] = default;
                _combatSnapshotEntities[i] = default;
                _combatPreviousCells[i] = default;
                _combatPreviousActive[i] = 0;
                _combatScheduleGenerations[i] = 0;
                _combatChangedIndices[i] = 0;
            }
            if (_combatGridMetadata.IsCreated)
                for (var i = 0; i < _combatGridMetadata.Length; i++) _combatGridMetadata[i] = 0;
        }
    }
}
