using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NhiQuoc.Tools
{
    public sealed class HeroAnimationDisplay : MonoBehaviour
    {
        [Serializable]
        public sealed class AttackEffect
        {
            public GameObject prefab;
            public Transform attachment;
            public Vector3 position;
            public Vector3 eulerAngles;
            public Vector3 scale = Vector3.one;
            [Tooltip("Clip frame to start FX. Negative frames prewarm FX before the animation starts.")]
            public int triggerFrame;
            [HideInInspector] public float normalizedDelay;
            public GameObject[] embeddedInstances = Array.Empty<GameObject>();
        }

        [Serializable]
        public sealed class Hero
        {
            public string name;
            public Animator animator;
            public Transform displayRoot;
            public AnimationClip[] clips = new AnimationClip[5];
            public AnimationClip alternateAttack;
            public AttackEffect[] attackEffects = Array.Empty<AttackEffect>();
            public AttackEffect[] alternateAttackEffects = Array.Empty<AttackEffect>();
            public AttackEffect[] ultiEffects = Array.Empty<AttackEffect>();
            [NonSerialized] public AttackEffect[] pendingEffects;
            [NonSerialized] public bool[] effectStarted;
            [NonSerialized] public List<GameObject> liveEffects = new List<GameObject>();
            [NonSerialized] public bool nextAttackIsAlternate;
            [NonSerialized] public AnimationClip activeClip;
            [NonSerialized] public PlayableGraph graph;
            [NonSerialized] public AnimationClipPlayable playable;
            [NonSerialized] public Vector3 position;
            [NonSerialized] public Quaternion rotation;
            [NonSerialized] public double elapsed;
        }

        public Hero[] heroes = Array.Empty<Hero>();
        public float rotationSpeed = 45f;
        public bool spinning;
        private int current;
        private static readonly string[] States = { "Idle", "Run", "Attack", "Death", "Ulti" };

        private void OnEnable()
        {
            foreach (var hero in heroes)
            {
                if (!hero.animator) continue;
                hero.nextAttackIsAlternate = false;
                if (!hero.displayRoot) hero.displayRoot = hero.animator.transform;
                hero.position = hero.displayRoot.localPosition;
                hero.rotation = hero.displayRoot.localRotation;
                hero.animator.applyRootMotion = false;
                // This preview owns FX playback; animation events must not spawn a second copy.
                hero.animator.fireEvents = false;
                hero.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                hero.graph = PlayableGraph.Create("Display " + hero.name);
                hero.graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationPlayableOutput.Create(hero.graph, "Animation", hero.animator);
            }
            Play(0);
        }

        public void Play(int state)
        {
            if (state < 0 || state >= States.Length) return;
            current = state;
            foreach (var hero in heroes)
            {
                if (!hero.graph.IsValid() || hero.clips == null || hero.clips.Length <= state || !hero.clips[state]) continue;
                ClearEffects(hero);
                if (hero.playable.IsValid()) hero.graph.DestroyPlayable(hero.playable);
                var clip = hero.clips[state];
                bool alternate = state == 2 && hero.alternateAttack && hero.nextAttackIsAlternate;
                if (state == 2 && hero.alternateAttack)
                {
                    if (hero.nextAttackIsAlternate) clip = hero.alternateAttack;
                    hero.nextAttackIsAlternate = !hero.nextAttackIsAlternate;
                }
                hero.activeClip = clip;
                hero.elapsed = 0;
                if (state == 2 || state == 4)
                {
                    hero.pendingEffects = state == 4 ? hero.ultiEffects :
                        (alternate ? hero.alternateAttackEffects : hero.attackEffects);
                    hero.effectStarted = new bool[hero.pendingEffects.Length];
                }
                hero.playable = AnimationClipPlayable.Create(hero.graph, clip);
                hero.playable.SetApplyFootIK(false);
                ((AnimationPlayableOutput)hero.graph.GetOutput(0)).SetSourcePlayable(hero.playable);
                hero.graph.Play();
                hero.graph.Evaluate(0);
            }
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame) Play(0);
                if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame) Play(1);
                if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame) Play(2);
                if (keyboard.digit4Key.wasPressedThisFrame || keyboard.numpad4Key.wasPressedThisFrame) Play(3);
                if (keyboard.digit5Key.wasPressedThisFrame || keyboard.numpad5Key.wasPressedThisFrame) Play(4);
                if (keyboard.dKey.wasPressedThisFrame) spinning = !spinning;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            for (int i = 0; i < 5; i++)
                if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)) || Input.GetKeyDown((KeyCode)((int)KeyCode.Keypad1 + i))) Play(i);
            if (Input.GetKeyDown(KeyCode.D)) spinning = !spinning;
#endif
            foreach (var hero in heroes)
            {
                if (!hero.playable.IsValid()) continue;
                var clip = hero.activeClip;
                if (clip.length <= 0) continue;
                hero.elapsed += Time.deltaTime;
                double time = hero.elapsed;
                double poseTime = current <= 1 ? time % clip.length : Math.Min(time, Math.Max(0, clip.length - 0.0001));
                hero.playable.SetTime(poseTime);
                hero.graph.Evaluate(0);
                if ((current == 2 || current == 4) && hero.pendingEffects != null)
                {
                    for (int i = 0; i < hero.pendingEffects.Length; i++)
                    {
                        var effect = hero.pendingEffects[i];
                        if (hero.effectStarted[i] || time * clip.frameRate + 0.0001 < effect.triggerFrame) continue;
                        hero.effectStarted[i] = true;
                        if (!effect.prefab || !effect.attachment) continue;
                        // Instantiate inactive so playOnAwake and OnEnable cannot run at the
                        // prefab's old position before the preview initializes it.
                        var staging = new GameObject("FX staging");
                        staging.SetActive(false);
                        var instance = Instantiate(effect.prefab, staging.transform);
                        instance.transform.localPosition = effect.position;
                        instance.transform.localRotation = Quaternion.Euler(effect.eulerAngles);
                        instance.transform.localScale = effect.scale;
                        var particles = instance.GetComponentsInChildren<ParticleSystem>(true);
                        var subEmitters = new HashSet<ParticleSystem>();
                        foreach (var particle in particles)
                        {
                            var main = particle.main;
                            main.playOnAwake = false;
                            main.loop = false;
                            particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                            var sub = particle.subEmitters;
                            for (int s = 0; s < sub.subEmittersCount; s++)
                                subEmitters.Add(sub.GetSubEmitterSystem(s));
                        }
                        instance.transform.SetParent(effect.attachment, false);
                        instance.SetActive(true);
                        Destroy(staging);
                        foreach (var particle in particles)
                            if (particle.gameObject.activeInHierarchy && !subEmitters.Contains(particle) && !particle.isPlaying)
                            {
                                float age = Mathf.Max(0, (float)time - effect.triggerFrame / clip.frameRate);
                                if (age > 0) particle.Simulate(age, false, true, false);
                                particle.Play(false);
                            }
                        hero.liveEffects.Add(instance);
                        Destroy(instance, Mathf.Max(clip.length + 2f, current == 4 ? 10f : 5f));
                    }
                }
            }
        }

        private void LateUpdate()
        {
            foreach (var hero in heroes)
            {
                if (!hero.animator) continue;
                MuteEmbeddedEffects(hero.attackEffects);
                MuteEmbeddedEffects(hero.alternateAttackEffects);
                MuteEmbeddedEffects(hero.ultiEffects);
                var target = hero.displayRoot;
                target.localPosition = hero.position;
                if (spinning) hero.rotation *= Quaternion.Euler(0, rotationSpeed * Time.deltaTime, 0);
                target.localRotation = hero.rotation;
            }
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(10, 10, 610, 56), "1 Idle   2 Run   3 Attack   4 Death   5 Ulti   |   D: Display rotation");
            GUI.Label(new Rect(24, 40, 580, 24), "Current: " + States[current] + "   |   Rotation: " + (spinning ? "ON" : "OFF") + "   |   Press the same key to replay");
            var camera = Camera.main;
            if (!camera) return;
            foreach (var hero in heroes)
            {
                if (!hero.animator) continue;
                Vector3 screen = camera.WorldToScreenPoint(hero.displayRoot.position);
                if (screen.z > 0) GUI.Label(new Rect(screen.x - 65, Screen.height - screen.y + 8, 140, 24), hero.name);
            }
        }

        private void OnDisable()
        {
            foreach (var hero in heroes)
            {
                ClearEffects(hero);
                if (hero.graph.IsValid()) hero.graph.Destroy();
            }
        }

        private void ClearEffects(Hero hero)
        {
            hero.pendingEffects = null;
            foreach (var effect in hero.liveEffects)
                if (effect) { effect.SetActive(false); Destroy(effect); }
            hero.liveEffects.Clear();
        }

        private static void MuteEmbeddedEffects(AttackEffect[] effects)
        {
            if (effects == null) return;
            foreach (var effect in effects)
                foreach (var embedded in effect.embeddedInstances)
                    if (embedded && embedded.activeSelf) embedded.SetActive(false);
        }
    }
}
