using System.Collections;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private bool _initialFormationLoading, _initialLayoutPrepared;

        private IEnumerator PrepareInitialLayoutAndStart()
        {
            BeginLoadingPart(LoadingPartFormation); // the whole bar when the pools were loaded earlier
            _poolPrewarmLabel = "Đang tính đội hình…";
            // Render the loading screen before calculating or projecting formation slots.
            yield return null;
            yield return PrepareEndgamePresentation();
            if (!TryBuildCustomInitialArmy(out var error))
            {
                _lobbyError = error;
                _initialFormationLoading = false;
                yield break;
            }
            _initialLayoutPrepared = true;
            _poolPrewarmLabel = "Đang xếp đội hình… 0%";
            if (!StartMatchFromLobby()) _initialFormationLoading = false;
        }

        private void UpdateInitialFormationLoading()
        {
            if (!_initialFormationLoading || !_lobbyStarted) return;
            var target = Mathf.Max(1, PreparedArmyTarget);
            var formed = Mathf.Clamp01(_fullDetailSoldierCount / (float)target);
            SetLoadingPart(formed);
            _poolPrewarmLabel = $"Đang xếp đội hình… {formed * 100f:F0}%";
            if (!InitialWaitingArmySpawned || !IsPreparedArmyRendered()) return;
            if (_waitingFormationActive && !_formationReady) return;
            SetLoadingPart(1f);
            _initialFormationLoading = false;
            _loadingWithPrewarm = false; // the next match only forms up: it fills the whole bar
        }

        private void DrawInitialFormationLoading()
        {
            var previousEnabled = GUI.enabled;
            GUI.enabled = false;
            try { DrawLobby(); }
            finally { GUI.enabled = previousEnabled; }
        }
    }
}
