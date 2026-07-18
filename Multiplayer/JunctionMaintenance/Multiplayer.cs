// File: Multiplayer.cs
// Namespace: JunctionMaintenance

using System;
using System.Collections.Generic;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using UnityEngine;

namespace JunctionMaintenance
{
    // ============================================================
    // PACKETS
    // ============================================================
	
    public class ServerBoundJunctionDamagePacket : IPacket
    {
        public string JunctionKey { get; set; } = string.Empty;
        public float DamageToAdd { get; set; }
    }

	public class ServerBoundJunctionRepairPacket : IPacket
	{
		public string JunctionKey { get; set; } = string.Empty;
		public float RepairAmount { get; set; }
		public double MoneyAmount { get; set; }
		public bool IsReward { get; set; }
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
    }

    public class ClientBoundJunctionSettingsPacket : IPacket
    {
        public bool EnableRandomFlip { get; set; }
        public float SafeNoFlipSpeedKmh { get; set; }
        public float FlipMultiplierPercent { get; set; }
        public float FlipCooldownAfterForcedSec { get; set; }

        public float RepairRadius { get; set; }
        public float RepairAmountPercent { get; set; }
        public float RepairVehicleSearchRadius { get; set; }
        public float MaxVehicleStandingSpeedKmh { get; set; }

        public float MaxRepairCostFull { get; set; }
        public float MaxRepairRewardFull { get; set; }
        public float MaintenanceLicensePrice { get; set; }

        public bool BlockManualSwitchAtFullDamage { get; set; }
        public int RepairMode { get; set; }
    }

    // ============================================================
    // CENTRAL API
    // ============================================================

    internal static class JM_Multiplayer
    {
        private static GameObject _runtimeObject;

        public static bool IsHost
        {
            get
            {
                try
                {
                    return MultiplayerAPI.Instance != null &&
                           MultiplayerAPI.Server != null &&
                           MultiplayerAPI.Instance.IsHost;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool IsClient
        {
            get
            {
                try
                {
                    return MultiplayerAPI.Instance != null &&
                           MultiplayerAPI.Client != null &&
                           !MultiplayerAPI.Instance.IsHost;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool IsMultiplayer
        {
            get
            {
                return IsHost || IsClient;
            }
        }

        public static void Initialize()
        {
            if (_runtimeObject != null)
            {
                return;
            }

            _runtimeObject = new GameObject("JunctionMaintenance_Multiplayer");

            UnityEngine.Object.DontDestroyOnLoad(_runtimeObject);

            _runtimeObject.AddComponent<JunctionMaintenanceMPClient>();
            _runtimeObject.AddComponent<JunctionMaintenanceMPServer>();

            Main.Log(
                "[MP] Multiplayer runtime created.",
                true);
        }

        // ========================================================
        // DAMAGE
        // ========================================================
		
        public static void ReportDamage(string junctionKey,float damageToAdd)
        {
            if (string.IsNullOrWhiteSpace(junctionKey))
            {
                return;
            }

            float clampedDamage =
                Mathf.Clamp01(damageToAdd);

            if (clampedDamage <= 0f)
            {
                return;
            }

            if (IsClient)
            {
                JunctionMaintenanceMPClient.Instance?
                    .SendDamageReport(
                        junctionKey,
                        clampedDamage);

                return;
            }

            if (IsHost)
            {
                JunctionMaintenanceMPServer.Instance?
                    .ApplyDamageAndBroadcast(
                        junctionKey,
                        clampedDamage);

                return;
            }

            DamageStore.AddPercent(junctionKey,clampedDamage);
        }

        // ========================================================
        // REPAIR
        // ========================================================

		public static void ReportRepair(
			string junctionKey,
			float repairAmount,
			double moneyAmount,
			bool isReward)
		{
			if (string.IsNullOrWhiteSpace(junctionKey))
			{
				return;
			}

			float clampedRepair =
				Mathf.Clamp01(repairAmount);

			if (clampedRepair <= 0f)
			{
				return;
			}

			double safeMoneyAmount =
				Math.Max(
					0.0,
					moneyAmount);

			if (IsClient)
			{
				JunctionMaintenanceMPClient.Instance?
					.SendRepairReport(
						junctionKey,
						clampedRepair,
						safeMoneyAmount,
						isReward);

				return;
			}

			if (IsHost)
			{
				JunctionMaintenanceMPServer.Instance?
					.ApplyRepairAndBroadcast(
						junctionKey,
						clampedRepair);

				return;
			}

			float before = DamageStore.Get(junctionKey);

			float after =
				Mathf.Max(
					0f,
					before - Mathf.Min(
						before,
						clampedRepair));

			DamageStore.Set(
				junctionKey,
				after);
		}

        public static void BroadcastHostSettings()
        {
            if (!IsHost)
            {
                return;
            }

            JunctionMaintenanceMPServer.Instance?
                .SendSettingsToAll();
        }
    }

    // ============================================================
    // CLIENT
    // ============================================================

    internal class JunctionMaintenanceMPClient : MonoBehaviour
    {
        public static JunctionMaintenanceMPClient Instance
        {
            get;
            private set;
        }

        private IClient _client;
        private bool _registered;
        private bool _readyPacketSent;		
		private bool _wasMultiplayerClient;
		private bool _clearedForCurrentConnection;
		private bool _authoritativeSnapshotReceived;
		private float _nextReadyRequestTime;
		private readonly Dictionary<string, float> _pendingDamageReports =
			new Dictionary<string, float>(
				StringComparer.Ordinal);

		private void Awake()
		{
			if (Instance == null)
			{
				Instance = this;
			}
			else if (Instance != this)
			{
				Destroy(this);
				return;
			}

			_wasMultiplayerClient = JM_Multiplayer.IsClient;

			RefreshConnectionState();
		}

        private void Update()
		{
			RefreshConnectionState();

			if (!_registered)
			{
				TryRegisterClient();
			}

			TrySendReadyPacket();
		}
		
		private void RefreshConnectionState()
		{
			bool isClientNow =
				JM_Multiplayer.IsClient;

			IClient currentClient =
				MultiplayerAPI.Client;

			bool clientObjectChanged =
				!ReferenceEquals(
					_client,
					currentClient);

			bool roleChanged =
				_wasMultiplayerClient !=
				isClientNow;

			if (clientObjectChanged)
            {
                if (_client != null)
                {
                    Main.RestoreLocalSettings();
                    MaintenanceLicense.ReapplyConfiguredPrice();
                }

                _client =currentClient;

                _registered = false;
                _readyPacketSent = false;

                _authoritativeSnapshotReceived = false;
                _nextReadyRequestTime = 0f;

                _clearedForCurrentConnection = false;

                _pendingDamageReports.Clear();

                Main.Log(
                    "[MP] Client connection object changed. " +
                    "Local settings restored and synchronization " +
                    "state reset.",
                    true);
            }

			if (roleChanged)
            {
                bool wasClientBefore =_wasMultiplayerClient;
                _wasMultiplayerClient =isClientNow;

                _readyPacketSent = false;
                _authoritativeSnapshotReceived = false;
                _nextReadyRequestTime = 0f;
                _clearedForCurrentConnection = false;

                _pendingDamageReports.Clear();

                if (wasClientBefore &&!isClientNow)
                {
                    Main.RestoreLocalSettings();
                    MaintenanceLicense.ReapplyConfiguredPrice();
                }

                Main.Log(
                    isClientNow
                        ? "[MP] Entered multiplayer as client."
                        : "[MP] Left multiplayer client state. " +
                          "Local settings restored.",
                    true);
            }

			if (isClientNow &&
				!_clearedForCurrentConnection)
			{
				// Lokalen Singleplayer-Zustand verwerfen.
				DamageStore.ReplaceAll(
					new Dictionary<string, float>());

				_clearedForCurrentConnection = true;

				Main.Log(
					"[MP] Local junction damage cleared. " +
					"Waiting for authoritative host snapshot.",
					true);

				RefreshOpenCareerManager();
			}

			if (!isClientNow)
			{
				_readyPacketSent = false;
				_authoritativeSnapshotReceived = false;
				_nextReadyRequestTime = 0f;
				_clearedForCurrentConnection = false;

				_pendingDamageReports.Clear();
			}
		}

        private void TryRegisterClient()
        {
            if (_registered)
            {
                return;
            }

            _client =
                MultiplayerAPI.Client;

            if (_client == null)
            {
                return;
            }

            _client.RegisterPacket<ClientBoundJunctionStatePacket>(
                OnJunctionStateReceived);

            _client.RegisterPacket<ClientBoundJunctionSnapshotPacket>(
                OnSnapshotReceived);

            _client.RegisterPacket<ClientBoundJunctionSettingsPacket>(
                OnSettingsReceived);

            _registered = true;

            Main.Log(
                "[MP] Client packet handlers registered.",
                true);
        }

        private void TrySendReadyPacket()
		{
			if (!_registered ||
				_client == null)
			{
				return;
			}

			if (!JM_Multiplayer.IsClient)
			{
				return;
			}

			if (_authoritativeSnapshotReceived)
			{
				return;
			}

			if (PlayerManager.PlayerTransform == null)
			{
				return;
			}

			if (!AStartGameData.carsAndJobsLoadingFinished)
			{
				return;
			}

			if (Time.unscaledTime <
				_nextReadyRequestTime)
			{
				return;
			}

			var packet =
				new ServerBoundJunctionReadyPacket
				{
					Ready = true
				};

			_client.SendPacketToServer(
				packet,
				reliable: true);

			bool wasAlreadySent =
				_readyPacketSent;

			_readyPacketSent = true;

			_nextReadyRequestTime =
				Time.unscaledTime + 2f;

			Main.Log(
				wasAlreadySent
					? "[MP] Snapshot request repeated."
					: "[MP] Client fully loaded. Snapshot requested.",
				true);
		}

        public void SendDamageReport(string junctionKey,float damageToAdd)
		{
			if (string.IsNullOrWhiteSpace(junctionKey))
			{
				return;
			}

			float safeDamage =
				Mathf.Clamp01(damageToAdd);

			if (safeDamage <= 0f)
			{
				return;
			}

			if (!_registered ||
				_client == null ||
				!_authoritativeSnapshotReceived)
			{
				if (_pendingDamageReports.TryGetValue(
						junctionKey,
						out float pendingDamage))
				{
					_pendingDamageReports[junctionKey] =
						Mathf.Clamp01(
							pendingDamage + safeDamage);
				}
				else
				{
					_pendingDamageReports[junctionKey] =
						safeDamage;
				}

				Main.Log(
					$"[MP] Damage queued until synchronization: " +
					$"{junctionKey} +{safeDamage * 100f:0.###}%",
					true);

				return;
			}

			SendDamagePacketImmediately(
				junctionKey,
				safeDamage);
		}
		
		private void SendDamagePacketImmediately(string junctionKey,float damageToAdd)
		{
			if (_client == null ||
				string.IsNullOrWhiteSpace(junctionKey))
			{
				return;
			}

			var packet =
				new ServerBoundJunctionDamagePacket
				{
					JunctionKey =
						junctionKey,

					DamageToAdd =
						Mathf.Clamp01(damageToAdd)
				};

			_client.SendPacketToServer(
				packet,
				reliable: true);

			Main.Log(
				$"[MP] Damage reported: {junctionKey} " +
				$"+{damageToAdd * 100f:0.###}%",
				true);
		}

		private void FlushPendingDamageReports()
		{
			if (!_registered ||
				_client == null ||
				!_authoritativeSnapshotReceived ||
				_pendingDamageReports.Count == 0)
			{
				return;
			}

			var pendingCopy =
				new Dictionary<string, float>(
					_pendingDamageReports,
					StringComparer.Ordinal);

			_pendingDamageReports.Clear();

			foreach (KeyValuePair<string, float> entry in pendingCopy)
			{
				if (string.IsNullOrWhiteSpace(entry.Key) ||
					entry.Value <= 0f)
				{
					continue;
				}

				SendDamagePacketImmediately(
					entry.Key,
					entry.Value);
			}

			Main.Log(
				$"[MP] Flushed {pendingCopy.Count} queued damage reports.",
				true);
		}

		public void SendRepairReport(string junctionKey,float repairAmount,double moneyAmount,bool isReward)
		{
			if (!_registered ||
				_client == null ||
				string.IsNullOrWhiteSpace(junctionKey))
			{
				return;
			}

			float safeRepairAmount =
				Mathf.Clamp01(repairAmount);

			double safeMoneyAmount =
				Math.Max(
					0.0,
					moneyAmount);

			var packet =
				new ServerBoundJunctionRepairPacket
				{
					JunctionKey =
						junctionKey,

					RepairAmount =
						safeRepairAmount,

					// NEW
					MoneyAmount =
						safeMoneyAmount,

					// NEW
					IsReward =
						isReward
				};

			_client.SendPacketToServer(
				packet,
				reliable: true);

			Main.Log(
				$"[MP] Repair reported: {junctionKey}, " +
				$"repair={safeRepairAmount * 100f:0.###}%, " +
				$"mode={(isReward ? "EARN" : "PAY")}, " +
				$"amount=${safeMoneyAmount:0.00}",
				true);
		}

        private void OnJunctionStateReceived(ClientBoundJunctionStatePacket packet)
        {
            if (packet == null ||
                string.IsNullOrWhiteSpace(packet.JunctionKey))
            {
                return;
            }

            float damage =
                Mathf.Clamp01(packet.Damage);

            DamageStore.Set(
                packet.JunctionKey,
                damage);

            Main.Log(
                $"[MP] Junction state received: " +
                $"{packet.JunctionKey} = {damage * 100f:0.###}%",
                false);

            RefreshOpenCareerManager();
        }

        private void OnSnapshotReceived(ClientBoundJunctionSnapshotPacket packet)
        {
            if (packet == null ||
                packet.JunctionKeys == null ||
                packet.DamageValues == null)
            {
                return;
            }

            if (packet.JunctionKeys.Length !=
                packet.DamageValues.Length)
            {
                Main.Log(
                    "[MP] Invalid junction snapshot received.",
                    true);
                return;
            }

            var map =
                new Dictionary<string, float>(
                    packet.JunctionKeys.Length);

            for (int i = 0;
                 i < packet.JunctionKeys.Length;
                 i++)
            {
                string key =
                    packet.JunctionKeys[i];

                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                map[key] =
                    Mathf.Clamp01(
                        packet.DamageValues[i]);
            }

            DamageStore.ReplaceAll(map);
			
			_authoritativeSnapshotReceived = true;
			_readyPacketSent = true;

            Main.Log(
                $"[MP] Junction snapshot applied: {map.Count} entries.",
                true);

			FlushPendingDamageReports();
            RefreshOpenCareerManager();
        }

		private void OnSettingsReceived(ClientBoundJunctionSettingsPacket packet)
        {
            if (packet == null)
                return;

            RepairMode hostRepairMode =
                RepairMode.Penalty;

            if (Enum.IsDefined(
                    typeof(RepairMode),
                    packet.RepairMode))
            {
                hostRepairMode =
                    (RepairMode)packet.RepairMode;
            }

            Settings hostSettings =
                new Settings
                {
                    logging = Main.LocalSettings != null && Main.LocalSettings.logging,
                    enableRandomFlip = packet.EnableRandomFlip,
                    safeNoFlipSpeedKmh = Mathf.Max(0f,packet.SafeNoFlipSpeedKmh),
                    flipMultiplierPercent = Mathf.Clamp(packet.FlipMultiplierPercent,0.01f,0.50f),
                    flipCooldownAfterForcedSec = Mathf.Max(0f,packet.FlipCooldownAfterForcedSec),
                    repairRadius = Mathf.Max(0f,packet.RepairRadius),
                    repairAmountPercent = Mathf.Clamp01(packet.RepairAmountPercent),
                    repairVehicleSearchRadius = Mathf.Max(0f,packet.RepairVehicleSearchRadius),
                    maxVehicleStandingSpeedKmh = Mathf.Max(0f,packet.MaxVehicleStandingSpeedKmh),
                    maxRepairCostFull = Mathf.Max(0f,packet.MaxRepairCostFull),
                    maxRepairRewardFull = Mathf.Max(0f,packet.MaxRepairRewardFull),
                    maintenanceLicensePrice = Mathf.Max(0f,packet.MaintenanceLicensePrice),
                    BlockManualSwitchAtFullDamage = packet.BlockManualSwitchAtFullDamage,
                    repairMode = hostRepairMode
                };

            Main.UseTemporaryHostSettings(hostSettings);
            MaintenanceLicense.ReapplyConfiguredPrice();

            Main.Log(
                "[MP] Temporary host settings applied.",
                true);

            RefreshOpenCareerManager();
        }

        private static void RefreshOpenCareerManager()
        {
            try
            {
                if (JM_CM_ListState.Active)
                {
                    JM_CM_ListState.Render();
                }
            }
            catch (Exception e)
            {
                Main.Log(
                    "[MP] Career Manager refresh failed: " + e,
                    true);
            }
        }

		private void OnDestroy()
        {
            Main.RestoreLocalSettings();
            MaintenanceLicense.ReapplyConfiguredPrice();

            if (Instance == this)
            {
                Instance = null;
            }

            _client = null;
            _registered = false;
            _readyPacketSent = false;
            _wasMultiplayerClient = false;
            _clearedForCurrentConnection = false;

            _authoritativeSnapshotReceived = false;
            _nextReadyRequestTime = 0f;
            _pendingDamageReports.Clear();
        }
    }

    // ============================================================
    // SERVER
    // ============================================================

    internal class JunctionMaintenanceMPServer : MonoBehaviour
    {
        public static JunctionMaintenanceMPServer Instance
        {
            get;
            private set;
        }

        private IServer _server;
        private bool _initialized;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else if (Instance != this)
            {
                Destroy(this);
                return;
            }

            TryInitializeServer();
        }

        private void Update()
        {
            if (!_initialized)
            {
                TryInitializeServer();
            }
        }

        private void TryInitializeServer()
        {
            if (_initialized)
            {
                return;
            }

            _server =
                MultiplayerAPI.Server;

            if (_server == null)
            {
                return;
            }

            // NEW: Client -> Server
            _server.RegisterPacket<ServerBoundJunctionDamagePacket>(
                OnDamageReported);

            _server.RegisterPacket<ServerBoundJunctionRepairPacket>(
                OnRepairReported);

            _server.RegisterPacket<ServerBoundJunctionReadyPacket>(
                OnClientReady);

            _initialized = true;

            Main.Log(
                "[MP] Server packet handlers registered.",
                true);
        }

        private void OnDamageReported(
            ServerBoundJunctionDamagePacket packet,
            IPlayer sender)
        {
            if (packet == null ||
                sender == null ||
                string.IsNullOrWhiteSpace(packet.JunctionKey))
            {
                return;
            }

            float damageToAdd =
                Mathf.Clamp01(packet.DamageToAdd);

            if (damageToAdd <= 0f)
            {
                return;
            }

            ApplyDamageAndBroadcast(
                packet.JunctionKey,
                damageToAdd);
        }

		private void OnRepairReported(
			ServerBoundJunctionRepairPacket packet,
			IPlayer sender)
		{
			if (packet == null ||
				sender == null ||
				string.IsNullOrWhiteSpace(packet.JunctionKey) ||
				Main.Settings == null)
			{
				return;
			}

			// NEW:
			// Ausschließlich der Host-Schadensstand ist autoritativ.
			float currentDamage =
				DamageStore.Get(
					packet.JunctionKey);

			if (currentDamage <= 0f)
			{
				Main.Log(
					$"[MP] Client repair rejected: " +
					$"{packet.JunctionKey} has no damage.",
					true);

				return;
			}

			// NEW:
			// Reparatur darf maximal dem Host-Setting entsprechen.
			float maximumRepair =
				Mathf.Clamp01(
					Main.Settings.repairAmountPercent);

			float repairAmount =
				Mathf.Clamp(
					packet.RepairAmount,
					0f,
					maximumRepair);

			// NEW:
			// Niemals mehr reparieren, als tatsächlich beschädigt ist.
			repairAmount =
				Mathf.Min(
					currentDamage,
					repairAmount);

			if (repairAmount <= 0f)
			{
				return;
			}

			// NEW:
			// Der Host bestimmt den echten Modus selbst.
			bool hostIsRewardMode =
				Main.Settings.repairMode == RepairMode.Reward ||
				(
					Main.Settings.repairMode == RepairMode.Dynamic &&
					MaintenanceLicense.HasLicense
				);

			// NEW:
			// Der Host berechnet den Betrag selbst.
			double hostCalculatedAmount;

			if (hostIsRewardMode)
			{
				hostCalculatedAmount =
					Math.Round(
						Math.Max(
							0.0,
							Main.Settings.maxRepairRewardFull) *
						repairAmount,
						2);
			}
			else
			{
				hostCalculatedAmount =
					Math.Round(
						Math.Max(
							0.0,
							Main.Settings.maxRepairCostFull) *
						repairAmount,
						2);
			}

			// NEW:
			// Client-Werte nur protokollieren, niemals autoritativ verwenden.
			if (packet.IsReward != hostIsRewardMode)
			{
				Main.Log(
					$"[MP] Repair mode mismatch for {packet.JunctionKey}: " +
					$"client={(packet.IsReward ? "EARN" : "PAY")}, " +
					$"host={(hostIsRewardMode ? "EARN" : "PAY")}. " +
					$"Host mode will be used.",
					true);
			}

			if (Math.Abs(
					packet.MoneyAmount -
					hostCalculatedAmount) > 0.01)
			{
				Main.Log(
					$"[MP] Repair amount mismatch for {packet.JunctionKey}: " +
					$"client=${packet.MoneyAmount:0.00}, " +
					$"host=${hostCalculatedAmount:0.00}. " +
					$"Host amount will be used.",
					true);
			}

			// NEW:
			// Nur wenn die Host-Geldtransaktion erfolgreich war,
			// wird die Reparatur tatsächlich angewendet.
			if (!ApplyHostMoneyTransaction(
					hostCalculatedAmount,
					hostIsRewardMode))
			{
				Main.Log(
					$"[MP] Client repair rejected because host money " +
					$"transaction failed: {packet.JunctionKey}.",
					true);

				return;
			}

			ApplyRepairAndBroadcast(
				packet.JunctionKey,
				repairAmount);
		}
		
		// NEW:
		// Führt die Geldtransaktion ausschließlich auf der Host-Wallet aus.
		// true  = Transaktion erfolgreich oder Betrag war 0
		// false = Inventory fehlt oder PAY-Betrag ist nicht verfügbar
		private static bool ApplyHostMoneyTransaction(
			double amount,
			bool isReward)
		{
			amount =
				Math.Round(
					Math.Max(
						0.0,
						amount),
					2);

			if (amount <= 0.0)
			{
				return true;
			}

			var inventory =
				DV.Utils.SingletonBehaviour<
					DV.InventorySystem.Inventory>.Instance;

			if (inventory == null)
			{
				Main.Log(
					"[MP] Host inventory is unavailable. " +
					"Money transaction was not applied.",
					true);

				return false;
			}

			if (isReward)
			{
				// EARN:
				// Belohnung direkt in die Wallet des Hosts.
				inventory.AddMoney(
					amount);

				Main.Log(
					$"[MP] Host wallet received ${amount:0.00} " +
					$"for a client junction repair. " +
					$"New balance=${inventory.PlayerMoney:0.00}",
					true);

				return true;
			}

			// PAY:
			// Erst prüfen, ob die Host-Wallet genügend Geld enthält.
			if (inventory.PlayerMoney + 0.001 < amount)
			{
				Main.Log(
					$"[MP] Host cannot pay for client junction repair. " +
					$"Required=${amount:0.00}, " +
					$"available=${inventory.PlayerMoney:0.00}",
					true);

				return false;
			}

			// CHANGE:
			// Geld korrekt von der Host-Wallet entfernen.
			bool removed =
				inventory.RemoveMoney(
					amount);

			if (!removed)
			{
				Main.Log(
					$"[MP] Host wallet payment failed. " +
					$"Required=${amount:0.00}, " +
					$"available=${inventory.PlayerMoney:0.00}",
					true);

				return false;
			}

			Main.Log(
				$"[MP] Host wallet paid ${amount:0.00} " +
				$"for a client junction repair. " +
				$"New balance=${inventory.PlayerMoney:0.00}",
				true);

			return true;
		}

        private void OnClientReady(
            ServerBoundJunctionReadyPacket packet,
            IPlayer sender)
        {
            if (packet == null ||
                sender == null ||
                !packet.Ready)
            {
                return;
            }

            // NEW:
            // Erst jetzt ist der Client vollständig geladen.
            SendSettingsToPlayer(sender);
            SendSnapshotToPlayer(sender);

            Main.Log(
                "[MP] Settings and junction snapshot sent " +
                "to fully loaded client.",
                true);
        }

        public void ApplyDamageAndBroadcast(
            string junctionKey,
            float damageToAdd)
        {
            if (string.IsNullOrWhiteSpace(junctionKey))
            {
                return;
            }

            float before =
                DamageStore.Get(junctionKey);

            float after =
                Mathf.Clamp01(
                    before + Mathf.Max(0f, damageToAdd));

            DamageStore.Set(
                junctionKey,
                after);

            BroadcastState(
                junctionKey,
                after);

            Main.Log(
                $"[MP] Damage applied: {junctionKey} " +
                $"{before * 100f:0.###}% -> " +
                $"{after * 100f:0.###}%",
                false);
        }

        public void ApplyRepairAndBroadcast(
            string junctionKey,
            float repairAmount)
        {
            if (string.IsNullOrWhiteSpace(junctionKey))
            {
                return;
            }

            float before =
                DamageStore.Get(junctionKey);

            float repaired =
                Mathf.Min(
                    before,
                    Mathf.Clamp01(repairAmount));

            float after =
                Mathf.Max(
                    0f,
                    before - repaired);

            DamageStore.Set(
                junctionKey,
                after);

            BroadcastState(
                junctionKey,
                after);

            Main.Log(
                $"[MP] Repair applied: {junctionKey} " +
                $"{before * 100f:0.###}% -> " +
                $"{after * 100f:0.###}%",
                false);
        }

        private void BroadcastState(
            string junctionKey,
            float damage)
        {
            if (!_initialized ||
                _server == null)
            {
                return;
            }

            var packet =
                new ClientBoundJunctionStatePacket
                {
                    JunctionKey = junctionKey,
                    Damage = Mathf.Clamp01(damage)
                };

            // Host hat den Zustand bereits lokal gesetzt.
            _server.SendPacketToAll(
                packet,
                reliable: true,
                excludeSelf: true);
        }

        public void SendSettingsToAll()
        {
            if (!_initialized ||
                _server == null ||
                Main.Settings == null)
            {
                return;
            }

            _server.SendPacketToAll(
                CreateSettingsPacket(),
                reliable: true,
                excludeSelf: true);
        }

        private void SendSettingsToPlayer(
            IPlayer player)
        {
            if (_server == null ||
                player == null)
            {
                return;
            }

            _server.SendPacketToPlayer(
                CreateSettingsPacket(),
                player,
                reliable: true);
        }

        private static ClientBoundJunctionSettingsPacket
            CreateSettingsPacket()
        {
            return new ClientBoundJunctionSettingsPacket
            {
                EnableRandomFlip =
                    Main.Settings.enableRandomFlip,

                SafeNoFlipSpeedKmh =
                    Main.Settings.safeNoFlipSpeedKmh,

                FlipMultiplierPercent =
                    Main.Settings.flipMultiplierPercent,

                FlipCooldownAfterForcedSec =
                    Main.Settings.flipCooldownAfterForcedSec,

                RepairRadius =
                    Main.Settings.repairRadius,

                RepairAmountPercent =
                    Main.Settings.repairAmountPercent,

                RepairVehicleSearchRadius =
                    Main.Settings.repairVehicleSearchRadius,

                MaxVehicleStandingSpeedKmh =
                    Main.Settings.maxVehicleStandingSpeedKmh,

                MaxRepairCostFull =
                    Main.Settings.maxRepairCostFull,

                MaxRepairRewardFull =
                    Main.Settings.maxRepairRewardFull,

                MaintenanceLicensePrice =
                    Main.Settings.maintenanceLicensePrice,

                BlockManualSwitchAtFullDamage =
                    Main.Settings.BlockManualSwitchAtFullDamage,

                RepairMode =
                    (int)Main.Settings.repairMode
            };
        }

        private void SendSnapshotToPlayer(
            IPlayer player)
        {
            if (_server == null ||
                player == null)
            {
                return;
            }

            Dictionary<string, float> map =
                DamageStore.All();

            string[] keys =
                new string[map.Count];

            float[] values =
                new float[map.Count];

            int index = 0;

            foreach (KeyValuePair<string, float> entry in map)
            {
                keys[index] =
                    entry.Key;

                values[index] =
                    Mathf.Clamp01(entry.Value);

                index++;
            }

            var packet =
                new ClientBoundJunctionSnapshotPacket
                {
                    JunctionKeys = keys,
                    DamageValues = values
                };

            _server.SendPacketToPlayer(
                packet,
                player,
                reliable: true);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            _server = null;
            _initialized = false;
        }
    }
}