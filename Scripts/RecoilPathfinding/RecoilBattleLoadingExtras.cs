using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Last loading steps before a match, so the battle itself does not create or load things on first use: battle
    /// sounds, combat / crowd buffers sized for the pooled army, siege impacts and projectiles, the HUD's glyphs, the
    /// hero intro player, and a garbage collection while the loading bar is still up.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        private const int PrewarmedSoldierProjectiles = 128;
        private const int PrewarmedHeroProjectiles = 8;
        private bool _hudGlyphsWarmed;

        private IEnumerator PrewarmBattleExtras(List<PrewarmPool> pools)
        {
            _poolPrewarmLabel = "Đang nạp âm thanh…";
            yield return PreloadBattleAudio();

            _poolPrewarmLabel = "Đang chuẩn bị hiệu ứng…";
            PrewarmSiegeImpacts();
            yield return null;
            yield return PrewarmAdvisorEffects();
            PrewarmProjectiles(pools);
            yield return null;

            _poolPrewarmLabel = "Đang chuẩn bị bộ nhớ trận…";
            ReserveBattleBuffers(pools);
            try { EnsureHeroIntroPlayer(); }
            catch (System.Exception exception) { Debug.LogException(exception, this); }
            yield return null;

            _poolPrewarmLabel = "Đang dọn bộ nhớ…";
            yield return Resources.UnloadUnusedAssets();
            System.GC.Collect();
            yield return null;
        }

        // ---------------------------------------------------------------- 1. sounds

        /// <summary>Most battle clips do not preload their data: load it now instead of on the first hit.</summary>
        private IEnumerator PreloadBattleAudio()
        {
            var clips = new HashSet<AudioClip> { battleAmbienceClip, armyMovementClip, warDrumClip };
            var effects = RecoilBattleEffects.Load();
            if (effects != null) clips.Add(effects.ExplosionSound);
            if (unitAudioBanks != null)
                foreach (var bank in unitAudioBanks)
                {
                    if (bank == null) continue;
                    clips.UnionWith(bank.attacks);
                    clips.UnionWith(bank.deaths);
                    clips.UnionWith(bank.movement);
                }
            clips.Remove(null);
            var loaded = 0;
            foreach (var clip in clips)
            {
                if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                if (++loaded % 8 == 0)
                {
                    _poolPrewarmLabel = $"Đang nạp âm thanh… {loaded}/{clips.Count}";
                    yield return null; // spread the decoding over frames so the bar keeps moving
                }
            }
            Debug.Log($"[Battle loading] Preloaded {clips.Count} battle sound clips.", this);
        }

        // ---------------------------------------------------------------- 2. buffers

        /// <summary>Combat and crowd buffers sized for every pooled unit, so they never grow (reallocate) mid-battle.</summary>
        private void ReserveBattleBuffers(List<PrewarmPool> pools)
        {
            var units = 0;
            foreach (var pool in pools) units += Mathf.Max(pool.Pool.Count, pool.Target);
            if (units <= 0) return;
            if (!_combatJobPending) EnsureCombatBuffers(units);
            if (pathService != null) pathService.PrewarmCrowdCapacity(units);
            Debug.Log($"[Battle loading] Reserved combat / crowd buffers for {units:N0} units.", this);
        }

        // ---------------------------------------------------------------- 3. effects

        private void PrewarmSiegeImpacts()
        {
            if (siegeImpactPrefab == null && !_siegeImpactDefaultsLoaded)
            {
                _siegeImpactDefaultsLoaded = true;
                var defaults = Resources.Load<RecoilBattleEffects>("RecoilBattleEffects");
                if (defaults != null) siegeImpactPrefab = defaults.SiegeImpactPrefab;
            }
            if (siegeImpactPrefab == null) return;
            while (_siegeImpactEffects.Count < MaximumSiegeImpactEffects) _siegeImpactEffects.Add(CreateSiegeImpactEffect());
        }

        /// <summary>Projectiles of every ranged soldier template and the heroes' flying skills, created hidden.</summary>
        private void PrewarmProjectiles(List<PrewarmPool> pools)
        {
            var projectilePool = GetOrCreateVisualProjectilePool();
            var done = new HashSet<GameObject>();
            foreach (var pool in pools)
            {
                if (pool.Template == null) continue;
                var presentation = pool.Template.GetComponentInChildren<RecoilCharacterPresentation>(true);
                if (presentation != null && presentation.ProjectilePrefab != null && done.Add(presentation.ProjectilePrefab))
                    projectilePool.Prewarm(presentation.ProjectilePrefab, PrewarmedSoldierProjectiles);
                var hero = pool.Template.GetComponent<NhiQuoc.Tools.HeroVatPlayer>();
                if (hero == null || hero.clips == null) continue;
                foreach (var clip in hero.clips) // only flying skills use this pool; other FX are pooled by the hero
                    if (clip != null && clip.straightProjectile && clip.effects != null)
                        foreach (var fx in clip.effects)
                            if (fx != null && fx.prefab != null && done.Add(fx.prefab))
                                projectilePool.Prewarm(fx.prefab, PrewarmedHeroProjectiles);
            }
        }

        // ---------------------------------------------------------------- 4. HUD glyphs

        /// <summary>
        /// Called from OnGUI while the loading bar shows (the IMGUI font is only reachable there): builds the default
        /// font's glyphs for digits and Vietnamese letters at the battle HUD's sizes, so the first "BỂ ĐIỂM", score or
        /// hero name does not rebuild the font atlas in the middle of the fight.
        /// </summary>
        private void WarmHudGlyphs()
        {
            if (_hudGlyphsWarmed) return;
            _hudGlyphsWarmed = true;
            var font = GUI.skin.label.font != null ? GUI.skin.label.font : GUI.skin.font;
            if (font == null || !font.dynamic) return;
            const string letters = "0123456789:.,%/+-–— ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
                                   "ÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬĐÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴ" +
                                   "àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ";
            var hud = battleHudLayout ?? new RecoilBattleHudLayout();
            var scale = RecoilBattleHudLayout.HudScale(Screen.width);
            var score = RecoilBattleHudLayout.FontSize(hud.poolScore, BattleHudPieceId.PoolScore, Screen.width);
            var sizes = new HashSet<int>
            {
                13, 15,
                RecoilBattleHudLayout.FontSize(hud.poolTitle, BattleHudPieceId.PoolTitle, Screen.width),
                score, Mathf.RoundToInt(score * 1.8f),
                Mathf.RoundToInt(18f * scale), Mathf.RoundToInt(20f * scale), Mathf.RoundToInt(34f * scale)
            };
            foreach (var size in sizes)
                if (size > 0) font.RequestCharactersInTexture(letters, size, FontStyle.Bold);
        }
    }
}
