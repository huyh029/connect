using System;
using System.Collections.Generic;
using System.IO;
using NhiQuoc.Tools;
using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Lightweight hero fight for quick checks, without the battle scene: the real hero VAT prefabs, skill data
    /// (RecoilHeroSkills) and CSV stats on a flat ground. Each hero walks to the nearest enemy hero, probes the reach
    /// of all its usable skills (attack 1, attack 2, ulti when ready), stands still while casting, and deals damage with
    /// the same hit zones and frames as the battle. Dead heroes respawn. Set up and started by
    /// Game Tools > Công Thành > Battle > "Test 8 tướng".
    /// </summary>
    public sealed class RecoilHeroArena : MonoBehaviour
    {
        [Serializable]
        public struct Entry
        {
            public int heroId;
            public bool blue;
        }

        [Tooltip("Tướng ra trận và phe (tool Test 8 tướng điền vào).")]
        public List<Entry> heroes = new();
        [Tooltip("Hai phe đứng cách tâm sân chừng này (m) lúc bắt đầu.")]
        [Min(2f)] public float sideDistance = 30f;
        [Tooltip("Khoảng cách giữa các tướng cùng phe lúc đứng vào sân (m).")]
        [Min(1f)] public float spacing = 9f;
        [Tooltip("Bán kính thân / trúng đòn của tướng (như trong trận).")]
        [Min(.1f)] public float bodyRadius = 2f;
        [Tooltip("Hồi sinh nhanh sau Quick Respawn Seconds thay vì thời gian hồi sinh trong CSV.")]
        public bool quickRespawn = true;
        [Min(0f)] public float quickRespawnSeconds = 5f;

        private const string CsvName = "recoil_hero_stats.csv";
        private const int IdleClip = 0, RunClip = 1, DeathClip = 3, UltiClip = 4;

        private sealed class Fighter
        {
            public Entry entry;
            public RecoilHeroConfig config;
            public HeroSkillProfile profile;
            public GameObject instance;
            public HeroVatPlayer vat;
            public Vector3 spawn;
            public int health;
            public bool alive;
            public float respawnAt, hideAt, nextAttack, nextUlti, animUntil;
            public int clip = -1;
            public Cast cast;
            public int casts, damageDealt;
            public string name;
        }

        private sealed class Cast
        {
            public HeroSkillClip skill;
            public HeroVatPlayer.Clip clip;
            public float started;
            public Quaternion facing;
            public Vector3 target;
            public Fighter targetFighter;
            public int[] nextFrames;
            public HashSet<Fighter>[] swept;
            public bool[] launched;
        }

        private sealed class Shot
        {
            public Fighter owner;
            public HeroHitZone zone;
            public Vector3 position, direction;
            public float remaining, speed;
            public readonly HashSet<Fighter> hit = new();
        }

        private readonly List<Fighter> _fighters = new();
        private readonly List<Shot> _shots = new();
        private readonly List<int> _candidates = new();
        private RecoilHeroPrefabCatalog _catalog;
        private RecoilHeroConfig[] _configs;
        private GUIStyle _label;
        private string _error;

        public bool Running => _fighters.Count > 0;

        private void Start() => Restart(heroes, quickRespawn);

        /// <summary>Clears the field and puts the given heroes on it.</summary>
        public void Restart(IReadOnlyList<Entry> entries, bool quick)
        {
            quickRespawn = quick;
            foreach (var fighter in _fighters) if (fighter.instance) Destroy(fighter.instance);
            _fighters.Clear();
            _shots.Clear();
            _error = null;
            _catalog ??= Resources.Load<RecoilHeroPrefabCatalog>("RecoilHeroPrefabs");
            if (_catalog == null || _catalog.Skills == null) { _error = "Thiếu Resources/RecoilHeroPrefabs."; return; }
            try { _configs = RecoilHeroConfigCsv.Read(File.ReadAllText(Path.Combine(Application.streamingAssetsPath, CsvName))); }
            catch (Exception exception) { _error = "Không đọc được " + CsvName + ": " + exception.Message; return; }

            int blueCount = 0, redCount = 0;
            foreach (var entry in entries) if (entry.blue) blueCount++; else redCount++;
            int blueIndex = 0, redIndex = 0;
            foreach (var entry in entries)
            {
                var config = Array.Find(_configs, c => c.Id == entry.heroId);
                var prefab = _catalog.Find(entry.heroId);
                if (config == null || prefab == null) { Debug.LogWarning($"[Đấu trường] Thiếu cấu hình / prefab tướng {entry.heroId}.", this); continue; }
                int slot = entry.blue ? blueIndex++ : redIndex++;
                int count = entry.blue ? blueCount : redCount;
                var spawn = new Vector3(entry.blue ? -sideDistance : sideDistance, 0f, (slot - (count - 1) * .5f) * spacing);
                var fighter = new Fighter
                {
                    entry = entry, config = config, profile = _catalog.Skills.Find(entry.heroId), spawn = spawn,
                    name = config.Name
                };
                _fighters.Add(fighter);
                Spawn(fighter, prefab);
            }
        }

        private void Spawn(Fighter fighter, GameObject prefab = null)
        {
            if (fighter.instance == null)
            {
                prefab ??= _catalog.Find(fighter.entry.heroId);
                fighter.instance = Instantiate(prefab, fighter.spawn,
                    Quaternion.LookRotation(fighter.entry.blue ? Vector3.right : Vector3.left), transform);
                fighter.instance.name = (fighter.entry.blue ? "Xanh " : "Đỏ ") + fighter.name;
                fighter.vat = fighter.instance.GetComponentInChildren<HeroVatPlayer>();
            }
            else
            {
                fighter.instance.transform.SetPositionAndRotation(fighter.spawn,
                    Quaternion.LookRotation(fighter.entry.blue ? Vector3.right : Vector3.left));
                fighter.instance.SetActive(true);
            }
            fighter.health = fighter.config.Stats.MaxHealth;
            fighter.alive = true;
            fighter.cast = null;
            fighter.clip = -1;
            fighter.animUntil = fighter.nextAttack = Time.time;
            fighter.nextUlti = Time.time + fighter.config.UltiCooldown;
            PlayLoop(fighter, IdleClip);
        }

        // ---------------------------------------------------------------- fight loop

        private void Update()
        {
            float now = Time.time, dt = Time.deltaTime;
            foreach (var fighter in _fighters)
            {
                if (!fighter.alive)
                {
                    if (fighter.instance && fighter.instance.activeSelf && now >= fighter.hideAt) fighter.instance.SetActive(false);
                    if (now >= fighter.respawnAt) Spawn(fighter);
                    continue;
                }
                if (fighter.vat == null) continue;
                if (fighter.cast != null) { RunCast(fighter, now); continue; } // stands still while casting
                if (now < fighter.animUntil) continue;

                var target = NearestEnemy(fighter);
                if (target == null) { PlayLoop(fighter, IdleClip); continue; }
                var offset = target.instance.transform.position - fighter.instance.transform.position;
                offset.y = 0f;
                float distance = offset.magnitude;
                float configured = fighter.config.Stats.Range;
                if (distance > EngagementRange(fighter, configured))
                {
                    var step = offset.normalized * (fighter.config.Stats.MoveSpeed * dt);
                    var position = fighter.instance.transform.position + step;
                    position = KeepApart(fighter, position);
                    fighter.instance.transform.SetPositionAndRotation(position, Quaternion.LookRotation(offset));
                    PlayLoop(fighter, RunClip);
                    continue;
                }
                if (offset.sqrMagnitude > .0001f) fighter.instance.transform.rotation = Quaternion.LookRotation(offset);
                if (now < fighter.nextAttack) { PlayLoop(fighter, IdleClip); continue; }
                int chosen = ChooseSkill(fighter, distance, configured);
                if (chosen < 0) chosen = Longest(fighter, configured);
                BeginCast(fighter, target, chosen, now);
            }

            for (int i = _shots.Count - 1; i >= 0; i--)
                if (!AdvanceShot(_shots[i], dt)) _shots.RemoveAt(i);
            LogSummary(now);
        }

        [Tooltip("Ghi một dòng [Arena] tóm tắt mỗi vài giây vào Console (máu, số chiêu, sát thương của từng tướng).")]
        public bool logSummary = true;
        [Min(1f)] public float logEvery = 5f;
        private float _nextLog;

        private void LogSummary(float now)
        {
            if (!logSummary || _fighters.Count == 0 || now < _nextLog) return;
            _nextLog = now + logEvery;
            var b = new System.Text.StringBuilder("[Arena] t=").Append(now.ToString("0"));
            foreach (var f in _fighters)
            {
                var target = f.alive ? NearestEnemy(f) : null;
                float distance = target != null
                    ? Vector3.Distance(f.instance.transform.position, target.instance.transform.position) : -1f;
                b.Append(" | ").Append(f.name).Append(f.entry.blue ? "(xanh)" : "(đỏ)")
                    .Append(f.alive ? $" máu {f.health * 100f / Mathf.Max(1, f.config.Stats.MaxHealth):0}%" : " CHẾT")
                    .Append(" chiêu ").Append(f.casts).Append(" st ").Append(f.damageDealt)
                    .Append(distance >= 0 ? $" cách {distance:0.0}/tầm {EngagementRange(f, f.config.Stats.Range):0.0}" : "");
            }
            Debug.Log(b.ToString(), this);
        }

        private void PlayLoop(Fighter fighter, int clip)
        {
            if (fighter.clip == clip || fighter.vat == null) return;
            fighter.clip = clip;
            fighter.vat.PlayExact(clip);
        }

        private Fighter NearestEnemy(Fighter fighter)
        {
            Fighter best = null;
            float bestDistance = float.MaxValue;
            foreach (var other in _fighters)
            {
                if (!other.alive || other.entry.blue == fighter.entry.blue) continue;
                float d = (other.instance.transform.position - fighter.instance.transform.position).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = other; }
            }
            return best;
        }

        /// <summary>Heroes do not walk through each other: push the step out of any other living hero's body.</summary>
        private Vector3 KeepApart(Fighter fighter, Vector3 position)
        {
            foreach (var other in _fighters)
            {
                if (other == fighter || !other.alive || other.instance == null) continue;
                var away = position - other.instance.transform.position;
                away.y = 0f;
                float min = bodyRadius * 2f, d = away.magnitude;
                if (d < min && d > .0001f) position += away / d * (min - d);
            }
            position.y = 0f;
            return position;
        }

        // ---------------------------------------------------------------- skills (same rules as the battle)

        private bool UltiReady(Fighter fighter) =>
            fighter.config.UltiCooldown > 0 && Time.time >= fighter.nextUlti && fighter.vat.clips != null && fighter.vat.clips.Length > UltiClip;

        private void Candidates(Fighter fighter)
        {
            _candidates.Clear();
            if (UltiReady(fighter)) _candidates.Add(UltiClip);
            int next = fighter.vat.NextAttackClipIndex;
            _candidates.Add(next);
            int other = next == 5 ? 2 : 5;
            if (other < fighter.vat.clips.Length) _candidates.Add(other);
        }

        private float Reach(Fighter fighter, int clip, float configured)
        {
            var skill = fighter.profile?.Find(clip);
            if (skill == null || clip >= fighter.vat.clips.Length) return -1;
            float reach = 0;
            var zoneScale = RecoilAutoBattleDemo.HeroZoneScale(fighter.vat);
            foreach (var zone in skill.Zones) if (zone.Enabled) reach = Mathf.Max(reach, zone.EngagementReach(configured, zoneScale));
            return reach <= 0 ? -1 : Mathf.Max(.1f, reach - Mathf.Min(.15f, reach * .05f));
        }

        private float EngagementRange(Fighter fighter, float configured)
        {
            Candidates(fighter);
            float best = -1;
            foreach (var clip in _candidates) best = Mathf.Max(best, Reach(fighter, clip, configured));
            return best > 0 ? best : configured;
        }

        private int ChooseSkill(Fighter fighter, float distance, float configured)
        {
            Candidates(fighter);
            foreach (var clip in _candidates)
            {
                float reach = Reach(fighter, clip, configured);
                if (reach > 0 && distance <= reach) return clip;
            }
            return -1;
        }

        private int Longest(Fighter fighter, float configured)
        {
            Candidates(fighter);
            int best = _candidates.Count > 0 ? _candidates[0] : 2;
            float bestReach = -1;
            foreach (var clip in _candidates)
            {
                float reach = Reach(fighter, clip, configured);
                if (reach > bestReach) { bestReach = reach; best = clip; }
            }
            return best;
        }

        private void BeginCast(Fighter fighter, Fighter target, int clipIndex, float now)
        {
            var clips = fighter.vat.clips;
            if (clips == null || clipIndex < 0 || clipIndex >= clips.Length) return;
            var targetPosition = target.instance.transform.position;
            fighter.vat.SetAttackTarget(targetPosition, target.instance.transform);
            fighter.vat.PlayExact(clipIndex);
            fighter.clip = clipIndex;
            var clip = clips[clipIndex];
            fighter.animUntil = now + clip.duration;
            fighter.nextAttack = now + Mathf.Max(fighter.config.Stats.AttackInterval, clip.duration);
            if (clipIndex == UltiClip) fighter.nextUlti = now + fighter.config.UltiCooldown;
            fighter.casts++;
            var skill = fighter.profile?.Find(clipIndex);
            if (skill == null) return;
            var frames = new int[skill.Zones.Length];
            for (int z = 0; z < frames.Length; z++) frames[z] = Mathf.Max(0, skill.Zones[z].StartFrame);
            fighter.cast = new Cast
            {
                skill = skill, clip = clip, started = now, facing = fighter.instance.transform.rotation,
                target = targetPosition, targetFighter = target, nextFrames = frames,
                swept = new HashSet<Fighter>[frames.Length], launched = new bool[frames.Length]
            };
        }

        private void RunCast(Fighter fighter, float now)
        {
            var cast = fighter.cast;
            if (cast.targetFighter != null && cast.targetFighter.alive && cast.targetFighter.instance != null &&
                Array.Exists(cast.skill.Zones, z => z.Enabled && z.Anchor == HeroHitAnchor.FollowTarget))
                fighter.vat.UpdateActiveEffectTarget(cast.targetFighter.instance.transform.position);
            float elapsed = now - cast.started;
            int frame = HeroSkillTimeline.FrameAt(elapsed, cast.clip.duration, cast.clip.frameCount);
            for (int z = 0; z < cast.skill.Zones.Length; z++)
            {
                var zone = cast.skill.Zones[z];
                int end = Mathf.Clamp(Mathf.Max(zone.StartFrame, zone.EndFrame), 0, cast.clip.frameCount - 1);
                while (zone.Enabled && cast.nextFrames[z] <= frame && cast.nextFrames[z] <= end)
                {
                    int hitFrame = cast.nextFrames[z];
                    if (zone.IsProjectile)
                    {
                        if (!cast.launched[z]) Launch(fighter, cast, zone);
                        cast.launched[z] = true;
                        cast.nextFrames[z] = int.MaxValue;
                        break;
                    }
                    ResolveZone(fighter, cast, zone, hitFrame, z);
                    cast.nextFrames[z] = HeroSkillTimeline.NextPulse(hitFrame, zone.IsSweep ? 1 : zone.RepeatFrames, end);
                }
            }
            if (elapsed >= cast.clip.duration) fighter.cast = null;
        }

        private void ResolveZone(Fighter fighter, Cast cast, HeroHitZone zone, int frame, int zoneIndex)
        {
            var follow = cast.targetFighter != null && cast.targetFighter.alive && cast.targetFighter.instance != null
                ? cast.targetFighter.instance.transform.position : cast.target;
            RecoilAutoBattleDemo.HeroZonePose(zone, cast.clip, frame, fighter.instance.transform.position,
                zone.Anchor == HeroHitAnchor.FollowTarget ? follow : cast.target, cast.facing,
                RecoilAutoBattleDemo.HeroZoneScale(fighter.vat), out var center, out var rotation);
            var toAuthored = 1f / RecoilAutoBattleDemo.HeroZoneScale(fighter.vat);
            var inverse = Quaternion.Inverse(rotation);
            float angle = -1;
            if (zone.IsSweep) zone.SweepSample(frame, out _, out angle);
            var once = zone.IsSweep ? cast.swept[zoneIndex] ??= new HashSet<Fighter>() : null;
            foreach (var enemy in _fighters)
            {
                if (!enemy.alive || enemy.entry.blue == fighter.entry.blue) continue;
                var local = inverse * (enemy.instance.transform.position - center) * toAuthored;
                if (!zone.Contains(local, bodyRadius * toAuthored, angle) || once != null && !once.Add(enemy)) continue;
                Damage(fighter, enemy, fighter.config.Stats.Damage * zone.DamageScale(local));
            }
        }

        private void Launch(Fighter fighter, Cast cast, HeroHitZone zone)
        {
            var origin = fighter.instance.transform.position +
                         cast.facing * (zone.Offset * RecoilAutoBattleDemo.HeroZoneScale(fighter.vat));
            var direction = cast.facing * Quaternion.Euler(0f, zone.Yaw, 0f) * Vector3.forward;
            direction.y = 0f;
            float range = Mathf.Max(fighter.config.Stats.Range + 2f, Vector3.Distance(origin, cast.target) + 2f);
            _shots.Add(new Shot
            {
                owner = fighter, zone = zone, position = origin, direction = direction.normalized,
                remaining = range, speed = Mathf.Max(.01f, zone.ProjectileSpeed)
            });
        }

        private bool AdvanceShot(Shot shot, float dt)
        {
            float step = Mathf.Min(shot.remaining, shot.speed * dt);
            var start = shot.position;
            shot.position += shot.direction * step;
            shot.remaining -= step;
            foreach (var enemy in _fighters)
            {
                if (!enemy.alive || enemy.entry.blue == shot.owner.entry.blue || shot.hit.Contains(enemy)) continue;
                var p = enemy.instance.transform.position;
                var t = Mathf.Clamp01(Vector3.Dot(p - start, shot.direction) / Mathf.Max(step, .0001f));
                var closest = start + shot.direction * (step * t);
                closest.y = p.y = 0f;
                if (Vector3.Distance(closest, p) > shot.zone.Radius * RecoilAutoBattleDemo.HeroZoneScale(shot.owner.vat) + bodyRadius) continue;
                shot.hit.Add(enemy);
                Damage(shot.owner, enemy, shot.owner.config.Stats.Damage * shot.zone.DamageMultiplier);
            }
            return shot.remaining > 0f;
        }

        private void Damage(Fighter attacker, Fighter target, float raw)
        {
            int damage = RecoilUnitDamage.Calculate(raw, attacker.config.Stats, target.config.Stats);
            if (damage <= 0) return;
            target.health = Mathf.Max(0, target.health - damage);
            attacker.damageDealt += damage;
            if (target.health > 0) return;
            target.alive = false;
            target.cast = null;
            target.vat?.PlayExact(DeathClip);
            target.clip = DeathClip;
            float deathLength = target.vat != null && target.vat.clips.Length > DeathClip ? target.vat.clips[DeathClip].duration : 1f;
            target.hideAt = Time.time + deathLength + .5f;
            target.respawnAt = Time.time + Mathf.Max(deathLength + .5f,
                quickRespawn ? quickRespawnSeconds : target.config.RespawnSeconds);
        }

        // ---------------------------------------------------------------- status

        /// <summary>One line per hero for the test tool: side, health, skills cast, damage dealt.</summary>
        public void GetStatus(List<RecoilAutoBattleDemo.TestHeroInfo> into)
        {
            into.Clear();
            foreach (var fighter in _fighters)
                into.Add(new RecoilAutoBattleDemo.TestHeroInfo
                {
                    Id = fighter.entry.heroId, Name = fighter.name, Blue = fighter.entry.blue, Alive = fighter.alive,
                    Waiting = !fighter.alive, Health01 = fighter.health / (float)Mathf.Max(1, fighter.config.Stats.MaxHealth),
                    Casts = fighter.casts, Damage = fighter.damageDealt
                });
        }

        private void OnGUI()
        {
            _label ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13 };
            if (_error != null) GUI.Label(new Rect(10, 10, 600, 24), _error);
            var camera = Camera.main;
            if (camera == null) return;
            foreach (var fighter in _fighters)
            {
                if (!fighter.alive || fighter.instance == null) continue;
                var screen = camera.WorldToScreenPoint(fighter.instance.transform.position + Vector3.up * 7f);
                if (screen.z <= 0) continue;
                var rect = new Rect(screen.x - 60f, Screen.height - screen.y - 30f, 120f, 18f);
                var color = GUI.color;
                GUI.color = fighter.entry.blue ? new Color(.6f, .8f, 1f) : new Color(1f, .6f, .55f);
                GUI.Label(rect, $"{fighter.name} · {fighter.casts} chiêu", _label);
                var bar = new Rect(rect.x + 10f, rect.yMax, 100f, 6f);
                GUI.color = Color.black;
                GUI.DrawTexture(bar, Texture2D.whiteTexture);
                GUI.color = fighter.entry.blue ? new Color(.3f, .55f, 1f) : new Color(1f, .35f, .3f);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * fighter.health / Mathf.Max(1f, fighter.config.Stats.MaxHealth), bar.height),
                    Texture2D.whiteTexture);
                GUI.color = color;
            }
        }
    }
}
