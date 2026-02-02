// ============================================================================
// FILE: GatewaySync.cs
// Main orchestrator for Unity <-> Gateway communication.
// EP5 FIX: Bulletproof config propagation, explicit SSE connect call
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ARObjectDetection.Gateway
{
    public class GatewaySync : MonoBehaviour
    {
        [Header("Configuration (REQUIRED)")]
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

        private AnchorClaimStateManager stateManager;
        private bool isInitialized = false;
        private Dictionary<string, float> pendingProposals = new Dictionary<string, float>();
        private const float PENDING_TIMEOUT_SECONDS = 30f;

        private static GatewaySync instance;
        public static GatewaySync Instance => instance;

        public bool IsConnected => sseClient != null && sseClient.IsConnected;
        public AnchorClaimStateManager StateManager => stateManager;
        public GatewayClient Client => gatewayClient;
        public GatewayConfig Config => config;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Debug.LogWarning("[GatewaySync] Duplicate instance found, destroying...");
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            // Initialize in Start to ensure all components are ready
            Initialize();
        }

        private void Initialize()
        {
            if (isInitialized) return;

            // CRITICAL: Check config FIRST
            if (config == null)
            {
                Debug.LogError("[GatewaySync] ERROR: GatewayConfig not assigned in Inspector! Drag your GatewayConfig ScriptableObject to the Config field.");
                return;
            }

            Debug.Log($"[GatewaySync] Initializing with config: {config.gatewayBaseUrl}");

            // Create state manager
            stateManager = new AnchorClaimStateManager();
            stateManager.OnStateChanged += HandleStateChanged;

            // Create or get GatewayClient
            if (gatewayClient == null)
            {
                gatewayClient = GetComponent<GatewayClient>();
                if (gatewayClient == null)
                {
                    gatewayClient = gameObject.AddComponent<GatewayClient>();
                }
            }
            // MUST set config immediately after ensuring component exists
            gatewayClient.Initialize(config);
            Debug.Log("[GatewaySync] GatewayClient initialized");

            // Create or get SSE client
            if (sseClient == null)
            {
                sseClient = GetComponent<GatewaySseClient>();
                if (sseClient == null)
                {
                    sseClient = gameObject.AddComponent<GatewaySseClient>();
                }
            }
            // Configure SSE client with all dependencies
            sseClient.Initialize(config, stateManager, gatewayClient);
            sseClient.OnConnected += HandleSseConnected;
            sseClient.OnDisconnected += HandleSseDisconnected;
            sseClient.OnEventReceived += HandleSseEvent;
            sseClient.OnError += HandleSseError;
            Debug.Log("[GatewaySync] GatewaySseClient initialized");

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
            Debug.Log($"[GatewaySync] ✓ Fully initialized. Gateway: {config.gatewayBaseUrl}");

            // NOW start SSE connection (after everything is wired up)
            sseClient.Connect();
        }

        private void OnDestroy()
        {
            if (stateManager != null)
                stateManager.OnStateChanged -= HandleStateChanged;

            if (sseClient != null)
            {
                sseClient.OnConnected -= HandleSseConnected;
                sseClient.OnDisconnected -= HandleSseDisconnected;
                sseClient.OnEventReceived -= HandleSseEvent;
                sseClient.OnError -= HandleSseError;
            }

            if (instance == this)
                instance = null;
        }

        private void Update()
        {
            if (!isInitialized) return;

            List<string> timedOut = new List<string>();
            float now = Time.realtimeSinceStartup;

            foreach (var kvp in pendingProposals)
            {
                if (now - kvp.Value > PENDING_TIMEOUT_SECONDS)
                    timedOut.Add(kvp.Key);
            }

            foreach (string assetId in timedOut)
            {
                pendingProposals.Remove(assetId);
                var state = stateManager.GetState(assetId);
                if (state != null && state.status == ClaimStatus.Pending)
                {
                    Debug.LogWarning($"[GatewaySync] Proposal timeout for {assetId} - no commit confirmation received");
                }
            }
        }

        public bool ProposeAnchor(string assetId, Pose worldPose, float confidence, float stabilityRms, int observationCount)
        {
            if (string.IsNullOrEmpty(assetId))
            {
                Debug.LogWarning("[GatewaySync] Cannot propose - empty assetId");
                return false;
            }

            if (!isInitialized || config == null)
            {
                Debug.LogError("[GatewaySync] Cannot propose - not initialized or config missing!");
                OnGatewayError?.Invoke("GatewaySync not initialized");
                return false;
            }

            if (pendingProposals.ContainsKey(assetId))
            {
                Debug.LogWarning($"[GatewaySync] Proposal already pending for {assetId}");
                return false;
            }

            var currentState = stateManager.GetState(assetId);
            if (currentState != null && !currentState.CanPropose)
            {
                Debug.LogWarning($"[GatewaySync] Cannot propose {assetId} - current status is {currentState.status}");
                return false;
            }

            // Convert world pose to site pose if available
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
                            Debug.Log($"[GatewaySync] Converted world pose to site pose: {sitePose.position}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GatewaySync] Failed to convert pose: {e.Message}");
                }
            }

            var poseSite = GatewayClient.CreatePoseSite(sitePose);
            var qualityMetrics = GatewayClient.CreateQualityMetrics(confidence, stabilityRms, observationCount);

            stateManager.SetPending(assetId);
            pendingProposals[assetId] = Time.realtimeSinceStartup;

            Debug.Log($"[GatewaySync] Proposing anchor for {assetId}...");

            gatewayClient.ProposeAnchor(assetId, poseSite, qualityMetrics,
                response =>
                {
                    var state = stateManager.GetOrCreateState(assetId);
                    state.claimId = response.claim_id;
                    state.conflictClassification = response.conflict_classification;
                    Debug.Log($"[GatewaySync] ✓ Propose submitted: claimId={response.claim_id}, awaiting commit confirmation...");
                },
                error =>
                {
                    pendingProposals.Remove(assetId);
                    var state = stateManager.GetState(assetId);
                    if (state != null && state.status == ClaimStatus.Pending)
                        state.status = ClaimStatus.None;
                    Debug.LogError($"[GatewaySync] Propose failed for {assetId}: {error}");
                    OnGatewayError?.Invoke(error);
                }
            );

            return true;
        }

        public ClaimStatus GetClaimStatus(string assetId)
        {
            var state = stateManager?.GetState(assetId);
            return state?.status ?? ClaimStatus.None;
        }

        public AnchorClaimState GetClaimState(string assetId)
        {
            return stateManager?.GetState(assetId);
        }

        public bool CanPropose(string assetId)
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized)
                return false;
            if (pendingProposals.ContainsKey(assetId))
                return false;
            var state = stateManager.GetState(assetId);
            return state == null || state.CanPropose;
        }

        private void HandleStateChanged(string assetId, AnchorClaimState state)
        {
            if (state.status != ClaimStatus.Pending)
                pendingProposals.Remove(assetId);
            OnClaimStatusChanged?.Invoke(assetId, state.status);
            if (enableDebugLogs)
                Debug.Log($"[GatewaySync] State changed: {assetId} -> {state.status}");
        }

        private void HandleSseConnected()
        {
            Debug.Log("[GatewaySync] ✓ Gateway SSE connected");
            OnGatewayConnected?.Invoke();
        }

        private void HandleSseDisconnected()
        {
            Debug.Log("[GatewaySync] Gateway SSE disconnected");
            OnGatewayDisconnected?.Invoke();
        }

        private void HandleSseEvent(GatewayEvent evt)
        {
            if (enableDebugLogs)
                Debug.Log($"[GatewaySync] SSE Event: {evt.type} for {evt.assetId}");
        }

        private void HandleSseError(string error)
        {
            Debug.LogError($"[GatewaySync] SSE Error: {error}");
            OnGatewayError?.Invoke(error);
        }
    }
}