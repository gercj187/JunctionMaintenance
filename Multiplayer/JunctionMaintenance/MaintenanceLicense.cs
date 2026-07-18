// File: MaintenanceLicense.cs
// Namespace: JunctionMaintenance

using System;
using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using DV;
using DV.Utils;
using DV.ThingTypes;

namespace JunctionMaintenance
{
    public static class MaintenanceLicense
    {
        // =============================
        // CONFIG
        // =============================

        // MUST match JSON Identifier EXACTLY
        public const string ID = "JunctionMaintenance";

        // =============================
        // RUNTIME CACHE
        // =============================

        private static GeneralLicenseType_v2 _cached;
        private static bool _searched;

        // =============================
        // FIND LICENSE
        // =============================

        public static GeneralLicenseType_v2 Get()
        {
            if (_searched) return _cached;
            _searched = true;

            try
            {
                var list = Globals.G.Types.generalLicenses;

                if (list == null || list.Count == 0)
                {
                    Main.Log("[JunctionMaintenance] License list not ready", true);
                    return null;
                }

                _cached = list.FirstOrDefault(l => l.id == ID);

                if (_cached == null)
                {
                    Main.Log("[JunctionMaintenance] License NOT FOUND: " + ID, true);

                    // DEBUG: list all available licenses
                    foreach (var l in list)
                        Main.Log("[JunctionMaintenance] Available: " + l.id);
                }
                else
                {
                    Main.Log("[JunctionMaintenance] License found: " + ID);
                }
            }
            catch (Exception e)
            {
                Main.Log("[JunctionMaintenance] Get ERROR: " + e, true);
            }

            return _cached;
        }

        // =============================
        // HAS LICENSE
        // =============================

        public static bool HasLicense
        {
            get
            {
                var lic = Get();
                if (lic == null) return false;

                try
                {
                    var mgr = SingletonBehaviour<LicenseManager>.Instance;
                    if (mgr == null) return false;

                    return mgr.IsGeneralLicenseAcquired(lic);
                }
                catch (Exception e)
                {
                    Main.Log("[JunctionMaintenance] HasLicense ERROR: " + e, true);
                    return false;
                }
            }
        }

        // =============================
        // DYNAMIC MODE HELPER
        // =============================

        public static RepairMode GetEffectiveMode()
		{
			if (Main.Settings == null)
				return RepairMode.Penalty;

			if (!Main.HasCustomLicensesMod)
				return Main.Settings.repairMode; // kein Dynamic möglich

			if (Main.Settings.repairMode != RepairMode.Dynamic)
				return Main.Settings.repairMode;

			return HasLicense
				? RepairMode.Reward
				: RepairMode.Penalty;
		}

        // =============================
        // PRICE OVERRIDE
        // =============================

        internal static void ReapplyConfiguredPrice()
        {
            try
            {
                if (Main.Settings == null)
                {
                    Debug.Log(
                        "[JunctionMaintenance] " +
                        "Settings null - skip license price");

                    return;
                }

                GeneralLicenseType_v2 lic =
                    Get();

                if (lic == null)
                {
                    Debug.LogError(
                        "[JunctionMaintenance] " +
                        "JunctionMaintenance license NOT FOUND");

                    return;
                }

                float price =
                    Mathf.Max(
                        0f,
                        Main.Settings.maintenanceLicensePrice);

                if (price > 0f)
                {
                    lic.price =
                        price;

                    Debug.Log(
                        "[JunctionMaintenance] " +
                        "License price applied: " +
                        price);
                }
                else
                {
                    Debug.Log(
                        "[JunctionMaintenance] " +
                        "License price unchanged (<= 0)");
                }
            }
            catch (Exception e)
            {
                Debug.LogError(
                    "[JunctionMaintenance] " +
                    "License price application ERROR: " +
                    e);
            }
        }

        // =============================
        // PRICE OVERRIDE PATCH
        // =============================

        [HarmonyPatch(typeof(LicenseManager), "LoadData")]
        public static class Patch_LicensePrice
        {
            static void Postfix(LicenseManager __instance)
            {
                ReapplyConfiguredPrice();
            }
        }
    }
}