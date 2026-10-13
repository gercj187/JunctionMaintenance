// File: Main.cs
// Namespace: JunctionMaintenance
// Contains: Main (Laden, Update, Logging)
//
// Aufbau wie TrackMaintenance:
//   Main.cs            Laden, OnUpdate, Logging
//   Settings.cs        Einstellungen (inkl. Einstellungsseite), Schadensspeicher, Spielstand
//   Localization.cs    Mehrsprachigkeit (en + de), Translations.cs (19 weitere Sprachen)
//   RepairEconomy.cs   Reparaturmodi, Versicherung, zwei Lizenzen (DVCustomLicenses)
//   Damage.cs          Schadensauslöser: Entgleisung, Mitschleifen, Explosion
//   Patches.cs         Spielstand, Auffahren der Weiche, zufälliges Umstellen, Sperre bei 100 %
//   CareerManager.cs   Menü, Liste, Reparatursperre mit Fortschrittsanzeige, Zughalt, Kasse
//   MaintenanceBusy.cs Fahrzeug beschäftigt – modübergreifend mit Track Maintenance (ohne Abhängigkeit)

using System;
using System.IO;
using HarmonyLib;
using UnityModManagerNet;

namespace JunctionMaintenance
{
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry;
        public static Settings Settings;        // wirksame Einstellungen (Client: die des Hosts)
        public static Settings LocalSettings;   // eigene, gespeicherte Einstellungen
        public static bool Enabled = true;

        private static Harmony harmony;

        static bool Load(UnityModManager.ModEntry entry)
        {
            ModEntry = entry;

            SelfHealSettingsFile(entry);

            try
            {
                LocalSettings = UnityModManager.ModSettings.Load<Settings>(entry);
            }
            catch (Exception e)
            {
                entry.Logger.Error("Settings could not be loaded, using defaults: " + e);
                LocalSettings = null;
            }
            if (LocalSettings == null)
            {
                LocalSettings = new Settings();
                try { LocalSettings.Save(entry); }
                catch (Exception e) { entry.Logger.Error("Saving default settings failed: " + e); }
            }
            Settings = LocalSettings;

            // Sprachen laden (eingebaut + <Mod>/lang/*.txt)
            Loc.Init(entry.Path);

            entry.OnToggle = OnToggle;
            // Immer die eigenen Einstellungen zeichnen/speichern (Client sieht nur einen Hinweis)
            entry.OnGUI = e => LocalSettings.Draw(e);
            entry.OnSaveGUI = OnSaveGUI;
            entry.OnUpdate = OnUpdate;

            harmony = new Harmony(entry.Info.Id);
            harmony.PatchAll(typeof(Main).Assembly);

            JM_Multiplayer.Initialize();

            entry.Logger.Log("JunctionMaintenance loaded");
            return true;
        }

        private static void OnSaveGUI(UnityModManager.ModEntry entry)
        {
            if (JM_Multiplayer.IsClient) return;   // Client: Einstellungen kommen vom Host
            LocalSettings.Save(entry);
            JM_Multiplayer.BroadcastHostSettings();
        }

        // Client: Einstellungen des Hosts für die Dauer der Sitzung verwenden (nichts wird gespeichert)
        internal static void UseHostSettings(Settings host)
        {
            if (host == null) return;
            host.logging = LocalSettings != null && LocalSettings.logging; // Logging bleibt lokal
            Settings = host;
            JunctionLicenses.ApplyValues();
            Log("[MP] Host settings active (local settings unchanged)");
        }

        internal static void RestoreLocalSettings()
        {
            if (LocalSettings == null || ReferenceEquals(Settings, LocalSettings)) return;
            Settings = LocalSettings;
            JunctionLicenses.ApplyValues();
            Log("[MP] Local settings restored");
        }

        private static bool OnToggle(UnityModManager.ModEntry entry, bool value)
        {
            Enabled = value;
            if (!value)
            {
                // Nichts darf hängen bleiben (Zughalt, offene Liste)
                RepairHold.Release();
                DerailTracker.Clear();
                JM_BusyPublisher.Clear();
            }
            return true;
        }

        // Normale Meldungen und Warnungen nur mit "Print debug logs".
        // Fehler werden immer ausgegeben, sonst sind Fehlerberichte unmöglich.
        public static void Log(string message)
        {
            if (Settings != null && Settings.logging) ModEntry?.Logger.Log(message);
        }

        public static void Warn(string message)
        {
            if (Settings != null && Settings.logging) ModEntry?.Logger.Warning(message);
        }

        public static void Error(string message)
        {
            ModEntry?.Logger.Error(message);
        }

        // Wird von UMM jeden Frame aufgerufen
        private static void OnUpdate(UnityModManager.ModEntry entry, float dt)
        {
            if (!Enabled) return;

            try
            {
                // Laufende Reparatur weiterzählen (Sperre)
                JunctionRepair.Tick();

                // Reparatur nur im Stand: bei Bewegung Liste/Kasse zwangsweise verlassen
                JM_ListState.Tick();

                // Zug festhalten, solange eine Reparatur läuft
                RepairHold.Tick();

                // Fahrzeuge mit laufender Reparatur für andere Wartungs-Mods kennzeichnen
                JM_BusyPublisher.Tick();

                // CareerManagerAPI: angeforderten Bildschirm öffnen
                CmApiCompat.Tick();
            }
            catch (Exception e)
            {
                Error("Repair update failed: " + e);
            }

            try
            {
                // Preis und Copay der Lizenzen aus den Einstellungen übernehmen
                JunctionLicenses.Tick();
            }
            catch (Exception e)
            {
                Error("License update failed: " + e);
            }

            if (!Settings.DamageWhenDragged) return;

            try
            {
                DerailTracker.Tick(dt);
            }
            catch (Exception e)
            {
                Error("Derail tracker failed: " + e);
            }
        }

        // Kaputte Settings.xml (leer/abgeschnitten) beiseitelegen, damit Standardwerte entstehen
        private static void SelfHealSettingsFile(UnityModManager.ModEntry entry)
        {
            try
            {
                string path = Path.Combine(entry.Path, "Settings.xml");
                if (!File.Exists(path)) return;

                bool bad = new FileInfo(path).Length < 8;
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
                    entry.Logger.Warning("Invalid Settings.xml moved to: " + bak + " (defaults will be created)");
                }
            }
            catch (Exception e)
            {
                entry.Logger.Error("SelfHealSettingsFile failed: " + e);
            }
        }
    }
}
