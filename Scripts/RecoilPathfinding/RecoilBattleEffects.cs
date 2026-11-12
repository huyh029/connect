using UnityEngine;

namespace RecoilPathfinding
{
    public sealed class RecoilBattleEffects : ScriptableObject
    {
        public GameObject SiegeImpactPrefab;

        [Header("Tiếng nổ")]
        [Tooltip("Tiếng nổ (đá máy bắn đá, thiên thạch cuối trận)")] public AudioClip ExplosionSound;
        [Range(0f, 3f), Tooltip("Âm lượng tiếng nổ của đá máy bắn đá (trên 1 = khuếch đại)")] public float SiegeExplosionVolume = .9f;
        [Min(0f), Tooltip("Hai tiếng nổ đá cách nhau ít nhất chừng này giây (nhiều máy bắn cùng lúc không bị ồn)")]
        public float SiegeExplosionMinInterval = .15f;
        [Range(0f, 3f), Tooltip("Âm lượng tiếng nổ thiên thạch cuối trận (trên 1 = khuếch đại)")] public float EndgameExplosionVolume = 1.6f;

        [Header("Hất tung khi đá máy bắn đá nổ")]
        public bool SiegeBlastToss = true;
        [Min(0f), Tooltip("Độ cao tối đa (lính ở tâm nổ)")] public float SiegeBlastHeight = 3f;
        [Min(0f), Tooltip("Quãng văng ra tối đa (lính ở tâm nổ)")] public float SiegeBlastDistance = 3f;
        [Min(.1f), Tooltip("Thời gian trên không (giây)")] public float SiegeBlastDuration = .8f;
        [Range(0f, 1f), Tooltip("Độ ngẫu nhiên: 0 = đều nhau, 1 = rất khác nhau")] public float SiegeBlastRandomness = .5f;
        [Range(0f, 90f), Tooltip("Lệch hướng văng ngẫu nhiên tối đa (độ)")] public float SiegeBlastAngleJitter = 30f;

        /// <summary>
        /// How a siege blast tosses one soldier (shared by the battle and the explosion test ground): outwards from the
        /// impact like a sphere, full strength at the centre and none at the edge of <paramref name="radius"/>, with
        /// randomness. False when it is not tossed.
        /// </summary>
        /// <param name="surfaceDistance">From the impact point to the soldier's body edge.</param>
        /// <param name="away">From the impact point to the soldier.</param>
        public bool SiegeToss(float surfaceDistance, float radius, Vector3 away, System.Random random,
            out float height, out Vector3 horizontal, out float duration)
        {
            height = 0f;
            horizontal = Vector3.zero;
            duration = 0f;
            if (!SiegeBlastToss || radius <= 0f) return false;
            var t = Mathf.Clamp01(surfaceDistance / radius);
            var strength = Mathf.Sqrt(1f - t * t);
            if (strength < .05f) return false;
            float Jitter(float amount) => 1f + ((float)random.NextDouble() * 2f - 1f) * amount;
            var randomness = Mathf.Clamp01(SiegeBlastRandomness);
            height = SiegeBlastHeight * strength * Jitter(randomness);
            var distance = SiegeBlastDistance * strength * Jitter(randomness);
            duration = Mathf.Max(.1f, SiegeBlastDuration * Mathf.Lerp(1f, .75f + .5f * strength, .5f) * Jitter(randomness * .4f));
            away.y = 0f;
            var direction = away.sqrMagnitude < .01f
                ? Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f) * Vector3.forward
                : Quaternion.Euler(0f, ((float)random.NextDouble() * 2f - 1f) * SiegeBlastAngleJitter, 0f) * away.normalized;
            horizontal = direction * distance;
            return true;
        }

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
