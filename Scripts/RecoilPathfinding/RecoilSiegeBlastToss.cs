using UnityEngine;

namespace RecoilPathfinding
{
    /// <summary>
    /// Siege stone explosions toss the soldiers they hit: outwards from the impact point like a sphere — the closer to
    /// the centre, the higher and farther — with some randomness in height, distance, direction and air time.
    /// Uses the same knock-up / displacement as the hero skills; heroes are not tossed.
    /// </summary>
    public sealed partial class RecoilAutoBattleDemo
    {
        [Header("Hất tung khi đá máy bắn đá nổ")]
        [SerializeField] private bool siegeBlastToss = true;
        [SerializeField, Min(0f), Tooltip("Độ cao tối đa (lính ở tâm nổ)")] private float siegeBlastHeight = 3f;
        [SerializeField, Min(0f), Tooltip("Quãng văng ra tối đa (lính ở tâm nổ)")] private float siegeBlastDistance = 3f;
        [SerializeField, Min(.1f), Tooltip("Thời gian trên không (giây)")] private float siegeBlastDuration = .8f;
        [SerializeField, Range(0f, 1f), Tooltip("Độ ngẫu nhiên: 0 = đều nhau, 1 = rất khác nhau")] private float siegeBlastRandomness = .5f;
        [SerializeField, Range(0f, 90f), Tooltip("Lệch hướng văng ngẫu nhiên tối đa (độ)")] private float siegeBlastAngleJitter = 30f;

        private readonly System.Random _siegeBlastRandom = new(7919);

        /// <summary>
        /// A soldier killed while flying (blast or hero skill knock-up / push): its body finishes the flight while the
        /// death animation plays. Dead units are no longer moved by the simulation, so the body is moved directly.
        /// </summary>
        private sealed class CorpseFlight
        {
            public Soldier Target;
            public float Started;
            public Vector3 Horizontal;
            public float HorizontalSeconds;
            public bool Lifted;
            public float LiftStarted, LiftDuration, LiftHeight, LiftInitial, BaseLift;
            public Vector3 Applied;
        }
        private readonly System.Collections.Generic.List<CorpseFlight> _corpseFlights = new();

        private CorpseFlight CorpseFlightFor(Soldier target)
        {
            var flight = _corpseFlights.Find(f => ReferenceEquals(f.Target, target));
            if (flight != null) return flight;
            flight = new CorpseFlight { Target = target, Started = SimulationNow };
            _corpseFlights.Add(flight);
            return flight;
        }

        /// <summary>The dead body still slides <paramref name="remaining"/> over <paramref name="seconds"/>.</summary>
        private void CorpseKeepsMoving(Soldier target, Vector3 remaining, float seconds)
        {
            if (target == null || target.HeroId > 0 || seconds <= 0f || remaining.sqrMagnitude < .0001f) return;
            var flight = CorpseFlightFor(target);
            flight.Horizontal = remaining;
            flight.HorizontalSeconds = seconds;
            flight.Started = SimulationNow;
        }

        /// <summary>The dead body finishes the knock-up arc it was on, then lies on the ground.</summary>
        private void CorpseKeepsFlying(Soldier target, HeroKnockUp knockUp)
        {
            if (target == null || target.HeroId > 0) return;
            var flight = CorpseFlightFor(target);
            flight.Lifted = true;
            flight.LiftStarted = knockUp.Started;
            flight.LiftDuration = knockUp.Duration;
            flight.LiftHeight = knockUp.Height;
            flight.LiftInitial = knockUp.InitialHeight;
            // The body is drawn where it last was, lift included.
            flight.BaseLift = target.Simulation != null && target.Simulation.Exists
                ? target.Simulation.Pose.PresentationLift : 0f;
        }

        private void UpdateCorpseFlights()
        {
            for (var i = _corpseFlights.Count - 1; i >= 0; i--)
            {
                var flight = _corpseFlights[i];
                var target = flight.Target;
                // The body was put back in the pool: nothing left to move.
                if (target == null || target.Agent == null) { _corpseFlights.RemoveAt(i); continue; }

                var moveT = flight.HorizontalSeconds > 0f
                    ? Mathf.Clamp01((SimulationNow - flight.Started) / flight.HorizontalSeconds) : 1f;
                var offset = flight.Horizontal * moveT;
                var liftT = 1f;
                if (flight.Lifted)
                {
                    liftT = Mathf.Clamp01((SimulationNow - flight.LiftStarted) / Mathf.Max(.03f, flight.LiftDuration));
                    offset.y += HeroSkillTimeline.AirborneHeight(liftT, flight.LiftHeight, flight.LiftInitial) - flight.BaseLift;
                }

                // A VAT death pins the body where it died each frame: move it through its offset instead.
                if (target.Vat != null && target.Vat.Dying) target.Vat.DeathOffset = offset;
                else target.Agent.transform.position += offset - flight.Applied;
                flight.Applied = offset;
                if (moveT >= 1f && liftT >= 1f) _corpseFlights.RemoveAt(i);
            }
        }

        private float SiegeBlastRandom(float randomness) =>
            1f + ((float)_siegeBlastRandom.NextDouble() * 2f - 1f) * randomness;

        /// <param name="surfaceDistance">From the impact point to the soldier's body edge.</param>
        private void TossFromSiegeBlast(Soldier target, Vector3 position, PendingCombatProjectile projectile,
            float surfaceDistance)
        {
            if (!siegeBlastToss || !projectile.SiegeStone || projectile.AreaRadius <= 0f) return;
            // A soldier the blast just killed is tossed too: it dies in the air (see CorpseKeepsFlying).
            if (target == null || target.HeroId > 0 || target.Retired || target.Simulation == null ||
                !target.Simulation.Exists) return;

            // Sphere profile: full strength at the centre, nothing at the edge of the blast.
            var t = Mathf.Clamp01(surfaceDistance / projectile.AreaRadius);
            var strength = Mathf.Sqrt(1f - t * t);
            if (strength < .05f) return;

            var randomness = Mathf.Clamp01(siegeBlastRandomness);
            var height = siegeBlastHeight * strength * SiegeBlastRandom(randomness);
            var distance = siegeBlastDistance * strength * SiegeBlastRandom(randomness);
            var duration = Mathf.Max(.1f, siegeBlastDuration * Mathf.Lerp(1f, .75f + .5f * strength, .5f) *
                SiegeBlastRandom(randomness * .4f));

            var away = position - projectile.ImpactPoint;
            away.y = 0f;
            Vector3 direction;
            if (away.sqrMagnitude < .01f)
                direction = Quaternion.Euler(0f, (float)_siegeBlastRandom.NextDouble() * 360f, 0f) * Vector3.forward;
            else
            {
                var jitter = ((float)_siegeBlastRandom.NextDouble() * 2f - 1f) * siegeBlastAngleJitter;
                direction = Quaternion.Euler(0f, jitter, 0f) * away.normalized;
            }

            if (height > .05f)
            {
                var flight = _heroKnockUps.Find(f => ReferenceEquals(f.Target, target));
                if (flight == null) { flight = new HeroKnockUp { Target = target }; _heroKnockUps.Add(flight); }
                flight.InitialHeight = target.Simulation.Pose.PresentationLift;
                flight.Started = SimulationNow;
                flight.Height = Mathf.Max(height, flight.InitialHeight);
                flight.Duration = duration;
                SetHeroAirborne(target, true, flight.InitialHeight);
            }
            if (distance > .05f)
            {
                SetForcedDisplacement(target, true);
                _heroDisplacements.Add(new HeroDisplacement
                {
                    Target = target, Impulse = true, Seconds = duration,
                    Velocity = direction * (distance / duration), Remaining = direction * distance
                });
            }
        }
    }
}
