using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Look of the lobby Settings screen: a dark, gold-accented theme over the dimmed lobby art, a clean sans font,
    /// rounded controls. Only the look lives here; every settings control keeps its behaviour.
    /// <para>While the settings draw, the default IMGUI skin's styles are swapped for themed ones (and put back right
    /// after), so every plain GUI.Label / Button / Toggle / TextField / Box / slider / scrollbar in the settings picks
    /// the theme up. Hover policy: only real buttons react to hovering; tabs, list entries, toggles, text boxes,
    /// sliders and scrollbars look the same hovered or not.</para>
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        // ---------------------------------------------------------------- palette

        private static readonly Color ThemePage = new(.055f, .063f, .09f, 1f);
        private static readonly Color ThemeSidebar = new(.03f, .035f, .052f, .94f);
        private static readonly Color ThemeSurface = new(1f, 1f, 1f, .045f);
        private static readonly Color ThemeSurfaceRaised = new(1f, 1f, 1f, .075f);
        private static readonly Color ThemeLine = new(1f, 1f, 1f, .11f);
        private static readonly Color ThemeLineStrong = new(1f, 1f, 1f, .2f);
        private static readonly Color ThemeText = new(.945f, .918f, .86f, 1f);
        private static readonly Color ThemeTextDim = new(.66f, .63f, .56f, 1f);
        private static readonly Color ThemeGold = new(.886f, .725f, .361f, 1f);
        private static readonly Color ThemeGoldBright = new(.95f, .82f, .52f, 1f);
        private static readonly Color ThemeOnGold = new(.1f, .075f, .03f, 1f);

        private static Color Alpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

        // ---------------------------------------------------------------- font and textures

        private static Font s_settingsFont;
        private static readonly Dictionary<string, Texture2D> s_themeTextures = new();

        /// <summary>Clean sans font with full Vietnamese coverage (Segoe UI on Windows).</summary>
        private static Font SettingsFont =>
            s_settingsFont != null ? s_settingsFont
                : s_settingsFont = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Liberation Sans" }, 18);

        /// <summary>
        /// Anti-aliased rounded rectangle, sized for 9-slicing: use <see cref="RoundedBorder"/> as the style border.
        /// </summary>
        private static Texture2D RoundedTexture(Color fill, Color border, int radius, float borderWidth = 1f)
        {
            var key = $"r|{fill}|{border}|{radius}|{borderWidth}";
            if (s_themeTextures.TryGetValue(key, out var cached) && cached != null) return cached;
            var size = radius * 2 + 5;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Settings Theme Rounded",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color[size * size];
            var half = size * .5f;
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Signed distance to the rounded box (negative inside).
                var qx = Mathf.Abs(x + .5f - half) - (half - radius);
                var qy = Mathf.Abs(y + .5f - half) - (half - radius);
                var outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                var distance = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
                var total = Mathf.Clamp01(.5f - distance);
                var inner = borderWidth > 0f ? Mathf.Clamp01(.5f - (distance + borderWidth)) : total;
                var ring = total - inner;
                var a = fill.a * inner + border.a * ring;
                pixels[y * size + x] = a <= 0f
                    ? new Color(fill.r, fill.g, fill.b, 0f)
                    : new Color((fill.r * fill.a * inner + border.r * border.a * ring) / a,
                        (fill.g * fill.a * inner + border.g * border.a * ring) / a,
                        (fill.b * fill.a * inner + border.b * border.a * ring) / a, a);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            s_themeTextures[key] = texture;
            return texture;
        }

        private static RectOffset RoundedBorder(int radius) => new(radius + 2, radius + 2, radius + 2, radius + 2);

        /// <summary>Square cell with a 1 px hairline border (tables), 9-sliced with a 1 px border.</summary>
        private static Texture2D CellTexture(Color fill, Color border)
        {
            var key = $"c|{fill}|{border}";
            if (s_themeTextures.TryGetValue(key, out var cached) && cached != null) return cached;
            var texture = new Texture2D(3, 3, TextureFormat.RGBA32, false)
            {
                name = "Settings Theme Cell",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixels(new[] { border, border, border, border, fill, border, border, border, border });
            texture.Apply(false, true);
            s_themeTextures[key] = texture;
            return texture;
        }

        // ---------------------------------------------------------------- page pieces

        /// <summary>Settings page background: the lobby art, dimmed, with a darker navigation column.</summary>
        private void DrawSettingsBackdrop()
        {
            if (Event.current.type != EventType.Repaint) return;
            var previous = GUI.color;
            var screen = new Rect(0f, 0f, Screen.width, Screen.height);
            GUI.color = Color.white;
            if (lobbyBackgroundImage != null)
                GUI.DrawTexture(screen, lobbyBackgroundImage, ScaleMode.ScaleAndCrop, true);
            GUI.DrawTexture(screen, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, Alpha(ThemePage, .9f), 0, 0);
            var sidebarWidth = SetupNavigationWidth + 24f;
            GUI.DrawTexture(new Rect(0f, 0f, sidebarWidth, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill,
                true, 0, ThemeSidebar, 0, 0);
            GUI.DrawTexture(new Rect(sidebarWidth - 1f, 0f, 1f, Screen.height), Texture2D.whiteTexture, ScaleMode.StretchToFill,
                true, 0, Alpha(ThemeGold, .28f), 0, 0);
            GUI.color = previous;
        }

        private GUIStyle _settingsPageTitleStyle;
        private GUIStyle SettingsPageTitleStyle => _settingsPageTitleStyle ??= new GUIStyle(GUIStyle.none)
        {
            font = SettingsFont, fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
            wordWrap = false, clipping = TextClipping.Clip, normal = { textColor = ThemeGoldBright },
            hover = { textColor = ThemeGoldBright }
        };

        /// <summary>Page title in gold with a fine rule under it.</summary>
        private void DrawSettingsTitle(Rect rect, string text)
        {
            GUI.Label(rect, text, SettingsPageTitleStyle);
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(rect.x, rect.yMax + 4f, rect.width, 1f), Texture2D.whiteTexture,
                    ScaleMode.StretchToFill, true, 0, Alpha(ThemeGold, .3f), 0, 0);
        }

        // ---------------------------------------------------------------- themed skin (swapped in while drawing)

        private GUISkin _themedSkinSource;
        private GUIStyle _savedLabel, _savedButton, _savedToggle, _savedTextField, _savedTextArea, _savedBox, _savedScrollView,
            _savedHSlider, _savedHThumb, _savedVSlider, _savedVThumb, _savedHScrollbar, _savedHScrollThumb, _savedVScrollbar,
            _savedVScrollThumb;
        private GUIStyle _themeLabel, _themeButton, _themeToggle, _themeTextField, _themeTextArea, _themeBox, _themeScrollView,
            _themeHSlider, _themeHThumb, _themeVSlider, _themeVThumb, _themeHScrollbar, _themeHScrollThumb, _themeVScrollbar,
            _themeVScrollThumb;
        private bool _settingsSkinApplied;

        /// <summary>Puts the themed styles into the current skin until <see cref="RestoreSettingsSkin"/>.</summary>
        private void ApplySettingsSkin()
        {
            if (_settingsSkinApplied) return;
            var skin = GUI.skin;
            if (_themedSkinSource != skin) BuildThemedStyles(skin);
            _savedLabel = skin.label; skin.label = _themeLabel;
            _savedButton = skin.button; skin.button = _themeButton;
            _savedToggle = skin.toggle; skin.toggle = _themeToggle;
            _savedTextField = skin.textField; skin.textField = _themeTextField;
            _savedTextArea = skin.textArea; skin.textArea = _themeTextArea;
            _savedBox = skin.box; skin.box = _themeBox;
            _savedScrollView = skin.scrollView; skin.scrollView = _themeScrollView;
            _savedHSlider = skin.horizontalSlider; skin.horizontalSlider = _themeHSlider;
            _savedHThumb = skin.horizontalSliderThumb; skin.horizontalSliderThumb = _themeHThumb;
            _savedVSlider = skin.verticalSlider; skin.verticalSlider = _themeVSlider;
            _savedVThumb = skin.verticalSliderThumb; skin.verticalSliderThumb = _themeVThumb;
            _savedHScrollbar = skin.horizontalScrollbar; skin.horizontalScrollbar = _themeHScrollbar;
            _savedHScrollThumb = skin.horizontalScrollbarThumb; skin.horizontalScrollbarThumb = _themeHScrollThumb;
            _savedVScrollbar = skin.verticalScrollbar; skin.verticalScrollbar = _themeVScrollbar;
            _savedVScrollThumb = skin.verticalScrollbarThumb; skin.verticalScrollbarThumb = _themeVScrollThumb;
            _settingsSkinApplied = true;
        }

        private void RestoreSettingsSkin()
        {
            if (!_settingsSkinApplied) return;
            var skin = GUI.skin;
            skin.label = _savedLabel;
            skin.button = _savedButton;
            skin.toggle = _savedToggle;
            skin.textField = _savedTextField;
            skin.textArea = _savedTextArea;
            skin.box = _savedBox;
            skin.scrollView = _savedScrollView;
            skin.horizontalSlider = _savedHSlider;
            skin.horizontalSliderThumb = _savedHThumb;
            skin.verticalSlider = _savedVSlider;
            skin.verticalSliderThumb = _savedVThumb;
            skin.horizontalScrollbar = _savedHScrollbar;
            skin.horizontalScrollbarThumb = _savedHScrollThumb;
            skin.verticalScrollbar = _savedVScrollbar;
            skin.verticalScrollbarThumb = _savedVScrollThumb;
            _settingsSkinApplied = false;
        }

        /// <summary>Builds the themed copies from the original (unthemed) skin styles, once per skin.</summary>
        private void BuildThemedStyles(GUISkin skin)
        {
            _themedSkinSource = skin;

            _themeLabel = Calm(new GUIStyle(skin.label) { font = SettingsFont, fontSize = 18 }, ThemeText, null);

            // Buttons: rounded, gold hairline; brighten on hover (buttons keep their hover), gold when selected/on.
            _themeButton = new GUIStyle(skin.button)
            {
                font = SettingsFont, fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                border = RoundedBorder(8), padding = new RectOffset(14, 14, 7, 8), overflow = new RectOffset()
            };
            SetState(_themeButton.normal, RoundedTexture(ThemeSurfaceRaised, Alpha(ThemeGold, .45f), 8), ThemeText);
            SetState(_themeButton.hover, RoundedTexture(Alpha(ThemeGold, .16f), Alpha(ThemeGold, .8f), 8), ThemeGoldBright);
            SetState(_themeButton.active, RoundedTexture(Alpha(ThemeGold, .3f), ThemeGold, 8), ThemeGoldBright);
            SetState(_themeButton.focused, _themeButton.normal.background, ThemeText);
            SetState(_themeButton.onNormal, RoundedTexture(Alpha(ThemeGold, .2f), ThemeGold, 8, 1.5f), ThemeGoldBright);
            SetState(_themeButton.onHover, RoundedTexture(Alpha(ThemeGold, .26f), ThemeGold, 8, 1.5f), ThemeGoldBright);
            SetState(_themeButton.onActive, RoundedTexture(Alpha(ThemeGold, .32f), ThemeGold, 8, 1.5f), ThemeGoldBright);
            SetState(_themeButton.onFocused, _themeButton.onNormal.background, ThemeGoldBright);

            // Toggles keep the skin's checkbox art; text in the theme colour; no hover change.
            _themeToggle = new GUIStyle(skin.toggle) { font = SettingsFont, fontSize = 18 };
            foreach (var state in new[] { _themeToggle.normal, _themeToggle.hover, _themeToggle.active, _themeToggle.focused,
                         _themeToggle.onNormal, _themeToggle.onHover, _themeToggle.onActive, _themeToggle.onFocused })
                state.textColor = ThemeText;
            _themeToggle.hover = _themeToggle.normal;
            _themeToggle.onHover = _themeToggle.onNormal;

            var input = RoundedTexture(ThemeSurface, ThemeLineStrong, 6);
            var inputFocused = RoundedTexture(ThemeSurfaceRaised, ThemeGold, 6, 1.5f);
            _themeTextField = Calm(new GUIStyle(skin.textField)
            { font = SettingsFont, fontSize = 18, border = RoundedBorder(6), padding = new RectOffset(10, 10, 5, 5) }, ThemeText, input);
            _themeTextField.focused.background = _themeTextField.onFocused.background = inputFocused;
            _themeTextArea = Calm(new GUIStyle(skin.textArea)
            { font = SettingsFont, fontSize = 18, border = RoundedBorder(6), padding = new RectOffset(10, 10, 6, 6) }, ThemeText, input);
            _themeTextArea.focused.background = _themeTextArea.onFocused.background = inputFocused;

            _themeBox = Calm(new GUIStyle(skin.box) { font = SettingsFont, border = RoundedBorder(12) }, ThemeText,
                RoundedTexture(Alpha(Color.white, .035f), ThemeLine, 12));
            _themeScrollView = Calm(new GUIStyle(skin.scrollView), ThemeText, null);

            // Sliders: slim rounded track, round cream thumb (sizes kept from the skin so nothing shifts).
            var track = RoundedTexture(Alpha(Color.white, .14f), Color.clear, 3, 0f);
            var thumb = RoundedTexture(ThemeText, Alpha(ThemeGold, .9f), 7, 1.5f);
            _themeHSlider = Calm(new GUIStyle(skin.horizontalSlider) { border = RoundedBorder(3) }, ThemeText, track);
            // The skin's thumbs are tiny (about 10x12): stretch the round image instead of 9-slicing it.
            _themeHThumb = Calm(new GUIStyle(skin.horizontalSliderThumb) { border = new RectOffset() }, ThemeText, thumb);
            _themeVSlider = Calm(new GUIStyle(skin.verticalSlider) { border = RoundedBorder(3) }, ThemeText, track);
            _themeVThumb = Calm(new GUIStyle(skin.verticalSliderThumb) { border = new RectOffset() }, ThemeText, thumb);

            // Scrollbars: thin and quiet.
            var gutter = RoundedTexture(Alpha(Color.white, .04f), Color.clear, 4, 0f);
            var scrollThumb = RoundedTexture(Alpha(Color.white, .24f), Color.clear, 4, 0f);
            _themeHScrollbar = Calm(new GUIStyle(skin.horizontalScrollbar) { border = RoundedBorder(4) }, ThemeText, gutter);
            _themeHScrollThumb = Calm(new GUIStyle(skin.horizontalScrollbarThumb) { border = RoundedBorder(4) }, ThemeText, scrollThumb);
            _themeVScrollbar = Calm(new GUIStyle(skin.verticalScrollbar) { border = RoundedBorder(4) }, ThemeText, gutter);
            _themeVScrollThumb = Calm(new GUIStyle(skin.verticalScrollbarThumb) { border = RoundedBorder(4) }, ThemeText, scrollThumb);
        }

        private static void SetState(GUIStyleState state, Texture2D background, Color textColor)
        {
            state.background = background;
            state.scaledBackgrounds = null;
            state.textColor = textColor;
        }

        /// <summary>One look for every state except focus (non-buttons do not react to hovering).</summary>
        private static GUIStyle Calm(GUIStyle style, Color textColor, Texture2D background)
        {
            foreach (var state in new[] { style.normal, style.hover, style.active, style.focused,
                         style.onNormal, style.onHover, style.onActive, style.onFocused })
            {
                if (background != null) state.background = background;
                state.scaledBackgrounds = null;
                state.textColor = textColor;
            }
            style.hover = style.normal;
            style.onHover = style.onNormal;
            return style;
        }

        /// <summary>Gives a (per-call) style the same look hovered as not hovered.</summary>
        private static GUIStyle WithoutHover(GUIStyle style)
        {
            style.hover = style.normal;
            style.onHover = style.onNormal;
            return style;
        }
    }
}
