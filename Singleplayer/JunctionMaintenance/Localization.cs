// File: Localization.cs
// Namespace: JunctionMaintenance
// Mehrsprachigkeit: Englisch + Deutsch hier, 19 weitere Sprachen in Translations.cs.
// Korrekturen oder zusätzliche Sprachen als Textdatei im Mod-Ordner (überschreiben die eingebauten):
//   <Mod>/lang/<code>.txt   (z. B. lang/fr.txt; gleiches Format wie bei TrackMaintenance)
//
// Format der Datei (UTF-8), eine Zeile pro Eintrag:
//   # Kommentar
//   lang.name = Français
//   menu.label = Réparation de voie
//   ...
// "\n" im Wert wird als Zeilenumbruch gelesen. Fehlende Schlüssel fallen auf Englisch zurück.
//
// Die Sprache wird immer automatisch erkannt: Sprache des Spiels (I2 Localization, per Reflection),
// sonst Systemsprache. Eine Auswahl in den Einstellungen gibt es nicht.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace JunctionMaintenance
{
    public static class Loc
    {
        private const string Fallback = "en";

        private static readonly Dictionary<string, Dictionary<string, string>> languages =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static string current = Fallback;
        private static float nextCheck;

        private static string cultureFor;
        private static CultureInfo culture = CultureInfo.InvariantCulture;

        private static bool i2Searched;
        private static PropertyInfo i2LanguageCode;

        // =========================
        // INITIALISIERUNG
        // =========================

        public static void Init(string modPath)
        {
            languages.Clear();
            AddBuiltIn();
            LoadFiles(modPath);
            Refresh();
        }

        // Aktuelle Sprache (wird alle 2 s neu ermittelt, damit ein Sprachwechsel im Spiel greift)
        public static string Current
        {
            get
            {
                if (Time.realtimeSinceStartup >= nextCheck) Refresh();
                return current;
            }
        }

        // Sprache immer automatisch: Sprache des Spiels, sonst Systemsprache
        public static void Refresh()
        {
            nextCheck = Time.realtimeSinceStartup + 2f;
            current = Resolve(GameLanguage());
        }

        // =========================
        // ÜBERSETZEN / FORMATIEREN
        // =========================

        public static string T(string key)
        {
            Dictionary<string, string> d;
            string value;
            if (languages.TryGetValue(Current, out d) && d.TryGetValue(key, out value)) return value;
            if (languages.TryGetValue(Fallback, out d) && d.TryGetValue(key, out value)) return value;
            return key;
        }

        public static string Format(string key, params object[] args)
        {
            try { return string.Format(Culture, T(key), args); }
            catch (FormatException) { return T(key); }
        }

        // Zahlenformat passend zur Sprache (1,234.00 bzw. 1.234,00)
        public static CultureInfo Culture
        {
            get
            {
                string lang = Current;
                if (lang != cultureFor)
                {
                    cultureFor = lang;
                    try { culture = CultureInfo.GetCultureInfo(lang == "en" ? "en-US" : lang); }
                    catch { culture = CultureInfo.InvariantCulture; }
                }
                return culture;
            }
        }

        // Geldbetrag wie im Spiel: "$" + N2
        public static string Money(double value)
        {
            return "$" + value.ToString("N2", Culture);
        }

        // =========================
        // SPRACHERKENNUNG
        // =========================

        private static string Resolve(string code)
        {
            if (string.IsNullOrEmpty(code)) return Fallback;
            code = code.Replace('_', '-');
            if (languages.ContainsKey(code)) return code;

            // Varianten, die das Spiel bzw. das System liefern kann
            string lower = code.ToLowerInvariant();
            if (lower == "zh-hant" || lower == "zh-tw" || lower == "zh-hk" || lower == "zh-mo") return Pick("zh-TW");
            if (lower.StartsWith("zh")) return Pick("zh-CN");
            if (lower == "nb" || lower == "nn" || lower.StartsWith("nb-") || lower.StartsWith("nn-")) return Pick("no");

            string baseCode = code.Split('-')[0];
            if (languages.ContainsKey(baseCode)) return baseCode;

            return Fallback;
        }

        private static string Pick(string code)
        {
            return languages.ContainsKey(code) ? code : Fallback;
        }

        private static string GameLanguage()
        {
            try
            {
                if (!i2Searched)
                {
                    i2Searched = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t;
                        try { t = asm.GetType("I2.Loc.LocalizationManager", false); }
                        catch { continue; }
                        if (t == null) continue;
                        i2LanguageCode = t.GetProperty("CurrentLanguageCode", BindingFlags.Public | BindingFlags.Static);
                        break;
                    }
                }

                if (i2LanguageCode != null)
                {
                    var code = i2LanguageCode.GetValue(null, null) as string;
                    if (!string.IsNullOrEmpty(code)) return code;
                }
            }
            catch { }

            switch (Application.systemLanguage)
            {
                case SystemLanguage.German: return "de";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.Hungarian: return "hu";
                case SystemLanguage.Swedish: return "sv";
                case SystemLanguage.Danish: return "da";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Finnish: return "fi";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.ChineseSimplified: return "zh-CN";
                case SystemLanguage.ChineseTraditional: return "zh-TW";
                default: return Fallback;
            }
        }

        // =========================
        // SPRACHDATEIEN
        // =========================

        private static void LoadFiles(string modPath)
        {
            if (string.IsNullOrEmpty(modPath)) return;

            string dir = Path.Combine(modPath, "lang");
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.GetFiles(dir, "*.txt"))
            {
                try
                {
                    string code = Path.GetFileNameWithoutExtension(file);
                    var entries = ParseLines(File.ReadAllLines(file, Encoding.UTF8));
                    entries.Remove("lang.code");

                    Add(code, entries);
                    Main.Log($"Language file loaded: {code} ({entries.Count} entries)");
                }
                catch (Exception e)
                {
                    Main.Error($"Language file '{file}' could not be read: {e.Message}");
                }
            }
        }

        // "schlüssel = wert" pro Zeile; # = Kommentar; "\n" im Wert = Zeilenumbruch
        private static Dictionary<string, string> ParseLines(IEnumerable<string> lines)
        {
            var entries = new Dictionary<string, string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                entries[key] = value;
            }
            return entries;
        }

        // Eingebauter Sprachblock (Translations.cs); die Sprache steht in "lang.code"
        private static void AddBlock(string block)
        {
            var entries = ParseLines(block.Split('\n'));
            string code;
            if (!entries.TryGetValue("lang.code", out code) || string.IsNullOrEmpty(code)) return;
            entries.Remove("lang.code");
            Add(code, entries);
        }

        private static void Add(string code, Dictionary<string, string> entries)
        {
            Dictionary<string, string> d;
            if (!languages.TryGetValue(code, out d))
            {
                d = new Dictionary<string, string>();
                languages[code] = d;
            }
            foreach (var kv in entries) d[kv.Key] = kv.Value;
        }

        // =========================
        // EINGEBAUTE TEXTE
        // =========================


        private static void AddBuiltIn()
        {
            Add("en", new Dictionary<string, string>
            {
                { "lang.name", "English" },

                // Menü / Liste
                { "menu.label", "Junction Maintenance" },
                { "repair.title", "Junction Maintenance" },
                { "station.title", "Station Junctions" },
                { "col.junction", "Junction" },
                { "col.damage", "Damage" },
                { "col.cost", "Cost" },
                { "col.payout", "Payout" },
                { "repair.none", "No damaged junctions nearby." },
                { "station.none", "No station junctions found." },
                { "repair.radius", "Radius: {0} m" },
                { "repair.noDamage", "{0} has no damage." },
                { "repair.stopTrain", "Stop the train to access junction maintenance." },
                { "repair.cabooseOnly", "Junction maintenance is only available in the caboose." },
                { "repair.needLicense", "Junction repair requires the junction maintenance license." },
                { "repair.busyOther", "This vehicle is busy with another repair." },
                { "repair.done", "Repaired {0} - {1}" },
                { "repair.earned", "Repaired {0} - earned {1}" },
                { "repair.insured", "Repaired {0} - paid {1}, insurance {2}" },
                { "repair.working", "Repair in progress" },
                { "repair.wait", "Please wait … {0} s" },
                { "unknown", "Unknown" },

                // Bezahlbildschirm
                { "pay.junction", "Junction:" },
                { "pay.insertWallet", "Insert wallet to pay" },
                { "pay.deposited", "Deposited" },
                { "pay.insurance", "Insurance covers {0}" },

                // Einstellungen
                { "set.damage", "Junction damage" },
                { "set.damageRunThrough", "Damage when a switch is run through" },
                { "set.damageAtDerail", "One-time damage on derailment" },
                { "set.damageWhenDragged", "Continuous damage when dragged" },
                { "set.damageByExplosion", "Damage by explosion" },
                { "set.interval", "Damage interval after derailment (s)" },
                { "set.minSpeed", "Minimum speed after derailment (km/h)" },
                { "set.sec.multipliers", "Multipliers" },
                { "set.baseNote", "Base damage (fixed): 1 % per started 10 km/h, multiplied by the multipliers below." },
                { "set.sec.drag", "Dragging" },
                { "set.perTon", "Multiplier per 1000 kg of vehicle weight" },
                { "set.perKmh", "Multiplier per 1 km/h of speed" },
                { "set.locoBonus", "Multiplier bonus for locomotives" },
                { "set.multiplierPreview", "Example 100 t at 50 km/h: car {0} %, locomotive {1} %" },
                { "set.sec.explosion", "Explosions" },
                { "set.explosionRadius", "Explosion radius (m)" },
                { "set.explosionDamage", "Damage at the center of the explosion (%)" },
                { "set.explosionNote", "The damage decreases linearly towards the edge of the radius." },
                { "set.sec.flip", "Random switching" },
                { "set.flipEnabled", "Damaged switches can change position randomly" },
                { "set.flipSafeSpeed", "No random switching up to (km/h)" },
                { "set.flipMultiplier", "Switching chance multiplier (chance = damage x multiplier)" },
                { "set.flipPreview", "Chance at 10 % / 50 % / 100 % damage: {0} % / {1} % / {2} %" },
                { "set.sec.switch", "Switch behavior" },
                { "set.blockFull", "Switch cannot be thrown at 100 % damage" },
                { "set.sec.repair", "Junction repair" },
                { "set.mode", "Repair mode" },
                { "set.mode.penalty", "Penalty" },
                { "set.mode.reward", "Reward" },
                { "set.mode.license", "License" },
                { "set.mode.needsLicenseMod", "License mode requires the DVCustomLicenses mod." },
                { "set.mode.licenseForced", "License mode is set automatically because the DVCustomLicenses mod is active." },
                { "set.mode.info.penalty", "You most likely caused the damage to the junction, so you pay for the repair. The game's insurance applies: once your deductible is reached, it covers the rest." },
                { "set.mode.info.reward", "Regardless of who caused the damage, you receive a compensation for every junction repair." },
                { "set.mode.info.license", "Without the junction maintenance license you cannot repair junctions. With it you pay for the repairs. With the junction maintenance contractor license you are paid for every repair." },
                { "set.repairRadius", "Repair radius around the caboose (m)" },
                { "set.repairMinTime", "Repair duration, minor damage (s)" },
                { "set.repairMaxTime", "Repair duration, 100 % damage (s)" },
                { "set.maxCost", "Repair cost per 100 % damage ($)" },
                { "set.maxReward", "Repair payout per 100 % damage ($)" },
                { "set.licensePrice", "Junction maintenance license: price ($)" },
                { "set.licenseCopay", "Junction maintenance license: copay increase ($)" },
                { "set.license2Price", "Junction maintenance contractor license: price ($)" },
                { "set.license2Copay", "Junction maintenance contractor license: copay increase ($)" },
                { "set.licenseNote", "Copay changes for licenses you already own take effect after loading a save." },
                { "set.sec.debug", "Debug" },
                { "set.debugLogs", "Print debug logs" },
                { "set.listDamaged", "List all damaged junctions in the log" },
            });

            Add("de", new Dictionary<string, string>
            {
                { "lang.name", "Deutsch" },

                // Menü / Liste
                { "menu.label", "Weicheninstandhaltung" },
                { "repair.title", "Weicheninstandhaltung" },
                { "station.title", "Weichen des Bahnhofs" },
                { "col.junction", "Weiche" },
                { "col.damage", "Schaden" },
                { "col.cost", "Kosten" },
                { "col.payout", "Vergütung" },
                { "repair.none", "Keine beschädigten Weichen in der Nähe." },
                { "station.none", "Keine Weichen des Bahnhofs gefunden." },
                { "repair.radius", "Radius: {0} m" },
                { "repair.noDamage", "{0} ist unbeschädigt." },
                { "repair.stopTrain", "Zug anhalten, um die Weicheninstandhaltung zu öffnen." },
                { "repair.cabooseOnly", "Weicheninstandhaltung gibt es nur im Begleitwagen." },
                { "repair.needLicense", "Für die Weichenreparatur brauchst du die Weicheninstandhaltungs-Lizenz." },
                { "repair.busyOther", "Dieses Fahrzeug ist gerade mit einer anderen Reparatur beschäftigt." },
                { "repair.done", "{0} repariert - {1}" },
                { "repair.earned", "{0} repariert - {1} verdient" },
                { "repair.insured", "{0} repariert - bezahlt {1}, Versicherung {2}" },
                { "repair.working", "Reparatur läuft" },
                { "repair.wait", "Bitte warten … {0} s" },
                { "unknown", "Unbekannt" },

                // Bezahlbildschirm
                { "pay.junction", "Weiche:" },
                { "pay.insertWallet", "Geldbörse einlegen zum Bezahlen" },
                { "pay.deposited", "Eingezahlt" },
                { "pay.insurance", "Versicherung übernimmt {0}" },

                // Einstellungen
                { "set.damage", "Weichenschäden" },
                { "set.damageRunThrough", "Schaden beim Auffahren einer Weiche" },
                { "set.damageAtDerail", "Einmaliger Schaden durch Entgleisung" },
                { "set.damageWhenDragged", "Andauernder Schaden beim Mitschleifen" },
                { "set.damageByExplosion", "Schaden durch Explosion" },
                { "set.interval", "Intervallschaden nach Entgleisung (s)" },
                { "set.minSpeed", "Mindestgeschwindigkeit nach Entgleisung (km/h)" },
                { "set.sec.multipliers", "Multiplikatoren" },
                { "set.baseNote", "Basisschaden (fest): 1 % je angefangene 10 km/h, multipliziert mit den Multiplikatoren." },
                { "set.sec.drag", "Mitschleifen" },
                { "set.perTon", "Multiplikator pro 1000 kg Fahrzeuggewicht" },
                { "set.perKmh", "Multiplikator pro 1 km/h Geschwindigkeit" },
                { "set.locoBonus", "Multiplikator-Bonus für Loks" },
                { "set.multiplierPreview", "Beispiel 100 t bei 50 km/h: Wagen {0} %, Lok {1} %" },
                { "set.sec.explosion", "Explosionen" },
                { "set.explosionRadius", "Explosionsradius (m)" },
                { "set.explosionDamage", "Schaden im Zentrum der Explosion (%)" },
                { "set.explosionNote", "Der Schaden nimmt zum Rand des Radius hin linear ab." },
                { "set.sec.flip", "Zufälliges Umstellen" },
                { "set.flipEnabled", "Beschädigte Weichen können sich zufällig umstellen" },
                { "set.flipSafeSpeed", "Kein Umstellen bis (km/h)" },
                { "set.flipMultiplier", "Multiplikator der Umstellchance (Chance = Schaden x Multiplikator)" },
                { "set.flipPreview", "Chance bei 10 % / 50 % / 100 % Schaden: {0} % / {1} % / {2} %" },
                { "set.sec.switch", "Weichenverhalten" },
                { "set.blockFull", "Weiche lässt sich bei 100 % Schaden nicht mehr umstellen" },
                { "set.sec.repair", "Weichenreparatur" },
                { "set.mode", "Reparaturmodus" },
                { "set.mode.penalty", "Bezahlen" },
                { "set.mode.reward", "Verdienen" },
                { "set.mode.license", "Lizenz" },
                { "set.mode.needsLicenseMod", "Der Lizenzmodus benötigt die Mod DVCustomLicenses." },
                { "set.mode.licenseForced", "Der Lizenzmodus ist automatisch gesetzt, weil die Mod DVCustomLicenses aktiv ist." },
                { "set.mode.info.penalty", "Du hast den Schaden an der Weiche höchstwahrscheinlich verursacht und zahlst die Reparatur. Die Versicherung des Spiels greift: Ist deine Selbstbeteiligung erreicht, übernimmt sie den Rest." },
                { "set.mode.info.reward", "Egal wer den Schaden verursacht hat, du erhältst für jede Weichenreparatur eine Vergütung." },
                { "set.mode.info.license", "Ohne die Weicheninstandhaltungs-Lizenz kannst du keine Weichen reparieren. Mit ihr bezahlst du die Reparaturen. Mit der Lizenz Weichenbau-Unternehmer wirst du für jede Reparatur bezahlt." },
                { "set.repairRadius", "Reparaturradius um den Begleitwagen (m)" },
                { "set.repairMinTime", "Reparaturdauer bei geringem Schaden (s)" },
                { "set.repairMaxTime", "Reparaturdauer bei 100 % Schaden (s)" },
                { "set.maxCost", "Reparaturkosten pro 100 % Schaden ($)" },
                { "set.maxReward", "Vergütung pro 100 % Schaden ($)" },
                { "set.licensePrice", "Lizenz Weicheninstandhaltung: Preis ($)" },
                { "set.licenseCopay", "Lizenz Weicheninstandhaltung: Erhöhung des Eigenanteils ($)" },
                { "set.license2Price", "Lizenz Weichenbau-Unternehmer: Preis ($)" },
                { "set.license2Copay", "Lizenz Weichenbau-Unternehmer: Erhöhung des Eigenanteils ($)" },
                { "set.licenseNote", "Geänderte Eigenanteile bereits gekaufter Lizenzen gelten nach dem Laden eines Spielstands." },
                { "set.sec.debug", "Debug" },
                { "set.debugLogs", "Debug-Logs ausgeben" },
                { "set.listDamaged", "Alle beschädigten Weichen ins Log schreiben" },
            });

            // Weitere Sprachen (Translations.cs)
            foreach (string block in BuiltInTranslations.Blocks)
                AddBlock(block);
        }
    }
}
