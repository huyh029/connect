using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    /// <summary>
    /// Lobby cursor: Settings tab "Setup chuột" (style, size, click point, smoothing) and frame pacing for the
    /// game-drawn cursor.
    /// <list type="bullet">
    /// <item>Chuột thường (Normal): the plain Windows pointer, drawn by the OS, so it follows the mouse with no lag.</item>
    /// <item>UI (game vẽ): the lobby cursor image drawn by the game every frame at its design size times the chosen
    /// scale. It only moves once per rendered frame. While it is shown the lobby can run at the display's refresh
    /// rate with VSync and a single queued frame instead of the global 60 FPS / no-VSync cap
    /// (<see cref="RecoilFrameRateLimit"/>); the battle gets the previous settings back.</item>
    /// <item>UI (Windows vẽ): the same image, resized to the chosen size, handed to the OS as its cursor: no lag,
    /// but some Windows setups shrink large OS cursors (about 32x32 in Unity).</item>
    /// </list>
    /// The player's choices are kept in PlayerPrefs. All styles keep the existing rule that no cursor shows while
    /// <c>_hideLobbyCursor</c> is set (battle, loading, C key).
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        public enum LobbyCursorStyle { Normal, UI, WindowsDrawnUI }

        private const string LobbyCursorStyleKey = "RecoilLobbyCursorStyle";
        private const string LobbyCursorScaleKey = "RecoilLobbyCursorScale";
        private const string LobbyCursorHotspotXKey = "RecoilLobbyCursorHotspotX";
        private const string LobbyCursorHotspotYKey = "RecoilLobbyCursorHotspotY";
        private const string LobbyCursorSmoothKey = "RecoilLobbyCursorSmooth";

        [SerializeField, Tooltip("Kiểu con trỏ khi người chơi chưa chọn trong Settings > Setup chuột. " +
                                 "Normal = chuột thường của Windows, UI = con trỏ hình do game vẽ, " +
                                 "WindowsDrawnUI = con trỏ hình do Windows vẽ.")]
        private LobbyCursorStyle defaultLobbyCursorStyle = LobbyCursorStyle.UI;
        [SerializeField, Tooltip("Mặc định của mục \"Mượt hơn ở sảnh\": chạy theo tần số màn hình + VSync, giảm hàng đợi khung hình " +
                                 "để con trỏ UI (game vẽ) bám tay hơn. Vào trận trả lại giới hạn FPS cũ.")]
        private bool smoothLobbyCursor = true;

        private bool _cursorSettingsOpen;
        private LobbyCursorStyle? _lobbyCursorStyle;
        private float? _lobbyCursorScale;
        private Vector2? _lobbyCursorHotspot;
        private bool? _lobbyCursorSmooth;
        private bool _cursorPrefsDirty;

        // Cursor drawn by Windows (the plain pointer, or the cursor image).
        private bool _osCursorShown;
        private Texture2D _appliedOsCursor;
        private Vector2 _appliedOsHotspot;
        private Texture2D _windowsCursorImage;
        private Vector2 _windowsCursorHotspot;
        private bool _windowsCursorFailed;

        private bool _lobbyPacingApplied;
        private int _savedVSyncCount, _savedTargetFrameRate, _savedMaxQueuedFrames;
        private GUIStyle _cursorChoiceStyle, _cursorToggleStyle, _cursorNoteStyle;

        // ---------------------------------------------------------------- player choices (PlayerPrefs)

        private LobbyCursorStyle CurrentLobbyCursorStyle => _lobbyCursorStyle ??= PlayerPrefs.HasKey(LobbyCursorStyleKey)
            ? (LobbyCursorStyle)Mathf.Clamp(PlayerPrefs.GetInt(LobbyCursorStyleKey), 0, 2)
            : defaultLobbyCursorStyle;

        private bool CursorIsImage => CurrentLobbyCursorStyle != LobbyCursorStyle.Normal;

        /// <summary>Multiplier on the designed display size (lobbyCursorDisplaySize).</summary>
        private float LobbyCursorScale => _lobbyCursorScale ??= Mathf.Clamp(PlayerPrefs.GetFloat(LobbyCursorScaleKey, 1f), .25f, 2.5f);

        /// <summary>Click point as a fraction of the image, from its top-left corner.</summary>
        private Vector2 LobbyCursorHotspot => _lobbyCursorHotspot ??= PlayerPrefs.HasKey(LobbyCursorHotspotXKey)
            ? new Vector2(PlayerPrefs.GetFloat(LobbyCursorHotspotXKey), PlayerPrefs.GetFloat(LobbyCursorHotspotYKey))
            : DefaultLobbyCursorHotspot;

        private Vector2 DefaultLobbyCursorHotspot =>
            lobbyCursorTexture != null && lobbyCursorTexture.width > 0 && lobbyCursorTexture.height > 0
                ? new Vector2(lobbyCursorSourceHotspot.x / lobbyCursorTexture.width,
                    lobbyCursorSourceHotspot.y / lobbyCursorTexture.height)
                : Vector2.zero;

        private bool LobbyCursorSmooth => _lobbyCursorSmooth ??= PlayerPrefs.GetInt(LobbyCursorSmoothKey, smoothLobbyCursor ? 1 : 0) == 1;

        /// <summary>On-screen size of the cursor image.</summary>
        private Vector2 LobbyCursorSize => lobbyCursorDisplaySize * LobbyCursorScale;

        private void SetLobbyCursorStyle(LobbyCursorStyle style)
        {
            if (style == CurrentLobbyCursorStyle) return;
            _lobbyCursorStyle = style;
            PlayerPrefs.SetInt(LobbyCursorStyleKey, (int)style);
            _cursorPrefsDirty = true;
            RefreshCursorNow();
        }

        private void ResetLobbyCursorSettings()
        {
            foreach (var key in new[] { LobbyCursorStyleKey, LobbyCursorScaleKey, LobbyCursorHotspotXKey, LobbyCursorHotspotYKey, LobbyCursorSmoothKey })
                PlayerPrefs.DeleteKey(key);
            _lobbyCursorStyle = null;
            _lobbyCursorScale = null;
            _lobbyCursorHotspot = null;
            _lobbyCursorSmooth = null;
            _settingsValueInputs.Remove(ScaleLabel);
            _settingsValueInputs.Remove(HotspotXLabel);
            _settingsValueInputs.Remove(HotspotYLabel);
            _cursorPrefsDirty = true;
            RefreshCursorNow();
        }

        private void RefreshCursorNow()
        {
            _osCursorShown = false;
            UpdateSystemCursorVisibility();
        }

        // ---------------------------------------------------------------- cursor drawn by Windows

        private bool UseOsCursor =>
            !_hideLobbyCursor &&
            (CurrentLobbyCursorStyle == LobbyCursorStyle.Normal ||
             CurrentLobbyCursorStyle == LobbyCursorStyle.WindowsDrawnUI && TryBuildWindowsCursorImage());

        private void ApplyOsCursor()
        {
            var image = CurrentLobbyCursorStyle == LobbyCursorStyle.WindowsDrawnUI ? _windowsCursorImage : null;
            var hotspot = image != null ? _windowsCursorHotspot : Vector2.zero;
            if (_osCursorShown && Cursor.visible && _appliedOsCursor == image && _appliedOsHotspot == hotspot) return;
            if (!_hasPreviousCursorVisibility)
            {
                _previousCursorVisibility = Cursor.visible;
                _hasPreviousCursorVisibility = true;
            }
            Cursor.SetCursor(image, hotspot, CursorMode.Auto);
            Cursor.visible = true;
            _osCursorShown = true;
            _appliedOsCursor = image;
            _appliedOsHotspot = hotspot;
        }

        /// <summary>
        /// The cursor image at the chosen size for Windows to draw (RGBA32, readable, no mips, as an OS cursor needs),
        /// halved step by step first so the small copy is not aliased. Rebuilt when the size changes.
        /// </summary>
        private bool TryBuildWindowsCursorImage()
        {
            if (_windowsCursorFailed || lobbyCursorTexture == null) return false;
            var size = LobbyCursorSize;
            var width = Mathf.Clamp(Mathf.RoundToInt(size.x), 8, 512);
            var height = Mathf.Clamp(Mathf.RoundToInt(size.y), 8, 512);
            // Hotspot in the image's pixels from its top-left, as Cursor.SetCursor expects.
            _windowsCursorHotspot = Vector2.Scale(LobbyCursorHotspot, new Vector2(width, height));
            if (_windowsCursorImage != null && _windowsCursorImage.width == width && _windowsCursorImage.height == height)
                return true;

            ReleaseWindowsCursorImage();
            RenderTexture step = null, final = null;
            var previousActive = RenderTexture.active;
            try
            {
                Texture source = lobbyCursorTexture;
                int w = lobbyCursorTexture.width, h = lobbyCursorTexture.height;
                while (w / 2 >= width && h / 2 >= height)
                {
                    w /= 2;
                    h /= 2;
                    var next = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(source, next);
                    if (step != null) RenderTexture.ReleaseTemporary(step);
                    step = next;
                    source = next;
                }
                final = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(source, final);
                RenderTexture.active = final;
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = "Lobby Cursor (Windows)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                texture.Apply(false, false);
                _windowsCursorImage = texture;
                return true;
            }
            catch (Exception exception)
            {
                _windowsCursorFailed = true;
                Debug.LogWarning("Không tạo được con trỏ do Windows vẽ, dùng con trỏ game vẽ: " + exception.Message, this);
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (step != null) RenderTexture.ReleaseTemporary(step);
                if (final != null) RenderTexture.ReleaseTemporary(final);
            }
        }

        private void ReleaseWindowsCursorImage()
        {
            if (_windowsCursorImage == null) return;
            Destroy(_windowsCursorImage);
            _windowsCursorImage = null;
        }

        // ---------------------------------------------------------------- Settings tab "Setup chuột"

        private const string ScaleLabel = "Kích thước (x)";
        private const string HotspotXLabel = "Điểm bấm ngang (%)";
        private const string HotspotYLabel = "Điểm bấm dọc (%)";

        private void DrawCursorSettings()
        {
            float left = SetupNavigationWidth + 28, width = Screen.width - left - 20;
            DrawSettingsTitle(new Rect(left, 16, width, 36), "CON TRỎ CHUỘT — dùng ở sảnh");

            _cursorNoteStyle ??= new GUIStyle(GUIStyle.none)
            {
                font = SettingsFont, fontSize = 16, wordWrap = true, alignment = TextAnchor.MiddleLeft,
                normal = { textColor = ThemeTextDim }
            };
            _cursorToggleStyle ??= new GUIStyle(GUI.skin.toggle)
            { fontSize = 18, wordWrap = true, normal = { textColor = ThemeText }, onNormal = { textColor = ThemeText } };

            var column = Mathf.Min(680f, width * .58f);
            var y = 72f;
            GUI.Label(new Rect(left, y, column, 32f), "Kiểu con trỏ", SettingsAttributeLabelStyle);
            y += 36f;
            var third = (column - 24f) / 3f;
            var style = CurrentLobbyCursorStyle;
            if (CursorChoiceButton(new Rect(left, y, third, 56f), "Chuột thường", style == LobbyCursorStyle.Normal))
                SetLobbyCursorStyle(LobbyCursorStyle.Normal);
            if (CursorChoiceButton(new Rect(left + third + 12f, y, third, 56f), "UI (game vẽ)", style == LobbyCursorStyle.UI))
                SetLobbyCursorStyle(LobbyCursorStyle.UI);
            if (CursorChoiceButton(new Rect(left + (third + 12f) * 2f, y, third, 56f), "UI (Windows vẽ)",
                    style == LobbyCursorStyle.WindowsDrawnUI))
                SetLobbyCursorStyle(LobbyCursorStyle.WindowsDrawnUI);
            y += 62f;
            GUI.Label(new Rect(left, y, column, 44f), StyleNote(CurrentLobbyCursorStyle), _cursorNoteStyle);
            y += 56f;

            // Size and click point apply to both image cursors; smoothing only to the game-drawn one.
            var wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && CursorIsImage;
            var scale = DrawStatSlider(left, y, column, ScaleLabel, LobbyCursorScale, .25f, 2.5f, "0.00");
            if (!Mathf.Approximately(scale, LobbyCursorScale))
            {
                _lobbyCursorScale = scale;
                PlayerPrefs.SetFloat(LobbyCursorScaleKey, scale);
                _cursorPrefsDirty = true;
                RefreshCursorNow();
            }
            y += 50f;
            var hotspot = LobbyCursorHotspot;
            var hotspotX = DrawStatSlider(left, y, column, HotspotXLabel, hotspot.x * 100f, 0f, 100f, "0.0") / 100f;
            y += 50f;
            var hotspotY = DrawStatSlider(left, y, column, HotspotYLabel, hotspot.y * 100f, 0f, 100f, "0.0") / 100f;
            if (!Mathf.Approximately(hotspotX, hotspot.x) || !Mathf.Approximately(hotspotY, hotspot.y))
            {
                _lobbyCursorHotspot = new Vector2(hotspotX, hotspotY);
                PlayerPrefs.SetFloat(LobbyCursorHotspotXKey, hotspotX);
                PlayerPrefs.SetFloat(LobbyCursorHotspotYKey, hotspotY);
                _cursorPrefsDirty = true;
                RefreshCursorNow();
            }
            y += 58f;
            GUI.enabled = wasEnabled && CurrentLobbyCursorStyle == LobbyCursorStyle.UI;
            var smooth = GUI.Toggle(new Rect(left, y, column, 56f), LobbyCursorSmooth,
                "  Mượt hơn ở sảnh (chạy theo tần số màn hình + VSync) — cho UI (game vẽ)", _cursorToggleStyle);
            if (smooth != LobbyCursorSmooth)
            {
                _lobbyCursorSmooth = smooth;
                PlayerPrefs.SetInt(LobbyCursorSmoothKey, smooth ? 1 : 0);
                _cursorPrefsDirty = true;
            }
            GUI.enabled = wasEnabled;
            y += 72f;
            if (CursorChoiceButton(new Rect(left, y, Mathf.Min(220f, column), 48f), "Mặc định", false))
                ResetLobbyCursorSettings();

            DrawCursorPreview(new Rect(left + column + 30f, 72f, width - column - 30f,
                Mathf.Min(380f, Screen.height - 72f - 140f)));
        }

        private static string StyleNote(LobbyCursorStyle style) => style switch
        {
            LobbyCursorStyle.Normal => "Mũi tên chuẩn của Windows: bám chuột tức thì, không đổi được hình và cỡ.",
            LobbyCursorStyle.UI => "Game tự vẽ hình con trỏ: đúng cỡ đã chọn, độ mượt phụ thuộc FPS.",
            _ => "Windows vẽ hình con trỏ: bám chuột tức thì. Một số máy Windows thu con trỏ lớn về khoảng 32×32."
        };

        /// <summary>The cursor at its chosen size, with its click point (red +) at the middle of the box.</summary>
        private void DrawCursorPreview(Rect area)
        {
            if (area.width < 160f || area.height < 120f) return;
            GUI.Label(new Rect(area.x, area.y, area.width, 32f), "Xem trước", SettingsAttributeLabelStyle);
            var box = new Rect(area.x, area.y + 36f, area.width, area.height - 36f);
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(box, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, ThemeSurface, 0, 12f);
                GUI.DrawTexture(box, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, ThemeLine, 1f, 12f);
            }

            if (!CursorIsImage || lobbyCursorTexture == null)
            {
                var centered = new GUIStyle(_cursorNoteStyle) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(box, "Đang dùng chuột thường của Windows:\nbám chuột tức thì, kích thước theo Windows.", centered);
                return;
            }

            GUI.BeginGroup(box);
            var center = box.size * .5f;
            var size = LobbyCursorSize;
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(new Rect(center - Vector2.Scale(LobbyCursorHotspot, size), size), lobbyCursorTexture,
                    ScaleMode.StretchToFill, true);
                var previous = GUI.color;
                GUI.color = new Color(.9f, .1f, .1f);
                GUI.DrawTexture(new Rect(center.x - 12f, center.y - 1f, 24f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(center.x - 1f, center.y - 12f, 2f, 24f), Texture2D.whiteTexture);
                GUI.color = previous;
            }
            GUI.EndGroup();
            var caption = $"Dấu + đỏ là điểm bấm. Cỡ trên màn hình: {size.x:0} × {size.y:0} px.";
            if (CurrentLobbyCursorStyle == LobbyCursorStyle.WindowsDrawnUI)
                caption += " Nếu con trỏ thật nhỏ hơn hình này, Windows đang giới hạn cỡ: dùng UI (game vẽ).";
            GUI.Label(new Rect(box.x, box.yMax + 6f, box.width, 52f), caption, _cursorNoteStyle);
        }

        private GUIStyle _cursorChoiceSelectedStyle;

        /// <summary>Choice button: gold when chosen, otherwise a quiet raised button with a gold hairline.</summary>
        private bool CursorChoiceButton(Rect rect, string text, bool selected)
        {
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                    selected ? ThemeGold : ThemeSurfaceRaised, 0, 10f);
                if (!selected)
                    GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0,
                        Alpha(ThemeGold, .45f), 1f, 10f);
            }
            if (_cursorChoiceStyle == null)
            {
                _cursorChoiceStyle = new GUIStyle(GUIStyle.none)
                {
                    font = SettingsFont, fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                    wordWrap = false, clipping = TextClipping.Clip, normal = { textColor = ThemeText }
                };
                _cursorChoiceSelectedStyle = new GUIStyle(_cursorChoiceStyle) { normal = { textColor = ThemeOnGold } };
            }
            return SettingsButton(rect, new GUIContent(text), selected ? _cursorChoiceSelectedStyle : _cursorChoiceStyle);
        }

        // ---------------------------------------------------------------- frame pacing for the game-drawn cursor

        private void UpdateLobbyFramePacing()
        {
            // Write the choices to disk once the player lets go (not on every slider step).
            var mouse = Mouse.current;
            if (_cursorPrefsDirty && (mouse == null || !mouse.leftButton.isPressed))
            {
                PlayerPrefs.Save();
                _cursorPrefsDirty = false;
            }

            var want = LobbyCursorSmooth && Application.isPlaying && !_hideLobbyCursor && !_lobbyStarted &&
                       lobbyCursorTexture != null && CurrentLobbyCursorStyle == LobbyCursorStyle.UI;
            if (want == _lobbyPacingApplied) return;
            if (!want)
            {
                RestoreLobbyFramePacing();
                return;
            }

            _savedVSyncCount = QualitySettings.vSyncCount;
            _savedTargetFrameRate = Application.targetFrameRate;
            _savedMaxQueuedFrames = QualitySettings.maxQueuedFrames;
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = DisplayRefreshRate(); // also caps the editor's Game view, which ignores VSync
            QualitySettings.maxQueuedFrames = 1;
            _lobbyPacingApplied = true;
        }

        private void RestoreLobbyFramePacing()
        {
            if (!_lobbyPacingApplied) return;
            QualitySettings.vSyncCount = _savedVSyncCount;
            Application.targetFrameRate = _savedTargetFrameRate;
            QualitySettings.maxQueuedFrames = _savedMaxQueuedFrames;
            _lobbyPacingApplied = false;
        }

        private static int DisplayRefreshRate()
        {
            var hz = Screen.currentResolution.refreshRateRatio.value;
            return hz >= 30.0 ? Mathf.RoundToInt((float)hz) : 60;
        }
    }
}
