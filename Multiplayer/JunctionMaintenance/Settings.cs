// File: Settings.cs
// Namespace: JunctionMaintenance
// Contains: Settings (inkl. Einstellungsseite), DamageEntry, DamageStore, FlipGuard, JM_Save, JM_SaveRuntime
//
// Einstellungsseite (wie TrackMaintenance selbst gezeichnet, alle Texte über Loc):
//   Weichenschäden
//     [x] Damage when run through      (Rt)
//     [x] One-time damage on derailment (Dad)
//     [x] Continuous damage when dragged (Dwd)
//     [x] Damage by explosion          (Dbe)
//   Multiplikatoren  (Rt || Dad || Dwd): Gewicht, Geschwindigkeit, Lok-Bonus (+ Beispielrechnung)
//   Mitschleifen     (Dwd):              Intervall, Mindestgeschwindigkeit
//   Explosionen      (Dbe):              Radius, Schaden im Zentrum (nach außen linear abnehmend)
//   Zufälliges Umstellen, Weichenverhalten
//
// Fest (nicht einstellbar):
//   Basisschaden = 1 % je angefangene 10 km/h (5 km/h -> 1 %, 15 km/h -> 2 %)
//   Radius unter dem Fahrzeug (Entgleisung/Mitschleifen) = 5 m
//   Weichenreparatur: Modus, Radius, Dauer, Kosten/Vergütung, zwei Lizenzen
//   Debug
//
// Feldnamen der alten Version bleiben erhalten (logging, maxRepairCostFull, maxRepairRewardFull,
// maintenanceLicensePrice, ...), damit bestehende Settings.xml weiter gelesen werden.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Serialization;
using UnityEngine;
using UnityModManagerNet;
using Newtonsoft.Json.Linq;
using DV;

namespace JunctionMaintenance
{
    [Serializable]
    public class Settings : UnityModManager.ModSettings
    {
        public bool logging = false;

        // ---- Schadensauslöser ----
        public bool damageRunThrough = true;       // Auffahren der Weiche (FORCED)
        public bool damageAtDerail = true;         // Entgleisung
        public bool damageWhenDragged = true;      // Mitschleifen
        public bool damageByExplosion = true;      // Explosion

        // ---- Mitschleifen ----
        public float intervalSeconds = 1f;
        public float minSpeedKmh = 3f;

        // ---- Multiplikatoren ----
        public float multiplierPerTon = 0.01f;
        public float multiplierPerKmh = 0.01f;
        public float locoBonus = 1f;

        // ---- Explosionen ----
        public float explosionRadius = 25f;
        public float explosionDamagePercent = 50f;

        // ---- Zufälliges Umstellen ----
        public bool enableRandomFlip = true;
        public float safeNoFlipSpeedKmh = 10.0f;
        public float flipMultiplierPercent = 0.15f; // 0.01..0.50 (Chance = Schaden x Multiplikator)
        public float flipCooldownAfterForcedSec = 5.0f;

        // ---- Weichenverhalten ----
        public bool BlockManualSwitchAtFullDamage = true;

        // ---- Reparatur ----
        public RepairMode repairMode = RepairMode.Penalty;
        public float repairRadius = 10f;
        public float repairAmountPercent = 1f;        // Anteil pro Reparatur (1 = alles), nicht in der Oberfläche
        public float maxVehicleStandingSpeedKmh = 1.0f;
        public float maxRepairCostFull = 10000f;
        public float maxRepairRewardFull = 1000f;
        public float repairMinSeconds = 5f;            // geringer Schaden
        public float repairMaxSeconds = 60f;           // 100 % Schaden

        // ---- Lizenzen (DVCustomLicenses) ----
        public float maintenanceLicensePrice = 50000f; // Lizenz "Weicheninstandhaltung" (zahlen, Versicherung)
        public float licenseCopay = 25000f;            //   Erhöhung des Eigenanteils
        public float license2Price = 100000f;          // Lizenz "Weichenbau-Unternehmer" (Vergütung)
        public float license2Copay = 50000f;           //   Erhöhung des Eigenanteils

        // Schadensspeicher liegt NICHT in den Settings (MP: Settings werden gegen die des Hosts getauscht)
        [XmlIgnore]
        public Dictionary<string, float> DamageMap => DamageStore.Map;

        // ---- Kopie über XML (alle öffentlichen Felder, ohne Schadensspeicher) ----
        private static System.Xml.Serialization.XmlSerializer xml;
        private static System.Xml.Serialization.XmlSerializer Xml
        {
            get { return xml ?? (xml = new System.Xml.Serialization.XmlSerializer(typeof(Settings))); }
        }

        public string ToXml()
        {
            using (var w = new System.IO.StringWriter(CultureInfo.InvariantCulture))
            {
                Xml.Serialize(w, this);
                return w.ToString();
            }
        }

        public static Settings FromXml(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                using (var r = new System.IO.StringReader(text))
                    return Xml.Deserialize(r) as Settings;
            }
            catch (Exception e)
            {
                Main.Error("Settings from host could not be read: " + e);
                return null;
            }
        }

        // ---- Schalter ----
        public bool DamageRunThrough => damageRunThrough;
        public bool DamageAtDerail => damageAtDerail;
        public bool DamageWhenDragged => damageWhenDragged;
        public bool DamageByExplosion => damageByExplosion;
        public bool AnyVehicleDamage => damageRunThrough || damageAtDerail || damageWhenDragged;

        // ---- Feste Werte (bewusst nicht einstellbar) ----
        public float DerailRadius => 5f;                  // Weiche muss unter dem Fahrzeug liegen (m)
        public const float BasePercentPer10Kmh = 1f;      // Basisschaden je angefangene 10 km/h

        // ---- Sicherheitsnetz, falls ein gespeicherter Wert außerhalb des Bereichs liegt ----
        public float IntervalSeconds => Mathf.Clamp(intervalSeconds, 1f, 10f);
        public float MinSpeedKmh => Mathf.Max(0f, minSpeedKmh);
        public float ExplosionRadius => Mathf.Clamp(explosionRadius, 5f, 50f);
        public float ExplosionDamagePercent => Mathf.Clamp(explosionDamagePercent, 1f, 100f);
        public float FlipMultiplier => Mathf.Clamp(flipMultiplierPercent, 0.01f, 0.50f);
        public float RepairRadius => Mathf.Clamp(repairRadius, 5f, 50f);
        public float RepairStep01 => Mathf.Clamp(repairAmountPercent, 0.01f, 1f);
        public float RepairMinSeconds => Mathf.Clamp(repairMinSeconds, 1f, 30f);
        public float RepairMaxSeconds => Mathf.Max(RepairMinSeconds, Mathf.Clamp(repairMaxSeconds, 1f, 60f));

        // Basisschaden in Prozent: 1 % je ANGEFANGENE 10 km/h (0 km/h = kein Schaden)
        //   5 km/h -> 1 %, 10 km/h -> 1 %, 10,1 km/h -> 2 %, 45 km/h -> 5 %
        public static float BasePercent(float speedKmh)
        {
            if (speedKmh <= 0.1f) return 0f;
            return Mathf.Ceil(speedKmh / 10f) * BasePercentPer10Kmh;
        }

        // Schaden eines Treffers (Auffahren, Entgleisung, Mitschleifen) in Prozent:
        //   Multiplikator = Gewicht x Faktor + Geschwindigkeit x Faktor (+ Lok-Bonus)
        //   Schaden       = Basisschaden x Multiplikator (höchstens 100 %)
        // Beispiel (Standardwerte): 20-t-Lok mit 5 km/h  -> 1 % x (0,20 + 0,05 + 1) = 1,25 %
        //                           20-t-Wagen mit 5 km/h -> 1 % x (0,20 + 0,05)     = 0,25 %
        public float CalculateHitPercent(float massKg, float speedKmh, bool isLoco, out float multiplier)
        {
            multiplier = (massKg / 1000f) * multiplierPerTon
                       + speedKmh * multiplierPerKmh
                       + (isLoco ? locoBonus : 0f);
            multiplier = Mathf.Max(0f, multiplier);
            return Mathf.Min(BasePercent(speedKmh) * multiplier, 100f);
        }

        // Explosionsschaden in Prozent bei Abstand 'distance': voller Wert im Zentrum,
        // nach außen linear abnehmend, 0 am Rand des Radius.
        public float ExplosionPercentAt(float distance)
        {
            float r = ExplosionRadius;
            if (distance >= r) return 0f;
            return ExplosionDamagePercent * (1f - Mathf.Clamp01(distance / r));
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            // Client: Einstellungen kommen vom Host, nichts speichern
            if (JM_Multiplayer.IsClient) return;
            Save(this, modEntry);
        }

        // =========================
        // EINSTELLUNGSSEITE
        // =========================

        private const float LabelWidth = 460f;
        private const float Indent = 24f;

        [NonSerialized] private GUIStyle boldStyle;
        [NonSerialized] private Dictionary<string, string> fieldBuffers;

        public void Draw(UnityModManager.ModEntry entry)
        {
            if (boldStyle == null) boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            if (fieldBuffers == null) fieldBuffers = new Dictionary<string, string>();

            // Client: Einstellungen werden vom Host vorgegeben
            if (JM_Multiplayer.IsClient)
            {
                var info = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, wordWrap = true, fontStyle = FontStyle.Bold };
                GUILayout.Box(Loc.T("mp.clientSettings"), info, GUILayout.ExpandWidth(true));
                return;
            }

            // Schadensauslöser (ohne Hauptschalter)
            Header("set.damage");
            damageRunThrough = Toggle("set.damageRunThrough", damageRunThrough, 1);
            damageAtDerail = Toggle("set.damageAtDerail", damageAtDerail, 1);
            damageWhenDragged = Toggle("set.damageWhenDragged", damageWhenDragged, 1);
            damageByExplosion = Toggle("set.damageByExplosion", damageByExplosion, 1);

            if (AnyVehicleDamage)
            {
                Header("set.sec.multipliers");
                Note("set.baseNote");
                multiplierPerTon = Field("set.perTon", multiplierPerTon);
                multiplierPerKmh = Field("set.perKmh", multiplierPerKmh);
                locoBonus = Field("set.locoBonus", locoBonus);

                float m;
                float car = CalculateHitPercent(100000f, 50f, false, out m);
                float loco = CalculateHitPercent(100000f, 50f, true, out m);
                Note(Loc.Format("set.multiplierPreview", car.ToString("0.##", Loc.Culture), loco.ToString("0.##", Loc.Culture)), raw: true);
            }

            if (DamageWhenDragged)
            {
                Header("set.sec.drag");
                intervalSeconds = Slider("set.interval", intervalSeconds, 1f, 10f, 1);
                minSpeedKmh = Field("set.minSpeed", minSpeedKmh);
            }

            if (DamageByExplosion)
            {
                Header("set.sec.explosion");
                explosionRadius = Slider("set.explosionRadius", explosionRadius, 5f, 50f, 0);
                explosionDamagePercent = Slider("set.explosionDamage", explosionDamagePercent, 1f, 100f, 0);
                Note("set.explosionNote");
            }

            // Zufälliges Umstellen
            Header("set.sec.flip");
            enableRandomFlip = Toggle("set.flipEnabled", enableRandomFlip, 0);
            if (enableRandomFlip)
            {
                safeNoFlipSpeedKmh = Slider("set.flipSafeSpeed", safeNoFlipSpeedKmh, 1f, 30f, 0);
                flipMultiplierPercent = Slider("set.flipMultiplier", flipMultiplierPercent, 0.01f, 0.50f, 2);
                float f = FlipMultiplier;
                Note(Loc.Format("set.flipPreview",
                    (10f * f).ToString("0.##", Loc.Culture),
                    (50f * f).ToString("0.##", Loc.Culture),
                    (100f * f).ToString("0.##", Loc.Culture)), raw: true);
            }

            // Weichenverhalten
            Header("set.sec.switch");
            BlockManualSwitchAtFullDamage = Toggle("set.blockFull", BlockManualSwitchAtFullDamage, 0);

            // Reparatur
            Header("set.sec.repair");
            DrawRepairMode();
            repairRadius = Slider("set.repairRadius", repairRadius, 5f, 50f, 0);
            repairMinSeconds = Slider("set.repairMinTime", repairMinSeconds, 1f, 30f, 0);
            repairMaxSeconds = Slider("set.repairMaxTime", repairMaxSeconds, 1f, 60f, 0);
            if (repairMaxSeconds < repairMinSeconds) repairMaxSeconds = repairMinSeconds;

            RepairMode mode = RepairEconomy.Mode;
            if (mode == RepairMode.Penalty || mode == RepairMode.License)
                maxRepairCostFull = Field("set.maxCost", maxRepairCostFull);
            if (mode == RepairMode.Reward || mode == RepairMode.License)
                maxRepairRewardFull = Field("set.maxReward", maxRepairRewardFull);
            if (mode == RepairMode.License)
            {
                maintenanceLicensePrice = Field("set.licensePrice", maintenanceLicensePrice);
                licenseCopay = Field("set.licenseCopay", licenseCopay);
                license2Price = Field("set.license2Price", license2Price);
                license2Copay = Field("set.license2Copay", license2Copay);
                Note("set.licenseNote");
            }

            // Debug
            Header("set.sec.debug");
            logging = Toggle("set.debugLogs", logging, 0);
            if (GUILayout.Button(Loc.T("set.listDamaged"), GUILayout.Width(LabelWidth)))
            {
                var log = Main.ModEntry.Logger;
                log.Log("==== DAMAGED JUNCTIONS ====");
                foreach (var kv in DamageStore.All())
                    log.Log($"Damaged: {kv.Key} -> {kv.Value * 100f:0.###}%");
                log.Log("==== END ====");
            }
        }

        // Modus-Auswahl: Bezahlen / Verdienen / Lizenz (wie TrackMaintenance)
        private void DrawRepairMode()
        {
            bool licenseMod = RepairEconomy.HasCustomLicensesMod;
            RepairMode mode = RepairEconomy.Mode;

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("set.mode"), GUILayout.Width(LabelWidth));
            // Mit DVCustomLicenses nur Lizenz wählbar, ohne die Mod nur Bezahlen/Verdienen
            if (ModeButton("set.mode.penalty", mode == RepairMode.Penalty, !licenseMod)) repairMode = RepairMode.Penalty;
            if (ModeButton("set.mode.reward", mode == RepairMode.Reward, !licenseMod)) repairMode = RepairMode.Reward;
            if (ModeButton("set.mode.license", mode == RepairMode.License, licenseMod)) repairMode = RepairMode.License;
            GUILayout.EndHorizontal();

            Note(licenseMod ? "set.mode.licenseForced" : "set.mode.needsLicenseMod");

            string info = mode == RepairMode.Reward ? "set.mode.info.reward"
                        : mode == RepairMode.License ? "set.mode.info.license"
                        : "set.mode.info.penalty";
            var box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = true };
            GUILayout.Box(Loc.T(info), box, GUILayout.ExpandWidth(true));
        }

        private static bool ModeButton(string key, bool active, bool enabled)
        {
            var style = new GUIStyle(GUI.skin.button);
            if (active)
            {
                style.normal.textColor = Color.green;
                style.fontStyle = FontStyle.Bold;
            }
            bool prev = GUI.enabled;
            GUI.enabled = enabled;
            bool clicked = GUILayout.Button(Loc.T(key), style, GUILayout.Width(150f));
            GUI.enabled = prev;
            return clicked && enabled;
        }

        // raw = Text schon übersetzt/formatiert
        private static void Note(string keyOrText, bool raw = false)
        {
            var gray = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, wordWrap = true };
            gray.normal.textColor = Color.gray;
            GUILayout.Label(raw ? keyOrText : Loc.T(keyOrText), gray);
        }

        private void Header(string key)
        {
            GUILayout.Space(10f);
            GUILayout.Label(Loc.T(key), boldStyle);
        }

        private bool Toggle(string key, bool value, int indentLevel)
        {
            GUILayout.BeginHorizontal();
            if (indentLevel > 0) GUILayout.Space(Indent * indentLevel);
            value = GUILayout.Toggle(value, " " + Loc.T(key));
            GUILayout.EndHorizontal();
            return value;
        }

        private float Slider(string key, float value, float min, float max, int precision)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(key), GUILayout.Width(LabelWidth));
            float v = GUILayout.HorizontalSlider(Mathf.Clamp(value, min, max), min, max, GUILayout.Width(200f));
            v = (float)Math.Round(v, precision);
            GUILayout.Label(" " + v.ToString("F" + precision, Loc.Culture), GUILayout.Width(80f));
            GUILayout.EndHorizontal();
            return v;
        }

        // Zahlenfeld; Komma und Punkt werden als Dezimaltrenner akzeptiert
        private float Field(string key, float value)
        {
            string buffer;
            if (!fieldBuffers.TryGetValue(key, out buffer))
                buffer = value.ToString(CultureInfo.InvariantCulture);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T(key), GUILayout.Width(LabelWidth));
            buffer = GUILayout.TextField(buffer, GUILayout.Width(120f));
            GUILayout.EndHorizontal();

            fieldBuffers[key] = buffer;

            float parsed;
            if (float.TryParse(buffer.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                value = parsed;
            return value;
        }
    }

    [Serializable]
    public class DamageEntry
    {
        [XmlAttribute("k")]
        public string Key;

        [XmlAttribute("v")]
        public float Value;
    }

    // =========================
    // SCHADENSSPEICHER (0..1 pro Weiche, im Spielstand gespeichert)
    // =========================

    public static class DamageStore
    {
        public static readonly Dictionary<string, float> Map = new Dictionary<string, float>(1024);

        public static float Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0f;
            float v;
            return Map.TryGetValue(key, out v) ? Mathf.Clamp01(v) : 0f;
        }

        public static void AddPercent(string key, float percent01)
        {
            if (string.IsNullOrEmpty(key)) return;
            Set(key, Get(key) + Mathf.Max(0f, percent01));
        }

        public static void Set(string key, float value01)
        {
            if (string.IsNullOrEmpty(key)) return;

            float v = Mathf.Clamp01(value01);
            if (v <= 0.0001f)
            {
                Map.Remove(key);
                return;
            }
            Map[key] = v;
        }

        public static Dictionary<string, float> All() => Map;

        public static void ReplaceAll(IDictionary<string, float> newMap)
        {
            Map.Clear();
            if (newMap == null) return;

            foreach (var entry in newMap)
            {
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                float v = Mathf.Clamp01(entry.Value);
                if (v <= 0.0001f) continue;
                Map[entry.Key] = v;
            }
        }

        public static string MakeKey(Junction j)
        {
            if (j == null) return "NULL";

            string id = null;
            try { id = j.junctionData.junctionIdLong; } catch { }
            id = (id ?? "X").Trim();

            if (string.IsNullOrEmpty(id))
                id = j.gameObject != null ? j.gameObject.name : "Switch";

            return id;
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
            float t;
            return blockUntil.TryGetValue(key, out t) && Time.unscaledTime < t;
        }
    }

    // =========================
    // SPIELSTAND
    // =========================

    internal static class JM_Save
    {
        public const string KEY = "JunctionMaintenance_Map";

        public static JArray ToJsonArray(IDictionary<string, float> map)
        {
            var arr = new JArray();
            if (map == null || map.Count == 0) return arr;
            foreach (var kv in map)
            {
                arr.Add(new JObject
                {
                    ["Junction"] = kv.Key,
                    ["Damage"] = Mathf.Clamp01(kv.Value)
                });
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

        public static void LoadFromSaveData(SaveGameData saveData, bool force = false)
        {
            try
            {
                if (saveData == null) return;

                // Client: Schäden kommen ausschließlich vom Host (Snapshot)
                if (JM_Multiplayer.IsClient) return;

                if (!force && ReferenceEquals(_lastLoadedSaveData, saveData)) return;

                JObject dataObject = saveData.GetJsonObject();
                var loadedMap = new Dictionary<string, float>(1024);

                JToken token;
                if (dataObject != null && dataObject.TryGetValue(JM_Save.KEY, out token) &&
                    token != null && token.Type != JTokenType.Null)
                {
                    loadedMap = JM_Save.FromToken(token);
                }

                DamageStore.ReplaceAll(loadedMap);
                _lastLoadedSaveData = saveData;

                Main.Log($"Loaded junction damage from savegame: {loadedMap.Count} entries.");
            }
            catch (Exception e)
            {
                Main.Error("LoadFromSaveData failed: " + e);
            }
        }

        public static void EnsureLoadedFromSaveOnce()
        {
            try
            {
                SaveGameData current = SaveGameManager.Instance?.data;
                if (current == null) return;
                LoadFromSaveData(current, force: false);
            }
            catch (Exception e)
            {
                Main.Error("EnsureLoadedFromSaveOnce failed: " + e);
            }
        }

        // Neuer Spielstand / neues Spiel: alles Laufende verwerfen
        public static void ResetForNewSession()
        {
            DamageStore.ReplaceAll(new Dictionary<string, float>());
            _lastLoadedSaveData = null;
            ResetRuntime();
            Main.Log("New session: junction damage runtime reset.");
        }

        public static void ResetRuntime()
        {
            JunctionRepair.Clear();
            RepairHold.Release();
            DerailTracker.Clear();
            JunctionLookup.Reset();
        }

        public static void InvalidateLoadedSave()
        {
            _lastLoadedSaveData = null;
        }
    }
}
