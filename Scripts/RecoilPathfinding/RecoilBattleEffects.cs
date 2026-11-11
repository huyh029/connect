using UnityEngine;

namespace RecoilPathfinding
{
    public sealed class RecoilBattleEffects : ScriptableObject
    {
        public GameObject SiegeImpactPrefab;

        [Header("Quân sư")]
        [Tooltip("Quân sư được triệu hồi vào trận")] public GameObject AdvisorSummonPrefab;
        [Tooltip("Quân sư ra đòn (dưới chân)")] public GameObject AdvisorCastPrefab;
        [Tooltip("Chỗ đòn của Quân sư đánh trúng")] public GameObject AdvisorHitPrefab;
        [Min(.05f)] public float AdvisorSummonScale = 1f;
        [Min(.05f)] public float AdvisorCastScale = .8f;
        [Min(.05f)] public float AdvisorHitScale = .6f;
        [Tooltip("Tên các phần của hiệu ứng ra đòn bị ẩn (cách nhau dấu phẩy) — các quả cầu tự xoay rồi tắt, thay bằng quả cầu bay tới mục tiêu")]
        public string AdvisorCastHide = "MainFlares";

        [Header("Quân sư — quả cầu")]
        [Tooltip("Prefab lấy hình quả cầu (trống = hiệu ứng ra đòn)")] public GameObject AdvisorOrbSource;
        [Tooltip("Tên phần trong prefab đó dùng làm quả cầu")] public string AdvisorOrbChild = "Flares";
        [Range(0, 8)] public int AdvisorOrbCount = 3;
        [Min(0f), Tooltip("Bán kính vòng xoay quanh Quân sư")] public float AdvisorOrbRadius = 1.2f;
        [Tooltip("Độ cao vòng xoay")] public float AdvisorOrbHeight = 2f;
        [Tooltip("Tốc độ xoay (độ / giây)")] public float AdvisorOrbSpin = 360f;
        [Min(.05f)] public float AdvisorOrbSize = .8f;
        [Min(.05f), Tooltip("Độ dài vệt sáng phía sau (giây)")] public float AdvisorOrbTrail = .25f;
        [Range(0f, 1f), Tooltip("Lúc phóng cầu, tính theo phần của hoạt ảnh đánh")] public float AdvisorOrbLaunch = .45f;
        [Min(0f), Tooltip("Thời gian bay tới mục tiêu (giây)")] public float AdvisorOrbFlight = .35f;
        [Tooltip("Độ vồng khi bay")] public float AdvisorOrbArc = .8f;
        [Tooltip("Điểm cầu bay tới: cao hơn chân mục tiêu bao nhiêu")] public float AdvisorOrbTargetHeight = 1f;
        [Tooltip("Vị trí hiệu ứng ra đòn so với Quân sư: x = sang phải, y = lên, z = về phía trước")]
        public Vector3 AdvisorCastOffset;
        [Tooltip("Vị trí hiệu ứng trúng đòn so với mục tiêu: x = sang phải, y = lên, z = tiếp theo hướng đòn")]
        public Vector3 AdvisorHitOffset;

        private static RecoilBattleEffects _loaded;
        private static bool _tried;

        /// <summary>Resources/RecoilBattleEffects (null if missing).</summary>
        public static RecoilBattleEffects Load()
        {
            if (_loaded == null && !_tried)
            {
                _tried = true;
                _loaded = Resources.Load<RecoilBattleEffects>("RecoilBattleEffects");
            }
            return _loaded;
        }

        /// <summary>0 = summon, 1 = cast, 2 = hit.</summary>
        public GameObject AdvisorPrefab(int kind) =>
            kind == 0 ? AdvisorSummonPrefab : kind == 1 ? AdvisorCastPrefab : AdvisorHitPrefab;

        public float AdvisorScale(int kind) =>
            kind == 0 ? AdvisorSummonScale : kind == 1 ? AdvisorCastScale : AdvisorHitScale;

        /// <summary>Hides the parts of the cast effect listed in <see cref="AdvisorCastHide"/>.</summary>
        public void HideCastParts(GameObject castInstance)
        {
            if (castInstance == null || string.IsNullOrWhiteSpace(AdvisorCastHide)) return;
            var names = AdvisorCastHide.Split(',');
            foreach (var child in castInstance.GetComponentsInChildren<Transform>(true))
                foreach (var name in names)
                    if (child != castInstance.transform && child.name == name.Trim()) child.gameObject.SetActive(false);
        }

        /// <summary><paramref name="origin"/> moved by <paramref name="offset"/> in a frame facing <paramref name="forward"/>.</summary>
        public static Vector3 Place(Vector3 origin, Vector3 forward, Vector3 offset)
        {
            forward.y = 0f;
            forward = forward.sqrMagnitude > .0001f ? forward.normalized : Vector3.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            return origin + right * offset.x + Vector3.up * offset.y + forward * offset.z;
        }

        /// <summary>
        /// Plays a pooled, non-looping copy of <paramref name="effect"/>'s particles at <paramref name="position"/>,
        /// with the prefab's own rotation and <paramref name="scale"/> applied to the prefab's size.
        /// </summary>
        public static void PlayOnce(GameObject instance, ParticleSystem[] particles, GameObject prefab, Vector3 position, float scale)
        {
            foreach (var particle in particles) particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            instance.transform.SetPositionAndRotation(position, prefab.transform.rotation);
            instance.transform.localScale = prefab.transform.localScale * scale;
            instance.SetActive(true);
            foreach (var particle in particles) particle.Play(false);
        }

        /// <summary>A hidden copy of <paramref name="prefab"/> whose particles each play once (no loop).</summary>
        public static GameObject CreateOneShot(GameObject prefab, Transform parent, out ParticleSystem[] particles)
        {
            var instance = Object.Instantiate(prefab, parent);
            instance.SetActive(false);
            particles = instance.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var particle in particles)
            {
                var main = particle.main;
                main.loop = false;
                main.stopAction = ParticleSystemStopAction.None;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }
            return instance;
        }
    }
}
