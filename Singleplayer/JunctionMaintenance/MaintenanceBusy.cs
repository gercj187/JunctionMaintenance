// File: MaintenanceBusy.cs
// Namespace: JunctionMaintenance

using System;
using System.Collections.Generic;

namespace JunctionMaintenance
{
    // =========================
    // FAHRZEUG BESCHÄFTIGT (MODÜBERGREIFEND)
    // =========================
    // Gemeinsames Kennzeichen für mehrere Wartungs-Mods (TrackMaintenance, JunctionMaintenance, ...),
    // OHNE gegenseitige Abhängigkeit: ein Dictionary<string, string> (Wagen-ID -> Mod-ID) in den
    // AppDomain-Daten unter "CJ187.Maintenance.BusyVehicles". Jede Mod trägt nur ihre eigenen Einträge
    // ein und aus und prüft vor einer Reparatur, ob das Fahrzeug für eine andere Mod arbeitet.
    // Im Multiplayer trägt jede Mod auf jedem Rechner die Fahrzeuge aller laufenden Reparaturen ein
    // (bei TM: Auslöser der Animationen, vom Host gemeldet).
    // Gegenstück zu TrackMaintenance (dort ModId "TrackMaintenance").
    internal static class MaintenanceBusy
    {
        public const string DataKey = "CJ187.Maintenance.BusyVehicles";
        public const string ModId = "JunctionMaintenance";

        private static readonly List<string> own = new List<string>();
        private static readonly HashSet<string> next = new HashSet<string>();

        private static Dictionary<string, string> Map
        {
            get
            {
                var map = AppDomain.CurrentDomain.GetData(DataKey) as Dictionary<string, string>;
                if (map == null)
                {
                    map = new Dictionary<string, string>();
                    AppDomain.CurrentDomain.SetData(DataKey, map);
                }
                return map;
            }
        }

        // Mod-ID, für die das Fahrzeug gerade arbeitet (null = frei)
        public static string BusyBy(TrainCar car)
        {
            if (car == null || string.IsNullOrEmpty(car.ID)) return null;
            string mod;
            return Map.TryGetValue(car.ID, out mod) ? mod : null;
        }

        public static bool BusyByOther(TrainCar car)
        {
            string mod = BusyBy(car);
            return mod != null && mod != ModId;
        }

        // Eigene Einträge auf genau diese Fahrzeuge setzen (jeden Frame)
        public static void Publish(IEnumerable<TrainCar> cars)
        {
            next.Clear();
            if (cars != null)
                foreach (TrainCar c in cars)
                    if (c != null && !string.IsNullOrEmpty(c.ID)) next.Add(c.ID);

            if (next.Count == 0 && own.Count == 0) return;

            var map = Map;
            for (int i = own.Count - 1; i >= 0; i--)
            {
                if (next.Contains(own[i])) continue;
                string mod;
                if (map.TryGetValue(own[i], out mod) && mod == ModId) map.Remove(own[i]);
                own.RemoveAt(i);
            }
            foreach (string id in next)
            {
                if (own.Contains(id)) continue;
                if (map.ContainsKey(id)) continue;   // gehört schon einer anderen Mod
                map[id] = ModId;
                own.Add(id);
            }
        }
    }
}
