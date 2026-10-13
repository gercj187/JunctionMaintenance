// File: Damage.cs
// Namespace: JunctionMaintenance
// Neue Schadensauslöser (wie bei TrackMaintenance, jeweils in den Einstellungen abschaltbar):
//
//   Entgleisung (einmalig)   Ein Wagen entgleist: alle Weichen im Radius um das Fahrzeug bekommen
//                            Basisschaden x (Gewicht + Geschwindigkeit + Lok-Bonus).
//   Mitschleifen (andauernd) Entgleiste Wagen werden verfolgt; solange sie schneller als die
//                            Mindestgeschwindigkeit sind, gibt es im Intervall erneut Schaden (gleiche Formel).
//   Explosion                Alle Weichen im Explosionsradius: voller Schaden im Zentrum, nach außen linear abnehmend.
//
// Abstand zu einer Weiche = kürzester Abstand zu ihren Fahrwegen ("[track through]" / "[track diverging]"),
// sonst zum Weichenpunkt. So zählt die ganze Weiche, nicht nur ihr Anfang.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using DV.PointSet;

namespace JunctionMaintenance
{
    // =========================
    // WEICHEN SUCHEN
    // =========================

    internal static class JunctionLookup
    {
        // Weichen sind deutlich kürzer; Vorfilter über den Weichenpunkt
        private const float MaxJunctionLength = 60f;
        private const float CacheSeconds = 30f;

        private static List<Junction> cache;
        private static float cacheTime = -999f;

        public static void Reset()
        {
            cache = null;
            cacheTime = -999f;
        }

        public static List<Junction> All()
        {
            bool stale = cache == null || cache.Count == 0
                      || Time.realtimeSinceStartup - cacheTime > CacheSeconds
                      || cache[0] == null; // Szenenwechsel (Unity-Null)
            if (stale)
            {
                try
                {
                    cache = new List<Junction>(UnityEngine.Object.FindObjectsOfType<Junction>());
                }
                catch (Exception e)
                {
                    Main.Error("Junction search failed: " + e);
                    cache = new List<Junction>();
                }
                cacheTime = Time.realtimeSinceStartup;
            }
            return cache;
        }

        // Kürzester Abstand der Weiche zu einem der Punkte
        public static float Distance(Junction j, IList<Vector3> points)
        {
            float best = float.MaxValue;
            Vector3 jp = j.transform.position;
            foreach (var p in points)
                best = Mathf.Min(best, Vector3.Distance(jp, p));

            var outs = j.outBranches;
            if (outs == null) return best;

            foreach (var b in outs)
            {
                if (b == null || b.track == null) continue;
                foreach (var p in points)
                {
                    try
                    {
                        var (point, sqrDist) = RailTrack.GetClosestPoint(b.track, p);
                        if (point == null) continue;
                        best = Mathf.Min(best, Mathf.Sqrt(sqrDist));
                    }
                    catch { }
                }
            }
            return best;
        }

        // Alle Weichen mit Abstand <= radius zu einem der Punkte
        public static List<(Junction j, float dist)> Within(IList<Vector3> points, float radius)
        {
            var result = new List<(Junction, float)>();
            if (points == null || points.Count == 0) return result;

            float pre = radius + MaxJunctionLength;
            float preSqr = pre * pre;

            foreach (var j in All())
            {
                if (j == null) continue;

                Vector3 jp = j.transform.position;
                bool near = false;
                foreach (var p in points)
                    if ((jp - p).sqrMagnitude <= preSqr) { near = true; break; }
                if (!near) continue;

                float d = Distance(j, points);
                if (d <= radius) result.Add((j, d));
            }
            return result;
        }

        // Messpunkte eines Fahrzeugs: Mitte und beide Drehgestelle
        public static List<Vector3> PointsOf(TrainCar car)
        {
            var list = new List<Vector3>(3);
            if (car == null) return list;
            list.Add(car.transform.position);
            if (car.FrontBogie != null) list.Add(car.FrontBogie.transform.position);
            if (car.RearBogie != null) list.Add(car.RearBogie.transform.position);
            return list;
        }
    }

    // =========================
    // SCHADEN VERBUCHEN
    // =========================

    internal static class JunctionDamage
    {
        public static void Add(Junction j, float percent, string reason)
        {
            if (j == null || percent <= 0f) return;

            string key = JunctionKeyCache.Get(j);
            float before = DamageStore.Get(key);
            DamageStore.AddPercent(key, percent / 100f);
            float after = DamageStore.Get(key);

            Main.Log($"{key}: +{percent:0.#}% ({reason}) -> {before * 100f:0.#}% to {after * 100f:0.#}%");
        }

        // Entgleisung / Mitschleifen: Weichen im Radius um das Fahrzeug
        public static void ForCar(TrainCar car, string trigger)
        {
            if (car == null) return;

            float speedKmh = car.rb != null ? car.rb.velocity.magnitude * 3.6f : 0f;
            float massKg = car.rb != null ? car.rb.mass : 0f;
            bool isLoco = car.IsLoco;

            float multiplier;
            float percent = Main.Settings.CalculateHitPercent(massKg, speedKmh, isLoco, out multiplier);
            if (percent <= 0f) return;

            var hits = JunctionLookup.Within(JunctionLookup.PointsOf(car), Main.Settings.DerailRadius);
            if (hits.Count == 0) return;

            string reason = $"{trigger}: car '{car.name}' {massKg / 1000f:F1} t at {speedKmh:F1} km/h, loco={isLoco}, x{multiplier:F2}";
            foreach (var hit in hits)
                Add(hit.j, percent, reason);
        }

        // Explosion: voller Schaden im Zentrum, nach außen linear abnehmend
        public static void ForExplosion(Vector3 position)
        {
            float radius = Main.Settings.ExplosionRadius;
            var hits = JunctionLookup.Within(new List<Vector3> { position }, radius);

            foreach (var hit in hits)
            {
                float percent = Main.Settings.ExplosionPercentAt(hit.dist);
                if (percent < 0.1f) continue;
                Add(hit.j, percent, $"explosion at {hit.dist:F1} m");
            }

            Main.Log($"Explosion: {hits.Count} junction(s) within {radius:F0} m");
        }
    }

    // =========================
    // AUSLÖSER: ENTGLEISTE WAGEN
    // =========================

    public static class DerailTracker
    {
        private static readonly Dictionary<TrainCar, float> cars = new Dictionary<TrainCar, float>();

        public static void Clear() { cars.Clear(); }

        public static void Register(TrainCar car)
        {
            if (car == null || cars.ContainsKey(car)) return;

            if (Main.Settings.DamageAtDerail)
                JunctionDamage.ForCar(car, "derailment");

            if (Main.Settings.DamageWhenDragged)
            {
                Main.Log($"Tracking derailed car '{car.name}'");
                // Nach dem Schaden bei der Entgleisung erst nach einem Intervall weiter,
                // sonst sofort, sobald der Wagen schnell genug ist
                cars[car] = Main.Settings.DamageAtDerail ? 0f : Main.Settings.IntervalSeconds;
            }
        }

        // Aus Main.OnUpdate
        public static void Tick(float dt)
        {
            if (cars.Count == 0) return;

            float interval = Main.Settings.IntervalSeconds;
            float minSpeed = Main.Settings.MinSpeedKmh;

            foreach (TrainCar car in cars.Keys.ToList())
            {
                // Zerstört, in den Pool zurückgegeben oder wieder aufgegleist
                if (car == null || !car.derailed)
                {
                    cars.Remove(car);
                    continue;
                }

                float timer = cars[car] + dt;
                if (timer < interval)
                {
                    cars[car] = timer;
                    continue;
                }

                float speedKmh = car.rb != null ? car.rb.velocity.magnitude * 3.6f : 0f;
                if (speedKmh <= minSpeed)
                {
                    // Zu langsam: warten, bis der Wagen wieder schnell genug ist
                    cars[car] = interval;
                    continue;
                }

                cars[car] = 0f;
                JunctionDamage.ForCar(car, "dragged");
            }
        }
    }

    [HarmonyPatch(typeof(TrainCar), nameof(TrainCar.Derail))]
    public static class JM_TrainCarDerailPatch
    {
        public static void Postfix(TrainCar __instance)
        {
            if (!Main.Enabled) return;
            if (!Main.Settings.DamageAtDerail && !Main.Settings.DamageWhenDragged) return;

            try
            {
                if (__instance != null && __instance.derailed)
                    DerailTracker.Register(__instance);
            }
            catch (Exception e)
            {
                Main.Error("Derail damage failed: " + e);
            }
        }
    }

    // =========================
    // AUSLÖSER: EXPLOSIONEN
    // =========================

    [HarmonyPatch(typeof(TrainCarExplosion), nameof(TrainCarExplosion.CreateExplosion))]
    public static class JM_TrainCarExplosionPatch
    {
        public static void Postfix(Vector3 explosionPosition)
        {
            if (!Main.Enabled || !Main.Settings.DamageByExplosion) return;

            try
            {
                JunctionDamage.ForExplosion(explosionPosition);
            }
            catch (Exception e)
            {
                Main.Error("Explosion damage failed: " + e);
            }
        }
    }
}
