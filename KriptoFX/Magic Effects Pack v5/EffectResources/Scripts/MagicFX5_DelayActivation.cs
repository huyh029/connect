using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MagicFX5
{
    public class MagicFX5_DelayActivation : MonoBehaviour
    {
        public GameObject GameObject;
        public float      Delay    = 1;
        public float      LifeTime = -1;

        
        void OnEnable()
        {
            if (!GameObject) return;
            GameObject.SetActive(false);
            Invoke("DelayActivate",               Delay);
            if (LifeTime > 0) Invoke("DelayDeactivate", Delay + LifeTime);
        }

        void OnDisable()
        {
            if (GameObject) GameObject.SetActive(false);
            CancelInvoke("DelayActivate");
            CancelInvoke("DelayDeactivate");
        }

        void DelayActivate()
        {
            if (GameObject) GameObject.SetActive(true);
        }

        void DelayDeactivate()
        {
            if (GameObject) GameObject.SetActive(false);
        }
    }
}
