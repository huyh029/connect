using UnityEngine;
using UnityEngine.UI;

namespace RecoilPathfinding
{
    /// <summary>
    /// Home-screen (lobby) clock: a UGUI canvas holding the lobby background and the interactive clock
    /// (<see cref="RecoilClockDial"/>). The lobby title, buttons and cursor are IMGUI, which always draws on top of
    /// UGUI, so:
    /// <list type="bullet">
    /// <item>the lobby background is drawn by the canvas (under everything) instead of by OnGUI;</item>
    /// <item>with <see cref="lobbyClockOnTop"/>, the clock's UGUI copy is hidden and OnGUI redraws its images after
    /// the title and buttons (only the cursor stays above it), using the poses the dial gives them.</item>
    /// </list>
    /// Built by Game Tools > Công Thành > Clock > "Thêm đồng hồ vào sảnh".
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        [Header("Đồng hồ ở sảnh")]
        [SerializeField, Tooltip("Canvas chứa nền sảnh + đồng hồ; chỉ hiện khi đang ở sảnh (chưa bấm Play, không mở Settings).")]
        private Canvas lobbyClockCanvas;
        [SerializeField, Tooltip("Vẽ đồng hồ đè lên mọi thứ ở sảnh, kể cả logo và các nút (chỉ con trỏ chuột ở trên nó).")]
        private bool lobbyClockOnTop = true;
        [SerializeField, Min(.05f), Tooltip("Bấm Bắt đầu: các nút và đồng hồ bay ra khỏi màn hình trong chừng này giây, " +
                                           "chỉ còn logo và thanh loading.")]
        private float lobbyExitDuration = .6f;

        // The open clock with its TIME button stays within this many clock units of its center.
        private const float LobbyClockReach = 700f;
        private float _lobbyExitStart = -1f;

        private RectTransform _lobbyClockRoot;
        private RecoilClockDial _lobbyClockDial;
        private CanvasGroup _lobbyClockHider;
        private readonly Vector3[] _lobbyClockCorners = new Vector3[4];

        /// <summary>The canvas paints the lobby background itself, so OnGUI must not paint it over the clock.</summary>
        private bool LobbyClockDrawsBackdrop => lobbyClockCanvas != null && lobbyClockCanvas.gameObject.activeInHierarchy;

        /// <summary>True exactly when OnGUI shows the lobby home view (same checks as DrawBattleScreenGui).</summary>
        private bool LobbyHomeVisible =>
            !CapacityBenchmarkHidesUi &&
            !(_startCountdownEndsAt > 0 && !_combatStarted && !_matchFinished) &&
            _templatesValidated && !_matchFinished && !_initialFormationLoading && !_poolPrewarmRunning &&
            !_lobbyStarted && !_settingsOpen;

        /// <summary>Loading the match after Start was pressed, while the lobby is still on screen.</summary>
        private bool LobbyLoading => !_lobbyStarted && !_matchFinished && (_poolPrewarmRunning || _initialFormationLoading);

        /// <summary>0..1 through the lobby's exit animation; 0 while not loading.</summary>
        private float LobbyExitProgress =>
            _lobbyExitStart < 0f ? 0f : Mathf.Clamp01((Time.unscaledTime - _lobbyExitStart) / Mathf.Max(lobbyExitDuration, .05f));

        /// <summary>Exit easing (back-in): a small pull back, then away faster and faster.</summary>
        private float LobbyExitEased
        {
            get
            {
                const float back = 1.70158f;
                var t = LobbyExitProgress;
                return t * t * ((back + 1f) * t - back);
            }
        }

        private void UpdateLobbyExit()
        {
            if (!LobbyLoading) _lobbyExitStart = -1f;
            else if (_lobbyExitStart < 0f) _lobbyExitStart = Time.unscaledTime;
        }

        private void UpdateLobbyClock()
        {
            UpdateLobbyExit();
            if (lobbyClockCanvas == null) return;
            // While loading, the canvas stays up until the clock has flown off (it also paints the background).
            var leaving = LobbyLoading && LobbyExitProgress < 1f && !CapacityBenchmarkHidesUi;
            var show = LobbyHomeVisible || leaving;
            if (lobbyClockCanvas.gameObject.activeSelf != show)
                lobbyClockCanvas.gameObject.SetActive(show);
            if (_lobbyClockDial != null && _lobbyClockDial.enabled != LobbyHomeVisible)
                _lobbyClockDial.enabled = LobbyHomeVisible; // no clicks or hover while it flies off
            if (!show) return;
            // Same fit the IMGUI lobby uses for its title and buttons, so the clock keeps its place among them.
            lobbyClockCanvas.scaleFactor = RecoilLobbyLayout.FitScale(Screen.width, Screen.height);

            if (_lobbyClockRoot == null)
            {
                var clock = lobbyClockCanvas.GetComponentInChildren<RecoilClockImagePreview>(true);
                if (clock == null || clock.ClockRoot == null) return;
                _lobbyClockRoot = clock.ClockRoot;
                _lobbyClockDial = clock.GetComponent<RecoilClockDial>();
                _lobbyClockHider = _lobbyClockRoot.GetComponent<CanvasGroup>();
                if (_lobbyClockHider == null) _lobbyClockHider = _lobbyClockRoot.gameObject.AddComponent<CanvasGroup>();
                _lobbyClockHider.blocksRaycasts = false;
                _lobbyClockHider.interactable = false;
            }
            PlaceLobbyClock();
            // On top: the UGUI copy stays laid out and animated (the dial drives it) but is not drawn; OnGUI draws it.
            _lobbyClockHider.alpha = lobbyClockOnTop ? 0f : 1f;
        }

        /// <summary>Puts the clock where the layout says: its holder is centered on the canvas, so positions are design pixels.</summary>
        private void PlaceLobbyClock()
        {
            var holder = _lobbyClockRoot != null ? _lobbyClockRoot.parent as RectTransform : null;
            if (holder == null) return;
            var piece = lobbyLayout.clock;
            holder.anchorMin = holder.anchorMax = new Vector2(.5f, .5f);
            holder.pivot = new Vector2(.5f, .5f);
            holder.sizeDelta = Vector2.zero;
            var position = RecoilLobbyLayout.CanvasPosition(piece.position);
            var exit = LobbyExitEased;
            if (exit != 0f)
            {
                // Off to the left, until even the open clock's right side has passed the screen's left edge.
                var canvasHalfWidth = Screen.width / Mathf.Max(lobbyClockCanvas.scaleFactor, .0001f) * .5f;
                var away = -canvasHalfWidth - LobbyClockReach * Mathf.Abs(piece.scale.x) - 20f;
                position.x += (away - position.x) * exit;
            }
            holder.anchoredPosition = position;
            holder.localScale = new Vector3(piece.scale.x, piece.scale.y, 1f);
            holder.localRotation = Quaternion.Euler(0f, 0f, piece.rotation);
        }

        /// <summary>True when a lobby button under the clock should ignore this click (GUI coordinates).</summary>
        private bool LobbyClockCovers(Vector2 guiPoint) =>
            lobbyClockOnTop && LobbyClockDrawsBackdrop && _lobbyClockDial != null && _lobbyClockDial.isActiveAndEnabled &&
            _lobbyClockDial.ContainsScreenPoint(new Vector2(guiPoint.x, Screen.height - guiPoint.y));

        /// <summary>Redraws the clock's images with IMGUI, after the lobby title and buttons, in their draw order.</summary>
        private void DrawLobbyClockOnTop()
        {
            if (!lobbyClockOnTop || !LobbyClockDrawsBackdrop || _lobbyClockRoot == null ||
                Event.current.type != EventType.Repaint) return;

            var oldMatrix = GUI.matrix;
            var oldColor = GUI.color;
            for (var i = 0; i < _lobbyClockRoot.childCount; i++)
            {
                var child = _lobbyClockRoot.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy) continue;
                var image = child.GetComponent<Image>();
                if (image == null || !image.enabled || image.sprite == null || image.color.a <= .001f) continue;

                // Screen Space - Overlay: world space is screen pixels (y up). GUI space is y down.
                child.GetWorldCorners(_lobbyClockCorners);
                var bottomLeft = ToGuiPoint(_lobbyClockCorners[0]);
                var topLeft = ToGuiPoint(_lobbyClockCorners[1]);
                var bottomRight = ToGuiPoint(_lobbyClockCorners[3]);
                var across = bottomRight - bottomLeft;
                var size = new Vector2(across.magnitude, (topLeft - bottomLeft).magnitude);
                if (size.x < .5f || size.y < .5f) continue;

                var sprite = image.sprite;
                var texture = sprite.texture;
                var source = sprite.textureRect;
                if (image.preserveAspect && source.height > 0f)
                {
                    var aspect = source.width / source.height;
                    size = size.x / size.y > aspect ? new Vector2(size.y * aspect, size.y) : new Vector2(size.x, size.x / aspect);
                }
                var center = (topLeft + bottomRight) * .5f;
                var angle = Mathf.Atan2(across.y, across.x) * Mathf.Rad2Deg;
                GUI.matrix = oldMatrix * Matrix4x4.TRS(center, Quaternion.Euler(0f, 0f, angle), Vector3.one);
                GUI.color = image.color;
                GUI.DrawTextureWithTexCoords(new Rect(-size * .5f, size), texture,
                    new Rect(source.x / texture.width, source.y / texture.height,
                        source.width / texture.width, source.height / texture.height), true);
            }
            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }

        private static Vector2 ToGuiPoint(Vector3 screen) => new(screen.x, Screen.height - screen.y);
    }
}
