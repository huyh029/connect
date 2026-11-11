using System;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>The pieces of the lobby (home) screen that can be placed with the layout tool.</summary>
    /// <summary>
    /// Pieces the layout tool can place. <see cref="TimeButton"/> is not stored here: it is a layer of the clock
    /// (its place is relative to the clock), so the tool writes it into the clock prefab.
    /// </summary>
    public enum LobbyPieceId { Title, StartButton, SettingsButton, Clock, LoadingBar, ErrorText, TimeButton }

    /// <summary>Transform of one lobby piece, in design space (see <see cref="RecoilLobbyLayout"/>).</summary>
    [Serializable]
    public struct LobbyPiece
    {
        [Tooltip("Tâm của hình, theo pixel của khung thiết kế 1920×1080 (gốc ở góc trên-trái, y hướng xuống).")]
        public Vector2 position;
        [Tooltip("Scale nhân với kích thước gốc của hình.")]
        public Vector2 scale;
        [Tooltip("Xoay quanh tâm, độ. Dương = ngược chiều kim đồng hồ.")]
        public float rotation;

        public LobbyPiece(Vector2 position, Vector2 scale, float rotation = 0f)
        {
            this.position = position;
            this.scale = scale;
            this.rotation = rotation;
        }
    }

    /// <summary>
    /// Where each lobby piece sits. The lobby is designed on a 1920×1080 frame; it fits the screen uniformly and
    /// stays centered, so a layout keeps its shape at any resolution. Positions are the pieces' centers.
    /// Edited with Game Tools > Công Thành > Lobby > "Chỉnh giao diện sảnh".
    /// </summary>
    [Serializable]
    public sealed class RecoilLobbyLayout
    {
        public const float DesignWidth = 1920f;
        public const float DesignHeight = 1080f;

        // Defaults reproduce the lobby as it was laid out by hand at 1920×1080.
        public LobbyPiece title = new(new Vector2(960f, 245f), Vector2.one);
        public LobbyPiece startButton = new(new Vector2(960f, 920f), Vector2.one);
        public LobbyPiece settingsButton = new(new Vector2(1520f, 670f), Vector2.one);
        public LobbyPiece clock = new(new Vector2(326f, 520f), new Vector2(.55f, .55f));
        public LobbyPiece loadingBar = new(new Vector2(960f, 759f), Vector2.one);
        public LobbyPiece errorText = new(new Vector2(960f, 763f), Vector2.one);

        /// <summary>Size of each piece at scale 1, in design pixels. The title's width follows its image's aspect.</summary>
        public static readonly Vector2 StartButtonBounds = new(1000f, 280f);
        public static readonly Vector2 SettingsButtonBounds = new(520f, 220f);
        public static readonly Vector2 ClockBounds = new(640f, 640f);
        public static readonly Vector2 LoadingBarBounds = new(937.5f, 38f);
        public static readonly Vector2 ErrorTextBounds = new(578f, 26f);
        public const float TitleHeight = 250f;

        public LobbyPiece Get(LobbyPieceId id) => id switch
        {
            LobbyPieceId.Title => title,
            LobbyPieceId.StartButton => startButton,
            LobbyPieceId.SettingsButton => settingsButton,
            LobbyPieceId.Clock => clock,
            LobbyPieceId.LoadingBar => loadingBar,
            LobbyPieceId.ErrorText => errorText,
            _ => default,
        };

        public void Set(LobbyPieceId id, LobbyPiece value)
        {
            switch (id)
            {
                case LobbyPieceId.Title: title = value; break;
                case LobbyPieceId.StartButton: startButton = value; break;
                case LobbyPieceId.SettingsButton: settingsButton = value; break;
                case LobbyPieceId.Clock: clock = value; break;
                case LobbyPieceId.LoadingBar: loadingBar = value; break;
                case LobbyPieceId.ErrorText: errorText = value; break;
                default: break; // TimeButton lives in the clock prefab
            }
        }

        /// <summary>Screen pixels per design pixel: the frame fits the screen, never enlarged past 1:1.</summary>
        public static float FitScale(float screenWidth, float screenHeight) =>
            Mathf.Min(1f, screenWidth / DesignWidth, screenHeight / DesignHeight);

        /// <summary>Design point → GUI point (IMGUI, origin top-left). The frame is centered on the screen.</summary>
        public static Vector2 DesignToGui(Vector2 design, float screenWidth, float screenHeight)
        {
            var fit = FitScale(screenWidth, screenHeight);
            return new Vector2(screenWidth * .5f + (design.x - DesignWidth * .5f) * fit,
                screenHeight * .5f + (design.y - DesignHeight * .5f) * fit);
        }

        /// <summary>
        /// GUI rectangle of a piece: its bounds at scale, centered on its position (not rotated; the rotation is
        /// applied with <see cref="GuiAngle"/>).
        /// </summary>
        public static Rect GuiRect(LobbyPiece piece, Vector2 bounds, float screenWidth, float screenHeight)
        {
            var fit = FitScale(screenWidth, screenHeight);
            var size = new Vector2(Mathf.Abs(bounds.x * piece.scale.x), Mathf.Abs(bounds.y * piece.scale.y)) * fit;
            var center = DesignToGui(piece.position, screenWidth, screenHeight);
            return new Rect(center - size * .5f, size);
        }

        /// <summary>Title bounds: the title's own aspect at <see cref="TitleHeight"/> tall.</summary>
        public static Vector2 TitleBounds(float aspect) => new(TitleHeight * aspect, TitleHeight);

        /// <summary>IMGUI rotates clockwise for positive angles; the layout turns counter-clockwise.</summary>
        public static float GuiAngle(float rotation) => -rotation;

        /// <summary>Clock position in canvas units (the canvas scales by <see cref="FitScale"/>, so these are design pixels).</summary>
        public static Vector2 CanvasPosition(Vector2 design) => new(design.x - DesignWidth * .5f, DesignHeight * .5f - design.y);

        /// <summary>Turns a design-space vector counter-clockwise on screen by <paramref name="degrees"/> (y points down).</summary>
        public static Vector2 RotateCcw(Vector2 v, float degrees)
        {
            var r = degrees * Mathf.Deg2Rad;
            var c = Mathf.Cos(r);
            var s = Mathf.Sin(r);
            return new Vector2(v.x * c + v.y * s, -v.x * s + v.y * c);
        }

        /// <summary>Inverse of <see cref="RotateCcw"/>.</summary>
        public static Vector2 UnrotateCcw(Vector2 v, float degrees) => RotateCcw(v, -degrees);
    }
}
