using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.VFX;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NhiQuoc.Tools
{
    [DefaultExecutionOrder(10000)]
    public sealed class HeroVatPlayer : MonoBehaviour
    {
        [Serializable] public sealed class Effect
        {
            public GameObject prefab;
            public int triggerFrame;
            public Vector3 position, rotation, scale;
            public Vector3[] socketPositions, socketScales;
            public Quaternion[] socketRotations;
        }
        [Serializable] public sealed class Clip
        {
            public string name;
            public int startFrame, frameCount;
            public float duration, sourceFps;
            public bool loop;
            [Tooltip("Hiệu ứng tuyệt kỹ bám tướng thay vì đặt tại mục tiêu.")]
            public bool casterCentered;
            public bool targetCentered;
            public bool straightProjectile;
            public Effect[] effects = Array.Empty<Effect>();
        }
        public Renderer[] parts;
        // Idle, Run, Attack1, Death, Ulti, Attack2 (optional).
        public Clip[] clips;
        public bool keyboardPreview;
        public float referenceBodyHeight;
        public float targetBodyHeight = 6f;
        private int state;
        private bool alternate, spinning;
        private float started;
        private Quaternion attackFacing;
        private float facingLockedUntil;
        private Vector3 pendingTarget, clipTarget;
        private Transform pendingTargetTransform, clipTargetTransform;
        private bool hasTarget;
        private MaterialPropertyBlock properties;
        [Serializable] private sealed class CachedEffect
        {
            public string key;
            public GameObject prefab;
            public GameObject socket, instance;
            [NonSerialized] public Effect effect;
            public ParticleSystem[] particles, emitters;
            public TrailRenderer[] trails;
            public AudioSource[] audio;
            public VisualEffect[] graphs;
            public float expires;
            public int clipIndex;
            public float clipStarted;
            public Quaternion rootFacing;
            public Vector3 worldOrigin;
            public bool fixedOrigin;
            public Transform targetAnchor;
            public MagicFX5_EffectSettings[] targetSettings;
            public MagicFX5.MagicFX5_FollowTarget[] followers;
        }
        // Serialize the cache so a domain reload does not leave disabled children
        // behind while replacing the dictionary that used to own them.
        [SerializeField, HideInInspector] private List<CachedEffect> pool = new List<CachedEffect>();
        private readonly List<CachedEffect> live = new List<CachedEffect>();
        private bool[] emitted;
        private bool idleFeetAligned;
        public int ActiveClipIndex => state;
        public int NextAttackClipIndex => clips != null && clips.Length > 5 && alternate ? 5 : 2;
        public bool ExternalProjectileEffects { get; set; }

        public void SamplePreview(int index, float seconds)
        {
            if (clips == null || index < 0 || index >= clips.Length) return;
            if (!idleFeetAligned) { AlignIdleFeetToOrigin(); idleFeetAligned = true; }
            var clip = clips[index];
            properties ??= new MaterialPropertyBlock();
            properties.SetVector("_VatClip", new Vector4(clip.startFrame, clip.frameCount, clip.duration, 0));
            properties.SetVector("_VatPlayback", new Vector4(Mathf.Clamp(seconds, 0, clip.duration), 0, 0, 1));
            foreach (var part in parts) if (part) part.SetPropertyBlock(properties);
        }

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            if (parts != null)
                foreach (var part in parts)
                    if (part != null) part.shadowCastingMode = ShadowCastingMode.On;
            PrepareForPool();
            Play(0);
        }

        public void PrepareForPool()
        {
            ExternalProjectileEffects = false;
            if (!idleFeetAligned)
            {
                AlignIdleFeetToOrigin();
                idleFeetAligned = true;
            }
            foreach (var item in pool) ResetEffect(item);
            if (clips != null)
                for (int c = 0; c < clips.Length; c++)
                    for (int e = 0; e < clips[c].effects.Length; e++)
                    {
                        var fx = clips[c].effects[e];
                        if (fx.prefab) GetEffect(fx, c + ":" + e, true);
                    }
        }

        private void AlignIdleFeetToOrigin()
        {
            // VAT mesh vertices contain the first Idle frame. Renderer.bounds covers
            // every animation (including weapons), so it cannot locate the feet.
            MeshFilter body = null;
            if (parts != null) foreach (var part in parts)
            {
                var mesh = part != null ? part.GetComponent<MeshFilter>() : null;
                if (mesh != null && mesh.sharedMesh != null && mesh.sharedMesh.isReadable &&
                    (body == null || mesh.sharedMesh.vertexCount > body.sharedMesh.vertexCount)) body = mesh;
            }
            if (body == null) return;
            var matrix = transform.worldToLocalMatrix * body.transform.localToWorldMatrix;
            float bottom = float.PositiveInfinity;
            foreach (var vertex in body.sharedMesh.vertices) bottom = Mathf.Min(bottom, matrix.MultiplyPoint3x4(vertex).y);
            if (!float.IsFinite(bottom) || Mathf.Abs(bottom) < .00001f) return;
            var correction = Vector3.down * bottom;
            var worldCorrection = transform.TransformVector(correction);
            foreach (var part in parts) if (part != null) part.transform.position += worldCorrection;
            if (clips != null) foreach (var clip in clips)
                if (clip.effects != null) foreach (var effect in clip.effects)
                {
                    if (effect.socketPositions != null)
                        for (int frame = 0; frame < effect.socketPositions.Length; frame++) effect.socketPositions[frame] += correction;
                }
        }
        public void SetAttackTarget(Vector3 worldPosition, Transform targetTransform = null)
        {
            pendingTarget = worldPosition;
            pendingTargetTransform = targetTransform;
            hasTarget = true;
        }

        public void UpdateActiveEffectTarget(Vector3 worldPosition)
        {
            clipTarget = worldPosition;
            foreach (var item in live)
            {
                if (item.clipIndex != state || clips[item.clipIndex].casterCentered || clips[item.clipIndex].straightProjectile) continue;
                if (item.fixedOrigin) item.worldOrigin = worldPosition;
                if (item.targetAnchor != null) item.targetAnchor.position = worldPosition;
            }
        }

        public void Play(int index)
        {
            if (clips == null || index < 0 || index >= clips.Length) return;
            if (index == 3)
            {
                foreach (var item in live) ResetEffect(item);
                live.Clear();
            }
            if (index == 2 && clips.Length > 5) { if (alternate) index = 5; alternate = !alternate; }
            state = index; started = Time.time;
            var clip = clips[index];
            if (emitted == null || emitted.Length < clip.effects.Length)
                emitted = new bool[clip.effects.Length];
            else Array.Clear(emitted, 0, emitted.Length);
            clipTarget = hasTarget ? pendingTarget : transform.position;
            clipTargetTransform = pendingTargetTransform;
            if (index == 2 || index == 4 || index == 5)
            {
                attackFacing = transform.rotation;
                facingLockedUntil = started + clip.duration;
            }
            else facingLockedUntil = 0;
            properties ??= new MaterialPropertyBlock();
            properties.SetVector("_VatClip", new Vector4(clip.startFrame, clip.frameCount, clip.duration, clip.loop ? 1 : 0));
            properties.SetVector("_VatPlayback", new Vector4(started, 1, 0, 0));
            foreach (var part in parts) if (part) part.SetPropertyBlock(properties);
        }
        private void Update()
        {
            if (Time.time < facingLockedUntil) transform.rotation = attackFacing;
            if (keyboardPreview)
            {
#if ENABLE_INPUT_SYSTEM
                var k = Keyboard.current;
                if (k != null)
                {
                    if (k.digit1Key.wasPressedThisFrame) Play(0);
                    if (k.digit2Key.wasPressedThisFrame) Play(1);
                    if (k.digit3Key.wasPressedThisFrame) Play(2);
                    if (k.digit4Key.wasPressedThisFrame) Play(3);
                    if (k.digit5Key.wasPressedThisFrame) Play(4);
                    if (k.dKey.wasPressedThisFrame) spinning = !spinning;
                }
#elif ENABLE_LEGACY_INPUT_MANAGER
                for (int i = 0; i < 5; i++) if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i))) Play(i);
                if (Input.GetKeyDown(KeyCode.D)) spinning = !spinning;
#endif
            }
            if (spinning) transform.Rotate(0, 45 * Time.deltaTime, 0);
            if (clips == null || clips.Length == 0 || emitted == null) return;
            var clip = clips[state];
            if (clip.targetCentered && clipTargetTransform != null)
                UpdateActiveEffectTarget(clipTargetTransform.position);
            float elapsed = Time.time - started;
            float sample = Mathf.Clamp01(elapsed / Mathf.Max(.001f, clip.duration)) * (clip.frameCount - 1);
            for (int i = 0; i < clip.effects.Length; i++)
            {
                var fx = clip.effects[i];
                if (emitted[i] || elapsed * clip.sourceFps < fx.triggerFrame) continue;
                emitted[i] = true;
                if (clip.straightProjectile && ExternalProjectileEffects) continue;
                if (!fx.prefab) continue;
                var cached = GetEffect(fx, state + ":" + i);
                if (cached == null) continue;
                var socket = cached.socket;
                var instance = cached.instance;
                ResetEffect(cached);
                live.Remove(cached);
                cached.clipIndex = state;
                cached.clipStarted = started;
                cached.rootFacing = Time.time < facingLockedUntil ? attackFacing : transform.rotation;
                cached.fixedOrigin = state == 4 && !clip.casterCentered || clip.targetCentered || clip.straightProjectile;
                cached.worldOrigin = clip.straightProjectile ? transform.position + cached.rootFacing *
                    Vector3.Scale(fx.socketPositions != null && fx.socketPositions.Length > 0 ?
                        fx.socketPositions[Mathf.Clamp((int)sample, 0, fx.socketPositions.Length - 1)] : Vector3.zero, transform.lossyScale) :
                    cached.fixedOrigin ? clipTarget : transform.position;
                if (cached.targetAnchor != null)
                {
                    cached.targetAnchor.position = clip.casterCentered ? transform.position : clipTarget;
                    foreach (var settings in cached.targetSettings)
                        if (settings != null)
                        {
                            settings.Targets = new[] { cached.targetAnchor };
                            settings.TargetCenterHeightOffset = 0;
                        }
                    foreach (var follower in cached.followers)
                        if (follower != null && follower.Target == null) follower.Target = cached.targetAnchor;
                }
                SetEffectSocket(cached, sample);
                instance.transform.localPosition = cached.fixedOrigin && !clip.straightProjectile ? Vector3.zero : fx.position;
                instance.transform.localRotation = Quaternion.Euler(fx.rotation);
                instance.transform.localScale = fx.scale;
                instance.SetActive(true);
                socket.SetActive(true);
                foreach (var graph in cached.graphs)
                {
                    if (!graph) continue;
                    graph.Reinit();
                    float age = Mathf.Max(0, elapsed - fx.triggerFrame / clip.sourceFps);
                    if (age > 0) graph.Simulate(1f / 30f, (uint)Mathf.FloorToInt(age * 30f));
                }
                foreach (var particle in cached.emitters)
                    if (particle && particle.gameObject.activeInHierarchy && !particle.isPlaying)
                    {
                        float age = Mathf.Max(0, elapsed - fx.triggerFrame / clip.sourceFps);
                        if (age > 0) particle.Simulate(age, false, true, false);
                        particle.Play(false);
                    }
                float lifetime = Mathf.Max(.5f, clip.duration);
                foreach (var particle in cached.particles)
                    if (particle)
                    {
                        var main = particle.main;
                        lifetime = Mathf.Max(lifetime, main.startDelay.constantMax + main.duration + main.startLifetime.constantMax);
                    }
                cached.expires = Time.time + lifetime + .5f;
                live.Add(cached);
            }
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var item = live[i];
                if (!item.socket || Time.time >= item.expires)
                { ResetEffect(item); live.RemoveAt(i); continue; }
                var sourceClip = clips[item.clipIndex];
                float sourceSample = Mathf.Clamp01((Time.time - item.clipStarted) / Mathf.Max(.001f, sourceClip.duration)) * (sourceClip.frameCount - 1);
                SetEffectSocket(item, sourceSample);
            }
        }

        private void LateUpdate()
        {
            if (Time.time < facingLockedUntil) transform.rotation = attackFacing;
            foreach (var item in live)
            {
                if (!item.socket) continue;
                var clip = clips[item.clipIndex];
                float sample = Mathf.Clamp01((Time.time - item.clipStarted) / Mathf.Max(.001f, clip.duration)) * (clip.frameCount - 1);
                SetEffectSocket(item, sample);
            }
        }

        private void SetEffectSocket(CachedEffect item, float sample)
        {
            if (clips[item.clipIndex].casterCentered && item.targetAnchor != null)
                item.targetAnchor.position = transform.position;
            var socket = item.socket.transform;
            SetSocket(socket, item.effect, sample);
            if (item.fixedOrigin)
            {
                // A target-space effect is centered on the hit point, not the caster's hand.
                socket.SetPositionAndRotation(item.worldOrigin, item.rootFacing);
                return;
            }
            var localPosition = socket.localPosition;
            var localRotation = socket.localRotation;
            // Follow the baked attachment track, using the facing captured at emission
            // instead of inheriting subsequent turns of the hero root.
            var origin = item.fixedOrigin ? item.worldOrigin : transform.position;
            socket.SetPositionAndRotation(origin + item.rootFacing * Vector3.Scale(localPosition, transform.lossyScale),
                item.rootFacing * localRotation);
        }

        private CachedEffect GetEffect(Effect fx, string key, bool allowCreate = false)
        {
            var cached = pool.Find(item => item.key == key);
            if (cached != null && cached.socket && cached.instance && cached.prefab == fx.prefab)
            { cached.effect = fx; return cached; }
            if (!allowCreate)
            {
                Debug.LogWarning("VAT FX slot " + key + " was destroyed by an external component. Skipping it rather than generating more FX. Disable/enable the hero to rebuild the pool.", this);
                return null;
            }
            // Allocate all slots during initialization, never inside an attack.
            var socket = cached != null && cached.socket ? cached.socket : new GameObject("VAT FX [" + key + "] - " + fx.prefab.name);
            socket.transform.SetParent(transform, false);
            socket.SetActive(false);
            var instance = Instantiate(fx.prefab, socket.transform);
            var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
            var subEmitters = new HashSet<ParticleSystem>();
            var emitters = new List<ParticleSystem>();
            foreach (var particle in particles)
            {
                var main = particle.main;
                // Preserve authoring playback: delayed child activation relies on
                // Play On Awake, and looping layers are stopped by the pool lifetime.
                main.stopAction = ParticleSystemStopAction.None;
                var sub = particle.subEmitters;
                for (int s = 0; s < sub.subEmittersCount; s++) subEmitters.Add(sub.GetSubEmitterSystem(s));
            }
            foreach (var particle in particles)
            {
                var renderer = particle.GetComponent<ParticleSystemRenderer>();
                if (!subEmitters.Contains(particle) && (particle.emission.enabled || !renderer || renderer.enabled)) emitters.Add(particle);
            }
            var replacement = new CachedEffect { key = key, prefab = fx.prefab, socket = socket, instance = instance, effect = fx, particles = particles,
                emitters = emitters.ToArray(), trails = instance.GetComponentsInChildren<TrailRenderer>(true),
                audio = instance.GetComponentsInChildren<AudioSource>(true), graphs = instance.GetComponentsInChildren<VisualEffect>(true) };
            replacement.targetSettings = instance.GetComponentsInChildren<MagicFX5_EffectSettings>(true);
            replacement.followers = instance.GetComponentsInChildren<MagicFX5.MagicFX5_FollowTarget>(true);
            if (key.StartsWith("4:", StringComparison.Ordinal) || clips[int.Parse(key.Split(':')[0])].targetCentered)
            {
                var anchor = new GameObject("VAT Ulti Target [" + key + "]");
                anchor.transform.SetParent(transform.parent, false);
                replacement.targetAnchor = anchor.transform;
            }
            if (cached != null) pool.Remove(cached);
            pool.Add(replacement);
            return replacement;
        }

        private static void ResetEffect(CachedEffect item)
        {
            foreach (var particle in item.particles)
                if (particle) particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            foreach (var trail in item.trails) if (trail) trail.Clear();
            foreach (var audio in item.audio) if (audio) audio.Stop();
            if (item.graphs != null) foreach (var graph in item.graphs) if (graph) graph.Stop();
            if (item.socket) item.socket.SetActive(false);
        }
        private static void SetSocket(Transform target, Effect fx, float sample)
        {
            int a = Mathf.Clamp((int)sample, 0, fx.socketPositions.Length - 1);
            int b = Mathf.Min(a + 1, fx.socketPositions.Length - 1);
            float t = sample - a;
            target.localPosition = Vector3.Lerp(fx.socketPositions[a], fx.socketPositions[b], t);
            target.localRotation = Quaternion.Slerp(fx.socketRotations[a], fx.socketRotations[b], t);
            target.localScale = Vector3.Lerp(fx.socketScales[a], fx.socketScales[b], t);
        }
        private void OnDisable()
        {
            foreach (var item in live) ResetEffect(item);
            live.Clear();
        }
        private void OnDestroy()
        {
            foreach (var item in pool)
                if (item.targetAnchor != null) Destroy(item.targetAnchor.gameObject);
        }
    }
}
