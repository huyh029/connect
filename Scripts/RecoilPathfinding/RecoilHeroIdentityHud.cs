using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        private Texture2D _heroIdentityBackdrop;

        private void DrawHeroIdentityLine(Rect rect, string text, Color color)
        {
            if (_heroIdentityBackdrop == null)
            {
                const int width = 128;
                _heroIdentityBackdrop = new Texture2D(width, 1, TextureFormat.RGBA32, false)
                {
                    name = "Hero name background",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                var pixels = new Color[width];
                for (var x = 0; x < width; x++)
                {
                    var t = x / (width - 1f);
                    pixels[x] = new Color(0, 0, 0, .85f * Mathf.SmoothStep(0, 1, Mathf.Min(t, 1f - t) * 5f));
                }
                _heroIdentityBackdrop.SetPixels(pixels);
                _heroIdentityBackdrop.Apply(false, true);
            }
            GUI.DrawTexture(rect, _heroIdentityBackdrop, ScaleMode.StretchToFill, true);
            _heroHealthLabel.fontStyle = FontStyle.Bold;
            _heroHealthLabel.fontSize = 15;
            _heroHealthLabel.clipping = TextClipping.Clip;
            _heroHealthLabel.richText = false;
            _heroHealthLabel.wordWrap = false;
            _heroHealthLabel.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 1), text, _heroHealthLabel);
            _heroHealthLabel.normal.textColor = color;
            GUI.Label(rect, text, _heroHealthLabel);
        }
    }
}
