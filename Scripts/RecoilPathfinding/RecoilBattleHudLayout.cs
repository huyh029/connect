using System;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>Battle HUD pieces placed with the battle HUD tool.</summary>
    public enum BattleHudPieceId { Timer, PoolTitle, PoolScore }

    /// <summary>Center and size of one battle HUD piece; scale grows its box and its text together.</summary>
    [Serializable]
    public struct BattleHudPiece
    {
        [Tooltip("Tâm, theo pixel của khung 1920 px bề ngang (x từ trái, y từ mép trên màn hình).")]
        public Vector2 position;
        [Tooltip("Phóng to / thu nhỏ cả khung lẫn cỡ chữ.")]
        public float scale;

        public BattleHudPiece(Vector2 position, float scale)
        {
            this.position = position;
            this.scale = scale;
        }
    }

    /// <summary>
    /// Where the match timer and the score pool sit on the battle screen. Like the rest of the top HUD, they are laid
    /// out on a 1920 px wide frame centered on the screen, measured from its top edge, and scaled by
    /// <see cref="HudScale"/>. Edited with Game Tools > Công Thành > Battle > "Chỉnh UI thời gian + bể điểm".
    /// </summary>
    [Serializable]
    public sealed class RecoilBattleHudLayout
    {
        public const float DesignWidth = 1920f;

        // Defaults reproduce the HUD as it was laid out by hand: timer on top, then "BỂ ĐIỂM", then the points.
        public BattleHudPiece timer = new(new Vector2(960f, 20f), 1f);
        public BattleHudPiece poolTitle = new(new Vector2(960f, 53f), 1f);
        public BattleHudPiece poolScore = new(new Vector2(960f, 88f), 1f);

        /// <summary>Box (design px) and font size of each piece at scale 1.</summary>
        public static Vector2 BoxOf(BattleHudPieceId id) => id switch
        {
            BattleHudPieceId.Timer => new Vector2(250f, 34f),
            BattleHudPieceId.PoolTitle => new Vector2(250f, 28f),
            _ => new Vector2(250f, 42f),
        };

        public static float FontOf(BattleHudPieceId id) => id switch
        {
            BattleHudPieceId.Timer => 30f,
            BattleHudPieceId.PoolTitle => 23f,
            _ => 36f,
        };

        public BattleHudPiece Get(BattleHudPieceId id) => id switch
        {
            BattleHudPieceId.Timer => timer,
            BattleHudPieceId.PoolTitle => poolTitle,
            _ => poolScore,
        };

        public void Set(BattleHudPieceId id, BattleHudPiece value)
        {
            switch (id)
            {
                case BattleHudPieceId.Timer: timer = value; break;
                case BattleHudPieceId.PoolTitle: poolTitle = value; break;
                default: poolScore = value; break;
            }
        }

        /// <summary>The top HUD's own scale: follows the screen width, between 0.62 and 1.</summary>
        public static float HudScale(float screenWidth) => Mathf.Clamp(screenWidth / DesignWidth, .62f, 1f);

        /// <summary>GUI rectangle of a piece on a screen <paramref name="screenWidth"/> wide.</summary>
        public static Rect GuiRect(BattleHudPiece piece, BattleHudPieceId id, float screenWidth)
        {
            var hud = HudScale(screenWidth);
            var size = BoxOf(id) * (Mathf.Max(.05f, piece.scale) * hud);
            var center = new Vector2(screenWidth * .5f + (piece.position.x - DesignWidth * .5f) * hud, piece.position.y * hud);
            return new Rect(center - size * .5f, size);
        }

        /// <summary>Font size of a piece on a screen <paramref name="screenWidth"/> wide.</summary>
        public static int FontSize(BattleHudPiece piece, BattleHudPieceId id, float screenWidth) =>
            Mathf.Max(1, Mathf.RoundToInt(FontOf(id) * Mathf.Max(.05f, piece.scale) * HudScale(screenWidth)));
    }
}
