using System;
using UnityEngine;

namespace RecoilPathfinding
{
    public enum HeroHitShape
    {
        [InspectorName("1. Hình quạt")] Cone = 3,
        [InspectorName("2. Hình tròn")] Circle = 0,
        [InspectorName("3. Vành khuyên")] Ring = 1,
        [InspectorName("4. Hình hộp")] Box = 2
    }
    public enum HeroHitAnchor
    {
        [InspectorName("Bám tâm tướng")] Caster = 0,
        [InspectorName("Điểm mục tiêu lúc tung chiêu")] Target = 1,
        [InspectorName("Bám điểm hiệu ứng VAT")] EffectSocket = 2,
        [InspectorName("Bám mục tiêu di chuyển")] FollowTarget = 3,
        [InspectorName("Đạn bay thẳng")] StraightProjectile = 4
    }
    public enum HeroHitForce { [InspectorName("Không")] None, [InspectorName("Hút vào tâm")] Pull, [InspectorName("Đẩy tỏa tròn")] RadialPush, [InspectorName("Đẩy về phía trước")] ForwardPush, [InspectorName("Hất tung")] KnockUp }
    public enum HeroHitMode { [InspectorName("Đánh cả vùng")] Instant, [InspectorName("Thanh sát thương quét")] Sweep }

    public static class HeroSkillTimeline
    {
        public static float AirborneHeight(float progress, float height, float initialHeight = 0)
        {
            float t = Mathf.Clamp01(progress);
            return Mathf.Max(0, initialHeight * (1 - t) + 4 * height * t * (1 - t));
        }
        public static int FrameAt(float elapsed, float duration, int frameCount) =>
            Mathf.FloorToInt(Mathf.Clamp01(elapsed / Mathf.Max(.001f, duration)) * Mathf.Max(0, frameCount - 1) + .0001f);

        public static int NextPulse(int current, int repeatFrames, int end)
        {
            long next = (long)current + repeatFrames;
            return repeatFrames > 0 && next <= end ? (int)next : int.MaxValue;
        }
    }

    [Serializable]
    public sealed class HeroHitZone
    {
        public string Name = "Vùng sát thương";
        public bool Enabled = true;
        public HeroHitShape Shape = HeroHitShape.Cone;
        public HeroHitMode Mode;
        public float SweepStartAngle = -60, SweepEndAngle = 60;
        [Range(1, 90)] public float SweepWidth = 6;
        public HeroHitAnchor Anchor = HeroHitAnchor.Caster;
        [Min(.01f)] public float ProjectileSpeed = 15;
        [Tooltip("Frame VAT đã bake, bắt đầu từ 0; không dùng frame của animation nguồn.")]
        public int StartFrame, EndFrame;
        [Tooltip("0 = đánh một lần. Giá trị dương = số frame giữa hai nhịp sát thương.")]
        public int RepeatFrames;
        public int EffectIndex;
        public Vector3 Offset;
        public float Yaw;
        [Min(.01f)] public float Radius = 3;
        [Min(0)] public float InnerRadius;
        public Vector2 BoxSize = new Vector2(4, 6);
        [Range(1, 360)] public float Angle = 90;
        [Min(0)] public float DamageMultiplier = 1;
        [Range(0, 1)] public float EdgeDamage = 1;
        public HeroHitForce Force;
        [Min(0)] public float ForceDistance = 3;
        [Min(.03f)] public float ForceDuration = .25f;
        [Min(0)] public float PushStartRadius;
        [Min(.01f)] public float PushEndRadius = 6;
        [Min(.01f)] public float KnockUpHeight = 4;
        [Min(.03f)] public float KnockUpDuration = .8f;

        public bool IsSweep => Mode == HeroHitMode.Sweep && Shape == HeroHitShape.Cone;
        public bool IsProjectile => Anchor == HeroHitAnchor.StraightProjectile;
        public float EngagementReach(float configuredRange)
        {
            if (Anchor == HeroHitAnchor.Target || Anchor == HeroHitAnchor.FollowTarget || IsProjectile)
                return Mathf.Max(.1f, configuredRange);
            float extent = Shape == HeroHitShape.Box ? BoxSize.magnitude * .5f : Radius;
            float reach = new Vector2(Offset.x, Offset.z).magnitude + Mathf.Max(.01f, extent);
            return Mathf.Max(.1f, Mathf.Min(configuredRange, reach));
        }
        public float ProjectileTravel(float frame, float duration, int frames) =>
            Mathf.Max(0, frame - StartFrame) * duration / Mathf.Max(1, frames - 1) * Mathf.Max(.01f, ProjectileSpeed);
        public bool ContainsProjectileSegment(Vector3 local, float length, float bodyRadius)
        {
            float z = local.z - Mathf.Clamp(local.z, -Mathf.Max(0, length), 0);
            return local.x * local.x + z * z <= Mathf.Pow(Mathf.Max(.01f, Radius) + Mathf.Max(0, bodyRadius), 2);
        }
        public float RadialPushDistance(float radius)
        {
            float start = Mathf.Max(0, PushStartRadius), end = Mathf.Max(start, PushEndRadius);
            return radius >= start && radius < end ? end - radius : 0;
        }
        public Vector3 OriginPosition(Vector3 caster, Vector3 target) =>
            Anchor == HeroHitAnchor.Target || Anchor == HeroHitAnchor.FollowTarget ? target : caster;
        public float SweepAngleAt(float frame) => Mathf.Lerp(SweepStartAngle, SweepEndAngle,
            Mathf.Clamp01((frame - StartFrame) / Mathf.Max(1, EndFrame - StartFrame)));
        public float SweepAngularSpeed(float clipDuration, int clipFrames) =>
            (SweepEndAngle - SweepStartAngle) / Mathf.Max(.001f,
                (EndFrame - StartFrame) * clipDuration / Mathf.Max(1, clipFrames - 1));

        public void SweepSample(int frame, out float yaw, out float width)
        {
            float current = SweepAngleAt(frame), previous = SweepAngleAt(Mathf.Max(StartFrame, frame - 1));
            yaw = (current + previous) * .5f;
            width = Mathf.Min(360, Mathf.Max(1, SweepWidth) + Mathf.Abs(current - previous));
        }

        // Ground-plane collision expanded by the victim's occupied body radius.
        public bool Contains(Vector3 local, float bodyRadius = 0, float angleOverride = -1)
        {
            var p = new Vector2(local.x, local.z);
            float r = Mathf.Max(0, bodyRadius), distance = p.magnitude;
            if (Shape == HeroHitShape.Box)
                return Mathf.Abs(p.x) <= Mathf.Abs(BoxSize.x) * .5f + r &&
                    Mathf.Abs(p.y) <= Mathf.Abs(BoxSize.y) * .5f + r;
            if (distance > Radius + r) return false;
            if (Shape == HeroHitShape.Ring) return distance + r >= Mathf.Min(InnerRadius, Radius);
            float angle = angleOverride >= 0 ? angleOverride : Angle;
            if (Shape != HeroHitShape.Cone || distance <= r || angle >= 360) return true;
            float allowance = Mathf.Asin(Mathf.Clamp01(r / distance)) * Mathf.Rad2Deg;
            return Vector2.Angle(Vector2.up, p) <= angle * .5f + allowance;
        }

        public float DamageScale(Vector3 local)
        {
            float distance = Shape == HeroHitShape.Box ? Mathf.Max(
                Mathf.Abs(local.x) / Mathf.Max(.01f, BoxSize.x * .5f),
                Mathf.Abs(local.z) / Mathf.Max(.01f, BoxSize.y * .5f)) :
                new Vector2(local.x, local.z).magnitude / Mathf.Max(.01f, Radius);
            return DamageMultiplier * Mathf.Lerp(1, EdgeDamage, Mathf.Clamp01(distance));
        }
    }

    [Serializable]
    public sealed class HeroSkillClip
    {
        public int ClipIndex = 2;
        public HeroHitZone[] Zones = Array.Empty<HeroHitZone>();
    }

    [Serializable]
    public sealed class HeroSkillProfile
    {
        public int HeroId;
        public string Name;
        public bool Enabled;
        public HeroSkillClip[] Clips = Array.Empty<HeroSkillClip>();
        public HeroSkillClip Find(int index) => Array.Find(Clips, c => c != null && c.ClipIndex == index);
    }

    [CreateAssetMenu(menuName = "Nhị Quốc/Thư viện kỹ năng tướng")]
    public sealed class RecoilHeroSkillLibrary : ScriptableObject
    {
        public HeroSkillProfile[] Heroes = Array.Empty<HeroSkillProfile>();
        public HeroSkillProfile Find(int id) => Array.Find(Heroes, h => h != null && h.HeroId == id && h.Enabled);
    }
}
