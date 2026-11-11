using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if USING_CINEMACHINE
using Unity.Cinemachine;
#endif

namespace MagicFX5
{
    public class MagicFX5_CinemachineImpulseTrigger : MonoBehaviour
    {
        public MagicFX5_EffectSettings EffectSettings;
#if USING_CINEMACHINE
        private Coroutine _initialization;
        void OnEnable()
        {
            if (!EffectSettings) EffectSettings = GetComponentInParent<MagicFX5_EffectSettings>(true);
            if (EffectSettings && EffectSettings.UseCameraShakeCinemachine)
                _initialization = StartCoroutine(OneFrameDelayInitialization());
        }

        void OnDisable()
        {
            if (_initialization != null) StopCoroutine(_initialization);
            _initialization = null;
        }

        IEnumerator OneFrameDelayInitialization()
        {
            yield return null; //1 frame delay for correct shaking initialization by time, else unity call Instantiate -> OnEnable -> Shake(!) -> Start Delay script -> Shake again later. 
            
            var source = GetComponent<CinemachineImpulseSource>();
            if (source && EffectSettings && EffectSettings.UseCameraShakeCinemachine) source.GenerateImpulse(1);
            _initialization = null;
        }

#endif
    }
}
