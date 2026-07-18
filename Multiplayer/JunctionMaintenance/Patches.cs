// File: Patches.cs
// Namespace: JunctionMaintenance
// Contains: Save/Load Harmony patches, gameplay patches (forced damage, random flip), SpeedEstimator
//           + Block manual switching at 100% damage (toggleable via Settings)
//           + TrainCarQuery (snapshot-based, no FindObjectsOfType)

using System;
using HarmonyLib;
using UnityEngine;
using DV;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;
using System.Collections.Generic;

namespace JunctionMaintenance
{	
	internal static class JM_SaveCleanup
	{
		public static bool PendingLegacyCleanup = false;
	}

	internal static class JunctionRegistry
	{
		public static readonly List<Junction> All = new List<Junction>(512);
		private static bool _initialized;
		private static int _lastTryFrame = -1;

		public static void EnsureInitialized()
		{
			if (_initialized && All.Count > 0)
				return;

			if (_lastTryFrame == Time.frameCount)
				return;

			_lastTryFrame = Time.frameCount;

			All.Clear();
			All.AddRange(GameObject.FindObjectsOfType<Junction>());

			if (All.Count > 0)
			{
				_initialized = true;
				Main.Log($"[JunctionRegistry] Initialized with {All.Count} junctions.", true);
			}
		}
	}
	
	internal static class JunctionFrameGuard
	{
		private static readonly Dictionary<Junction, int> _lastFrame = new();

		public static bool AlreadyProcessed(Junction j)
		{
			int f = Time.frameCount;
			if (_lastFrame.TryGetValue(j, out var lf) && lf == f)
				return true;

			_lastFrame[j] = f;
			return false;
		}
	}

	internal static class JunctionKeyCache
	{
		private static readonly ConditionalWeakTable<Junction, Cached> _map = new();
		private sealed class Cached { public string Key; }

		public static string Get(Junction j)
		{
			if (j == null) return "Switch";
			if (_map.TryGetValue(j, out var c) && !string.IsNullOrEmpty(c.Key)) return c.Key;

			string k;
			try { k = DamageStore.MakeKey(j); }
			catch { k = j?.gameObject?.name ?? "Switch"; }

			_map.Remove(j);
			_map.Add(j, new Cached { Key = k });
			return k;
		}
	}
	
	[HarmonyPatch(
		typeof(StartGameData_FromSaveGame),
		nameof(StartGameData_FromSaveGame.GetSaveGameData))]
	internal static class JM_Patch_LoadFromSave
	{
		static void Postfix(
			SaveGameData __result)
		{
			try
			{
				JM_SaveCleanup.PendingLegacyCleanup = true;

				JM_SaveRuntime.LoadFromSaveData(
					__result,
					force: true);

				Main.Log(
					"LoadFromSave: junction damage map applied.",
					true);
			}
			catch (Exception e)
			{
				Main.Log(
					"LoadFromSave patch exception: " + e,
					true);
			}
		}
	}

    [HarmonyPatch(typeof(SaveGameManager), "Save")]
	static class JM_Patch_SaveGame
	{
		static void Prefix()
        {
            try
            {
                if (JM_Multiplayer.IsClient)
                {
                    Main.Log(
                        "[MP] Junction damage save skipped on client.",
                        true);

                    return;
                }

                var saveData = SaveGameManager.Instance?.data;

                if (saveData == null)
                    return;

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
						Main.Log(
							"Removed legacy save entry 'JunctionMaintanence_Map' (cleanup on save).",
							true
						);
					}
				}

				dataObject[JM_Save.KEY] = JM_Save.ToJsonArray(Main.Settings.DamageMap);
				Main.Log($"Saved {Main.Settings.DamageMap.Count} damage entries into savegame.");
			}
			catch (Exception e)
			{
				Main.Log("SaveGame Prefix exception: " + e, true);
			}
		}
	}

	[HarmonyPatch(
		typeof(StartGameData_NewCareer),
		nameof(StartGameData_NewCareer.PrepareNewSaveData))]
	internal static class JM_Init_JunctionRegistry_New
	{
		static void Postfix()
		{
			try
			{
				JM_SaveCleanup.PendingLegacyCleanup = false;

				JM_SaveRuntime.ResetForNewSession();

				Main.Log(
					"New career: junction damage map reset.",
					true);
			}
			catch (Exception e)
			{
				Main.Log(
					"New career junction reset exception: " + e,
					true);
			}
		}
	}	
	
	[HarmonyPatch(
		typeof(StartGameData_NewFreeRoam),
		nameof(StartGameData_NewFreeRoam.GetSaveGameData))]
	internal static class JM_Init_JunctionRegistry_NewFreeRoam
	{
		private static readonly ConditionalWeakTable<
			StartGameData_NewFreeRoam,
			InitializationMarker> InitializedInstances =
			new ConditionalWeakTable<
				StartGameData_NewFreeRoam,
				InitializationMarker>();

		private sealed class InitializationMarker
		{
		}

		static void Postfix(
			StartGameData_NewFreeRoam __instance,
			SaveGameData __result)
		{
			try
			{
				if (__instance == null)
				{
					return;
				}

				if (InitializedInstances.TryGetValue(
						__instance,
						out InitializationMarker existingMarker))
				{
					return;
				}

				InitializedInstances.Add(
					__instance,
					new InitializationMarker());

				JM_SaveCleanup.PendingLegacyCleanup = false;

				JM_SaveRuntime.ResetForNewSession();

				if (__result != null)
				{
					JM_SaveRuntime.LoadFromSaveData(
						__result,
						force: true);
				}

				Main.Log(
					"New FreeRoam: junction damage map initialized once.",
					true);
			}
			catch (Exception e)
			{
				Main.Log(
					"New FreeRoam junction reset exception: " + e,
					true);
			}
		}
	}

    // ------------------- BLOCK MANUAL SWITCHING AT 100% DAMAGE -------------------

    internal static class _JM_SwitchBlockHelper
    {
        public static bool AllowSwitch(Junction j, Junction.SwitchMode mode)
        {
            if (!Main.Enabled) return true;

            // Setting: Feature global abschaltbar
            if (!Main.Settings.BlockManualSwitchAtFullDamage) return true;

            // Forced run-throughs sollen weiter funktionieren (Schadensmodell/Postfix).
            if (mode == Junction.SwitchMode.FORCED) return true;

            try
            {
                string key = JunctionKeyCache.Get(j);
                float dmg = DamageStore.Get(key); // 0..1
                if (dmg >= 0.999f)
                {
                    Main.Log($"Blocked manual switch at fully damaged junction {key} (mode={mode}).");
                    return false; // Original überspringen -> kein Umschalten
                }
            }
            catch (Exception e)
            {
                Main.Log("AllowSwitch check failed: " + e, true);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode) })]
    public static class Patch_Junction_Switch_Block_NoBranch
    {
        static bool Prefix(Junction __instance, Junction.SwitchMode mode)
            => _JM_SwitchBlockHelper.AllowSwitch(__instance, mode);
    }

    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode), typeof(byte) })]
    public static class Patch_Junction_Switch_Block_WithBranch
    {
        static bool Prefix(Junction __instance, Junction.SwitchMode mode, ref byte branch)
            => _JM_SwitchBlockHelper.AllowSwitch(__instance, mode);
    }

    // ------------------- DAMAGE ON FORCED RUN-THROUGH -------------------

	internal static class ForcedJunctionDamageGuard
	{
		private static readonly Dictionary<Junction, int> LastProcessedFrame =
			new Dictionary<Junction, int>();

		public static bool AlreadyProcessedThisFrame(
			Junction junction)
		{
			if (junction == null)
			{
				return true;
			}

			int currentFrame =
				Time.frameCount;

			if (LastProcessedFrame.TryGetValue(
					junction,
					out int lastFrame) &&
				lastFrame == currentFrame)
			{
				return true;
			}

			LastProcessedFrame[junction] =
				currentFrame;

			return false;
		}
	}

	internal static class ForcedJunctionDamageHandler
	{
		public static void Handle(
			Junction junction,
			Junction.SwitchMode mode,
			string source)
		{
			if (!Main.Enabled ||
				junction == null)
			{
				return;
			}

			if (mode != Junction.SwitchMode.FORCED)
			{
				return;
			}

			if (ForcedJunctionDamageGuard.AlreadyProcessedThisFrame(
					junction))
			{
				Main.Log(
					$"FORCED duplicate ignored from {source}.",
					false);

				return;
			}

			try
			{
				float speedKmh =
					SpeedEstimator.EstimateImpactSpeedKmh(
						junction);

				int tier =
					Mathf.FloorToInt(
						speedKmh / 10f);

				float addPercent01 =
					tier <= 0
						? 0f
						: tier / 100f;

				string key =
					JunctionKeyCache.Get(
						junction);

				Main.Log(
					$"FORCED detected at {key} via {source}, " +
					$"speed={speedKmh:0.0} km/h, tier={tier}.",
					true);

				if (addPercent01 <= 0f)
				{
					Main.Log(
						$"FORCED at {key} produced no damage because " +
						$"speed was below 10 km/h.",
						true);

					return;
				}

				JM_Multiplayer.ReportDamage(
					key,
					addPercent01);

				FlipGuard.Block(
					key,
					Main.Settings.flipCooldownAfterForcedSec);

				if (JM_Multiplayer.IsClient)
				{
					Main.Log(
						$"FORCED at {key} via {source}: " +
						$"v={speedKmh:0.0} km/h -> " +
						$"+{addPercent01 * 100f:0.#}% reported to host.",
						true);
				}
				else
				{
					float currentDamage =
						DamageStore.Get(
							key);

					Main.Log(
						$"FORCED at {key} via {source}: " +
						$"v={speedKmh:0.0} km/h -> " +
						$"+{addPercent01 * 100f:0.#}% damage, " +
						$"total={currentDamage * 100f:0.###}%.",
						true);
				}
			}
			catch (Exception e)
			{
				Main.Log(
					$"Exception in forced junction damage handler " +
					$"from {source}: {e}",
					true);
			}
		}
	}

	[HarmonyPatch(
		typeof(Junction),
		nameof(Junction.Switch),
		new Type[]
		{
			typeof(Junction.SwitchMode)
		})]
	public static class Patch_Junction_Switch_FORCED_NoBranch
	{
		static void Postfix(
			Junction __instance,
			Junction.SwitchMode mode)
		{
			ForcedJunctionDamageHandler.Handle(
				__instance,
				mode,
				"Switch(mode)");
		}
	}

	[HarmonyPatch(
		typeof(Junction),
		nameof(Junction.Switch),
		new Type[]
		{
			typeof(Junction.SwitchMode),
			typeof(byte)
		})]
	public static class Patch_Junction_Switch_FORCED_WithBranch
	{
		static void Postfix(
			Junction __instance,
			Junction.SwitchMode mode,
			byte branch)
		{
			ForcedJunctionDamageHandler.Handle(
				__instance,
				mode,
				"Switch(mode, branch)");
		}
	}

    // Random flip chance when coming from in-branch, proportional to (damage × multiplier/100)
    [HarmonyPatch(typeof(Junction), nameof(Junction.GetNextBranch))]
    public static class Patch_Junction_GetNextBranch
    {
        static void Postfix(Junction __instance, RailTrack currentTrack, bool first, ref Junction.Branch __result)
        {
            if (!Main.Enabled) return;

			if (JunctionFrameGuard.AlreadyProcessed(__instance))
				return;

            try
            {
                var probe = new Junction.Branch(currentTrack, first);

                bool fromIn = (__instance?.inBranch != null &&
                               __instance.inBranch.track != null &&
                               __instance.inBranch.EqualsFields(probe));

                if (!fromIn) return;

                if (__instance?.outBranches != null)
                {
                    for (int i = 0; i < __instance.outBranches.Count; i++)
                    {
                        var ob = __instance.outBranches[i];
                        if (ob != null && ob.track != null && ob.EqualsFields(probe)) return;
                    }
                }

                string key = JunctionKeyCache.Get(__instance);
				if (FlipThrottle.ShouldSkip(key)) return;

                if (FlipGuard.IsBlocked(key)) return;

                float dmg01 = DamageStore.Get(key);
                if (dmg01 <= 0.0001f)
					return;

                float speedKmh = SpeedEstimator.EstimateImpactSpeedKmh(__instance);
                if (speedKmh <= Main.Settings.safeNoFlipSpeedKmh)
                {
                    // Under safe speed, no randomness
                    return;
                }

                float multiplier = Mathf.Clamp(Main.Settings.flipMultiplierPercent, 1, 50) / 100f; // 0.01 .. 0.50
                float chance = dmg01 * multiplier; // e.g., 100% damage with 50 => 50% chance

                if (UnityEngine.Random.value <= chance)
                {
                    int count = __instance.outBranches != null ? __instance.outBranches.Count : 0;
                    if (count >= 2)
                    {
                        byte currentSel = __instance.selectedBranch;
                        byte newSel = currentSel;
                        for (int i = 0; i < 8; i++)
                        {
                            byte candidate = (byte)UnityEngine.Random.Range(0, count);
                            if (candidate != currentSel) { newSel = candidate; break; }
                        }

                        __result = __instance.outBranches[newSel];
                        Main.Log($"Random flip at {key}: chance {chance * 100f:0.#}% (damage {dmg01 * 100f:0.#}%, mult {multiplier * 100f:0.#}%), {currentSel} -> {newSel}");
                    }
                }
            }
            catch (Exception e)
            {
                Main.Log("Exception in GetNextBranch Postfix: " + e, true);
            }
        }
    }

    // ------------------- FAST TRAINCAR SNAPSHOT (no FindObjectsOfType) -------------------

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
                    int n = src.Count;
					
                    var buf = new TrainCar[n];
                    int k = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var c = src[i];
                        if (c != null) buf[k++] = c;
                    }
                    if (k == n) _snapshot = buf;
                    else
                    {
                        _snapshot = new TrainCar[k];
                        for (int i = 0; i < k; i++) _snapshot[i] = buf[i];
                    }
                }
                _ts = now;
            }
            return _snapshot;
        }
    }
	
	internal static class FlipThrottle
	{
		private static readonly Dictionary<string, float> _lastTs = new();
		private const float MIN_INTERVAL = 0.12f; // 120 ms

		public static bool ShouldSkip(string key)
		{
			float now = Time.unscaledTime;
			if (_lastTs.TryGetValue(key, out var ts) && (now - ts) < MIN_INTERVAL)
				return true;

			_lastTs[key] = now;
			return false;
		}
	}
	
	public static class SpeedEstimator
	{
		private sealed class SpeedCache
		{
			public int Frame = -1;
			public float SpeedKmh;
		}

		private static readonly ConditionalWeakTable<
			Junction,
			SpeedCache> Cache =
			new ConditionalWeakTable<
				Junction,
				SpeedCache>();

		public static float EstimateImpactSpeedKmh(
			Junction junction)
		{
			if (junction == null)
			{
				return 0f;
			}

			SpeedCache cache =
				Cache.GetOrCreateValue(junction);

			int currentFrame =
				Time.frameCount;

			if (cache.Frame == currentFrame)
			{
				return cache.SpeedKmh;
			}

			cache.Frame =
				currentFrame;

			cache.SpeedKmh =
				0f;

			Vector3 junctionPosition =
				junction.transform.position;

			float bestDistanceSquared =
				50f * 50f;

			TrainCar[] cars =
				TrainCarQuery.GetAll();

			for (int i = 0; i < cars.Length; i++)
			{
				TrainCar car =
					cars[i];

				if (car == null ||
					car.rb == null)
				{
					continue;
				}

				float distanceSquared =
					(car.transform.position -
					 junctionPosition).sqrMagnitude;

				if (distanceSquared >= bestDistanceSquared)
				{
					continue;
				}

				bestDistanceSquared =
					distanceSquared;

				cache.SpeedKmh =
					car.rb.velocity.magnitude * 3.6f;
			}

			return cache.SpeedKmh;
		}
	}
}