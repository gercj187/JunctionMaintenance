// File: Multiplayer.cs
// Namespace: JunctionMaintenance
// Multiplayer (dv-multiplayer, MultiplayerAPI). Der Host ist maßgeblich:
//
//   Schaden      Auffahren: Client meldet dem Host (ServerBoundJunctionDamagePacket), Host übernimmt und verteilt.
//                Entgleisung, Mitschleifen, Explosion: nur der Host wertet aus (sonst doppelt gezählt).
//   Reparatur    Client: Schaden + Sperre sofort lokal, Meldung an den Host (Weiche, Anteil, Wagen-ID).
//                Host prüft (Schaden vorhanden, Weiche nicht schon in Reparatur), rechnet Preis bzw.
//                Vergütung selbst (inkl. Versicherung des Hosts) und bucht auf der Wallet des Hosts.
//                Bei Erfolg: Zustand + Reparaturstart an alle; sonst Abbruch an den Client.
//                Host: Reparatur wie im Einzelspieler, danach Zustand + Reparaturstart an alle.
//   Sperre       Laufende Reparaturen (Weiche, Name, Restdauer, Wagen-ID) kennen alle Rechner:
//                Weiche nicht umstellbar, Liste in der Nähe zeigt den Fortschritt, der auslösende
//                Zug wird festgehalten (auf Host und Client).
//   Einstellungen  Host schickt seine Einstellungen als XML (alle Felder, neue Felder automatisch mit).
//                Der Client verwendet sie nur für die Sitzung; seine eigenen bleiben unverändert.
//   Spielstand   Nur der Host lädt und speichert die Weichenschäden. Ein Client bekommt nach dem Laden
//                einen Snapshot (Schäden + laufende Reparaturen).

using System;
using System.Collections.Generic;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using UnityEngine;

namespace JunctionMaintenance
{
    // ============================================================
    // PAKETE
    // ============================================================

    public class ServerBoundJunctionDamagePacket : IPacket
    {
        public string JunctionKey { get; set; } = string.Empty;
        public float DamageToAdd { get; set; }
    }

    public class ServerBoundJunctionRepairPacket : IPacket
    {
        public string JunctionKey { get; set; } = string.Empty;
        public string JunctionName { get; set; } = string.Empty;
        public float RepairAmount { get; set; }
        public double MoneyAmount { get; set; }     // nur zur Kontrolle, der Host rechnet selbst
        public bool IsReward { get; set; }          // nur zur Kontrolle
        public string CarId { get; set; } = string.Empty;
    }

    public class ServerBoundJunctionReadyPacket : IPacket
    {
        public bool Ready { get; set; }
    }

    public class ClientBoundJunctionStatePacket : IPacket
    {
        public string JunctionKey { get; set; } = string.Empty;
        public float Damage { get; set; }
    }

    public class ClientBoundJunctionSnapshotPacket : IPacket
    {
        public string[] JunctionKeys { get; set; } = Array.Empty<string>();
        public float[] DamageValues { get; set; } = Array.Empty<float>();

        // Laufende Reparaturen
        public string[] RepairKeys { get; set; } = Array.Empty<string>();
        public string[] RepairNames { get; set; } = Array.Empty<string>();
        public string[] RepairCarIds { get; set; } = Array.Empty<string>();
        public float[] RepairRemaining { get; set; } = Array.Empty<float>();
    }

    public class ClientBoundJunctionSettingsPacket : IPacket
    {
        public string SettingsXml { get; set; } = string.Empty;
    }

    public class ClientBoundJunctionRepairStartedPacket : IPacket
    {
        public string JunctionKey { get; set; } = string.Empty;
        public string JunctionName { get; set; } = string.Empty;
        public float Duration { get; set; }
        public string CarId { get; set; } = string.Empty;
    }

    public class ClientBoundJunctionRepairRejectedPacket : IPacket
    {
        public string JunctionKey { get; set; } = string.Empty;
        public float Damage { get; set; }           // Schaden laut Host
    }

    // ============================================================
    // ZENTRALE SCHNITTSTELLE
    // ============================================================

    internal static class JM_Multiplayer
    {
        private static GameObject runtimeObject;

        public static bool IsHost
        {
            get
            {
                try { return MultiplayerAPI.Instance != null && MultiplayerAPI.Server != null && MultiplayerAPI.Instance.IsHost; }
                catch { return false; }
            }
        }

        public static bool IsClient
        {
            get
            {
                try { return MultiplayerAPI.Instance != null && MultiplayerAPI.Client != null && !MultiplayerAPI.Instance.IsHost; }
                catch { return false; }
            }
        }

        public static bool IsMultiplayer { get { return IsHost || IsClient; } }

        public static void Initialize()
        {
            if (runtimeObject != null) return;

            try
            {
                runtimeObject = new GameObject("JunctionMaintenance_Multiplayer");
                UnityEngine.Object.DontDestroyOnLoad(runtimeObject);
                runtimeObject.AddComponent<JunctionMaintenanceMPClient>();
                runtimeObject.AddComponent<JunctionMaintenanceMPServer>();
                Main.Log("[MP] Multiplayer runtime created.");
            }
            catch (Exception e)
            {
                Main.Error("[MP] Multiplayer runtime could not be created: " + e);
            }
        }

        // ---------- Schaden ----------

        public static void ReportDamage(string junctionKey, float damageToAdd01)
        {
            if (string.IsNullOrWhiteSpace(junctionKey)) return;

            float dmg = Mathf.Clamp01(damageToAdd01);
            if (dmg <= 0f) return;

            if (IsClient)
            {
                JunctionMaintenanceMPClient.Instance?.SendDamageReport(junctionKey, dmg);
                return;
            }

            if (IsHost && JunctionMaintenanceMPServer.Instance != null)
            {
                JunctionMaintenanceMPServer.Instance.ApplyDamageAndBroadcast(junctionKey, dmg);
                return;
            }

            DamageStore.AddPercent(junctionKey, dmg);
        }

        // ---------- Reparatur ----------

        // Client: Reparatur an den Host melden (Schaden und Sperre sind lokal schon gesetzt)
        public static void ReportRepair(string junctionKey, string junctionName, float repair01, RepairQuote q, string carId)
        {
            if (!IsClient || string.IsNullOrWhiteSpace(junctionKey)) return;
            JunctionMaintenanceMPClient.Instance?.SendRepairReport(junctionKey, junctionName, repair01, q, carId);
        }

        // Host: eigene Reparatur an alle Clients verteilen
        public static void HostRepairStarted(string junctionKey, string junctionName, float damageAfter, float duration, string carId)
        {
            if (!IsHost || JunctionMaintenanceMPServer.Instance == null) return;
            JunctionMaintenanceMPServer.Instance.BroadcastState(junctionKey, damageAfter);
            JunctionMaintenanceMPServer.Instance.BroadcastRepairStarted(junctionKey, junctionName, duration, carId);
        }

        // ---------- Einstellungen ----------

        public static void BroadcastHostSettings()
        {
            if (!IsHost) return;
            JunctionMaintenanceMPServer.Instance?.SendSettingsToAll();
        }

        // ---------- Bildschirm aktualisieren ----------

        internal static void RefreshOpenCareerManager()
        {
            try
            {
                if (JM_ListState.Active && !JM_Payment.Active)
                {
                    JM_ListState.Rebuild();
                    JM_ListState.Render();
                }
            }
            catch (Exception e)
            {
                Main.Error("[MP] Career Manager refresh failed: " + e);
            }
        }
    }

    // ============================================================
    // CLIENT
    // ============================================================

    internal class JunctionMaintenanceMPClient : MonoBehaviour
    {
        public static JunctionMaintenanceMPClient Instance { get; private set; }

        private IClient client;
        private bool registered;
        private bool wasClient;
        private bool clearedForConnection;
        private bool snapshotReceived;
        private float nextReadyRequest;

        // Schäden, die vor dem Snapshot entstehen, werden gesammelt und danach gesendet
        private readonly Dictionary<string, float> pendingDamage = new Dictionary<string, float>(StringComparer.Ordinal);

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this)
            {
                Destroy(this);
                return;
            }

            wasClient = JM_Multiplayer.IsClient;
            RefreshConnectionState();
        }

        private void Update()
        {
            try
            {
                RefreshConnectionState();
                if (!registered) TryRegister();
                TrySendReady();
            }
            catch (Exception e)
            {
                Main.Error("[MP] Client update failed: " + e);
            }
        }

        private void ResetSyncState()
        {
            registered = false;
            snapshotReceived = false;
            nextReadyRequest = 0f;
            clearedForConnection = false;
            pendingDamage.Clear();
        }

        private void RefreshConnectionState()
        {
            bool isClientNow = JM_Multiplayer.IsClient;
            IClient current = MultiplayerAPI.Client;

            // Neue Verbindung (oder getrennt): eigene Einstellungen zurück, Zustand neu aufbauen
            if (!ReferenceEquals(client, current))
            {
                if (client != null) Main.RestoreLocalSettings();
                client = current;
                ResetSyncState();
                Main.Log("[MP] Client connection changed, synchronization reset.");
            }

            if (wasClient != isClientNow)
            {
                bool wasBefore = wasClient;
                wasClient = isClientNow;
                snapshotReceived = false;
                nextReadyRequest = 0f;
                clearedForConnection = false;
                pendingDamage.Clear();

                if (wasBefore && !isClientNow) Main.RestoreLocalSettings();
                Main.Log(isClientNow ? "[MP] Entered multiplayer as client." : "[MP] Left multiplayer client state.");
            }

            // Als Client: lokalen Einzelspieler-Zustand verwerfen und auf den Host warten
            if (isClientNow && !clearedForConnection)
            {
                DamageStore.ReplaceAll(new Dictionary<string, float>());
                JunctionRepair.Clear();
                clearedForConnection = true;
                Main.Log("[MP] Local junction state cleared, waiting for host snapshot.");
                JM_Multiplayer.RefreshOpenCareerManager();
            }

            if (!isClientNow)
            {
                snapshotReceived = false;
                nextReadyRequest = 0f;
                clearedForConnection = false;
                pendingDamage.Clear();
            }
        }

        private void TryRegister()
        {
            client = MultiplayerAPI.Client;
            if (client == null) return;

            client.RegisterPacket<ClientBoundJunctionStatePacket>(OnState);
            client.RegisterPacket<ClientBoundJunctionSnapshotPacket>(OnSnapshot);
            client.RegisterPacket<ClientBoundJunctionSettingsPacket>(OnSettings);
            client.RegisterPacket<ClientBoundJunctionRepairStartedPacket>(OnRepairStarted);
            client.RegisterPacket<ClientBoundJunctionRepairRejectedPacket>(OnRepairRejected);

            registered = true;
            Main.Log("[MP] Client packet handlers registered.");
        }

        // Erst wenn der Client vollständig geladen ist: Einstellungen + Snapshot anfordern (alle 2 s, bis er da ist)
        private void TrySendReady()
        {
            if (!registered || client == null || !JM_Multiplayer.IsClient || snapshotReceived) return;
            if (PlayerManager.PlayerTransform == null || !AStartGameData.carsAndJobsLoadingFinished) return;
            if (Time.unscaledTime < nextReadyRequest) return;

            client.SendPacketToServer(new ServerBoundJunctionReadyPacket { Ready = true }, reliable: true);
            nextReadyRequest = Time.unscaledTime + 2f;
            Main.Log("[MP] Snapshot requested.");
        }

        // ---------- Senden ----------

        public void SendDamageReport(string junctionKey, float damage01)
        {
            if (string.IsNullOrWhiteSpace(junctionKey) || damage01 <= 0f) return;

            if (!registered || client == null || !snapshotReceived)
            {
                float pending;
                pendingDamage.TryGetValue(junctionKey, out pending);
                pendingDamage[junctionKey] = Mathf.Clamp01(pending + damage01);
                Main.Log($"[MP] Damage queued until synchronization: {junctionKey} +{damage01 * 100f:0.###}%");
                return;
            }

            SendDamageNow(junctionKey, damage01);
        }

        private void SendDamageNow(string junctionKey, float damage01)
        {
            client.SendPacketToServer(new ServerBoundJunctionDamagePacket
            {
                JunctionKey = junctionKey,
                DamageToAdd = Mathf.Clamp01(damage01)
            }, reliable: true);

            Main.Log($"[MP] Damage reported: {junctionKey} +{damage01 * 100f:0.###}%");
        }

        private void FlushPendingDamage()
        {
            if (pendingDamage.Count == 0 || client == null) return;

            var copy = new Dictionary<string, float>(pendingDamage, StringComparer.Ordinal);
            pendingDamage.Clear();
            foreach (var kv in copy)
                if (kv.Value > 0f) SendDamageNow(kv.Key, kv.Value);

            Main.Log($"[MP] Flushed {copy.Count} queued damage report(s).");
        }

        public void SendRepairReport(string junctionKey, string junctionName, float repair01, RepairQuote q, string carId)
        {
            if (!registered || client == null) return;

            client.SendPacketToServer(new ServerBoundJunctionRepairPacket
            {
                JunctionKey = junctionKey,
                JunctionName = junctionName ?? string.Empty,
                RepairAmount = Mathf.Clamp01(repair01),
                MoneyAmount = q.IsReward ? q.Payout : q.Pay,
                IsReward = q.IsReward,
                CarId = carId ?? string.Empty
            }, reliable: true);

            Main.Log($"[MP] Repair reported: {junctionKey} {repair01 * 100f:0.###}%, {(q.IsReward ? "EARN" : "PAY")}, car {carId}");
        }

        // ---------- Empfangen ----------

        private void OnState(ClientBoundJunctionStatePacket p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.JunctionKey)) return;
            DamageStore.Set(p.JunctionKey, Mathf.Clamp01(p.Damage));
            JM_Multiplayer.RefreshOpenCareerManager();
        }

        private void OnSnapshot(ClientBoundJunctionSnapshotPacket p)
        {
            if (p == null || p.JunctionKeys == null || p.DamageValues == null) return;
            if (p.JunctionKeys.Length != p.DamageValues.Length)
            {
                Main.Error("[MP] Invalid junction snapshot received.");
                return;
            }

            var map = new Dictionary<string, float>(p.JunctionKeys.Length);
            for (int i = 0; i < p.JunctionKeys.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(p.JunctionKeys[i])) continue;
                map[p.JunctionKeys[i]] = Mathf.Clamp01(p.DamageValues[i]);
            }
            DamageStore.ReplaceAll(map);

            // Laufende Reparaturen übernehmen (Sperre, Fortschritt, Zughalt)
            JunctionRepair.Clear();
            int n = p.RepairKeys != null ? p.RepairKeys.Length : 0;
            for (int i = 0; i < n; i++)
            {
                string key = p.RepairKeys[i];
                if (string.IsNullOrWhiteSpace(key)) continue;
                string name = p.RepairNames != null && i < p.RepairNames.Length ? p.RepairNames[i] : key;
                string car = p.RepairCarIds != null && i < p.RepairCarIds.Length ? p.RepairCarIds[i] : null;
                float rest = p.RepairRemaining != null && i < p.RepairRemaining.Length ? p.RepairRemaining[i] : 0f;
                if (rest > 0.1f) JunctionRepair.Start(key, name, rest, string.IsNullOrEmpty(car) ? null : car);
            }

            snapshotReceived = true;
            Main.Log($"[MP] Junction snapshot applied: {map.Count} damaged, {n} repair(s) running.");

            FlushPendingDamage();
            JM_Multiplayer.RefreshOpenCareerManager();
        }

        private void OnSettings(ClientBoundJunctionSettingsPacket p)
        {
            if (p == null) return;
            Settings host = Settings.FromXml(p.SettingsXml);
            if (host == null) return;

            Main.UseHostSettings(host);
            JM_Multiplayer.RefreshOpenCareerManager();
        }

        private void OnRepairStarted(ClientBoundJunctionRepairStartedPacket p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.JunctionKey)) return;

            // Eigene Reparatur läuft schon (lokal gestartet)
            if (JunctionRepair.IsRunning(p.JunctionKey)) return;

            JunctionRepair.Start(p.JunctionKey, p.JunctionName, p.Duration,
                string.IsNullOrEmpty(p.CarId) ? null : p.CarId);
            JM_Multiplayer.RefreshOpenCareerManager();
        }

        // Host hat die eigene Reparatur abgelehnt: Sperre aufheben, Schaden zurück
        private void OnRepairRejected(ClientBoundJunctionRepairRejectedPacket p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.JunctionKey)) return;

            JunctionRepair.Cancel(p.JunctionKey);
            DamageStore.Set(p.JunctionKey, Mathf.Clamp01(p.Damage));
            JM_ListState.Status = Loc.T("mp.rejected");
            Main.Log($"[MP] Repair of {p.JunctionKey} rejected by host.");
            JM_Multiplayer.RefreshOpenCareerManager();
        }

        private void OnDestroy()
        {
            Main.RestoreLocalSettings();
            if (Instance == this) Instance = null;
            client = null;
            wasClient = false;
            ResetSyncState();
        }
    }

    // ============================================================
    // SERVER (HOST)
    // ============================================================

    internal class JunctionMaintenanceMPServer : MonoBehaviour
    {
        public static JunctionMaintenanceMPServer Instance { get; private set; }

        private IServer server;
        private bool initialized;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else if (Instance != this)
            {
                Destroy(this);
                return;
            }
            TryInitialize();
        }

        private void Update()
        {
            // Server kann neu entstehen (neue Sitzung)
            if (initialized && !ReferenceEquals(server, MultiplayerAPI.Server))
            {
                initialized = false;
                server = null;
            }
            if (!initialized) TryInitialize();
        }

        private void TryInitialize()
        {
            try
            {
                server = MultiplayerAPI.Server;
                if (server == null) return;

                server.RegisterPacket<ServerBoundJunctionDamagePacket>(OnDamageReported);
                server.RegisterPacket<ServerBoundJunctionRepairPacket>(OnRepairReported);
                server.RegisterPacket<ServerBoundJunctionReadyPacket>(OnClientReady);

                initialized = true;
                Main.Log("[MP] Server packet handlers registered.");
            }
            catch (Exception e)
            {
                server = null;
                Main.Error("[MP] Server registration failed: " + e);
            }
        }

        // ---------- Empfangen ----------

        private void OnDamageReported(ServerBoundJunctionDamagePacket p, IPlayer sender)
        {
            if (p == null || sender == null || string.IsNullOrWhiteSpace(p.JunctionKey)) return;
            float dmg = Mathf.Clamp01(p.DamageToAdd);
            if (dmg > 0f) ApplyDamageAndBroadcast(p.JunctionKey, dmg);
        }

        private void OnRepairReported(ServerBoundJunctionRepairPacket p, IPlayer sender)
        {
            if (p == null || sender == null || string.IsNullOrWhiteSpace(p.JunctionKey) || Main.Settings == null) return;

            string key = p.JunctionKey;
            float current = DamageStore.Get(key);

            // Prüfen: Schaden vorhanden, Weiche nicht schon in Reparatur
            if (current <= 0f)
            {
                Reject(sender, key, "no damage");
                return;
            }
            if (JunctionRepair.IsRunning(key))
            {
                Reject(sender, key, "repair already running");
                return;
            }

            // Fahrzeug des Clients arbeitet gerade für eine andere Wartungs-Mod (z. B. Gleisreparatur)
            TrainCar requester = JunctionRepair.FindCar(p.CarId);
            if (requester != null && MaintenanceBusy.BusyByOther(requester))
            {
                Reject(sender, key, "vehicle busy with another maintenance mod");
                return;
            }

            // Nie mehr als laut Host-Einstellung und tatsächlichem Schaden
            float r = Mathf.Min(current, Mathf.Clamp(p.RepairAmount, 0f, Main.Settings.RepairStep01));
            if (r <= 0f)
            {
                Reject(sender, key, "nothing to repair");
                return;
            }

            // Preis/Vergütung rechnet der Host selbst (Modus, Lizenzen und Versicherung des Hosts)
            RepairQuote q = RepairEconomy.Quote(r);
            double hostAmount = q.IsReward ? q.Payout : q.Pay;

            if (q.IsReward != p.IsReward || Math.Abs(hostAmount - p.MoneyAmount) > 0.01)
                Main.Log($"[MP] Repair {key}: client {(p.IsReward ? "EARN" : "PAY")} ${p.MoneyAmount:0.00}, " +
                         $"host {(q.IsReward ? "EARN" : "PAY")} ${hostAmount:0.00} – host values are used");

            if (!ApplyHostMoney(q))
            {
                Reject(sender, key, "host money transaction failed");
                return;
            }

            float after = current - r;
            float duration = JunctionRepair.DurationFor(r);
            string carId = string.IsNullOrEmpty(p.CarId) ? null : p.CarId;
            string name = string.IsNullOrEmpty(p.JunctionName) ? key : p.JunctionName;

            DamageStore.Set(key, after);
            JunctionRepair.Start(key, name, duration, carId);   // Host hält den Zug des Clients fest

            BroadcastState(key, after);
            BroadcastRepairStarted(key, name, duration, carId);
            JM_Multiplayer.RefreshOpenCareerManager();

            Main.Log($"[MP] Client repair applied: {key} {current * 100f:0.###}% -> {after * 100f:0.###}%");
        }

        private void Reject(IPlayer sender, string key, string reason)
        {
            Main.Log($"[MP] Client repair rejected: {key} ({reason})");
            if (server == null || sender == null) return;

            server.SendPacketToPlayer(new ClientBoundJunctionRepairRejectedPacket
            {
                JunctionKey = key,
                Damage = DamageStore.Get(key)
            }, sender, reliable: true);
        }

        // Geld ausschließlich auf der Wallet des Hosts
        private static bool ApplyHostMoney(RepairQuote q)
        {
            if (q.IsReward)
            {
                double payout = Math.Round(Math.Max(0.0, q.Payout), 2);
                if (payout <= 0.0) return true;

                var inv = DV.Utils.SingletonBehaviour<DV.InventorySystem.Inventory>.Instance;
                if (inv == null)
                {
                    Main.Error("[MP] Host inventory unavailable, payout not applied");
                    return false;
                }
                inv.AddMoney(payout);
                Main.Log($"[MP] Host wallet received ${payout:0.00} for a client junction repair");
                return true;
            }

            double pay = Math.Round(Math.Max(0.0, q.Pay), 2);
            if (pay > 0.0)
            {
                var inv = DV.Utils.SingletonBehaviour<DV.InventorySystem.Inventory>.Instance;
                if (inv == null)
                {
                    Main.Error("[MP] Host inventory unavailable, payment not applied");
                    return false;
                }
                if (inv.PlayerMoney + 0.001 < pay)
                {
                    Main.Log($"[MP] Host cannot pay ${pay:0.00} (available ${inv.PlayerMoney:0.00})");
                    return false;
                }
                if (!inv.RemoveMoney(pay))
                {
                    Main.Log($"[MP] Host wallet payment of ${pay:0.00} failed");
                    return false;
                }
                Main.Log($"[MP] Host wallet paid ${pay:0.00} for a client junction repair");
            }

            // Versicherung des Hosts wie im Einzelspieler
            RepairEconomy.AfterPaidRepair(q);
            return true;
        }

        private void OnClientReady(ServerBoundJunctionReadyPacket p, IPlayer sender)
        {
            if (p == null || sender == null || !p.Ready) return;
            SendSettingsToPlayer(sender);
            SendSnapshotToPlayer(sender);
            Main.Log("[MP] Settings and junction snapshot sent to client.");
        }

        // ---------- Senden ----------

        public void ApplyDamageAndBroadcast(string junctionKey, float damage01)
        {
            if (string.IsNullOrWhiteSpace(junctionKey)) return;

            float before = DamageStore.Get(junctionKey);
            float after = Mathf.Clamp01(before + Mathf.Max(0f, damage01));
            DamageStore.Set(junctionKey, after);
            BroadcastState(junctionKey, after);
            JM_Multiplayer.RefreshOpenCareerManager();

            Main.Log($"[MP] Damage applied: {junctionKey} {before * 100f:0.###}% -> {after * 100f:0.###}%");
        }

        public void BroadcastState(string junctionKey, float damage01)
        {
            if (!initialized || server == null) return;
            server.SendPacketToAll(new ClientBoundJunctionStatePacket
            {
                JunctionKey = junctionKey,
                Damage = Mathf.Clamp01(damage01)
            }, reliable: true, excludeSelf: true);
        }

        public void BroadcastRepairStarted(string junctionKey, string junctionName, float duration, string carId)
        {
            if (!initialized || server == null) return;
            server.SendPacketToAll(new ClientBoundJunctionRepairStartedPacket
            {
                JunctionKey = junctionKey,
                JunctionName = junctionName ?? junctionKey,
                Duration = duration,
                CarId = carId ?? string.Empty
            }, reliable: true, excludeSelf: true);
        }

        public void SendSettingsToAll()
        {
            if (!initialized || server == null || Main.Settings == null) return;
            server.SendPacketToAll(CreateSettingsPacket(), reliable: true, excludeSelf: true);
        }

        private void SendSettingsToPlayer(IPlayer player)
        {
            if (server == null || player == null || Main.Settings == null) return;
            server.SendPacketToPlayer(CreateSettingsPacket(), player, reliable: true);
        }

        private static ClientBoundJunctionSettingsPacket CreateSettingsPacket()
        {
            return new ClientBoundJunctionSettingsPacket { SettingsXml = Main.Settings.ToXml() };
        }

        private void SendSnapshotToPlayer(IPlayer player)
        {
            if (server == null || player == null) return;

            var map = DamageStore.All();
            var keys = new string[map.Count];
            var values = new float[map.Count];
            int i = 0;
            foreach (var kv in map)
            {
                keys[i] = kv.Key;
                values[i] = Mathf.Clamp01(kv.Value);
                i++;
            }

            var rKeys = new List<string>();
            var rNames = new List<string>();
            var rCars = new List<string>();
            var rRest = new List<float>();
            foreach (var job in JunctionRepair.Jobs)
            {
                rKeys.Add(job.Key);
                rNames.Add(job.Name ?? job.Key);
                rCars.Add(job.CarId ?? string.Empty);
                rRest.Add(job.Remaining);
            }

            server.SendPacketToPlayer(new ClientBoundJunctionSnapshotPacket
            {
                JunctionKeys = keys,
                DamageValues = values,
                RepairKeys = rKeys.ToArray(),
                RepairNames = rNames.ToArray(),
                RepairCarIds = rCars.ToArray(),
                RepairRemaining = rRest.ToArray()
            }, player, reliable: true);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            server = null;
            initialized = false;
        }
    }
}
