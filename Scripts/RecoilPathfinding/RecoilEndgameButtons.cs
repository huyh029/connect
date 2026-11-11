using UnityEngine;
using UnityEngine.SceneManagement;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {


        private bool _endgameLeaving;
        private readonly float[] _endgameButtonScale = { 1f, 1f };

        private void DrawEndgameButtons(float reveal, float scale)
        {
            var t = Mathf.Clamp01((reveal - 2.8f) / .65f);
            if (t <= 0) return;
            var y = (950f + (1f - EndgameBack(t)) * 120f) * scale;
            var buttonWidth = 290f * scale;
            var buttonHeight = 110f * scale;
            var gap = 45f * scale;
            var left = (Screen.width - buttonWidth * 2 - gap) * .5f;
            var oldColor = GUI.color;
            var oldMatrix = GUI.matrix;
            var oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && !_endgameLeaving && t >= .95f;
            for (var i = 0; i < 2; i++)
            {
                var rect = new Rect(left + i * (buttonWidth + gap), y, buttonWidth, buttonHeight);
                var hover = GUI.enabled && rect.Contains(Event.current.mousePosition);
                if (Event.current.type == EventType.Repaint)
                    _endgameButtonScale[i] = Mathf.Lerp(_endgameButtonScale[i], hover ? 1.055f : 1f,
                        1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
                GUI.matrix = oldMatrix;
                GUIUtility.ScaleAroundPivot(Vector2.one * _endgameButtonScale[i], rect.center);
                GUI.color = new Color(1, 1, 1, t);
                var texture = i == 0 ? endgameRetryImage : endgameHomeImage;
                if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
                if (GUI.Button(rect, texture != null ? GUIContent.none : new GUIContent(i == 0 ? "RETRY" : "HOME"),
                    texture != null ? GUIStyle.none : GUI.skin.button))
                    ReloadFromEndgame(i == 0);
            }
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
            GUI.enabled = oldEnabled;
        }

        private void ReloadFromEndgame(bool retry)
        {
            if (_endgameLeaving) return;
            if (retry)
            {
                BeginRetryInPlace();
                return;
            }
            var scene = gameObject.scene;
            if (string.IsNullOrEmpty(scene.path))
            { Debug.LogWarning("Hãy lưu scene trước khi dùng Retry / Home.", this); return; }
            _endgameLeaving = true;


            RestoreEndgameLighting();
            try
            {
#if UNITY_EDITOR
                if (scene.buildIndex < 0)
                    UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(scene.path,
                        new LoadSceneParameters(LoadSceneMode.Single));
                else
#endif
                    SceneManager.LoadSceneAsync(scene.path, LoadSceneMode.Single);
            }
            catch (System.Exception exception)
            {
                _endgameLeaving = false;

                Debug.LogException(exception, this);
            }
        }
    }
}
