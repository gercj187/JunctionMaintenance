// File: Main.cs
// Namespace: JunctionMaintenance
// Contains: Main (loader, GUI, update), repair helpers

using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityModManagerNet;
using HarmonyLib;
using DV;

namespace JunctionMaintenance
{
    public class Main
    {
        public static UnityModManager.ModEntry Mod;
        public static Settings Settings;
        public static bool Enabled;
        private static Harmony _harmony;
		public static bool HasCustomLicensesMod;

        static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            SelfHealSettingsFile(modEntry);
			
			HasCustomLicensesMod = UnityModManager.modEntries.Any(m => m.Info.Id.Equals("DVCustomLicenses", StringComparison.OrdinalIgnoreCase));

			Log("[JunctionMaintenance] DVCustomLicenses installed: " + HasCustomLicensesMod, true);

            try
            {
                Settings = Settings.Load<Settings>(modEntry);
            }
            catch (Exception ex)
            {
                Log("Settings.Load failed, creating defaults. " + ex, force: true);
                Settings = new Settings();
                try { Settings.Save(modEntry); } catch (Exception ex2) { Log("Settings.Save failed: " + ex2, force: true); }
            }
			
			// FORCE MODE BASED ON CUSTOM LICENSE MOD
			if (HasCustomLicensesMod)
			{
				if (Settings.repairMode != RepairMode.Dynamic)
				{
					Settings.repairMode = RepairMode.Dynamic;
					Log("[JunctionMaintenance] Forcing Dynamic mode (DVCustomLicenses detected)", true);
				}
			}
			else
			{
				if (Settings.repairMode == RepairMode.Dynamic)
				{
					Settings.repairMode = RepairMode.Penalty;
					Log("[JunctionMaintenance] Dynamic mode disabled (DVCustomLicenses missing) -> fallback to Penalty", true);
				}
			}

            modEntry.OnToggle  = OnToggle;
            modEntry.OnGUI     = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;

            _harmony = new Harmony(modEntry.Info.Id);
            _harmony.PatchAll(typeof(Main).Assembly);

            Log("Loaded JunctionMaintenance.", force: true);
            return true;
        }

        private static void SelfHealSettingsFile(UnityModManager.ModEntry modEntry)
        {
            try
            {
                string path = Path.Combine(modEntry.Path, "Settings.xml");
                if (!File.Exists(path)) return;

                var fi = new FileInfo(path);
                bool bad = fi.Length < 8;
                if (!bad)
                {
                    string all = File.ReadAllText(path).TrimStart();
                    if (!all.StartsWith("<") || (!all.Contains("<Settings") && !all.Contains("<JunctionMaintenance.Settings")))
                        bad = true;
                }
                if (bad)
                {
                    string bak = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml";
                    File.Move(path, bak);
                    Log("Detected invalid Settings.xml. Moved to: " + bak + " and will regenerate defaults.", force: true);
                }
            }
            catch (Exception ex)
            {
                Log("SelfHealSettingsFile exception: " + ex, force: true);
            }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            Log("Toggled: " + (Enabled ? "ON" : "OFF"), force: true);
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
		{
			bool hasMod = Main.HasCustomLicensesMod;

			// =============================
			// GAME MODE (TOP)
			// =============================
			GUILayout.Label("Game Mode:", UnityModManager.UI.bold);
            GUILayout.Space(2);
			GUIStyle box = new GUIStyle(GUI.skin.box);
			box.alignment = TextAnchor.UpperLeft;
			box.wordWrap = true;
			GUILayout.Space(5);			
			GUILayout.BeginHorizontal();
			GUIStyle active   = new GUIStyle(GUI.skin.button);
			GUIStyle inactive = new GUIStyle(GUI.skin.button);
			
			if (hasMod)
			{
				GUI.enabled = false;
				GUILayout.Button("PAY", GUILayout.Width(150));
				GUILayout.Button("EARN", GUILayout.Width(150));
				GUI.enabled = true;
				
				if (GUILayout.Button("LICENSE",Settings.repairMode == RepairMode.Dynamic ? active : inactive,GUILayout.Width(150)))
				{
					Settings.repairMode = RepairMode.Dynamic;
				}
			}
			else
			{
				GUILayout.BeginHorizontal();

				active.normal.textColor = Color.green;

				// PAY
				if (GUILayout.Button("PAY",
					Settings.repairMode == RepairMode.Penalty ? active : inactive,
					GUILayout.Width(150)))
				{
					Settings.repairMode = RepairMode.Penalty;
				}

				// EARN
				if (GUILayout.Button("EARN",
					Settings.repairMode == RepairMode.Reward ? active : inactive,
					GUILayout.Width(150)))
				{
					Settings.repairMode = RepairMode.Reward;
				}

				// LICENSE
				if (hasMod)
				{
					if (GUILayout.Button("LICENSE",Settings.repairMode == RepairMode.Dynamic ? active : inactive,GUILayout.Width(150)))
					{
						Settings.repairMode = RepairMode.Dynamic;
					}
				}
				else
				{
					GUI.enabled = false;
					GUILayout.Button("LICENSE", GUILayout.Width(150));
					GUI.enabled = true;
				}

				GUILayout.EndHorizontal();
			}
			GUILayout.EndHorizontal();
			if (!hasMod)
			{
				GUIStyle style = new GUIStyle(GUI.skin.label);
				style.normal.textColor = Color.gray;
				style.fontStyle = FontStyle.Italic;

				GUILayout.Label("License mode requires the DVCustomLicenses mod.", style);
			}
			GUILayout.Space(5);
			// =============================
			// INFO BOX
			// =============================
			string info = "";
			if (Settings.repairMode == RepairMode.Penalty)
			{
				info = "Since you most likely caused the damage to the junction, you are responsible for paying the repair costs.";
			}
			else if (Settings.repairMode == RepairMode.Reward)
			{
				info = "Regardless of who caused the damage, repairing a junction grants you a small compensation for your effort.";
			}
			else
			{
				info = "Railway switches are expensive.\n" +
					   "To avoid paying full repair costs for every incident, you can acquire the maintenance license\n" +
					   "and get partially compensated as an authorized technician.";
			}			
            GUILayout.Space(2);	
			GUILayout.Box(info, box, GUILayout.ExpandWidth(true));
			GUILayout.Space(5);

			// =============================
			// ECONOMY SETTINGS
			// =============================
			GUILayout.Label("Economy", UnityModManager.UI.bold);
            GUILayout.Space(2);
			GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Space(2);

			// PAY
			if (Settings.repairMode == RepairMode.Penalty)
			{
				Settings.maxRepairCostFull = FloatFieldL("Max Repair Cost:", Settings.maxRepairCostFull);
			}

			// EARN
			if (Settings.repairMode == RepairMode.Reward)
			{
				Settings.maxRepairRewardFull = FloatFieldL("Max Repair Payout:", Settings.maxRepairRewardFull);
			}

			// LICENSE (Dynamic)
			if (Settings.repairMode == RepairMode.Dynamic)
			{
				Settings.maxRepairCostFull   = FloatFieldL("Max Repair Cost:", Settings.maxRepairCostFull);
				Settings.maxRepairRewardFull = FloatFieldL("Max Repair Payout:", Settings.maxRepairRewardFull);

				GUILayout.Space(3);
				Settings.maintenanceLicensePrice = FloatFieldL("Maintenance License Price:", Settings.maintenanceLicensePrice);
			}
            GUILayout.Space(2);
			GUILayout.EndVertical();	
			GUILayout.Space(5);

			// =============================
			// DAMAGE BEHAVIOR
			// =============================
			GUILayout.Label("Damage Behavior", UnityModManager.UI.bold);
            GUILayout.Space(2);
			GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Space(2);

			Settings.BlockManualSwitchAtFullDamage =
				GUILayout.Toggle(Settings.BlockManualSwitchAtFullDamage,
				"Disable switches at 100% damage");

            GUILayout.Space(2);
			GUILayout.EndVertical();	
			GUILayout.Space(5);
			// =============================
			// RANDOM FLIP
			// =============================
			GUILayout.Label("Random Flip", UnityModManager.UI.bold);
            GUILayout.Space(2);
			GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Space(2);

			Settings.enableRandomFlip = GUILayout.Toggle(Settings.enableRandomFlip, "Enable random flip");

			if (Settings.enableRandomFlip)
			{
				GUILayout.Space(5);

				GUILayout.BeginHorizontal();
				GUILayout.Label($"Safe Speed: {Settings.safeNoFlipSpeedKmh:0} km/h", GUILayout.Width(250));
				Settings.safeNoFlipSpeedKmh = Mathf.Round(
					GUILayout.HorizontalSlider(Settings.safeNoFlipSpeedKmh, 1, 30, GUILayout.Width(250))
				);
				GUILayout.EndHorizontal();

				GUILayout.BeginHorizontal();
				GUILayout.Label($"Multiplier: {Settings.flipMultiplierPercent:0.00}", GUILayout.Width(250));
				Settings.flipMultiplierPercent =
					Mathf.Round(GUILayout.HorizontalSlider(Settings.flipMultiplierPercent, 0.01f, 0.50f, GUILayout.Width(250)) * 100f) / 100f;
				GUILayout.EndHorizontal();

				GUILayout.Space(5);

				GUILayout.Label("Formula Preview:");
				GUILayout.Label($"1% damage  → {(1f * Settings.flipMultiplierPercent):0.##}% chance");
				GUILayout.Label($"10% damage → {(10f * Settings.flipMultiplierPercent):0.##}% chance");
				GUILayout.Label($"100% damage → {(100f * Settings.flipMultiplierPercent):0.##}% chance");
			}

            GUILayout.Space(2);
			GUILayout.EndVertical();	
			GUILayout.Space(5);

			// =============================
			// LOGGING
			// =============================
			//Settings.logging = GUILayout.Toggle(Settings.logging, "Enable logging");

			GUILayout.Space(10);

			// =============================
			// DEBUG BUTTON
			// =============================
			if (GUILayout.Button("List all damaged junctions in log", GUILayout.Width(500)))
			{
				Log("==== DAMAGED JUNCTIONS ====", true);

				foreach (var kv in DamageStore.All())
					Log($"Damaged: {kv.Key} -> {kv.Value * 100f:0.###}%", true);

				Log("==== END ====", true);
			}
		}

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry);
            Log("Settings saved.", force: true);
        }

        // --- Logging and UI helpers ---

        public static void Log(string msg, bool force = false)
        {
            if (!force && Settings != null && !Settings.logging) return;
            Mod?.Logger.Log(msg);
        }

        private static float FloatFieldL(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(320));
            string s = GUILayout.TextField(value.ToString(System.Globalization.CultureInfo.InvariantCulture), GUILayout.Width(120));
            GUILayout.EndHorizontal();
            if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                return f;
            return value;
        }

        private static string TextFieldL(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(320));
            string s = GUILayout.TextField(value ?? string.Empty, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            return s;
        }

        private static bool ToggleL(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            bool v = GUILayout.Toggle(value, label, GUILayout.Width(320));
            GUILayout.EndHorizontal();
            return v;
        }
    }
}
