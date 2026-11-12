using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private const string BlueBurningCastleRootName = "CASTLE_CHAY_blue";
        private const string RedBurningCastleRootName = "CASTLE_CHAY_red (1)";

        [SerializeField] private Object blueCastleBurningPrefab;
        [SerializeField] private Object redCastleBurningPrefab;

        private readonly RecoilCastleMaterialState[] _castleVisualStates = new RecoilCastleMaterialState[2];
        private readonly Renderer[][] _castleBaseRenderers = new Renderer[2][];
        private readonly bool[][] _castleBaseRendererEnabled = new bool[2][];
        private readonly GameObject[] _castleBurningEffects = new GameObject[2];
        private readonly ParticleSystem[][] _castleBurningParticles = new ParticleSystem[2][];
        private readonly bool[] _castleBurningConfigurationErrorsLogged = new bool[2];

        private void SetCastleVisualBurning(RecoilCastleTeam team, bool burning)
        {
            var index = team == RecoilCastleTeam.Blue ? 0 : 1;
            if (_castleVisualStates[index] == null)
            {
                var castle = GameObject.Find(index == 0 ? "CASTLE_blue" : "CASTLE_red");
                _castleVisualStates[index] = castle != null ? castle.GetComponent<RecoilCastleMaterialState>() : null;
            }
            CacheCastleBaseRenderers(index);
            _castleVisualStates[index]?.SetBurning(burning);

            if (burning)
                PlayCastleBurningParticles(index);
            else
                StopCastleBurningParticles(index);
        }

        private void PlayCastleBurningParticles(int index)
        {
            if (_castleBurningEffects[index] == null && !CreateCastleBurningEffects(index))
                return;

            SetCastleBaseRenderersVisible(index, false);
            var effects = _castleBurningEffects[index];
            effects.SetActive(true);
            foreach (var particle in _castleBurningParticles[index])
                if (particle != null && !particle.isPlaying)
                    particle.Play(false);
        }

        /// <summary>
        /// Loading: makes both castles' (hidden) burning copies now instead of at the first hit, and hands them to the
        /// render warm-up so their meshes, materials and shaders are ready before the castle first catches fire.
        /// </summary>
        private void PrepareCastleBurningEffects(HashSet<GameObject> renderRoots)
        {
            for (var index = 0; index < 2; index++)
            {
                // The castle's own renderers first: the burning copy becomes its child and must not be among them.
                CacheCastleBaseRenderers(index);
                if (_castleBurningEffects[index] == null && !CreateCastleBurningEffects(index)) continue;
                renderRoots?.Add(_castleBurningEffects[index]);
            }
        }

        private void CacheCastleBaseRenderers(int index)
        {
            if (_castleBaseRenderers[index] != null) return;

            var castle = GameObject.Find(index == 0 ? "CASTLE_blue" : "CASTLE_red");
            if (castle == null) return;

            // Only the castle's own renderers: never those of its burning copy (a child of the castle), which must
            // stay visible when the castle is hidden.
            var burning = _castleBurningEffects[index] != null ? _castleBurningEffects[index].transform : null;
            _castleBaseRenderers[index] = System.Array.FindAll(castle.GetComponentsInChildren<Renderer>(true),
                renderer => burning == null || !renderer.transform.IsChildOf(burning));
            _castleBaseRendererEnabled[index] = new bool[_castleBaseRenderers[index].Length];
            for (var i = 0; i < _castleBaseRenderers[index].Length; i++)
                _castleBaseRendererEnabled[index][i] = _castleBaseRenderers[index][i] != null &&
                    _castleBaseRenderers[index][i].enabled;
        }

        private void SetCastleBaseRenderersVisible(int index, bool visible)
        {
            if (_castleBaseRenderers[index] == null) return;
            for (var i = 0; i < _castleBaseRenderers[index].Length; i++)
                if (_castleBaseRenderers[index][i] != null)
                    _castleBaseRenderers[index][i].enabled = visible && _castleBaseRendererEnabled[index][i];
        }

        private bool CreateCastleBurningEffects(int index)
        {
            var teamName = index == 0 ? "xanh" : "đỏ";
            var prefabAsset = index == 0 ? blueCastleBurningPrefab : redCastleBurningPrefab;
            var castle = GameObject.Find(index == 0 ? "CASTLE_blue" : "CASTLE_red");
            if (prefabAsset == null || castle == null)
            {
                LogCastleBurningConfigurationError(index,
                    $"Thiếu prefab cháy hoặc lâu đài {teamName}; hãy gán prefab 'lúc cháy {teamName}' trong RecoilAutoBattleDemo.");
                return false;
            }

            var staging = new GameObject($"Castle {teamName} burning effects staging");
            staging.SetActive(false);
            var clone = Object.Instantiate(prefabAsset, staging.transform, false);
            if (clone == null)
            {
                LogCastleBurningConfigurationError(index,
                    $"Không tạo được bản sao từ prefab cháy {teamName} '{prefabAsset.name}'.");
                Destroy(staging);
                return false;
            }

            var instanceTransform = clone switch
            {
                GameObject gameObject => gameObject.transform,
                Component component => component.transform,
                _ => null
            };
            if (instanceTransform == null)
            {
                LogCastleBurningConfigurationError(index,
                    $"Asset cháy {teamName} '{prefabAsset.name}' phải là GameObject hoặc Component prefab, nhận được {clone.GetType().Name}.");
                Destroy(staging);
                return false;
            }

            while (instanceTransform.parent != staging.transform && instanceTransform.parent != null)
                instanceTransform = instanceTransform.parent;
            if (instanceTransform.parent != staging.transform)
            {
                LogCastleBurningConfigurationError(index,
                    $"Không thể xác định GameObject gốc được tạo từ prefab cháy {teamName} '{prefabAsset.name}'.");
                Destroy(staging);
                return false;
            }
            var instance = instanceTransform.gameObject;
            instance.name = $"{prefabAsset.name} ({castle.name} effects)";

            var expectedRootName = index == 0 ? BlueBurningCastleRootName : RedBurningCastleRootName;
            var castleRoot = FindCastleRoot(instance.transform, expectedRootName);
            if (castleRoot == null || castleRoot.parent != instance.transform)
            {
                LogCastleBurningConfigurationError(index,
                    $"Không tìm thấy root '{expectedRootName}' là con trực tiếp trong prefab {prefabAsset.name}.");
                Destroy(staging);
                return false;
            }

            var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
            if (particles.Length == 0)
            {
                LogCastleBurningConfigurationError(index,
                    $"Prefab {prefabAsset.name} không có ParticleSystem nào để phát.");
                Destroy(staging);
                return false;
            }

            foreach (var particle in particles)
            {
                var main = particle.main;
                main.playOnAwake = false;
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            if (!AlignCastleRoot(instance.transform, castleRoot))
            {
                LogCastleBurningConfigurationError(index,
                    $"Root {castleRoot.name} có scale bằng 0 trong prefab {prefabAsset.name}.");
                Destroy(staging);
                return false;
            }

            instance.SetActive(false);
            instance.transform.SetParent(castle.transform, false);
            Destroy(staging);

            _castleBurningEffects[index] = instance;
            _castleBurningParticles[index] = particles;
            return true;
        }

        private static Transform FindCastleRoot(Transform root, string expectedName)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == expectedName)
                    return child;
            return null;
        }

        private static bool AlignCastleRoot(Transform group, Transform castleRoot)
        {
            var sourceScale = castleRoot.localScale;
            if (Mathf.Approximately(sourceScale.x, 0f) ||
                Mathf.Approximately(sourceScale.y, 0f) ||
                Mathf.Approximately(sourceScale.z, 0f))
                return false;

            group.localScale = new Vector3(1f / sourceScale.x, 1f / sourceScale.y, 1f / sourceScale.z);
            group.localRotation = Quaternion.Inverse(castleRoot.localRotation);
            group.localPosition = -(group.localRotation * Vector3.Scale(group.localScale, castleRoot.localPosition));
            return true;
        }

        private void StopCastleBurningParticles(int index)
        {
            if (_castleBurningEffects[index] != null)
            {
                foreach (var particle in _castleBurningParticles[index])
                    if (particle != null)
                        particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _castleBurningEffects[index].SetActive(false);
            }
            SetCastleBaseRenderersVisible(index, true);
        }

        private void LogCastleBurningConfigurationError(int index, string message)
        {
            if (_castleBurningConfigurationErrorsLogged[index]) return;
            _castleBurningConfigurationErrorsLogged[index] = true;
            Debug.LogError(message, this);
        }
    }
}
