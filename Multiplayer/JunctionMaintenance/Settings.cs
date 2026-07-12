// File: Settings.cs
// Namespace: JunctionMaintenance
// Contains: Settings, DamageEntry, DamageStore, FlipGuard, JM_Save, JM_SaveRuntime

using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using UnityEngine;
using UnityModManagerNet;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using MPAPI;
using DV;

namespace JunctionMaintenance
{
	public enum RepairMode
	{
		Penalty = 0,
		Reward = 1,
		Dynamic = 2
	}
	
    [Serializable]
    public class Settings : UnityModManager.ModSettings
    {
        public bool logging = false;

        // Flip config
        public bool enableRandomFlip = true;
		public float safeNoFlipSpeedKmh = 10.0f;
        public float flipMultiplierPercent = 0.15f; // 0.01..0.50
        public float flipCooldownAfterForcedSec = 5.0f;
		
        // Repair config
        public float repairRadius = 10f;
        public float repairAmountPercent = 1f;
        public float repairVehicleSearchRadius = 12f;
        public float maxVehicleStandingSpeedKmh = 1.0f;
        public float maxRepairCostFull = 10000f;
		public float maxRepairRewardFull = 1000f;
		public float maintenanceLicensePrice = 50000f;
		public bool BlockManualSwitchAtFullDamage = true;
		
		// GameMode
		public RepairMode repairMode = RepairMode.Penalty;

        [XmlIgnore]
        private Dictionary<string, float> _damageMap = new Dictionary<string, float>(1024);

        [XmlIgnore]
        public Dictionary<string, float> DamageMap => _damageMap;

    }

    [Serializable]
    public class DamageEntry
    {
        [XmlAttribute("k")]
        public string Key;

        [XmlAttribute("v")]
        public float Value;
    }

    // Runtime damage map accessors (shared)
    public static class DamageStore
    {
        public static float Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0f;
            if (Main.Settings.DamageMap.TryGetValue(key, out var v)) return Mathf.Clamp01(v);
            return 0f;
        }

        public static void AddPercent(string key, float percent01)
        {
            if (string.IsNullOrEmpty(key)) return;
            float cur = Get(key);
            float next = Mathf.Clamp01(cur + Mathf.Max(0f, percent01));
            Main.Settings.DamageMap[key] = next;
        }

        public static void Set(
			string key,
			float value01)
		{
			if (string.IsNullOrEmpty(key) ||
				Main.Settings == null)
			{
				return;
			}

			float clampedValue =
				Mathf.Clamp01(value01);

			if (clampedValue <= 0.0001f)
			{
				Main.Settings.DamageMap.Remove(
					key);

				return;
			}

			Main.Settings.DamageMap[key] =
				clampedValue;
		}

        public static Dictionary<string, float> All() => Main.Settings.DamageMap;

        public static void ReplaceAll(IDictionary<string, float> newMap)
		{
			if (Main.Settings == null)
			{
				return;
			}

			Main.Settings.DamageMap.Clear();

			if (newMap == null)
			{
				return;
			}

			foreach (KeyValuePair<string, float> entry in newMap)
			{
				if (string.IsNullOrWhiteSpace(entry.Key))
				{
					continue;
				}

				float value =
					Mathf.Clamp01(entry.Value);

				if (value <= 0.0001f)
				{
					continue;
				}

				Main.Settings.DamageMap[entry.Key] =
					value;
			}
		}

        public static string MakeKey(Junction j)
		{
			if (j == null) return "NULL";

			string id = (j.junctionData.junctionIdLong ?? "X").Trim();

			if (string.IsNullOrEmpty(id))
			{
				id = j?.gameObject?.name ?? "Switch";
			}

			return id;
		}

        private static int HashPositionXZ(Vector3 p)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(p.x * 10f);
                h = h * 31 + Mathf.RoundToInt(p.z * 10f);
                return h;
            }
        }
    }

    public static class FlipGuard
    {
        private static readonly Dictionary<string, float> blockUntil = new Dictionary<string, float>(256);

        public static void Block(string key, float seconds)
        {
            if (string.IsNullOrEmpty(key)) return;
            blockUntil[key] = Time.unscaledTime + Mathf.Max(0f, seconds);
        }

        public static bool IsBlocked(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!blockUntil.TryGetValue(key, out var t)) return false;
            return Time.unscaledTime < t;
        }
    }

    internal static class JM_Save
    {
        public const string KEY = "JunctionMaintenance_Map";

        public static JArray ToJsonArray(IDictionary<string, float> map)
        {
            var arr = new JArray();
            if (map == null || map.Count == 0) return arr;
            foreach (var kv in map)
            {
                var o = new JObject
                {
                    ["Junction"] = kv.Key,
                    ["Damage"] = Mathf.Clamp01(kv.Value)
                };
                arr.Add(o);
            }
            return arr;
        }

        public static Dictionary<string, float> FromToken(JToken token)
        {
            var result = new Dictionary<string, float>(1024);
            if (token == null || token.Type == JTokenType.Null) return result;

            if (token.Type == JTokenType.Array)
            {
                foreach (var t in (JArray)token)
                {
                    var key = (string)t["Junction"];
                    var valTok = t["Damage"];
                    if (string.IsNullOrEmpty(key) || valTok == null) continue;
                    float v = 0f;
                    try { v = valTok.ToObject<float>(); } catch { }
                    result[key] = Mathf.Clamp01(v);
                }
            }
            else if (token.Type == JTokenType.Object)
            {
                foreach (var prop in ((JObject)token).Properties())
                {
                    float v = 0f;
                    try { v = prop.Value.ToObject<float>(); } catch { }
                    result[prop.Name] = Mathf.Clamp01(v);
                }
            }
            return result;
        }
    }
	internal static class JM_SaveRuntime
	{
		private static SaveGameData _lastLoadedSaveData;

		public static void LoadFromSaveData(
			SaveGameData saveData,
			bool force = false)
		{
			try
			{
				if (saveData == null)
				{
					Main.Log(
						"LoadFromSaveData skipped: SaveGameData is null.",
						true);

					return;
				}
				
				if (!MultiplayerAPI.Instance.IsHost)
				{
					Main.Log(
						"LoadFromSaveData skipped: Player is Client.",
						true);

					return;
				}

				if (!force &&
					ReferenceEquals(
						_lastLoadedSaveData,
						saveData))
				{
					return;
				}

				JObject dataObject =
					saveData.GetJsonObject();

				var loadedMap =
					new Dictionary<string, float>(1024);

				if (dataObject != null &&
					dataObject.TryGetValue(
						JM_Save.KEY,
						out JToken token) &&
					token != null &&
					token.Type != JTokenType.Null)
				{
					loadedMap =
						JM_Save.FromToken(token);
				}

				DamageStore.ReplaceAll(
					loadedMap);

				_lastLoadedSaveData =
					saveData;

				Main.Log(
					$"Loaded junction damage from savegame: " +
					$"{loadedMap.Count} entries.",
					true);
			}
			catch (Exception e)
			{
				Main.Log(
					"LoadFromSaveData error: " + e,
					true);
			}
		}

		public static void EnsureLoadedFromSaveOnce()
		{
			try
			{
				SaveGameData currentSaveData =
					SaveGameManager.Instance?.data;

				if (currentSaveData == null)
				{
					return;
				}

				LoadFromSaveData(
					currentSaveData,
					force: false);
			}
			catch (Exception e)
			{
				Main.Log(
					"EnsureLoadedFromSaveOnce error: " + e,
					true);
			}
		}
		
		public static void ResetForNewSession()
		{
			DamageStore.ReplaceAll(
				new Dictionary<string, float>());

			_lastLoadedSaveData = null;

			Main.Log(
				"New session: junction damage runtime reset.",
				true);
		}
		
		public static void InvalidateLoadedSave()
		{
			_lastLoadedSaveData = null;
		}
	}
}
