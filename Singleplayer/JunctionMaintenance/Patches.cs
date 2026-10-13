// File: Patches.cs
// Namespace: JunctionMaintenance
// Contains: Spielstand (Laden/Speichern/Neues Spiel), Sperre bei 100 % Schaden und während der Reparatur,
//           Schaden beim Auffahren (FORCED), zufälliges Umstellen, TrainCarQuery, SpeedEstimator

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using DV;
using Newtonsoft.Json.Linq;

namespace JunctionMaintenance
{
    internal static class JM_SaveCleanup
    {
        public static bool PendingLegacyCleanup = false;
    }

    internal static class JunctionFrameGuard
    {
        private static readonly Dictionary<Junction, int> _lastFrame = new Dictionary<Junction, int>();

        public static bool AlreadyProcessed(Junction j)
        {
            int f = Time.frameCount;
            int lf;
            if (_lastFrame.TryGetValue(j, out lf) && lf == f) return true;
            _lastFrame[j] = f;
            return false;
        }
    }

    internal static class JunctionKeyCache
    {
        private sealed class Cached { public string Key; }
        private static readonly ConditionalWeakTable<Junction, Cached> _map = new ConditionalWeakTable<Junction, Cached>();

        public static string Get(Junction j)
        {
            if (j == null) return "Switch";
            Cached c;
            if (_map.TryGetValue(j, out c) && !string.IsNullOrEmpty(c.Key)) return c.Key;

            string k;
            try { k = DamageStore.MakeKey(j); }
            catch { k = j.gameObject != null ? j.gameObject.name : "Switch"; }

            _map.Remove(j);
            _map.Add(j, new Cached { Key = k });
            return k;
        }
    }

    // =========================
    // SPIELSTAND
    // =========================

    [HarmonyPatch(typeof(StartGameData_FromSaveGame), nameof(StartGameData_FromSaveGame.GetSaveGameData))]
    internal static class JM_Patch_LoadFromSave
    {
        static void Postfix(SaveGameData __result)
        {
            try
            {
                JM_SaveCleanup.PendingLegacyCleanup = true;
                JM_SaveRuntime.ResetRuntime();
                JM_SaveRuntime.LoadFromSaveData(__result, force: true);
            }
            catch (Exception e)
            {
                Main.Error("LoadFromSave patch failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(SaveGameManager), "Save")]
    internal static class JM_Patch_SaveGame
    {
        static void Prefix()
        {
            try
            {
                var saveData = SaveGameManager.Instance?.data;
                if (saveData == null) return;

                var trav = Traverse.Create(saveData).Field("dataObject");
                var dataObject = trav.GetValue<JObject>();
                if (dataObject == null)
                {
                    dataObject = new JObject();
                    trav.SetValue(dataObject);
                }

                if (JM_SaveCleanup.PendingLegacyCleanup)
                {
                    JM_SaveCleanup.PendingLegacyCleanup = false;

                    const string OLD_KEY = "JunctionMaintanence_Map";
                    if (dataObject.ContainsKey(OLD_KEY))
                    {
                        dataObject.Remove(OLD_KEY);
                        Main.Log("Removed legacy save entry 'JunctionMaintanence_Map'.");
                    }
                }

                dataObject[JM_Save.KEY] = JM_Save.ToJsonArray(Main.Settings.DamageMap);
                Main.Log($"Saved {Main.Settings.DamageMap.Count} damage entries into savegame.");
            }
            catch (Exception e)
            {
                Main.Error("SaveGame Prefix failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(StartGameData_NewCareer), nameof(StartGameData_NewCareer.PrepareNewSaveData))]
    internal static class JM_Init_NewCareer
    {
        static void Postfix()
        {
            try
            {
                JM_SaveCleanup.PendingLegacyCleanup = false;
                JM_SaveRuntime.ResetForNewSession();
            }
            catch (Exception e)
            {
                Main.Error("New career reset failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(StartGameData_NewFreeRoam), nameof(StartGameData_NewFreeRoam.GetSaveGameData))]
    internal static class JM_Init_NewFreeRoam
    {
        private sealed class InitializationMarker { }

        private static readonly ConditionalWeakTable<StartGameData_NewFreeRoam, InitializationMarker> InitializedInstances =
            new ConditionalWeakTable<StartGameData_NewFreeRoam, InitializationMarker>();

        static void Postfix(StartGameData_NewFreeRoam __instance, SaveGameData __result)
        {
            try
            {
                if (__instance == null) return;

                InitializationMarker marker;
                if (InitializedInstances.TryGetValue(__instance, out marker)) return;
                InitializedInstances.Add(__instance, new InitializationMarker());

                JM_SaveCleanup.PendingLegacyCleanup = false;
                JM_SaveRuntime.ResetForNewSession();

                if (__result != null)
                    JM_SaveRuntime.LoadFromSaveData(__result, force: true);
            }
            catch (Exception e)
            {
                Main.Error("New FreeRoam reset failed: " + e);
            }
        }
    }

    // =========================
    // UMSTELLEN SPERREN
    // =========================
    // - bei 100 % Schaden (abschaltbar)
    // - während die Weiche repariert wird
    // Erzwungenes Umstellen (Auffahren) bleibt immer möglich.

    internal static class JM_SwitchBlockHelper
    {
        public static bool AllowSwitch(Junction j, Junction.SwitchMode mode)
        {
            if (!Main.Enabled) return true;
            if (mode == Junction.SwitchMode.FORCED) return true;

            try
            {
                string key = JunctionKeyCache.Get(j);

                if (JunctionRepair.IsRunning(key))
                {
                    Main.Log($"Blocked manual switch at {key}: repair in progress (mode={mode}).");
                    return false;
                }

                if (Main.Settings.BlockManualSwitchAtFullDamage && DamageStore.Get(key) >= 0.999f)
                {
                    Main.Log($"Blocked manual switch at fully damaged junction {key} (mode={mode}).");
                    return false;
                }
            }
            catch (Exception e)
            {
                Main.Error("AllowSwitch check failed: " + e);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode) })]
    public static class Patch_Junction_Switch_Block_NoBranch
    {
        static bool Prefix(Junction __instance, Junction.SwitchMode mode)
            => JM_SwitchBlockHelper.AllowSwitch(__instance, mode);
    }

    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode), typeof(byte) })]
    public static class Patch_Junction_Switch_Block_WithBranch
    {
        static bool Prefix(Junction __instance, Junction.SwitchMode mode, ref byte branch)
            => JM_SwitchBlockHelper.AllowSwitch(__instance, mode);
    }

    // =========================
    // SCHADEN BEIM AUFFAHREN (FORCED)
    // =========================
    // Schaden = Basisschaden (1 % je angefangene 10 km/h) x Multiplikator des auffahrenden Fahrzeugs
    // (Gewicht, Geschwindigkeit, Lok-Bonus) – gleiche Formel wie bei Entgleisung/Mitschleifen.

    internal static class ForcedJunctionDamageGuard
    {
        private static readonly Dictionary<Junction, int> LastProcessedFrame = new Dictionary<Junction, int>();

        public static bool AlreadyProcessedThisFrame(Junction junction)
        {
            if (junction == null) return true;
            int f = Time.frameCount;
            int last;
            if (LastProcessedFrame.TryGetValue(junction, out last) && last == f) return true;
            LastProcessedFrame[junction] = f;
            return false;
        }
    }

    internal static class ForcedJunctionDamageHandler
    {
        public static void Handle(Junction junction, Junction.SwitchMode mode, string source)
        {
            if (!Main.Enabled || junction == null) return;
            if (mode != Junction.SwitchMode.FORCED) return;
            if (!Main.Settings.DamageRunThrough) return;
            if (ForcedJunctionDamageGuard.AlreadyProcessedThisFrame(junction)) return;

            try
            {
                TrainCar car;
                float speedKmh = SpeedEstimator.EstimateImpact(junction, out car);

                float multiplier = 1f;
                bool isLoco = false;
                float massKg = 0f;
                float percent;
                if (car != null && car.rb != null)
                {
                    massKg = car.rb.mass;
                    isLoco = car.IsLoco;
                    percent = Main.Settings.CalculateHitPercent(massKg, speedKmh, isLoco, out multiplier);
                }
                else
                {
                    percent = Settings.BasePercent(speedKmh); // kein Fahrzeug gefunden: nur Basisschaden
                }

                string key = JunctionKeyCache.Get(junction);
                if (percent <= 0f)
                {
                    Main.Log($"FORCED at {key} via {source}: {speedKmh:0.0} km/h, no damage.");
                    return;
                }

                JunctionDamage.Add(junction, percent,
                    $"run-through via {source}: {massKg / 1000f:F1} t at {speedKmh:0.0} km/h, loco={isLoco}, x{multiplier:F2}");
                FlipGuard.Block(key, Main.Settings.flipCooldownAfterForcedSec);
            }
            catch (Exception e)
            {
                Main.Error($"Forced junction damage from {source} failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode) })]
    public static class Patch_Junction_Switch_FORCED_NoBranch
    {
        static void Postfix(Junction __instance, Junction.SwitchMode mode)
            => ForcedJunctionDamageHandler.Handle(__instance, mode, "Switch(mode)");
    }

    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode), typeof(byte) })]
    public static class Patch_Junction_Switch_FORCED_WithBranch
    {
        static void Postfix(Junction __instance, Junction.SwitchMode mode, byte branch)
            => ForcedJunctionDamageHandler.Handle(__instance, mode, "Switch(mode, branch)");
    }

    // =========================
    // ZUFÄLLIGES UMSTELLEN
    // =========================
    // Beim Befahren von der Weichenspitze (inBranch): Chance = Schaden (0..1) x Multiplikator (0.01..0.50).
    // Beispiel: 100 % Schaden, Multiplikator 0.15 -> 15 % Chance. Bis zur sicheren Geschwindigkeit nie.
    // (Fix: früher wurde der Multiplikator auf 1..50 begrenzt und durch 100 geteilt -> immer 1 %.)

    [HarmonyPatch(typeof(Junction), nameof(Junction.GetNextBranch))]
    public static class Patch_Junction_GetNextBranch
    {
        static void Postfix(Junction __instance, RailTrack currentTrack, bool first, ref Junction.Branch __result)
        {
            // Wird vom Spiel sehr oft aufgerufen (nicht nur von Drehgestellen, auch bei Wegberechnungen,
            // z. B. wenn ein großer Bahnhof wie Harbor in Reichweite kommt). Deshalb zuerst die billigsten
            // Prüfungen: unbeschädigte Weichen (fast alle) verlassen den Patch sofort, ohne Speicher anzulegen.
            if (!Main.Enabled || !Main.Settings.enableRandomFlip || __instance == null) return;
            if (DamageStore.All().Count == 0) return;

            string key = JunctionKeyCache.Get(__instance);
            if (DamageStore.Get(key) <= 0.0001f) return;

            if (JunctionFrameGuard.AlreadyProcessed(__instance)) return;

            try
            {
                var probe = new Junction.Branch(currentTrack, first);

                bool fromIn = __instance.inBranch != null &&
                              __instance.inBranch.track != null &&
                              __instance.inBranch.EqualsFields(probe);
                if (!fromIn) return;

                if (__instance.outBranches != null)
                {
                    for (int i = 0; i < __instance.outBranches.Count; i++)
                    {
                        var ob = __instance.outBranches[i];
                        if (ob != null && ob.track != null && ob.EqualsFields(probe)) return;
                    }
                }

                if (FlipThrottle.ShouldSkip(key)) return;
                if (FlipGuard.IsBlocked(key)) return;
                if (JunctionRepair.IsRunning(key)) return;

                float dmg01 = DamageStore.Get(key);
                if (dmg01 <= 0.0001f) return;

                float speedKmh = SpeedEstimator.EstimateImpactSpeedKmh(__instance);
                if (speedKmh <= Main.Settings.safeNoFlipSpeedKmh) return;

                float chance = dmg01 * Main.Settings.FlipMultiplier;
                if (UnityEngine.Random.value > chance) return;

                int count = __instance.outBranches != null ? __instance.outBranches.Count : 0;
                if (count < 2) return;

                byte currentSel = __instance.selectedBranch;
                byte newSel = currentSel;
                for (int i = 0; i < 8; i++)
                {
                    byte candidate = (byte)UnityEngine.Random.Range(0, count);
                    if (candidate != currentSel) { newSel = candidate; break; }
                }

                __result = __instance.outBranches[newSel];
                Main.Log($"Random flip at {key}: chance {chance * 100f:0.#}% (damage {dmg01 * 100f:0.#}%), {currentSel} -> {newSel}");
            }
            catch (Exception e)
            {
                Main.Error("GetNextBranch Postfix failed: " + e);
            }
        }
    }

    internal static class FlipThrottle
    {
        private static readonly Dictionary<string, float> _lastTs = new Dictionary<string, float>();
        private const float MIN_INTERVAL = 0.12f; // 120 ms

        public static bool ShouldSkip(string key)
        {
            float now = Time.unscaledTime;
            float ts;
            if (_lastTs.TryGetValue(key, out ts) && (now - ts) < MIN_INTERVAL) return true;
            _lastTs[key] = now;
            return false;
        }
    }

    // =========================
    // FAHRZEUGE / GESCHWINDIGKEIT
    // =========================

    public static class TrainCarQuery
    {
        private static TrainCar[] _snapshot = Array.Empty<TrainCar>();
        private static float _ts = -999f;
        private const float TTL_SEC = 0.20f;

        public static TrainCar[] GetAll()
        {
            float now = Time.unscaledTime;
            if (_snapshot == null || now - _ts > TTL_SEC)
            {
                var src = CarSpawner.Instance?.AllCars;
                if (src == null)
                {
                    _snapshot = Array.Empty<TrainCar>();
                }
                else
                {
                    var buf = new List<TrainCar>(src.Count);
                    for (int i = 0; i < src.Count; i++)
                        if (src[i] != null) buf.Add(src[i]);
                    _snapshot = buf.ToArray();
                }
                _ts = now;
            }
            return _snapshot;
        }
    }

    public static class SpeedEstimator
    {
        private sealed class SpeedCache
        {
            public int Frame = -1;
            public float SpeedKmh;
            public TrainCar Car;
        }

        private static readonly ConditionalWeakTable<Junction, SpeedCache> Cache =
            new ConditionalWeakTable<Junction, SpeedCache>();

        public static float EstimateImpactSpeedKmh(Junction junction)
        {
            TrainCar car;
            return EstimateImpact(junction, out car);
        }

        // Nächster Wagen (max. 50 m) an der Weiche und seine Geschwindigkeit
        public static float EstimateImpact(Junction junction, out TrainCar car)
        {
            car = null;
            if (junction == null) return 0f;

            SpeedCache cache = Cache.GetOrCreateValue(junction);
            if (cache.Frame == Time.frameCount)
            {
                car = cache.Car;
                return cache.SpeedKmh;
            }

            cache.Frame = Time.frameCount;
            cache.SpeedKmh = 0f;
            cache.Car = null;

            Vector3 jp = junction.transform.position;
            float best = 50f * 50f;

            foreach (TrainCar c in TrainCarQuery.GetAll())
            {
                if (c == null || c.rb == null) continue;
                float d = (c.transform.position - jp).sqrMagnitude;
                if (d >= best) continue;
                best = d;
                cache.Car = c;
                cache.SpeedKmh = c.rb.velocity.magnitude * 3.6f;
            }

            car = cache.Car;
            return cache.SpeedKmh;
        }
    }
}
