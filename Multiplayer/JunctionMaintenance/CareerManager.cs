// File: CareerManager.cs
// Namespace: JunctionMaintenance
// Menü "Weicheninstandhaltung" im CareerManager (Aufbau wie TrackMaintenance):
//   - Begleitwagen (Caboose): Liste der Weichen im Reparaturradius mit Schaden und Kosten/Vergütung,
//     Reparatur nur im Stand. Andere Fahrzeuge mit CareerManager bekommen keinen Eintrag.
//   - Bahnhof: Übersicht der Weichen des Bahnhofs (S-*-<CODE>), nur Anzeige.
//   - Reparatursperre: während einer Reparatur Fortschrittsanzeige (Balken, 0-100 %, Restzeit),
//     keine weitere Reparatur, Zugverband wird festgehalten (Bremse + Fahrschalter 0).
//     Die Sperre hängt an der laufenden Reparatur, nicht am Bildschirm.
//   - Bei Bewegung (> 1,5 km/h) werden Liste und Kasse zwangsweise verlassen.
//   - Kompatibel mit CareerManagerAPI (wwwDayDream) ohne Abhängigkeit (Reflection).
//   Alle Texte laufen über Loc.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using DV;
using DV.Utils;
using DV.InventorySystem;
using DV.ServicePenalty.UI;
using DV.Simulation.Brake;

namespace JunctionMaintenance
{
    // =========================
    // HILFEN
    // =========================

    internal static class JM_Helpers
    {
        private static readonly MethodInfo _miSetInfo =
            AccessTools.Method(typeof(CareerManagerInfoScreen), "SetInfoData",
                new[] { typeof(IDisplayScreen), typeof(string), typeof(string),
                        typeof(string), typeof(string), typeof(string), typeof(string), typeof(Action) });

        private const string CABOOSE_CM_NAME = "CareerManagerTrainInterior";

        // Fahrzeug, zu dessen Innenraum ein Objekt gehört (null = kein Fahrzeug, z. B. Station)
        public static TrainCar CarOf(Transform host)
        {
            if (host == null) return null;
            var interior = host.GetComponentInParent<TrainCarInteriorObject>();
            if (interior != null && interior.actualTrainCar != null) return interior.actualTrainCar;
            return host.GetComponentInParent<TrainCar>();
        }

        public static bool IsCaboose(TrainCar car)
        {
            return car != null && car.carType.ToString().IndexOf("Caboose", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // CareerManager im Innenraum eines Fahrzeugs (auch wenn das Fahrzeug nicht zu ermitteln ist)
        public static bool IsVehicleCareerManager(Transform host)
        {
            if (CarOf(host) != null) return true;
            for (Transform t = host; t != null; t = t.parent)
                if (t.name.StartsWith(CABOOSE_CM_NAME, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsCabooseCareerManager(Transform host)
        {
            TrainCar car = CarOf(host);
            if (car != null) return IsCaboose(car);

            for (Transform t = host; t != null; t = t.parent)
                if (t.name.StartsWith(CABOOSE_CM_NAME, StringComparison.OrdinalIgnoreCase))
                    return IsCaboose(ResolveSelfCar(host));
            return false;
        }

        // Eintrag in: Caboose und Bahnhöfen; nicht in anderen Fahrzeugen
        public static bool WantsEntry(Transform host)
        {
            return IsVehicleCareerManager(host) ? IsCabooseCareerManager(host) : true;
        }

        public static Transform HostOf(CareerManagerMainScreen screen)
        {
            return screen.screenSwitcher != null ? screen.screenSwitcher.transform : screen.transform;
        }

        // ---------- Bildschirm-Besitz ----------
        // Junction Maintenance und Track Maintenance (und ggf. weitere Mods) teilen sich denselben
        // Info-Bildschirm des CareerManagers. Jede Mod merkt sich nur "meine Liste ist dort aktiv".
        // Übernimmt eine andere Mod den Bildschirm, ohne dass er gewechselt wird (kein Disable), bliebe
        // unser Zustand aktiv und wir würden deren Tasten abfangen (Fehlerbild: Liste nicht mehr bedienbar).
        // Deshalb: Titel merken, den wir zuletzt gesetzt haben. Steht dort etwas anderes, gehört der
        // Bildschirm nicht mehr uns.
        private static string lastTitle;

        public static bool OwnsScreen(CareerManagerInfoScreen info)
        {
            if (info == null) return false;
            try
            {
                var tmp = info.title;
                if (tmp == null || lastTitle == null) return true;   // nicht prüfbar: als unseren behandeln
                return string.Equals(tmp.text, lastTitle, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        // Beliebig viele Zeilen im Paragraph-Feld (L1..L4 bleiben leer)
        public static void SetInfoLines(CareerManagerInfoScreen info, IDisplayScreen returnScreen,
                                        string title, IList<string> lines)
        {
            try
            {
                lastTitle = title;
                string paragraph = (lines == null || lines.Count == 0) ? string.Empty : string.Join("\n", lines);
                _miSetInfo?.Invoke(info, new object[] { returnScreen, title, paragraph, "", "", "", "", null });
                info.Activate(returnScreen);
            }
            catch (Exception e)
            {
                Main.Error("SetInfoLines failed: " + e);
            }
        }

        public static void GetMenuColors(DisplayScreenSwitcher sw, out Color regular, out Color highlighted)
        {
            regular = Color.white;
            highlighted = new Color(1f, 0.8f, 0.3f);
            if (sw == null) return;
            try
            {
                var t = sw.GetType();
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var fReg = t.GetField("REGULAR_COLOR", flags);
                var fHL = t.GetField("HIGHLIGHTED_COLOR", flags);
                if (fReg != null)
                {
                    var v = fReg.IsStatic ? fReg.GetValue(null) : fReg.GetValue(sw);
                    if (v is Color) regular = (Color)v;
                }
                if (fHL != null)
                {
                    var v = fHL.IsStatic ? fHL.GetValue(null) : fHL.GetValue(sw);
                    if (v is Color) highlighted = (Color)v;
                }
            }
            catch { }
        }

        // true = Fahrzeug steht (oder keins gefunden)
        public static bool IsStandingSelf(TrainCar selfCar)
        {
            if (selfCar == null || selfCar.rb == null) return true;
            float maxKmh = Mathf.Max(0.1f, Main.Settings.maxVehicleStandingSpeedKmh);
            return selfCar.rb.velocity.magnitude * 3.6f <= maxKmh;
        }

        // Fahrzeug des Host-Transforms, sonst nächster Caboose innerhalb 12 m
        public static TrainCar ResolveSelfCar(Transform hostTf)
        {
            if (hostTf == null) return null;

            var tc = CarOf(hostTf);
            if (tc != null) return tc;

            var all = CarSpawner.Instance?.AllCars;
            if (all == null) return null;

            Vector3 p = hostTf.position;
            float bestD2 = 12f * 12f;
            TrainCar best = null;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null) continue;
                string nm = c.name ?? "";
                if (nm.IndexOf("caboose", StringComparison.OrdinalIgnoreCase) < 0) continue;
                float d2 = (c.transform.position - p).sqrMagnitude;
                if (d2 < bestD2) { bestD2 = d2; best = c; }
            }
            return best;
        }

        // ---------- Weichen ----------

        public static string GetDisplayName(Junction j)
        {
            if (j == null) return "Switch";
            try
            {
                string idLong = j.junctionData.junctionIdLong;
                if (!string.IsNullOrWhiteSpace(idLong)) return idLong;
            }
            catch { }

            try
            {
                var goName = j.gameObject != null ? j.gameObject.name : null;
                return string.IsNullOrWhiteSpace(goName) ? "Switch" : goName;
            }
            catch { return "Switch"; }
        }

        public static List<(Junction j, float dSqr)> GetAllSortedWithin(Vector3 center, float radius)
        {
            float rSqr = radius * radius;
            var list = new List<(Junction, float)>(32);
            foreach (var j in JunctionLookup.All())
            {
                if (j == null) continue;
                float dSqr = (j.transform.position - center).sqrMagnitude;
                if (dSqr <= rSqr) list.Add((j, dSqr));
            }
            list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return list;
        }

        // Muster: S-<Nummer>-<STATION>
        private static readonly Regex RxStationSwitch = new Regex(@"^S-[^-]+-([A-Z]{1,4})$", RegexOptions.Compiled);

        // Stationscode aus den umgebenden Weichen (Mehrheit)
        public static string InferStationCode(Vector3 center, float probeRadius = 500f)
        {
            var hist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            float rSqr = probeRadius * probeRadius;

            foreach (var j in JunctionLookup.All())
            {
                if (j == null) continue;
                if ((j.transform.position - center).sqrMagnitude > rSqr) continue;

                var m = RxStationSwitch.Match(GetDisplayName(j));
                if (!m.Success) continue;

                string code = m.Groups[1].Value;
                int c;
                hist.TryGetValue(code, out c);
                hist[code] = c + 1;
            }

            string best = null;
            int bestC = -1;
            foreach (var kv in hist)
                if (kv.Value > bestC) { best = kv.Key; bestC = kv.Value; }
            return best;
        }

        // Alle Weichen der Station (S-*-<code>), nach Name sortiert
        public static List<(Junction j, float dSqr)> GetStationSwitches(string stationCode, Vector3 center)
        {
            var list = new List<(Junction, float)>(64);
            if (string.IsNullOrEmpty(stationCode)) return list;

            foreach (var j in JunctionLookup.All())
            {
                if (j == null) continue;
                string name = GetDisplayName(j);
                if (!name.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith("-" + stationCode, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add((j, (j.transform.position - center).sqrMagnitude));
            }

            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(GetDisplayName(a.Item1), GetDisplayName(b.Item1));
                return c != 0 ? c : a.Item2.CompareTo(b.Item2);
            });
            return list;
        }

        // ---------- Tabellenlayout (wie TrackMaintenance) ----------
        // <pos=NN%> setzt Spaltenanfänge fest, <mspace> macht Zahlen zur Festbreitenschrift,
        // Kosten rechtsbündig über einen Zeilenumbruch mit Zeilenhöhe 0.

        private const int POS_DAMAGE = 48;
        private const string RIGHT_BEGIN = "<line-height=0>\n<align=right>";
        private const string RIGHT_END = "<line-height=100%><align=left>";
        private const string MONO = "<mspace=0.5em>";
        private const string MONO_END = "</mspace>";
        private const string HEADER_SIZE = "<size=85%>";
        private const string HEADER_SIZE_END = "</size>";

        private const int NAME_MAX_CHARS = 18;
        private const int DAMAGE_WIDTH = 5;  // "100 %"

        private static string Fit(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length > max ? text.Substring(0, max) : text;
        }

        private static string Mono(string text, int width)
        {
            return MONO + (text ?? string.Empty).PadLeft(width) + MONO_END;
        }

        public static string HeaderLine(bool withPrice)
        {
            string s = "<align=left>" + HEADER_SIZE
                     + Fit(Loc.T("col.junction"), 20)
                     + "<pos=" + POS_DAMAGE + "%>" + Loc.T("col.damage");
            if (withPrice)
                s += RIGHT_BEGIN + Loc.T(RepairEconomy.IsRewardNow ? "col.payout" : "col.cost") + RIGHT_END;
            return s + HEADER_SIZE_END;
        }

        public static string FormatLine(JM_Entry e, bool withPrice)
        {
            string s = "<align=left>"
                     + Fit(e.Name, NAME_MAX_CHARS)
                     + "<pos=" + POS_DAMAGE + "%>" + Mono(e.Percent + " %", DAMAGE_WIDTH);
            if (withPrice && e.Percent > 0)
                s += RIGHT_BEGIN + Mono(PriceText(e.Quote), 0) + RIGHT_END;
            return s;
        }

        // Kosten = Spieleranteil (nach Versicherung), Vergütung mit "+"
        public static string PriceText(RepairQuote q)
        {
            return q.IsReward ? "+" + Loc.Money(q.Payout) : Loc.Money(q.Pay);
        }
    }

    // =========================
    // LISTENEINTRÄGE
    // =========================

    internal sealed class JM_Entry
    {
        public Junction J;
        public string Key;
        public string Name;
        public float Damage01;
        public int Percent;
        public RepairQuote Quote;
    }

    internal static class JM_Entries
    {
        public static List<JM_Entry> Build(Vector3 center, bool stationMode, string stationCode)
        {
            JM_SaveRuntime.EnsureLoadedFromSaveOnce();

            var src = stationMode && !string.IsNullOrEmpty(stationCode)
                ? JM_Helpers.GetStationSwitches(stationCode, center)
                : JM_Helpers.GetAllSortedWithin(center, Main.Settings.RepairRadius);

            float step = Main.Settings.RepairStep01;
            var list = new List<JM_Entry>(src.Count);
            foreach (var item in src)
            {
                if (item.j == null) continue;
                string key = JunctionKeyCache.Get(item.j);
                float dmg = DamageStore.Get(key);

                // Begleitwagen: nur beschädigte Weichen (reparierte verschwinden aus der Liste).
                // Bahnhof: Übersicht aller Weichen des Bahnhofs.
                bool damaged = dmg > 0.0001f;
                if (!stationMode && !damaged) continue;

                var e = new JM_Entry
                {
                    J = item.j,
                    Key = key,
                    Name = JM_Helpers.GetDisplayName(item.j),
                    Damage01 = dmg,
                    // Kleiner Restschaden nicht als "0 %" anzeigen
                    Percent = damaged ? Mathf.Max(1, Mathf.RoundToInt(dmg * 100f)) : 0
                };
                if (!stationMode && dmg > 0f)
                    e.Quote = RepairEconomy.Quote(Mathf.Min(step, dmg));
                list.Add(e);
            }
            return list;
        }
    }

    // =========================
    // LAUFENDE REPARATUREN (Sperre)
    // =========================
    // Der Schaden ist sofort repariert und gespeichert; die Reparatur "läuft" danach noch die
    // eingestellte Dauer (je nach repariertem Anteil zwischen Min und Max, nie sofort). Solange:
    //   - Fortschrittsanzeige statt Liste, keine weitere Reparatur in der Nähe
    //   - der Zugverband des auslösenden Begleitwagens wird festgehalten (RepairHold)
    //   - die Weiche lässt sich nicht von Hand umstellen und stellt sich nicht zufällig um
    // Hängt nur an der Zeit, nicht am Bildschirm.
    //
    // Multiplayer: mehrere Reparaturen gleichzeitig möglich. Gesperrt ist man, wenn
    //   - der eigene Begleitwagen eine Reparatur ausgelöst hat, oder
    //   - im eigenen Reparaturradius gerade eine Weiche repariert wird.
    // Laufende Reparaturen anderer Spieler kommen vom Host (Paket mit Weiche, Dauer, Wagen-ID).

    internal static class JunctionRepair
    {
        internal sealed class Job
        {
            public string Key;
            public string Name;
            public string CarId;        // auslösender Begleitwagen (wird festgehalten)
            public TrainCar Car;        // aufgelöst aus CarId (Cache)
            public Vector3 Position;    // Position der Weiche
            public bool HasPosition;
            public float StartTime;
            public float Duration;

            public float Progress { get { return Duration > 0f ? Mathf.Clamp01((Time.time - StartTime) / Duration) : 1f; } }
            public float Remaining { get { return Mathf.Max(0f, StartTime + Duration - Time.time); } }
        }

        private static readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();

        public static bool AnyRunning { get { return jobs.Count > 0; } }
        public static IEnumerable<Job> Jobs { get { return jobs.Values; } }

        public static bool IsRunning(string junctionKey)
        {
            return junctionKey != null && jobs.ContainsKey(junctionKey);
        }

        // Dauer je nach repariertem Anteil: geringer Schaden = Min, 100 % = Max
        public static float DurationFor(float repaired01)
        {
            return Mathf.Lerp(Main.Settings.RepairMinSeconds, Main.Settings.RepairMaxSeconds, Mathf.Clamp01(repaired01));
        }

        public static Job Start(string junctionKey, string junctionName, float seconds, string carId)
        {
            if (string.IsNullOrEmpty(junctionKey)) return null;

            var job = new Job
            {
                Key = junctionKey,
                Name = string.IsNullOrEmpty(junctionName) ? junctionKey : junctionName,
                CarId = carId,
                StartTime = Time.time,
                Duration = Mathf.Max(0.1f, seconds)
            };

            Junction j = JunctionLookup.Find(junctionKey);
            if (j != null)
            {
                job.Position = j.transform.position;
                job.HasPosition = true;
            }

            jobs[junctionKey] = job;
            Main.Log($"Repair of {job.Name} started: {job.Duration:F1} s (car {carId ?? "-"})");
            return job;
        }

        // Laufende Reparatur, die diesen Begleitwagen bzw. diesen Ort sperrt (null = frei)
        public static Job BusyFor(TrainCar self, Vector3 center)
        {
            if (jobs.Count == 0) return null;

            string id = CarIdOf(self);
            float r = Main.Settings.RepairRadius;
            float rSqr = r * r;

            foreach (var job in jobs.Values)
            {
                if (id != null && job.CarId == id) return job;
                if (job.HasPosition && (job.Position - center).sqrMagnitude <= rSqr) return job;
            }
            return null;
        }

        // Aus Main.OnUpdate
        public static void Tick()
        {
            if (jobs.Count == 0) return;

            List<string> done = null;
            foreach (var kv in jobs)
            {
                if (Time.time - kv.Value.StartTime < kv.Value.Duration) continue;
                if (done == null) done = new List<string>();
                done.Add(kv.Key);
            }
            if (done == null) return;

            foreach (string key in done)
            {
                Main.Log($"Repair of {jobs[key].Name} finished");
                jobs.Remove(key);
            }
        }

        public static void Clear()
        {
            jobs.Clear();
        }

        // MP: vom Host abgelehnte eigene Reparatur aufheben
        public static void Cancel(string junctionKey)
        {
            if (junctionKey != null && jobs.Remove(junctionKey))
                Main.Log($"Repair of {junctionKey} cancelled");
        }

        // ---------- Fahrzeug-IDs (im MP auf allen Rechnern gleich) ----------

        public static string CarIdOf(TrainCar car)
        {
            try { return car != null ? car.ID : null; }
            catch { return null; }
        }

        public static TrainCar CarOf(Job job)
        {
            if (job == null || string.IsNullOrEmpty(job.CarId)) return null;
            if (job.Car != null) return job.Car;
            job.Car = FindCar(job.CarId);
            return job.Car;
        }

        public static TrainCar FindCar(string carId)
        {
            if (string.IsNullOrEmpty(carId)) return null;
            var all = CarSpawner.Instance?.AllCars;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c != null && CarIdOf(c) == carId) return c;
            }
            return null;
        }
    }

    // =========================
    // LISTEN-ZUSTAND
    // =========================

    internal static class JM_ListState
    {
        public static bool Active;
        public static bool MessageActive;   // "Zug anhalten" / "Lizenz nötig" ...
        public static List<JM_Entry> Items = new List<JM_Entry>();
        public static int Selected;
        public static int Top;
        public static string Status;
        public static CareerManagerInfoScreen Info;
        public static CareerManagerMainScreen MainScreen;
        public static Vector3 Center;
        public static TrainCar SelfCar;     // Caboose, die stehen muss, solange die Reparatur offen ist

        public static bool StationMode;     // Bahnhof: nur Anzeige
        public static string StationCode;   // z. B. "SM"

        private static bool wasBusy;
        private static float nextBusyRender;
        private const float BusyRenderInterval = 0.1f;
        private const int BarLength = 20;

        // Etwas über der Öffnungsgrenze, damit Physik-Zittern im Stand nicht auslöst
        private const float KickOutKmh = 1.5f;

        // ---------- Eigene Reparatur: Ladebalken bleibt bis zum Abschluss stehen ----------
        // Während der eigenen Reparatur ist "Abbrechen" gesperrt. Wird der Bildschirm trotzdem gewechselt
        // (Spiel, andere Mod, Weggehen), holt Tick den Ladebalken sofort zurück.
        private static string lockKey;
        private static CareerManagerMainScreen lockMain;
        private static CareerManagerInfoScreen lockInfo;
        private static TrainCar lockCar;
        private static Vector3 lockCenter;
        private static float nextReclaim;
        private const float ReclaimInterval = 0.25f;

        public static void LockOwnRepair(string junctionKey)
        {
            if (MainScreen == null || Info == null || string.IsNullOrEmpty(junctionKey)) return;
            lockKey = junctionKey;
            lockMain = MainScreen;
            lockInfo = Info;
            lockCar = SelfCar;
            lockCenter = Center;
        }

        public static bool OwnRepairLocked
        {
            get
            {
                if (lockKey == null) return false;
                if (JunctionRepair.IsRunning(lockKey)) return true;
                ReleaseLock();
                return false;
            }
        }

        public static void ReleaseLock()
        {
            lockKey = null;
            lockMain = null;
            lockInfo = null;
            lockCar = null;
        }

        // Fahrzeug, mit dem gerade selbst repariert wird (für MaintenanceBusy)
        public static TrainCar LockCar { get { return OwnRepairLocked ? lockCar : null; } }

        internal const int VISIBLE = 9;

        // Reparatur, die diese Liste gerade sperrt (Bahnhof: nie)
        private static JunctionRepair.Job BusyJob
        {
            get { return StationMode ? null : JunctionRepair.BusyFor(SelfCar, Center); }
        }

        public static bool Busy { get { return BusyJob != null; } }

        private static string Title
        {
            get { return Loc.T(StationMode ? "station.title" : "repair.title"); }
        }

        public static void Start(CareerManagerMainScreen main, CareerManagerInfoScreen info, Vector3 center,
                                 bool stationMode, string stationCode)
        {
            MainScreen = main;
            Info = info;
            Center = center;
            Status = null;
            MessageActive = false;
            StationMode = stationMode;
            StationCode = stationCode;

            Items = JM_Entries.Build(center, stationMode, stationCode);
            Selected = 0;
            Top = 0;
            Active = true;

            Render();
            MainScreen.screenSwitcher?.SetActiveDisplay(Info);
        }

        public static void ShowMessage(CareerManagerMainScreen main, CareerManagerInfoScreen info, string message)
        {
            Info = info;
            MainScreen = main;
            MessageActive = true;
            JM_Helpers.SetInfoLines(info, main, Loc.T("repair.title"), new[] { message });
            main.screenSwitcher?.SetActiveDisplay(info);
        }

        public static void Stop()
        {
            Active = false;
            MessageActive = false;
            Items.Clear();
            Info = null;
            MainScreen = null;
            SelfCar = null;
            Selected = Top = 0;
            Status = null;
            StationMode = false;
            StationCode = null;
        }

        // Jeden Frame aus Main.OnUpdate
        public static void Tick()
        {
            // Eigene Reparatur: Ladebalken zurückholen, falls der Bildschirm gewechselt wurde
            if (OwnRepairLocked)
            {
                bool ours = Active && ReferenceEquals(Info, lockInfo) && JM_Helpers.OwnsScreen(lockInfo);
                if (!ours && Time.unscaledTime >= nextReclaim && lockMain != null && lockInfo != null)
                {
                    nextReclaim = Time.unscaledTime + ReclaimInterval;
                    var car = lockCar;
                    Main.Log("Repair screen reclaimed (own repair still running)");
                    Start(lockMain, lockInfo, lockCenter, stationMode: false, stationCode: null);
                    SelfCar = car;
                }
            }
            // Bildschirm gehört inzwischen einer anderen Mod: unseren Zustand freigeben
            else if ((Active || MessageActive) && !JM_Payment.Active && Info != null && !JM_Helpers.OwnsScreen(Info))
            {
                Main.Log("Info screen taken over by another mod, junction list released");
                Stop();
                return;
            }

            if (!Active && !JM_Payment.Active) return;

            // Caboose setzt sich in Bewegung: Liste/Kasse schließen, Hinweis "Zug anhalten"
            if (!StationMode && SelfCar != null && SelfCar.rb != null && SelfCar.rb.velocity.magnitude * 3.6f > KickOutKmh)
            {
                KickOut();
                return;
            }

            // Reparatursperre: Fortschritt anzeigen, danach wieder die Liste
            if (Active && Info != null && !StationMode)
            {
                bool busy = Busy;
                if (busy)
                {
                    if (Time.time >= nextBusyRender)
                    {
                        nextBusyRender = Time.time + BusyRenderInterval;
                        Render();
                    }
                }
                else if (wasBusy)
                {
                    Rebuild();
                    Render();
                }
                wasBusy = busy;
            }
        }

        public static void KickOut()
        {
            var main = MainScreen;
            var info = Info;

            // Offene Bezahlung abbrechen (eingeworfenes Geld bleibt in der Kasse wie beim Abbrechen)
            if (JM_Payment.Active)
            {
                var licensePay = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                if (licensePay != null && licensePay.cashReg != null)
                    licensePay.cashReg.ClearCurrentTransaction();
            }

            Main.Log("Junction maintenance closed: caboose is moving");

            if (main != null && main.screenSwitcher != null)
            {
                main.screenSwitcher.SetActiveDisplay(main);
                JM_Payment.Clear();
                Stop();

                if (info != null)
                    ShowMessage(main, info, Loc.T("repair.stopTrain"));
            }
            else
            {
                JM_Payment.Clear();
                Stop();
            }
        }

        public static void Rebuild()
        {
            Items = JM_Entries.Build(Center, StationMode, StationCode);
            Selected = Mathf.Clamp(Selected, 0, Math.Max(0, Items.Count - 1));
            Top = Mathf.Clamp(Top, 0, Math.Max(0, Items.Count - VISIBLE));
        }

        public static void MoveSelection(int delta)
        {
            if (!Active || Items.Count == 0) return;
            if (Busy) return;

            Status = null;
            Selected = Mathf.Clamp(Selected + delta, 0, Items.Count - 1);
            if (Selected < Top) Top = Selected;
            if (Selected > Top + (VISIBLE - 1)) Top = Selected - (VISIBLE - 1);
            Top = Mathf.Clamp(Top, 0, Math.Max(0, Items.Count - VISIBLE));

            Render();
        }

        public static void Render()
        {
            if (!Active || Info == null) return;

            var busyJob = BusyJob;
            if (busyJob != null)
            {
                RenderBusy(busyJob);
                return;
            }

            Color reg, hl;
            JM_Helpers.GetMenuColors(MainScreen?.screenSwitcher, out reg, out hl);

            bool withPrice = !StationMode;
            var lines = new List<string>(VISIBLE + 1);

            if (Items.Count == 0)
            {
                if (!string.IsNullOrEmpty(Status)) lines.Add(Status);
                else lines.Add(Loc.T(StationMode && !string.IsNullOrEmpty(StationCode) ? "station.none" : "repair.none"));
                if (!StationMode || string.IsNullOrEmpty(StationCode))
                    lines.Add(Loc.Format("repair.radius", Main.Settings.RepairRadius.ToString("0")));
            }
            else
            {
                lines.Add(string.IsNullOrEmpty(Status) ? JM_Helpers.HeaderLine(withPrice) : Status);
                for (int i = 0; i < VISIBLE; i++)
                {
                    int idx = Top + i;
                    if (idx >= Items.Count) { lines.Add(""); continue; }

                    string line = JM_Helpers.FormatLine(Items[idx], withPrice);
                    Color col = (idx == Selected) ? hl : reg;
                    lines.Add("<color=#" + ColorUtility.ToHtmlStringRGB(col) + ">" + line + "</color>");
                }
            }

            JM_Helpers.SetInfoLines(Info, MainScreen, Title, lines);
        }

        // Fortschrittsanzeige der laufenden Reparatur (0-100 %)
        private static void RenderBusy(JunctionRepair.Job job)
        {
            wasBusy = true;

            Color reg, hl;
            JM_Helpers.GetMenuColors(MainScreen?.screenSwitcher, out reg, out hl);
            string hlHex = ColorUtility.ToHtmlStringRGB(hl);
            string dimHex = ColorUtility.ToHtmlStringRGB(Color.Lerp(reg, Color.black, 0.6f));

            int pct = Mathf.Clamp(Mathf.FloorToInt(job.Progress * 100f), 0, 100);
            int filled = Mathf.Clamp(pct * BarLength / 100, 0, BarLength);
            int remaining = Mathf.CeilToInt(job.Remaining);

            string bar = "<mspace=0.6em><color=#" + hlHex + ">" + new string('|', filled) + "</color>"
                       + "<color=#" + dimHex + ">" + new string('|', BarLength - filled) + "</color></mspace>";

            var lines = new List<string>
            {
                "<align=center>" + Loc.T("repair.working"),
                string.IsNullOrEmpty(job.Name) ? " " : job.Name,
                " ",
                "<size=200%>" + bar + "</size>",
                "<size=200%><color=#" + hlHex + ">" + pct + " %</color></size>",
                " ",
                Loc.Format("repair.wait", remaining.ToString())
            };

            JM_Helpers.SetInfoLines(Info, MainScreen, Title, lines);
        }
    }

    // =========================
    // BEZAHLUNG / REPARATUR AUSFÜHREN
    // =========================

    internal static class JM_Payment
    {
        public static bool Active;
        public static JM_Entry Entry;
        public static float Repaired01;
        public static RepairQuote Quote;
        public static double Cost { get { return Quote.Pay; } }

        public static void Start(JM_Entry entry, float repaired01, RepairQuote quote)
        {
            Active = true;
            Entry = entry;
            Repaired01 = repaired01;
            Quote = quote;
        }

        public static void Clear()
        {
            Active = false;
            Entry = null;
            Repaired01 = 0f;
            Quote = default(RepairQuote);
        }

        // Schaden senken, Reparatur starten (Sperre + Zughalt), Geld verrechnen, Liste aktualisieren.
        // MP-Client: Schaden und Sperre sofort lokal (wird vom Host bestätigt bzw. korrigiert),
        //            Geld bucht ausschließlich der Host (Wallet des Hosts).
        // MP-Host:   wie Einzelspieler, danach Zustand und Reparatur an alle Clients.
        public static void ApplyRepair(JM_Entry entry, float repaired01, RepairQuote q)
        {
            float before = DamageStore.Get(entry.Key);
            float r = Mathf.Min(repaired01, before);
            float after = before - r;
            float duration = JunctionRepair.DurationFor(r);
            string carId = JunctionRepair.CarIdOf(JM_ListState.SelfCar);

            DamageStore.Set(entry.Key, after);
            JunctionRepair.Start(entry.Key, entry.Name, duration, carId);

            if (JM_Multiplayer.IsClient)
            {
                JM_Multiplayer.ReportRepair(entry.Key, entry.Name, r, q, carId);
                JM_ListState.Status = q.IsReward
                    ? Loc.Format("mp.hostCredited", entry.Name, Loc.Money(q.Payout))
                    : Loc.Format("mp.hostCharged", entry.Name, Loc.Money(q.Pay));
            }
            else
            {
                if (q.IsReward)
                {
                    RepairEconomy.PrintMoney(q.Payout);
                    JM_ListState.Status = Loc.Format("repair.earned", entry.Name, Loc.Money(q.Payout));
                }
                else
                {
                    RepairEconomy.AfterPaidRepair(q);
                    JM_ListState.Status = q.Covered > 0.0
                        ? Loc.Format("repair.insured", entry.Name, Loc.Money(q.Pay), Loc.Money(q.Covered))
                        : Loc.Format("repair.done", entry.Name, Loc.Money(q.Pay));
                }

                JM_Multiplayer.HostRepairStarted(entry.Key, entry.Name, after, duration, carId);
            }

            JM_ListState.LockOwnRepair(entry.Key);

            Main.Log($"Repaired {entry.Key} by {r * 100f:0.#}% ({(q.IsReward ? "payout " + q.Payout : "paid " + q.Pay + ", insurance " + q.Covered)})");
            JM_ListState.Rebuild();
        }
    }

    // =========================
    // FESTHALTEN WÄHREND DER REPARATUR
    // =========================
    // Handbremsen aller Wagen des Zugverbands angezogen und Fahrschalter aller Loks auf 0, solange die
    // Reparatur läuft. Keine Luft wird verbraucht (kein Zischen, Hauptluftleitung bleibt voll).
    // Danach bekommen alle Handbremsen ihre vorherige Stellung zurück.

    internal static class RepairHold
    {
        private static bool active;
        private static readonly List<TrainCar> anchors = new List<TrainCar>();

        public static bool Active { get { return active; } }

        public static void Release()
        {
            HandbrakeHold.RestoreAll();
            if (active) Main.Log("Repair finished: handbrakes restored");
            active = false;
        }

        // Aus Main.OnUpdate: alle Begleitwagen mit laufender Reparatur festhalten
        // (Zugverband jedes Mal neu, wegen Kuppeln/Entkuppeln)
        public static void Tick()
        {
            if (!JunctionRepair.AnyRunning)
            {
                if (active || HandbrakeHold.Any) Release();
                return;
            }

            anchors.Clear();
            foreach (var job in JunctionRepair.Jobs)
            {
                TrainCar car = JunctionRepair.CarOf(job);
                if (car == null) continue;
                anchors.Add(car);
                ThrottleCut.ZeroTrain(car);
            }

            HandbrakeHold.Hold(anchors);

            if (!active && anchors.Count > 0) Main.Log("Repair running: train is held (handbrakes)");
            active = anchors.Count > 0;
        }
    }

    // Handbremsen des Zugverbands anziehen (ohne Luft: SimulateBrakingForce nimmt max(Zylinder, Handbremse)).
    // Vorherige Stellungen werden gemerkt und nach der Reparatur wiederhergestellt.
    // Wagen, die während der Reparatur abgekuppelt werden, bekommen ihre Stellung sofort zurück.
    internal static class HandbrakeHold
    {
        private static readonly Dictionary<BrakeSystem, float> saved = new Dictionary<BrakeSystem, float>();
        private static readonly HashSet<BrakeSystem> current = new HashSet<BrakeSystem>();

        public static bool Any { get { return saved.Count > 0; } }

        // Alle Wagen in den Zugverbänden der angegebenen Fahrzeuge festhalten
        public static void Hold(IEnumerable<TrainCar> anchors)
        {
            current.Clear();
            if (anchors != null)
            {
                foreach (TrainCar anchor in anchors)
                {
                    if (anchor == null) continue;
                    var set = anchor.trainset;
                    if (set == null || set.cars == null) Add(anchor);
                    else foreach (TrainCar c in set.cars) Add(c);
                }
            }

            foreach (var bs in current)
            {
                if (!saved.ContainsKey(bs)) saved[bs] = bs.handbrakePosition;
                if (bs.handbrakePosition < 0.999f) bs.SetHandbrakePosition(1f);
            }

            // Nicht mehr festgehaltene Wagen (abgekuppelt) zurückstellen
            if (saved.Count > current.Count)
            {
                var release = new List<BrakeSystem>();
                foreach (var bs in saved.Keys)
                    if (!current.Contains(bs)) release.Add(bs);
                foreach (var bs in release) Restore(bs);
            }
        }

        public static void RestoreAll()
        {
            foreach (var bs in new List<BrakeSystem>(saved.Keys)) Restore(bs);
            saved.Clear();
            current.Clear();
        }

        private static void Add(TrainCar car)
        {
            var bs = car != null ? car.brakeSystem : null;
            if (bs != null && bs.hasHandbrake) current.Add(bs);
        }

        private static void Restore(BrakeSystem bs)
        {
            float before;
            if (saved.TryGetValue(bs, out before)) saved.Remove(bs);
            try
            {
                if (bs != null && bs.hasHandbrake) bs.SetHandbrakePosition(before);
            }
            catch (Exception e)
            {
                Main.Error("Restoring handbrake failed: " + e);
            }
        }
    }

    // Fahrzeuge mit laufender Weichenreparatur für andere Wartungs-Mods kennzeichnen (MaintenanceBusy).
    // Im MP auf jedem Rechner für alle laufenden Reparaturen (auch die anderer Spieler und die eigene,
    // solange sie noch auf den Host wartet – sie läuft lokal schon).
    internal static class JM_BusyPublisher
    {
        private static readonly List<TrainCar> cars = new List<TrainCar>();

        public static void Tick()
        {
            cars.Clear();
            foreach (var job in JunctionRepair.Jobs)
            {
                var c = JunctionRepair.CarOf(job);
                if (c != null) cars.Add(c);
            }
            MaintenanceBusy.Publish(cars);
        }

        public static void Clear() { cars.Clear(); MaintenanceBusy.Publish(cars); }
    }

    // Fahrschalter aller Loks auf 0 (wie BaseControlsOverrider.SetNeutralState im Spiel)
    internal static class ThrottleCut
    {
        public static void ZeroTrain(TrainCar car)
        {
            if (car == null) return;

            var trainset = car.trainset;
            if (trainset == null || trainset.cars == null)
            {
                Zero(car);
                return;
            }

            foreach (TrainCar c in trainset.cars)
                if (c != null) Zero(c);
        }

        private static void Zero(TrainCar car)
        {
            var sim = car.SimController;
            if (sim == null) return;

            var overrider = sim.controlsOverrider;
            if (overrider == null) return;

            var throttle = overrider.Throttle;
            if (throttle == null) return;

            if (throttle.Value > 0.001f)
                throttle.Set(0f);
        }
    }

    // =========================
    // MENÜEINTRAG (Caboose + Bahnhöfe)
    // =========================

    internal sealed class JM_IndexBox
    {
        public int Index = -1;
        public Component Comp;
    }

    internal static class JM_Menu
    {
        public static readonly ConditionalWeakTable<CareerManagerMainScreen, JM_IndexBox> Boxes =
            new ConditionalWeakTable<CareerManagerMainScreen, JM_IndexBox>();

        public static string Label { get { return Loc.T("menu.label"); } }

        public static void SetText(Component c, string text)
        {
            if (c == null) return;
            try
            {
                var prop = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null) prop.SetValue(c, text);
            }
            catch { }
        }
    }

    // Läuft VOR TrackMaintenance (dort [HarmonyAfter("JunctionMaintenance")]): unser Eintrag kommt direkt
    // unter "Stats", die Einträge von TrackMaintenance darunter.
    [HarmonyPatch(typeof(CareerManagerMainScreen), "Awake")]
    internal static class JM_CM_Awake_AddMenuItem
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                JM_IndexBox existing;
                if (JM_Menu.Boxes.TryGetValue(__instance, out existing)) return;

                // CareerManagerAPI baut das Menü selbst neu auf: dann tragen wir uns dort ein
                if (CmApiCompat.Present)
                {
                    CmApiCompat.EnsureHooked();
                    return;
                }

                if (!JM_Helpers.WantsEntry(JM_Helpers.HostOf(__instance))) return;

                var fSelectable = AccessTools.Field(typeof(CareerManagerMainScreen), "selectableText");
                var arrObj = fSelectable.GetValue(__instance) as Array;
                if (arrObj == null || arrObj.Length < 4) return; // unerwartetes Layout

                var statsComp = arrObj.GetValue(3) as Component;
                var ownedComp = arrObj.GetValue(2) as Component;
                if (statsComp == null || ownedComp == null) return;

                var elementType = arrObj.GetType().GetElementType();
                int index = arrObj.Length;

                var cloneGO = UnityEngine.Object.Instantiate(statsComp.gameObject, statsComp.transform.parent);
                cloneGO.name = "RepairSwitchEntry";
                var comp = cloneGO.GetComponent(elementType) as Component;
                JM_Menu.SetText(comp, JM_Menu.Label);

                var lineStep = statsComp.transform.localPosition - ownedComp.transform.localPosition;
                cloneGO.transform.localPosition = statsComp.transform.localPosition + lineStep * (index - 3);

                var newArr = Array.CreateInstance(elementType, index + 1);
                for (int i = 0; i < index; i++) newArr.SetValue(arrObj.GetValue(i), i);
                newArr.SetValue(comp, index);
                fSelectable.SetValue(__instance, newArr);

                AccessTools.Field(typeof(ScrollableDisplayScreen), "activeSlotCount").SetValue(__instance, index + 1);
                AccessTools.Field(typeof(ScrollableDisplayScreen), "selector")
                    .SetValue(__instance, new IntIterator(0, index + 1, isWrappable: true));

                JM_Menu.Boxes.Add(__instance, new JM_IndexBox { Index = index, Comp = comp });
            }
            catch (Exception e)
            {
                Main.Error("CM Awake inject failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), "Activate")]
    internal static class JM_CM_Activate_Label
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                JM_IndexBox box;
                if (JM_Menu.Boxes.TryGetValue(__instance, out box)) JM_Menu.SetText(box.Comp, JM_Menu.Label);
            }
            catch (Exception e) { Main.Error("CM Activate label failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), "Disable")]
    internal static class JM_CM_Disable_ClearLabel
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                JM_IndexBox box;
                if (JM_Menu.Boxes.TryGetValue(__instance, out box)) JM_Menu.SetText(box.Comp, string.Empty);
            }
            catch (Exception e) { Main.Error("CM Disable clear failed: " + e); }
        }
    }

    // =========================
    // KOMPATIBILITÄT: CareerManagerAPI (optional, ohne Abhängigkeit)
    // =========================
    // CareerManagerAPI baut die Menüeinträge jedes CareerManagers komplett neu auf und löscht fremde.
    // Ist die Mod aktiv, tragen wir uns über ihr Ereignis "CareerManagerAwake" und "TryAddToMainScreen<T>"
    // ein – alles per Reflection, keine Referenz auf CareerManagerAPI.dll. Ohne die Mod läuft alles wie bisher.

    internal static class CmApiCompat
    {
        private const string ModId = "CareerManagerAPI";

        private static bool checkedPresent;
        private static bool present;
        private static bool hooked;
        private static Type apiType;

        private static CareerManagerMainScreen pendingMain;

        public static bool Present
        {
            get
            {
                if (!checkedPresent)
                {
                    checkedPresent = true;
                    present = Detect();
                }
                return present;
            }
        }

        private static bool Detect()
        {
            try
            {
                var mod = UnityModManagerNet.UnityModManager.FindMod(ModId);
                if (mod == null || !mod.Enabled) return false;

                apiType = AccessTools.TypeByName("CareerManagerAPI.CareerManagerAPI");
                if (apiType == null || apiType.GetEvent("CareerManagerAwake") == null)
                {
                    Main.ModEntry.Logger.Warning("CareerManagerAPI found, but its API is unknown – using own menu entry");
                    return false;
                }

                Main.ModEntry.Logger.Log("CareerManagerAPI detected – menu entry is added through it");
                return true;
            }
            catch (Exception e)
            {
                Main.Error("CareerManagerAPI detection failed: " + e);
                return false;
            }
        }

        public static void EnsureHooked()
        {
            if (hooked || !Present) return;
            hooked = true;

            try
            {
                EventInfo ev = apiType.GetEvent("CareerManagerAwake");
                MethodInfo handler = typeof(CmApiCompat).GetMethod(nameof(OnCareerManagerAwake),
                    BindingFlags.NonPublic | BindingFlags.Static);
                Delegate d = Delegate.CreateDelegate(ev.EventHandlerType, handler);
                ev.AddEventHandler(null, d);
            }
            catch (Exception e)
            {
                Main.Error("CareerManagerAPI hook failed: " + e);
            }
        }

        // Signatur passend zu CareerManagerAPI.CareerManagerInitialized
        private static void OnCareerManagerAwake(object tracker, string locationName, StationController station, TrainCar trainCar)
        {
            try
            {
                var comp = tracker as Component;
                var main = comp != null ? comp.GetComponent<CareerManagerMainScreen>() : null;
                if (main == null) return;

                // Fahrzeuge: nur die Caboose; Bahnhöfe: immer (Übersicht)
                if (trainCar != null && !JM_Helpers.IsCaboose(trainCar)) return;

                MethodInfo tryAdd = tracker.GetType().GetMethod("TryAddToMainScreen");
                if (tryAdd == null) return;

                // TryAddToMainScreen<T>(string text, OnCanConfirm canConfirm = null, string after = null, string before = null)
                var generic = tryAdd.MakeGenericMethod(typeof(JM_ApiScreen));
                var args = new object[generic.GetParameters().Length]; // optionale Parameter = null
                args[0] = JM_Menu.Label;
                bool ok = (bool)generic.Invoke(tracker, args);
                if (!ok) return;

                // Die API legt ein GameObject mit dem Namen des Eintrags an: Hauptbildschirm merken
                var parent = main.feesScreen != null ? main.feesScreen.transform.parent : null;
                var go = parent != null ? parent.Find(JM_Menu.Label) : null;
                var screen = go != null ? go.GetComponent<JM_ApiScreen>() : null;
                if (screen != null) screen.MainScreen = main;
            }
            catch (Exception e)
            {
                Main.Error("CareerManagerAPI menu entry failed: " + e);
            }
        }

        public static void Request(CareerManagerMainScreen main)
        {
            pendingMain = main;
        }

        // Aus Main.OnUpdate: nicht innerhalb eines Bildschirmwechsels öffnen
        public static void Tick()
        {
            if (pendingMain == null) return;
            var main = pendingMain;
            pendingMain = null;
            JM_Open.Open(main);
        }
    }

    // Platzhalter-Bildschirm für CareerManagerAPI: leitet im nächsten Frame auf unsere Liste weiter
    internal sealed class JM_ApiScreen : DisplayScreen
    {
        public CareerManagerMainScreen MainScreen;

        public override void Activate(IDisplayScreen previousScreen)
        {
            var main = MainScreen ?? previousScreen as CareerManagerMainScreen;
            if (main == null) return;
            CmApiCompat.Request(main);
        }

        public override void Disable() { }

        public override void HandleInputAction(InputAction input)
        {
            var main = MainScreen;
            if (input == InputAction.Cancel && main != null && main.screenSwitcher != null)
                main.screenSwitcher.SetActiveDisplay(main);
        }
    }

    // =========================
    // ÖFFNEN
    // =========================

    internal static class JM_Open
    {
        public static void Open(CareerManagerMainScreen main)
        {
            if (main == null) return;

            var host = JM_Helpers.HostOf(main);
            var info = main.infoScreen;
            if (info == null) return;

            // Bahnhof: Übersicht (nur Anzeige)
            if (!JM_Helpers.IsVehicleCareerManager(host))
            {
                string code = JM_Helpers.InferStationCode(host.position, 500f);
                JM_ListState.Start(main, info, host.position, stationMode: true, stationCode: code);
                return;
            }

            // Fahrzeug: nur die Caboose
            if (!JM_Helpers.IsCabooseCareerManager(host))
            {
                JM_ListState.ShowMessage(main, info, Loc.T("repair.cabooseOnly"));
                return;
            }

            // Lizenzmodus: ohne Lizenz keine Reparatur
            if (!RepairEconomy.CanRepair)
            {
                JM_ListState.ShowMessage(main, info, Loc.T("repair.needLicense"));
                return;
            }

            var selfCar = JM_Helpers.ResolveSelfCar(host);

            // Fahrzeug arbeitet gerade für eine andere Mod (z. B. Gleisreparatur der Track Maintenance)
            if (MaintenanceBusy.BusyByOther(selfCar))
            {
                JM_ListState.ShowMessage(main, info, Loc.T("repair.busyOther"));
                return;
            }

            // Nur bei stehendem Wagen
            if (!JM_Helpers.IsStandingSelf(selfCar))
            {
                JM_ListState.ShowMessage(main, info, Loc.T("repair.stopTrain"));
                return;
            }

            JM_ListState.Start(main, info, host.position, stationMode: false, stationCode: null);
            JM_ListState.SelfCar = selfCar;
        }
    }

    // Bestätigen auf unserem Eintrag -> Liste öffnen
    [HarmonyPatch(typeof(CareerManagerMainScreen), "HandleInputAction")]
    internal static class JM_CM_HandleInput_Open
    {
        static bool Prefix(CareerManagerMainScreen __instance, InputAction input)
        {
            try
            {
                if (input != InputAction.Confirm) return true;

                JM_IndexBox box;
                if (!JM_Menu.Boxes.TryGetValue(__instance, out box)) return true;

                var sel = (IntIterator)AccessTools.Field(typeof(ScrollableDisplayScreen), "selector").GetValue(__instance);
                if (sel == null || sel.Current != box.Index) return true;

                JM_Open.Open(__instance);
                return false;
            }
            catch (Exception e)
            {
                Main.Error("CM HandleInput (open) failed: " + e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(CareerManagerInfoScreen), "Disable")]
    internal static class JM_Info_Disable
    {
        static void Postfix()
        {
            // Beim Wechsel zum Bezahlbildschirm bleibt der Listenzustand erhalten
            if (!JM_Payment.Active)
                JM_ListState.Stop();
        }
    }

    // Listeneingaben
    [HarmonyPatch(typeof(CareerManagerInfoScreen), "HandleInputAction")]
    internal static class JM_Info_HandleInput
    {
        static bool Prefix(CareerManagerInfoScreen __instance, InputAction input)
        {
            // Bildschirm gehört einer anderen Mod (z. B. Track Maintenance): nichts abfangen
            if ((JM_ListState.Active || JM_ListState.MessageActive) &&
                ReferenceEquals(__instance, JM_ListState.Info) && !JM_Helpers.OwnsScreen(__instance))
            {
                if (!JM_ListState.OwnRepairLocked) JM_ListState.Stop();
                return true;
            }

            // Hinweis-Bildschirm: alles außer Abbrechen schlucken
            if (JM_ListState.MessageActive && ReferenceEquals(__instance, JM_ListState.Info))
            {
                if (input == InputAction.Cancel)
                {
                    JM_ListState.MessageActive = false;
                    var target = JM_ListState.MainScreen as IDisplayScreen ?? __instance;
                    __instance.screenSwitcher?.SetActiveDisplay(target);
                }
                return false;
            }

            if (!JM_ListState.Active || !ReferenceEquals(__instance, JM_ListState.Info))
                return true;

            switch (input)
            {
                case InputAction.Up:
                    JM_ListState.MoveSelection(-1);
                    return false;

                case InputAction.Down:
                    JM_ListState.MoveSelection(+1);
                    return false;

                case InputAction.Confirm:
                {
                    if (JM_ListState.StationMode) return false;      // Bahnhof: nur Anzeige
                    if (JM_ListState.Items.Count == 0) return false;
                    if (JM_ListState.Busy) return false;             // Reparatursperre

                    JM_Entry entry = JM_ListState.Items[JM_ListState.Selected];
                    if (entry == null || entry.J == null) return false;
                    if (JunctionRepair.IsRunning(entry.Key)) return false; // wird gerade (von jemand anderem) repariert

                    // Nur im Stand reparieren
                    if (!JM_Helpers.IsStandingSelf(JM_ListState.SelfCar))
                    {
                        JM_ListState.KickOut();
                        return false;
                    }

                    // Fahrzeug arbeitet gerade für eine andere Mod
                    if (MaintenanceBusy.BusyByOther(JM_ListState.SelfCar))
                    {
                        JM_ListState.Status = Loc.T("repair.busyOther");
                        JM_ListState.Render();
                        return false;
                    }

                    // Lizenz könnte inzwischen fehlen (z. B. anderer Spielstand)
                    if (!RepairEconomy.CanRepair)
                    {
                        JM_ListState.Status = Loc.T("repair.needLicense");
                        JM_ListState.Render();
                        return false;
                    }

                    float before = DamageStore.Get(entry.Key);
                    float repaired01 = Mathf.Min(Main.Settings.RepairStep01, before);
                    if (repaired01 <= 0f)
                    {
                        JM_ListState.Status = Loc.Format("repair.noDamage", entry.Name);
                        JM_ListState.Render();
                        return false;
                    }

                    // Preis jetzt neu berechnen (Schaden/Versicherung können sich geändert haben)
                    RepairQuote quote = RepairEconomy.Quote(repaired01);

                    // Vergütung oder nichts zu zahlen: sofort reparieren
                    if (quote.IsReward || quote.Pay <= 0.0)
                    {
                        JM_Payment.ApplyRepair(entry, repaired01, quote);
                        JM_ListState.Render();
                        return false;
                    }

                    var licensePay = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                    if (licensePay == null || licensePay.cashReg == null)
                    {
                        Main.Error("Payment screen not found");
                        return false;
                    }

                    licensePay.cashReg.ClearCurrentTransaction();
                    licensePay.cashReg.SetTotalCost(quote.Pay);

                    JM_Payment.Start(entry, repaired01, quote);
                    licensePay.screenSwitcher?.SetActiveDisplay(licensePay);
                    return false;
                }

                case InputAction.Cancel:
                    // Eigene Reparatur läuft: Ladebalken bleibt bis zum Abschluss stehen
                    if (JM_ListState.OwnRepairLocked) return false;
                    if (JM_ListState.MainScreen != null)
                        __instance.screenSwitcher?.SetActiveDisplay(JM_ListState.MainScreen);
                    return false;

                default:
                    return true;
            }
        }
    }

    // =========================
    // BEZAHLBILDSCHIRM
    // =========================

    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "Activate")]
    internal static class JM_LicensePay_Activate
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance)
        {
            if (!JM_Payment.Active) return true;

            try
            {
                var cashReg = __instance.cashReg;
                if (cashReg == null) return true;

                cashReg.ClearCurrentTransaction();
                cashReg.SetTotalCost(JM_Payment.Cost);

                var entry = JM_Payment.Entry;
                string name = entry != null ? entry.Name : Loc.T("unknown");

                if (__instance.title1 != null) __instance.title1.text = Loc.T("repair.title");
                if (__instance.title2 != null) __instance.title2.text = Loc.T("pay.junction");
                if (__instance.licenseNameText != null) __instance.licenseNameText.text = name;
                if (__instance.licensePriceText != null) __instance.licensePriceText.text = Loc.Money(JM_Payment.Cost);
                if (__instance.insertWallet != null)
                    __instance.insertWallet.text = JM_Payment.Quote.Covered > 0.0
                        ? Loc.Format("pay.insurance", Loc.Money(JM_Payment.Quote.Covered)) + "\n" + Loc.T("pay.insertWallet")
                        : Loc.T("pay.insertWallet");
                if (__instance.depositedText != null) __instance.depositedText.text = Loc.T("pay.deposited");
                if (__instance.depositedValue != null) __instance.depositedValue.text = Loc.Money(cashReg.DepositedCash);

                cashReg.CashAdded -= OnCashAdded;
                cashReg.CashAdded += OnCashAdded;
                return false;
            }
            catch (Exception e)
            {
                Main.Error("LicensePay.Activate override failed: " + e);
                return true;
            }
        }

        internal static void OnCashAdded()
        {
            try
            {
                var screen = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                if (screen == null || screen.cashReg == null || screen.depositedValue == null) return;
                screen.depositedValue.text = Loc.Money(screen.cashReg.DepositedCash);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "Disable")]
    internal static class JM_LicensePay_Disable
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance)
        {
            if (!JM_Payment.Active) return true;

            try
            {
                var cashReg = __instance.cashReg;
                if (cashReg != null)
                {
                    cashReg.CashAdded -= JM_LicensePay_Activate.OnCashAdded;
                    cashReg.ClearCurrentTransaction();
                }

                Clear(__instance.title1);
                Clear(__instance.title2);
                Clear(__instance.licenseNameText);
                Clear(__instance.licensePriceText);
                Clear(__instance.insertWallet);
                Clear(__instance.depositedText);
                Clear(__instance.depositedValue);
                return false;
            }
            catch (Exception e)
            {
                Main.Error("LicensePay.Disable override failed: " + e);
                return true;
            }
        }

        private static void Clear(TextMeshPro tmp) { if (tmp != null) tmp.text = string.Empty; }
    }

    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "HandleInputAction")]
    internal static class JM_LicensePay_HandleInput
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance, InputAction input)
        {
            if (!JM_Payment.Active) return true;

            switch (input)
            {
                case InputAction.Cancel:
                    try
                    {
                        __instance.cashReg?.ClearCurrentTransaction();

                        JM_ListState.Active = true;
                        JM_ListState.Rebuild();
                        JM_ListState.Render();

                        IDisplayScreen target = (JM_ListState.Info as IDisplayScreen)
                                                ?? (JM_ListState.MainScreen as IDisplayScreen)
                                                ?? __instance;
                        __instance.screenSwitcher?.SetActiveDisplay(target);
                    }
                    catch (Exception e)
                    {
                        Main.Error("LicensePay.Cancel failed: " + e);
                        JM_ListState.MainScreen?.screenSwitcher?.SetActiveDisplay(JM_ListState.MainScreen);
                    }
                    finally
                    {
                        JM_Payment.Clear();
                    }
                    return false;

                case InputAction.Confirm:
                    try
                    {
                        // Die Reparatur selbst passiert im Postfix von Buy()
                        if (__instance.cashReg != null) __instance.cashReg.Buy();
                    }
                    catch (Exception e)
                    {
                        Main.Error("LicensePay.Confirm failed: " + e);
                    }
                    return false;

                default:
                    return true;
            }
        }
    }

    // ---------- Kasse ----------

    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "GetTotalCost")]
    internal static class JM_CashReg_GetTotalCost
    {
        static bool Prefix(ref double __result)
        {
            if (!JM_Payment.Active) return true;
            __result = Math.Max(0.0, JM_Payment.Cost);
            return false;
        }
    }

    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "TotalUnitsInBasket")]
    internal static class JM_CashReg_TotalUnits
    {
        static bool Prefix(ref float __result)
        {
            if (!JM_Payment.Active) return true;
            __result = 1f; // mindestens eine "Einheit", sonst bricht Buy() ab
            return false;
        }
    }

    // Erfolgreich bezahlt -> Weiche reparieren (Sperre + Fortschritt), zurück zur Liste
    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "Buy")]
    internal static class JM_Payment_OnBuy
    {
        static void Postfix(bool __result)
        {
            if (!__result || !JM_Payment.Active) return;

            try
            {
                JM_Entry entry = JM_Payment.Entry;
                if (entry != null)
                    JM_Payment.ApplyRepair(entry, JM_Payment.Repaired01, JM_Payment.Quote);

                if (JM_ListState.Info != null && JM_ListState.MainScreen != null)
                {
                    JM_ListState.Active = true;
                    JM_ListState.Render();
                    JM_ListState.MainScreen.screenSwitcher?.SetActiveDisplay(JM_ListState.Info);
                }
            }
            catch (Exception e)
            {
                Main.Error("Repair after payment failed: " + e);
            }
            finally
            {
                JM_Payment.Clear();
            }
        }
    }
}
