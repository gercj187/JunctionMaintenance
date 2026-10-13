// File: RepairEconomy.cs
// Namespace: JunctionMaintenance
// Reparatur-Modi und Versicherung (gleiches Modell wie TrackMaintenance). Ersetzt MaintenanceLicense.cs.
//
//   Penalty  = Spieler zahlt die Reparatur. Die Versicherung des Spiels greift (Selbstbeteiligung).
//   Reward   = Spieler erhält für jede Reparatur eine Vergütung.
//   License  = zwei Lizenzen (DVCustomLicenses):
//              ohne Lizenz:                          keine Reparatur möglich
//              "JunctionMaintenance":                wie Penalty (zahlen, Versicherung greift)
//              "JunctionMaintenanceContractor":      wie Reward (Vergütung); setzt die erste Lizenz voraus
//              Preis und Eigenanteil-Erhöhung (Copay) beider Lizenzen sind einstellbar.
//
// Ist DVCustomLicenses aktiv, gilt immer License; ohne die Mod nur Penalty oder Reward.
//
// Versicherung (Penalty und License mit erster Lizenz), wie im Spiel (CareerManagerDebtController):
//   - Spieleranteil einer Reparatur = min(Kosten, noch offener Teil der Quote); Rest trägt die Versicherung.
//   - Nach der Zahlung wird der Spieleranteil auf die Quote angerechnet.
//   - Ist die Quote erreicht, wird die Versicherungsleistung ausgelöst wie beim Tilgen im CareerManager.

using System;
using System.Linq;
using System.Xml.Serialization;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using DV;
using DV.Utils;
using DV.ThingTypes;
using DV.ServicePenalty;
using DV.ServicePenalty.UI;
using DV.InventorySystem;

namespace JunctionMaintenance
{
    public enum RepairMode
    {
        Penalty = 0,
        Reward = 1,

        // In alten Settings.xml heißt der Lizenzmodus "Dynamic": weiterhin so lesen und schreiben
        [XmlEnum("Dynamic")]
        License = 2
    }

    // Ergebnis der Preisberechnung für eine Reparatur
    internal struct RepairQuote
    {
        public bool IsReward;      // true = Spieler bekommt Geld (Payout), sonst zahlt er (Pay)
        public double Payout;      // Vergütung (nur Reward)
        public double FullCost;    // voller Reparaturpreis
        public double Pay;         // Spieleranteil an der Kasse
        public double Covered;     // von der Versicherung übernommen
        public bool Insured;       // Versicherung ist an dieser Reparatur beteiligt
    }

    internal static class RepairEconomy
    {
        // ---------- Modus ----------

        // DVCustomLicenses ist installiert UND aktiviert (nötig für den License-Modus)
        public static bool HasCustomLicensesMod
        {
            get
            {
                if (!licenseModChecked)
                {
                    licenseModChecked = true;
                    licenseModPresent = UnityModManager.modEntries != null && UnityModManager.modEntries.Any(m =>
                        m.Info != null && m.Enabled &&
                        string.Equals(m.Info.Id, "DVCustomLicenses", StringComparison.OrdinalIgnoreCase));
                    Main.Log("DVCustomLicenses active: " + licenseModPresent);
                }
                return licenseModPresent;
            }
        }
        private static bool licenseModChecked, licenseModPresent;

        // Wirksamer Modus:
        //   DVCustomLicenses aktiv  -> immer License
        //   DVCustomLicenses fehlt  -> Penalty oder Reward laut Einstellung
        public static RepairMode Mode
        {
            get
            {
                if (HasCustomLicensesMod) return RepairMode.License;
                var m = Main.Settings.repairMode;
                return m == RepairMode.License ? RepairMode.Penalty : m;
            }
        }

        public static bool IsRewardNow
        {
            get
            {
                switch (Mode)
                {
                    case RepairMode.Reward: return true;
                    case RepairMode.License: return JunctionLicenses.Contractor.Has;
                    default: return false;
                }
            }
        }

        public static bool InsuranceApplies
        {
            get { return Mode == RepairMode.Penalty || Mode == RepairMode.License; }
        }

        // Im Lizenzmodus darf nur mit Lizenz repariert werden
        public static bool CanRepair
        {
            get
            {
                if (Mode != RepairMode.License) return true;
                return JunctionLicenses.Basic.Has || JunctionLicenses.Contractor.Has;
            }
        }

        // ---------- Preise ----------

        public static double CostFor(float fraction01)
        {
            return Math.Round(Math.Max(0.0, Main.Settings.maxRepairCostFull) * Mathf.Clamp01(fraction01), 2);
        }

        public static double PayoutFor(float fraction01)
        {
            return Math.Round(Math.Max(0.0, Main.Settings.maxRepairRewardFull) * Mathf.Clamp01(fraction01), 2);
        }

        public static RepairQuote Quote(float fraction01)
        {
            var q = new RepairQuote();

            if (IsRewardNow)
            {
                q.IsReward = true;
                q.Payout = PayoutFor(fraction01);
                return q;
            }

            q.FullCost = CostFor(fraction01);
            q.Pay = q.FullCost;

            InsuranceFeeQuota fq = InsuranceApplies && q.FullCost > 0.0 ? Quota() : null;
            if (fq != null)
            {
                double left = Math.Max(0.0, fq.LeftToReachQuota);
                q.Insured = true;
                q.Pay = Math.Round(Math.Min(q.FullCost, left), 2);
                q.Covered = Math.Round(q.FullCost - q.Pay, 2);
            }
            return q;
        }

        // ---------- Versicherung ----------

        // Gecacht: FindObjectOfType durchsucht die ganze Szene (sonst pro Listenzeile)
        private static CareerManagerDebtController controller;
        private static float nextControllerSearch;

        private static CareerManagerDebtController Controller()
        {
            if (controller != null) return controller;
            if (Time.realtimeSinceStartup < nextControllerSearch) return null;
            nextControllerSearch = Time.realtimeSinceStartup + 2f;

            // Nicht über SingletonBehaviour<>.Instance: das würde den Controller ggf. erst anlegen
            controller = UnityEngine.Object.FindObjectOfType<CareerManagerDebtController>();
            return controller;
        }

        private static InsuranceFeeQuota Quota()
        {
            try
            {
                var ctrl = Controller();
                if (ctrl == null || ctrl.feeQuota == null) return null;
                var fq = ctrl.feeQuota;
                if (!fq.InsuranceUsed || fq.Quota <= 0f) return null;
                return fq;
            }
            catch (Exception e)
            {
                Main.Error("Reading insurance quota failed: " + e);
                return null;
            }
        }

        // Nach der Reparatur: Spieleranteil anrechnen; bei erreichter Quote Versicherungsleistung auslösen.
        public static bool AfterPaidRepair(RepairQuote q)
        {
            if (!q.Insured) return false;

            try
            {
                var ctrl = Controller();
                if (ctrl == null || ctrl.feeQuota == null || !ctrl.feeQuota.InsuranceUsed) return false;

                if (q.Pay > 0.0)
                    ctrl.UpdateInsuranceFeePaidAmount((float)q.Pay);

                if (!ctrl.feeQuota.QuotaReached) return false;

                ctrl.RefreshExistingDebtsState();
                ctrl.ClearDebtsViaInsuranceQuotaReached();
                Main.Log($"Insurance quota reached by junction repair: covered {q.Covered:F2}, other fees cleared, quota reset");
                return true;
            }
            catch (Exception e)
            {
                Main.Error("Insurance handling failed: " + e);
                return false;
            }
        }

        // ---------- Vergütung ----------

        public static bool PrintMoney(double amount)
        {
            if (amount <= 0.0) return true;
            try
            {
                var printer = UnityEngine.Object.FindObjectOfType<MoneyPrinter>();
                if (printer == null)
                {
                    Main.Error("MoneyPrinter not found, payout not printed");
                    return false;
                }
                printer.PrintMoney(amount);
                return true;
            }
            catch (Exception e)
            {
                Main.Error("Printing payout failed: " + e);
                return false;
            }
        }
    }

    // =========================
    // LIZENZEN (DVCustomLicenses)
    // =========================

    internal sealed class JunctionLicense
    {
        // Muss EXAKT dem "Identifier" in der jeweiligen license.json entsprechen
        public readonly string ID;

        private GeneralLicenseType_v2 cached;
        private float nextSearch;

        public JunctionLicense(string id) { ID = id; }

        public GeneralLicenseType_v2 Get()
        {
            if (cached != null) return cached;

            // Nicht bei jedem Aufruf suchen (wird pro Listenzeile abgefragt), höchstens alle 5 s
            if (Time.realtimeSinceStartup < nextSearch) return null;
            nextSearch = Time.realtimeSinceStartup + 5f;

            try
            {
                var list = Globals.G.Types.generalLicenses;
                if (list == null || list.Count == 0) return null; // Liste noch nicht geladen

                cached = list.FirstOrDefault(l => l != null && l.id == ID);
                if (cached == null)
                    Main.Warn("License not found: " + ID + " (DVCustomLicenses JSON missing?)");
            }
            catch (Exception e)
            {
                Main.Error("License lookup failed (" + ID + "): " + e);
            }
            return cached;
        }

        public bool Has
        {
            get
            {
                var lic = Get();
                if (lic == null) return false;
                try
                {
                    var mgr = SingletonBehaviour<LicenseManager>.Instance;
                    return mgr != null && mgr.IsGeneralLicenseAcquired(lic);
                }
                catch (Exception e)
                {
                    Main.Error("License check failed (" + ID + "): " + e);
                    return false;
                }
            }
        }

        // Preis und Eigenanteil-Erhöhung (Copay) aus den Einstellungen übernehmen
        public void ApplyValues(float price, float copay)
        {
            var lic = Get();
            if (lic == null) return;

            if (price > 0f && Math.Abs(lic.price - price) > 0.5f)
            {
                lic.price = price;
                Main.Log($"License {ID}: price set to {price:F0}");
            }
            if (copay >= 0f && Math.Abs(lic.insuranceFeeQuotaIncrease - copay) > 0.5f)
            {
                lic.insuranceFeeQuotaIncrease = copay;
                Main.Log($"License {ID}: copay increase set to {copay:F0}");
            }
        }
    }

    internal static class JunctionLicenses
    {
        // Erste Lizenz: darf reparieren, zahlt (mit Versicherung). ID der bisherigen Lizenz bleibt.
        public static readonly JunctionLicense Basic = new JunctionLicense("JunctionMaintenance");
        // Zweite Lizenz (setzt die erste voraus): wird für Reparaturen bezahlt
        public static readonly JunctionLicense Contractor = new JunctionLicense("JunctionMaintenanceContractor");

        private static float nextApply;

        public static void ApplyValues()
        {
            if (Main.Settings == null || !RepairEconomy.HasCustomLicensesMod) return;
            try
            {
                Basic.ApplyValues(Main.Settings.maintenanceLicensePrice, Main.Settings.licenseCopay);
                Contractor.ApplyValues(Main.Settings.license2Price, Main.Settings.license2Copay);
            }
            catch (Exception e)
            {
                Main.Error("Applying license values failed: " + e);
            }
        }

        // Aus Main.OnUpdate: Änderungen in den Einstellungen übernehmen
        public static void Tick()
        {
            if (Time.realtimeSinceStartup < nextApply) return;
            nextApply = Time.realtimeSinceStartup + 2f;
            ApplyValues();
        }
    }

    // Preise und Copay setzen, bevor bzw. nachdem die Lizenzen des Spielstands geladen werden
    [HarmonyPatch(typeof(LicenseManager), "LoadData")]
    internal static class JM_LicenseValues
    {
        static void Prefix() { JunctionLicenses.ApplyValues(); }
        static void Postfix() { JunctionLicenses.ApplyValues(); }
    }
}
