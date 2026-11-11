using System;
using System.Collections.Generic;
using UnityEngine;

namespace MagicFX5
{
    public class MagicFX5_AnimatorSpeedCurve : MagicFX5_IScriptInstance
    {
        public AnimatorSpeedModeEnum AnimatorSpeedMode = AnimatorSpeedModeEnum.SpeedOverLifetime;
        public MagicFX5_EffectSettings    OverrideToUseTarget;

        public AnimationCurve SpeedCurve               = AnimationCurve.EaseInOut(0, 1, 1, 0);
        public float          SpeedMultiplier          = 1;
        public float          SpeedCurveLifeTime       = 1;
        public float          StartNormalizeTimeOffset = -1;

        private List<AnimatorState> _animatorStates = new List<AnimatorState>();


        class AnimatorState
        {
            public Animator Animator;
            public float    InitialSpeed;
            public float    AnimationLeftTime = 0;
            public bool     IsActive;

            public AnimatorState(Animator anim, float startNormalizeTimeOffset)
            {
                Animator          = anim;
                InitialSpeed      = anim.speed;
                AnimationLeftTime = 0;
                IsActive          = true;
                if (startNormalizeTimeOffset > 0) Animator.Play(0, -1, startNormalizeTimeOffset);
            }

            public void ResetState()
            {
                if (Animator != null) Animator.speed = InitialSpeed;
            }
        }


        public enum AnimatorSpeedModeEnum
        {
            SpeedOverLifetime,
            SpeedOverNormalizedAnimationTime
        }

        internal override void OnEnableExtended()
        {
            _animatorStates.Clear();
           
            if (OverrideToUseTarget != null) OverrideToUseTarget.OnEffectCollisionEnter += OnEffectCollisionEnter;
            else AddAnimator(GetComponent<Animator>());
        }

        private void AddAnimator(Animator animator)
        {
            if (!animator || _animatorStates.Exists(state => state.Animator == animator)) return;
            _animatorStates.Add(new AnimatorState(animator, StartNormalizeTimeOffset));
        }

        private void OnEffectCollisionEnter(MagicFX5_EffectSettings.EffectCollisionHit hitInfo)
        {
            if (!hitInfo.Target) return;
            var currentAnimator = hitInfo.Target.GetComponent<Animator>();
            if (!currentAnimator) currentAnimator = hitInfo.Target.GetComponentInParent<Animator>();
            AddAnimator(currentAnimator);
        }

        internal override void OnDisableExtended()
        {
            if (OverrideToUseTarget != null) OverrideToUseTarget.OnEffectCollisionEnter -= OnEffectCollisionEnter;

            if (AnimatorSpeedMode == AnimatorSpeedModeEnum.SpeedOverLifetime)
            {
                foreach (var anim in _animatorStates) anim.ResetState();
            }
            
        }

        internal override void ManualUpdate()
        {
            switch (AnimatorSpeedMode)
            {
                case AnimatorSpeedModeEnum.SpeedOverLifetime:
                {
                    var time = Time.deltaTime;
                    foreach (var state in _animatorStates)
                    {
                        if (!state.IsActive || !state.Animator) continue;
                        if (state.AnimationLeftTime > SpeedCurveLifeTime)
                        {
                            state.IsActive = false;
                            continue;
                        }

                        state.AnimationLeftTime += time;
                        var evalTime     = Mathf.Clamp01(state.AnimationLeftTime / SpeedCurveLifeTime);
                        var currentSpeed = SpeedCurve.Evaluate(evalTime);
                        state.Animator.speed = currentSpeed;
                    }
                    break;
                }
                case AnimatorSpeedModeEnum.SpeedOverNormalizedAnimationTime:
                {
                    if (_animatorStates.Count == 0 || !_animatorStates[0].Animator) break;
                    var info = _animatorStates[0].Animator.GetCurrentAnimatorStateInfo(0);
                    var eval = SpeedCurve.Evaluate(Mathf.Clamp01(info.normalizedTime));
                    _animatorStates[0].Animator.speed = eval * SpeedMultiplier;
                    break;
                }
            }
        }
    }
}
