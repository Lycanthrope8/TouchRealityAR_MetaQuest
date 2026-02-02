// ============================================================================
// FILE: GatewaySync.cs
// Main orchestrator for Unity <-> Gateway communication.
// Manages state, events, and provides API for UI components.
// 
// FIX: Now properly passes GatewayConfig to GatewayClient and GatewaySseClient
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// Main orchestrator for Gateway synchronization.
    /// Add this to a single GameObject in the scene.
    /// </summary>
    public class GatewaySync : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private GatewayConfig config;

        [Header("Components (auto-created if null)")]
        [SerializeField] private GatewayClient gatewayClient;
        [SerializeField] private GatewaySseClient sseClient;

        [Header("Dependencies")]
        [SerializeField] private MonoBehaviour siteFrameManager;

        [Header("Events")]
        public UnityEvent<string, ClaimStatus> OnClaimStatusChanged;
        public UnityEvent OnGatewayConnected;
        public UnityEvent OnGatewayDisconnected;
        public UnityEvent<string> OnGatewayError;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;
        [SerializeField] private bool showDebugGUI = false;

        // State
        private AnchorClaimStateManager stateManager;
        private bool isInitialized = false;

        // Pending proposals (local state before gateway response)
        private Dictionary<string, float> pendingProposals = new Dictionary<string, float>();
        private const float PENDING_TIMEOUT_SECONDS = 30f;

        // Singleton
        private static GatewaySync instance;
        public static GatewaySync Instance => instance;

        // Public accessors
        public bool IsConnected => sseClient != null && sseClient.IsConnected;
        public AnchorClaimStateManager StateManager => stateManager;
        public GatewayClient Client => gatewayClient;
        public GatewayConfig Config => config;

        private void Awake()
        {
            // Singleton
            if (instance != null && instance != this)
            {
                Debug.LogWarning("[GatewaySync] Duplicate instance found, destroying...");
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            Initialize();
        }

        private void Initialize()
        {
            if (isInitialized)
                return;

            // Check for config first
            if (config == null)
            {
                Debug.LogError("[GatewaySync] GatewayConfig not assigned in Inspector! Please assign a GatewayConfig asset.");
                // Try to find one in Resources as fallback
                config = Resources.Load<GatewayConfig>("GatewayConfig");
                if (config == null)
                {
                    Debug.LogError("[GatewaySync] Could not find GatewayConfig in Resources either. Gateway will not function.");
                }
            }

            // Create state manager
            stateManager = new AnchorClaimStateManager();
            stateManager.OnStateChanged += HandleStateChanged;

            // Find or create GatewayClient
            if (gatewayClient == null)
            {
                gatewayClient = GetComponent<GatewayClient>();
                if (gatewayClient == null)
                {
                    gatewayClient = gameObject.AddComponent<GatewayClient>();
                }
            }
            // Pass config to GatewayClient
            if (config != null)
            {
                gatewayClient.SetConfig(config);
            }

            // Find or create SSE client
            if (sseClient == null)
            {
                sseClient = GetComponent<GatewaySseClient>();
                if (sseClient == null)
                {
                    sseClient = gameObject.AddComponent<GatewaySseClient>();
                }
            }
            // Pass config to SSE client
            if (config != null)
            {
                sseClient.SetConfig(config);
            }

            // Configure SSE client
            sseClient.SetStateManager(stateManager);
            sseClient.SetGatewayClient(gatewayClient);
            sseClient.OnConnected += HandleSseConnected;
            sseClient.OnDisconnected += HandleSseDisconnected;
            sseClient.OnEventReceived += HandleSseEvent;
            sseClient.OnError += HandleSseError;

            // Find SiteFrameManager if not assigned
            if (siteFrameManager == null)
            {
                var sfmType = Type.GetType("ARObjectDetection.SiteFrameManager, Assembly-CSharp");
                if (sfmType != null)
                {
                    siteFrameManager = FindFirstObjectByType(sfmType) as MonoBehaviour;
                }
            }

            isInitialized = true;

            if (config != null)
            {
                Debug.Log($"[GatewaySync] Initialized with config: {config.gatewayBaseUrl}");
            }
            else
            {
                Debug.LogWarning("[GatewaySync] Initialized WITHOUT config - assign GatewayConfig in Inspector!");
            }
        }

        private void OnDestroy()
        {
            if (stateManager != null)
            {
                stateManager.OnStateChanged -= HandleStateChanged;
            }

            if (sseClient != null)
            {
                sseClient.OnConnected -= HandleSseConnected;
                sseClient.OnDisconnected -= HandleSseDisconnected;
                sseClient.OnEventReceived -= HandleSseEvent;
                sseClient.OnError -= HandleSseError;
            }

            if (instance == this)
            {
                instance = null;
            }
        }

        private void Update()
        {
            // Check for pending proposal timeouts
            List<string> timedOut = new List<string>();
            float now = Time.realtimeSinceStartup;

            foreach (var kvp in pendingProposals)
            {
                if (now - kvp.Value > PENDING_TIMEOUT_SECONDS)
                {
                    timedOut.Add(kvp.Key);
                }
            }

            foreach (string assetId in timedOut)
            {
                pendingProposals.Remove(assetId);
                var state = stateManager.GetState(assetId);
                if (state != null && state.status == ClaimStatus.Pending)
                {
                    Debug.LogWarning($"[GatewaySync] Proposal timeout for {assetId} - no commit confirmation received");
                    // Keep in Pending state - don't fake a success
                }
            }
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        /// <summary>
        /// Propose an anchor claim for an asset.
        /// </summary>
        /// <param name="assetId">Asset ID (e.g., "laptop_TAG_5")</param>
        /// <param name="worldPose">World pose of the anchor</param>
        /// <param name="confidence">Detection confidence</param>
        /// <param name="stabilityRms">Stability RMS (pose jitter)</param>
        /// <param name="observationCount">Number of observations</param>
        /// <returns>True if proposal was initiated</returns>
        public bool ProposeAnchor(string assetId, Pose worldPose, float confidence, float stabilityRms, int observationCount)
        {
            if (string.IsNullOrEmpty(assetId))
            {
                Debug.LogWarning("[GatewaySync] Cannot propose - empty assetId");
                return false;
            }

            if (config == null)
            {
                Debug.LogError("[GatewaySync] Cannot propose - GatewayConfig not assigned!");
                OnGatewayError?.Invoke("GatewayConfig not assigned");
                return false;
            }

            if (gatewayClient == null)
            {
                Debug.LogError("[GatewaySync] Cannot propose - GatewayClient not available!");
                OnGatewayError?.Invoke("GatewayClient not available");
                return false;
            }

            // Check if already pending
            if (pendingProposals.ContainsKey(assetId))
            {
                Debug.LogWarning($"[GatewaySync] Proposal already pending for {assetId}");
                return false;
            }

            // Check current state
            var currentState = stateManager.GetState(assetId);
            if (currentState != null && !currentState.CanPropose)
            {
                Debug.LogWarning($"[GatewaySync] Cannot propose {assetId} - current status is {currentState.status}");
                return false;
            }

            // Convert world pose to site pose if SiteFrame is available
            Pose sitePose = worldPose;
            if (siteFrameManager != null)
            {
                try
                {
                    var isValidProp = siteFrameManager.GetType().GetProperty("IsValid");
                    if (isValidProp != null && (bool)isValidProp.GetValue(siteFrameManager))
                    {
                        var method = siteFrameManager.GetType().GetMethod("SitePoseFromWorldPose");
                        if (method != null)
                        {
                            sitePose = (Pose)method.Invoke(siteFrameManager, new object[] { worldPose });
                            if (enableDebugLogs)
                            {
                                Debug.Log($"[GatewaySync] Converted world pose to site pose: {sitePose.position}");
                            }
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[GatewaySync] SiteFrame not valid - using world pose directly");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GatewaySync] Failed to convert pose: {e.Message}");
                }
            }

            // Create request data
            var poseSite = GatewayClient.CreatePoseSite(sitePose);
            var qualityMetrics = GatewayClient.CreateQualityMetrics(confidence, stabilityRms, observationCount);

            // Mark as pending locally
            stateManager.SetPending(assetId);
            pendingProposals[assetId] = Time.realtimeSinceStartup;

            Debug.Log($"[GatewaySync] Proposing anchor for {assetId}...");

            // Send to gateway
            gatewayClient.ProposeAnchor(assetId, poseSite, qualityMetrics,
                response =>
                {
                    // Success - update state with claimId (still Pending until SSE confirms)
                    var state = stateManager.GetOrCreateState(assetId);
                    state.claimId = response.claim_id;
                    state.conflictClassification = response.conflict_classification;
                    // Note: We do NOT set status to Proposed here - wait for SSE event

                    Debug.Log($"[GatewaySync] ✓ Propose submitted: claimId={response.claim_id}, awaiting commit confirmation...");
                },
                error =>
                {
                    // Error - revert to None
                    pendingProposals.Remove(assetId);
                    var state = stateManager.GetState(assetId);
                    if (state != null && state.status == ClaimStatus.Pending)
                    {
                        state.status = ClaimStatus.None;
                    }

                    Debug.LogError($"[GatewaySync] Propose failed for {assetId}: {error}");
                    OnGatewayError?.Invoke(error);
                }
            );

            return true;
        }

        /// <summary>
        /// Get the claim status for an asset.
        /// </summary>
        public ClaimStatus GetClaimStatus(string assetId)
        {
            var state = stateManager.GetState(assetId);
            return state?.status ?? ClaimStatus.None;
        }

        /// <summary>
        /// Get the full claim state for an asset.
        /// </summary>
        public AnchorClaimState GetClaimState(string assetId)
        {
            return stateManager.GetState(assetId);
        }

        /// <summary>
        /// Check if an asset can be proposed.
        /// </summary>
        public bool CanPropose(string assetId)
        {
            if (string.IsNullOrEmpty(assetId))
                return false;

            if (config == null)
                return false;

            if (pendingProposals.ContainsKey(assetId))
                return false;

            var state = stateManager.GetState(assetId);
            if (state == null)
                return true;

            return state.CanPropose;
        }

        /// <summary>
        /// Force refresh state from gateway snapshot.
        /// </summary>
        public void RefreshFromSnapshot()
        {
            if (gatewayClient != null)
            {
                gatewayClient.FetchSnapshot(
                    snapshot =>
                    {
                        stateManager.ApplySnapshot(snapshot.assets);
                        Debug.Log($"[GatewaySync] Snapshot applied: {snapshot.assets?.Count ?? 0} assets");
                    },
                    error =>
                    {
                        Debug.LogError($"[GatewaySync] Snapshot failed: {error}");
                    }
                );
            }
        }

        /// <summary>
        /// Clear all local state (for testing/reset).
        /// </summary>
        public void ClearLocalState()
        {
            stateManager.ClearAll();
            pendingProposals.Clear();
            Debug.Log("[GatewaySync] Local state cleared");
        }

        // ============================================================
        // EVENT HANDLERS
        // ============================================================

        private void HandleStateChanged(string assetId, AnchorClaimState state)
        {
            // Remove from pending if no longer pending
            if (state.status != ClaimStatus.Pending)
            {
                pendingProposals.Remove(assetId);
            }

            // Notify listeners
            OnClaimStatusChanged?.Invoke(assetId, state.status);

            if (enableDebugLogs)
            {
                Debug.Log($"[GatewaySync] State changed: {assetId} -> {state.status}");
            }
        }

        private void HandleSseConnected()
        {
            Debug.Log("[GatewaySync] Gateway SSE connected");
            OnGatewayConnected?.Invoke();
        }

        private void HandleSseDisconnected()
        {
            Debug.Log("[GatewaySync] Gateway SSE disconnected");
            OnGatewayDisconnected?.Invoke();
        }

        private void HandleSseEvent(GatewayEvent evt)
        {
            // Events are already applied to state manager by SSE client
            // This is just for additional handling if needed

            if (enableDebugLogs)
            {
                Debug.Log($"[GatewaySync] SSE Event: {evt.type} for {evt.assetId}");
            }
        }

        private void HandleSseError(string error)
        {
            Debug.LogError($"[GatewaySync] SSE Error: {error}");
            OnGatewayError?.Invoke(error);
        }

        // ============================================================
        // DEBUG GUI
        // ============================================================

        private void OnGUI()
        {
            if (!showDebugGUI)
                return;

            GUILayout.BeginArea(new Rect(10, 600, 400, 200));
            GUILayout.Label("=== GATEWAY SYNC ===");

            string connStatus = IsConnected ? "<color=green>CONNECTED</color>" : "<color=red>DISCONNECTED</color>";
            GUILayout.Label($"Status: {connStatus}");
            GUILayout.Label($"Config: {(config != null ? config.gatewayBaseUrl : "NOT SET")}");

            if (sseClient != null)
            {
                GUILayout.Label($"Last event: {sseClient.SecondsSinceLastEvent:F1}s ago");
            }

            GUILayout.Label($"Pending proposals: {pendingProposals.Count}");

            // Show recent states
            var states = stateManager?.GetAllStates();
            if (states != null)
            {
                int count = 0;
                foreach (var state in states)
                {
                    if (count++ >= 5) break;
                    GUILayout.Label($"  {state.assetId}: {state.status}");
                }
            }

            GUILayout.EndArea();
        }
    }
}