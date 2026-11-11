using System.Collections;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [SerializeField] private GameObject endgameMeteorPrefab;
        [SerializeField, Min(.1f)] private float endgameMeteorScaleMultiplier = 3f;
        private Texture2D _endgameNameGradient;
        [SerializeField] private Texture2D endgameTitleImage, endgameBlueFlagImage, endgameRedFlagImage;
        [SerializeField] private Texture2D endgameRetryImage, endgameHomeImage;
        [SerializeField, Min(.5f)] private float endgameCameraSeconds = 2.2f;
        [SerializeField, Range(0f, 5f)] private float endgameShakeStrength = 1.2f;
        private GameObject _endgameMeteor;
        private ParticleSystem[] _endgameMeteorParticles;
        private Camera _endgameCamera;
        private bool _endgameBlueWins, _endgameExplosionPlayed, _endgamePrepared;
        private float _endgameStartedAt = -1f, _endgameOriginalFar, _endgameStartOrtho, _endgameTargetOrtho;
        private Vector3 _endgameStartPosition, _endgameCameraPosition, _endgameFocus, _endgameImpact;
        private Quaternion _endgameStartRotation, _endgameCameraRotation;
        private readonly string[] _endgameNames = new string[3];
        private readonly int[] _endgameScores = new int[3];
        private GUIStyle _endgameHeadingStyle, _endgameNameStyle, _endgameScoreStyle;

        private IEnumerator PrepareEndgamePresentation()
        {
            if (_endgamePrepared) yield break;
            _endgamePrepared = true;
            foreach (var texture in new[] { endgameTitleImage, endgameBlueFlagImage, endgameRedFlagImage, endgameRetryImage, endgameHomeImage })
                if (texture != null && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                    texture.GetNativeTexturePtr();
            if (endgameMeteorPrefab == null) yield break;
            var holder = new GameObject("Endgame meteor pool");
            holder.transform.SetParent(transform, false);
            holder.SetActive(false);
            _endgameMeteor = Instantiate(endgameMeteorPrefab, holder.transform);
            _endgameMeteor.SetActive(false);
            _endgameMeteor.transform.localScale = endgameMeteorPrefab.transform.localScale * endgameMeteorScaleMultiplier;
            _endgameMeteorParticles = _endgameMeteor.GetComponentsInChildren<ParticleSystem>(true);
            foreach (var particle in _endgameMeteorParticles)
            {
                var main = particle.main;
                main.loop = false;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.stopAction = ParticleSystemStopAction.None;
                main.startColor = BoostMeteorColors(main.startColor);
                var lifetimeColor = particle.colorOverLifetime;
                if (lifetimeColor.enabled) lifetimeColor.color = BoostMeteorColors(lifetimeColor.color);
                var emission = particle.emission;
                emission.rateOverTimeMultiplier *= 2.8f;
                var bursts = new ParticleSystem.Burst[emission.burstCount];
                emission.GetBursts(bursts);
                for (var i = 0; i < bursts.Length; i++)
                {
                    var count = bursts[i].count;
                    if (count.mode == ParticleSystemCurveMode.Constant)
                        bursts[i].count = new ParticleSystem.MinMaxCurve(count.constant * 2.8f);
                    else if (count.mode == ParticleSystemCurveMode.TwoConstants)
                        bursts[i].count = new ParticleSystem.MinMaxCurve(count.constantMin * 2.8f, count.constantMax * 2.8f);
                }
                emission.SetBursts(bursts);
                particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
            yield return null;
        }

        private static Color BoostMeteorColor(Color color)
        {
            return new Color(color.r * 1.5f, color.g * 1.5f, color.b * 1.5f, Mathf.Clamp01(color.a * 2f));
        }

        private static Gradient BoostMeteorGradient(Gradient source)
        {
            var colors = source.colorKeys;
            var alpha = source.alphaKeys;
            for (var i = 0; i < colors.Length; i++) colors[i].color = BoostMeteorColor(colors[i].color);
            for (var i = 0; i < alpha.Length; i++) alpha[i].alpha = Mathf.Clamp01(alpha[i].alpha * 2f);
            var result = new Gradient { mode = source.mode };
            result.SetKeys(colors, alpha);
            return result;
        }

        private static ParticleSystem.MinMaxGradient BoostMeteorColors(ParticleSystem.MinMaxGradient source)
        {
            switch (source.mode)
            {
                case ParticleSystemGradientMode.Color: source.color = BoostMeteorColor(source.color); break;
                case ParticleSystemGradientMode.TwoColors:
                    source.colorMin = BoostMeteorColor(source.colorMin);
                    source.colorMax = BoostMeteorColor(source.colorMax); break;
                case ParticleSystemGradientMode.TwoGradients:
                    source.gradientMin = BoostMeteorGradient(source.gradientMin);
                    source.gradientMax = BoostMeteorGradient(source.gradientMax); break;
                default: source.gradient = BoostMeteorGradient(source.gradient); break;
            }
            return source;
        }
        private Light[] _endgameSceneLights;
        private float[] _endgameSceneLightIntensities;
        private float _endgameAmbientIntensity, _endgameReflectionIntensity;
        private Color _endgameAmbientSky, _endgameAmbientEquator, _endgameAmbientGround, _endgameAmbientFlat;
        private bool _endgameLightingCaptured;

        private void CaptureEndgameLighting()
        {
            if (_endgameLightingCaptured) return;
            _endgameLightingCaptured = true;
            _endgameSceneLights = System.Array.FindAll(FindObjectsByType<Light>(FindObjectsSortMode.None),
                light => light.type == LightType.Directional);
            _endgameSceneLightIntensities = new float[_endgameSceneLights.Length];
            for (var i = 0; i < _endgameSceneLights.Length; i++) _endgameSceneLightIntensities[i] = _endgameSceneLights[i].intensity;
            _endgameAmbientIntensity = RenderSettings.ambientIntensity;
            _endgameReflectionIntensity = RenderSettings.reflectionIntensity;
            _endgameAmbientSky = RenderSettings.ambientSkyColor;
            _endgameAmbientEquator = RenderSettings.ambientEquatorColor;
            _endgameAmbientGround = RenderSettings.ambientGroundColor;
            _endgameAmbientFlat = RenderSettings.ambientLight;
        }

        private void DimEndgameLighting(float t)
        {
            if (!_endgameLightingCaptured) return;
            var multiplier = Mathf.Lerp(1f, .4f, t);
            for (var i = 0; i < _endgameSceneLights.Length; i++)
                if (_endgameSceneLights[i] != null) _endgameSceneLights[i].intensity = _endgameSceneLightIntensities[i] * multiplier;
            RenderSettings.ambientIntensity = _endgameAmbientIntensity * multiplier;
            RenderSettings.reflectionIntensity = _endgameReflectionIntensity * multiplier;
            if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Trilight)
            {
                RenderSettings.ambientSkyColor = _endgameAmbientSky * multiplier;
                RenderSettings.ambientEquatorColor = _endgameAmbientEquator * multiplier;
                RenderSettings.ambientGroundColor = _endgameAmbientGround * multiplier;
            }
            else if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Flat)
                RenderSettings.ambientLight = _endgameAmbientFlat * multiplier;
        }

        private void RestoreEndgameLighting()
        {
            if (!_endgameLightingCaptured) return;
            for (var i = 0; i < _endgameSceneLights.Length; i++)
                if (_endgameSceneLights[i] != null) _endgameSceneLights[i].intensity = _endgameSceneLightIntensities[i];
            RenderSettings.ambientIntensity = _endgameAmbientIntensity;
            RenderSettings.reflectionIntensity = _endgameReflectionIntensity;
            RenderSettings.ambientSkyColor = _endgameAmbientSky;
            RenderSettings.ambientEquatorColor = _endgameAmbientEquator;
            RenderSettings.ambientGroundColor = _endgameAmbientGround;
            if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Flat) RenderSettings.ambientLight = _endgameAmbientFlat;
            _endgameLightingCaptured = false;
        }
        private void BeginEndgamePresentation(bool blueWins)
        {
            ClearHeroSkillCasts();
            CaptureEndgameLighting();
            _endgameBlueWins = blueWins;
            _endgameStartedAt = Time.unscaledTime;
            _startCountdownEndsAt = 0;
            var names = blueWins ? battleBlueTopNames : battleRedTopNames;
            var scores = blueWins ? battleBlueTopScores : battleRedTopScores;
            for (var i = 0; i < 3; i++)
            {
                var source = BattleTopSourceIndex(blueWins, i);
                _endgameNames[i] = source >= 0 ? names[source] : null;
                _endgameDonors[i] = source >= 0 ? _topDonors[blueWins ? 1 : 0,source] : null;
                _endgameScores[i] = source >= 0 ? scores[source] : 0;
            }
            var losingTeam = blueWins ? RecoilCastleTeam.Red : RecoilCastleTeam.Blue;
            if (!TryGetCastleBounds(losingTeam, out var bounds))
                bounds = new Bounds(GetSpawnTargetPosition(!blueWins), Vector3.one * 25f);
            _endgameFocus = bounds.center + Vector3.up * bounds.size.y * .1f;
            _endgameImpact = new Vector3(bounds.center.x, bounds.min.y + .5f, bounds.center.z);
            _endgameCamera = Camera.main;
            if (_endgameCamera == null) return;
            _endgameStartPosition = _endgameCamera.transform.position;
            _endgameStartRotation = _endgameCamera.transform.rotation;
            _endgameOriginalFar = _endgameCamera.farClipPlane;
            _endgameStartOrtho = _endgameCamera.orthographicSize;
            var middle = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
            var towardField = middle - bounds.center; towardField.y = 0;
            towardField = towardField.sqrMagnitude > .01f ? towardField.normalized : Vector3.back;
            var radius = Mathf.Max(15f, bounds.extents.magnitude);
            var halfFov = Mathf.Min(_endgameCamera.fieldOfView * .5f,
                Mathf.Atan(Mathf.Tan(_endgameCamera.fieldOfView * .5f * Mathf.Deg2Rad) * _endgameCamera.aspect) * Mathf.Rad2Deg);
            var distance = radius / Mathf.Max(.1f, Mathf.Tan(halfFov * Mathf.Deg2Rad)) * 1.25f;
            _endgameCameraPosition = _endgameFocus + (towardField + Vector3.up * .8f).normalized * distance;
            _endgameCameraRotation = Quaternion.LookRotation(_endgameFocus - _endgameCameraPosition, Vector3.up);
            _endgameTargetOrtho = radius * 1.3f / Mathf.Min(1f, _endgameCamera.aspect);
            _endgameCamera.farClipPlane = Mathf.Max(_endgameOriginalFar, distance + radius * 5f);
        }

        private void LateUpdate()
        {
            UpdateSystemCursorVisibility();
            if (!_matchFinished || _endgameStartedAt < 0 || _endgameLeaving) return;
            var elapsed = Time.unscaledTime - _endgameStartedAt;
            DimEndgameLighting(Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / endgameCameraSeconds)));
            var move = Mathf.SmoothStep(0, 1, Mathf.Clamp01(elapsed / endgameCameraSeconds));
            var position = Vector3.Lerp(_endgameStartPosition, _endgameCameraPosition, move);
            var rotation = Quaternion.Slerp(_endgameStartRotation, _endgameCameraRotation, move);
            var impactTime = elapsed - endgameCameraSeconds;
            if (impactTime >= 0 && !_endgameExplosionPlayed)
            {
                _endgameExplosionPlayed = true;
                if (_endgameMeteor != null)
                {
                    _endgameMeteor.transform.SetPositionAndRotation(_endgameImpact, endgameMeteorPrefab.transform.rotation);
                    _endgameMeteor.transform.parent.gameObject.SetActive(true);
                    _endgameMeteor.SetActive(true);
                    foreach (var particle in _endgameMeteorParticles) particle.Play(false);
                }
            }
            if (_endgameCamera == null) return;
            if (impactTime >= 0)
            {
                var decay = Mathf.Pow(1f - Mathf.Clamp01(impactTime / 1.4f), 2f);
                var shake = new Vector3(Mathf.PerlinNoise(impactTime * 28, 1) * 2 - 1,
                    Mathf.PerlinNoise(3, impactTime * 32) * 2 - 1, 0) * endgameShakeStrength * decay;
                position += rotation * shake;
                rotation *= Quaternion.Euler(shake.y * .65f, shake.x * .45f, shake.x * .8f);
            }
            _endgameCamera.transform.SetPositionAndRotation(position, rotation);
            if (_endgameCamera.orthographic)
                _endgameCamera.orthographicSize = Mathf.Lerp(_endgameStartOrtho, _endgameTargetOrtho, move);
        }

        private void EnsureEndgameNameGradient()
        {
            if (_endgameNameGradient != null) return;
            const int width = 128;
            _endgameNameGradient = new Texture2D(width, 1, TextureFormat.RGBA32, false)
            {
                name = "Endgame top name fade",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color[width];
            for (var i = 0; i < width; i++)
            {
                var t = i / (width - 1f);
                pixels[i] = new Color(0, 0, 0, .7f * Mathf.SmoothStep(0, 1, Mathf.Min(t, 1f - t) * 3f));
            }
            _endgameNameGradient.SetPixels(pixels);
            _endgameNameGradient.Apply(false, true);
        }
        private static float EndgameBack(float t)
        {
            t = Mathf.Clamp01(t) - 1f;
            return 1f + 2.70158f * t * t * t + 1.70158f * t * t;
        }

        private void DrawEndgamePresentation()
        {
            if (_endgameStartedAt < 0) return;
            var elapsed = Time.unscaledTime - _endgameStartedAt;
            var impactTime = elapsed - endgameCameraSeconds;
            var oldColor = GUI.color;
            var oldMatrix = GUI.matrix;
            if (impactTime >= 0 && impactTime < .28f)
            {
                GUI.color = new Color(1, .83f, .6f, .38f * (1f - impactTime / .28f));
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            }
            var reveal = impactTime - 1f;
            if (reveal < 0) { GUI.color = oldColor; return; }
            var scale = Mathf.Min(Screen.width / 1920f, Screen.height / 1080f);
            var fade = Mathf.Clamp01(reveal / .6f);
            GUI.color = new Color(0, 0, 0, .12f * fade);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            _endgameHeadingStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, richText = false };
            _endgameNameStyle ??= new GUIStyle(_endgameHeadingStyle) { clipping = TextClipping.Clip, wordWrap = false };
            _endgameScoreStyle ??= new GUIStyle(_endgameHeadingStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            _endgameHeadingStyle.fontSize = Mathf.Max(18, Mathf.RoundToInt(40 * scale));
            _endgameNameStyle.fontSize = Mathf.Max(14, Mathf.RoundToInt(28 * scale));
            _endgameScoreStyle.fontSize = Mathf.Max(18, Mathf.RoundToInt(38 * scale));
            var title = new Rect(Screen.width * .5f - 560 * scale, 0 * scale, 1120 * scale, 280 * scale);
            var flagEase = EndgameBack((reveal - .25f) / .75f);
            var flagWidth = 245 * 3f * .8f * scale;
            var flagX = _endgameBlueWins ? title.xMax + 8 * scale : title.x - flagWidth - 8 * scale;
            flagX = Mathf.Clamp(flagX, 12 * scale, Screen.width - flagWidth - 12 * scale);
            var flag = new Rect(flagX, Mathf.Max(12 * scale, title.center.y - 348 * scale) +
                (1f - flagEase) * -130 * scale, flagWidth, 696 * scale);
            var flagImage = _endgameBlueWins ? endgameBlueFlagImage : endgameRedFlagImage;
            GUI.color = new Color(1, 1, 1, Mathf.Clamp01((reveal - .25f) * 3));
            if (flagImage != null) GUI.DrawTexture(flag, flagImage, ScaleMode.ScaleToFit, true);
            // The flag and footer remain fixed; all victory and ranking content shares this scroll.
            float resultViewportHeight = Mathf.Min(Screen.height, 975 * scale);
            float rankingRowHeight = Mathf.Max(46, 64 * scale);
            float resultContentHeight = Mathf.Max(resultViewportHeight,
                722 * scale + Mathf.Max(redRanking.Count, blueRanking.Count) * (rankingRowHeight + 1) + 110 * scale);
            rankingScroll = GUI.BeginScrollView(new Rect(0, 0, Screen.width, resultViewportHeight), rankingScroll,
                new Rect(0, 0, Screen.width - 18, resultContentHeight), false, false, GUIStyle.none, GUIStyle.none);
            var titleEase = EndgameBack(reveal / .8f);
            GUI.matrix = oldMatrix;
            GUIUtility.ScaleAroundPivot(Vector2.one * Mathf.Max(.01f, Mathf.LerpUnclamped(.7f, 1f, titleEase)), title.center);
            GUI.color = new Color(1, 1, 1, fade);
            if (endgameTitleImage != null) GUI.DrawTexture(title, endgameTitleImage, ScaleMode.ScaleToFit, true);
            else GUI.Label(title, "KẾT THÚC TRẬN ĐẤU", _endgameHeadingStyle);
            GUI.matrix = oldMatrix;
            SetLabelTextColor(_endgameHeadingStyle, _endgameBlueWins ? new Color(.45f, .75f, 1f) : new Color(1f, .45f, .3f));
            DrawHighlightedLabel(new Rect(Screen.width * .5f - 320 * scale, 245 * scale, 640 * scale, 45 * scale),
                _endgameBlueWins ? "ĐỘI XANH CHIẾN THẮNG" : "ĐỘI ĐỎ CHIẾN THẮNG", _endgameHeadingStyle, scale);
            for (var rank = 0; rank < 3; rank++)
            {
                if (string.IsNullOrWhiteSpace(_endgameNames[rank])) continue;
                var local = reveal - .65f - rank * .22f;
                if (local < 0) continue;
                var t = Mathf.Clamp01(local / .85f);
                var land = EndgameBack(t);
                var x = Screen.width * .5f + (rank == 0 ? 0 : rank == 1 ? -300 : 300) * scale;
                var targetY = (rank == 0 ? 295 : 310) * scale;
                var y = Mathf.LerpUnclamped(-340 * scale, targetY, land);
                var card = new Rect(x - 80 * scale, y, 160 * scale, 174 * scale);
                var bounce = Mathf.Sin(Mathf.Clamp01((t - .7f) / .3f) * Mathf.PI) * .07f;
                GUI.matrix = oldMatrix;
                GUIUtility.ScaleAroundPivot(new Vector2(1 + bounce, 1 - bounce), card.center);
                GUIUtility.RotateAroundPivot(Mathf.Sin(t * Mathf.PI * 2) * (1 - t) * (rank == 1 ? -6 : 6), card.center);
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01(local * 4));
                if (battleTopBorderImages != null && rank < battleTopBorderImages.Length && battleTopBorderImages[rank] != null)
                {
                    DrawDonationAvatar(card, _endgameDonors[rank]);
                    GUI.DrawTexture(card, battleTopBorderImages[rank], ScaleMode.ScaleToFit, true);
                }
                GUI.matrix = oldMatrix;
                var nameRect = new Rect(x - 145 * scale, y + card.height + 4 * scale, 290 * scale, 30 * scale);
                EnsureEndgameNameGradient();
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01(local * 4));
                GUI.DrawTexture(new Rect(nameRect.x, nameRect.y, nameRect.width, nameRect.height + 54 * scale),
                    _endgameNameGradient, ScaleMode.StretchToFill, true);
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01(local * 4));
                _endgameNameStyle.fontSize = Mathf.Max(12, Mathf.RoundToInt(22 * scale));
                var fittedName = FitTopName(_endgameNames[rank], _endgameNameStyle, nameRect.width);
                DrawOutlinedNameLabel(nameRect, fittedName, _endgameNameStyle, scale,
                    UserNameColor(_endgameNames[rank], _endgameBlueWins));
                SetLabelTextColor(_endgameScoreStyle, new Color(1f, .82f, .3f));
                var points = Mathf.RoundToInt(_endgameScores[rank] * Mathf.SmoothStep(0, 1, Mathf.Clamp01((local - .5f) / 1.2f)));
                var scoreY = string.IsNullOrWhiteSpace(_endgameNames[rank])
                    ? nameRect.y + 23 * scale : nameRect.yMax + 4 * scale;
                _endgameScoreStyle.fontSize = Mathf.Max(14, Mathf.RoundToInt(28 * scale));
                var scoreRect = new Rect(card.center.x - nameRect.width * .5f, scoreY, nameRect.width, 36 * scale);
                DrawHighlightedLabel(scoreRect, points.ToString("N0"), _endgameScoreStyle, scale);
            }
            GUI.matrix = oldMatrix;
            GUI.color = Color.white;
            DrawMatchScoring();
            GUI.EndScrollView();
            DrawEndgameButtons(reveal, scale);
            GUI.color = oldColor;
        }
    }
}
