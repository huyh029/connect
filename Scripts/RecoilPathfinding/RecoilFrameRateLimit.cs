using UnityEngine;

namespace RecoilPathfinding
{
    public static class RecoilFrameRateLimit
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            // VSync on: frames are shown in step with the monitor, so turning the camera no longer tears the picture
            // into two halves moving at different speeds. The 60 cap stays as a fallback where VSync is not honoured
            // (the editor's Game view uses its own VSync toggle, and some drivers force it off).
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = 60;
        }
    }
}
