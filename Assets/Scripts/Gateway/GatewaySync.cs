// ============================================================================
// FILE: GatewaySync.cs
// Main orchestrator for Unity <-> Gateway communication
//
// PHASE 6: Annotation service fully removed. AnnotationStateManager, the
// annotation events, RequestAnnotation/GetAnnotationState/etc., and the
// annotation snapshot-restore are deleted. Anchor propose/revoke and SSE-driven
// claim-state tracking are unchanged. The LLM-mediated flow lives in
// SkillFlowController + SkillGatewayClient (separate components).
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

        [Header("Revocation Events")]
        public UnityEvent<string, string> OnRevokePending;
        public UnityEvent<string> OnRevokeCompleted;
        public UnityEvent<string, string> OnRevokeRejected;

        // Annotation events REMOVED in Phase 6.

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;

        private AnchorClaimStateManager stateManager;
        private bool isInitialized = false;
        private Dictionary<string, float> pendingProposals = new Dictionary<string, float>();
        private Dictionary<string, float> pendingRevokes = new Dictionary<string, float>();
        private const float PENDING_TIMEOUT_SECONDS = 30f;

        private static GatewaySync instance;
        public static GatewaySync Instance => instance;

        public bool IsConnected => sseClient != null && sseClient.IsConnected;
        public AnchorClaimStateManager StateManager => stateManager;
        public GatewayClient Client => gatewayClient;
        public GatewayConfig Config => config;
        public string MyOrgId => config?.organizationId ?? "org1";
        public string MyMspId => config?.MspId ?? "Org1MSP";

        private void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start() => Initialize();

        private void Initialize()
        {
            if (isInitialized) return;
            if (config == null) { Debug.LogError("[GatewaySync] GatewayConfig not assigned!"); return; }

            Debug.Log($"[GatewaySync] Initializing as {config.organizationName} ({config.MspId})");

            stateManager = new AnchorClaimStateManager();
            stateManager.OnStateChanged += HandleStateChanged;
            stateManager.OnRevokePending += HandleRevokePending;
            stateManager.OnRevoked += HandleRevoked;

            if (gatewayClient == null) gatewayClient = GetComponent<GatewayClient>() ?? gameObject.AddComponent<GatewayClient>();
            gatewayClient.Initialize(config);

            if (sseClient == null) sseClient = GetComponent<GatewaySseClient>() ?? gameObject.AddComponent<GatewaySseClient>();
            sseClient.Initialize(config, stateManager, gatewayClient);
            sseClient.OnConnected += HandleSseConnected;
            sseClient.OnDisconnected += HandleSseDisconnected;
            sseClient.OnEventReceived += HandleSseEvent;
            sseClient.OnError += HandleSseError;

            isInitialized = true;
            Debug.Log($"[GatewaySync] ✓ Initialized as {config.organizationName}");
            sseClient.Connect();
        }

        private void OnDestroy()
        {
            if (stateManager != null) { stateManager.OnStateChanged -= HandleStateChanged; stateManager.OnRevokePending -= HandleRevokePending; stateManager.OnRevoked -= HandleRevoked; }
            if (sseClient != null) { sseClient.OnConnected -= HandleSseConnected; sseClient.OnDisconnected -= HandleSseDisconnected; sseClient.OnEventReceived -= HandleSseEvent; sseClient.OnError -= HandleSseError; }
            if (instance == this) instance = null;
        }

        private void Update()
        {
            if (!isInitialized) return;
            float now = Time.realtimeSinceStartup;
            CheckTimeouts(pendingProposals, now, "Proposal");
            CheckTimeouts(pendingRevokes, now, "Revoke");
        }

        private void CheckTimeouts(Dictionary<string, float> pending, float now, string opType)
        {
            List<string> timedOut = new List<string>();
            foreach (var kvp in pending)
                if (now - kvp.Value > PENDING_TIMEOUT_SECONDS) timedOut.Add(kvp.Key);
            foreach (string id in timedOut)
            {
                pending.Remove(id);
                Debug.LogWarning($"[GatewaySync] {opType} timeout for {id}");
            }
        }

        // =====================================================================
        // ANCHOR OPERATIONS (unchanged)
        // =====================================================================

        public bool ProposeAnchor(string assetId, Pose worldPose, float confidence, float stabilityRms, int observationCount)
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized || config == null) return false;
            if (pendingProposals.ContainsKey(assetId)) return false;
            var currentState = stateManager.GetState(assetId);
            if (currentState != null && !currentState.CanPropose) return false;

            Pose sitePose = ConvertToSitePose(worldPose);
            var poseSite = GatewayClient.CreatePoseSite(sitePose);
            var qualityMetrics = GatewayClient.CreateQualityMetrics(confidence, stabilityRms, observationCount);

            stateManager.SetPending(assetId);
            pendingProposals[assetId] = Time.realtimeSinceStartup;

            gatewayClient.ProposeAnchor(assetId, poseSite, qualityMetrics,
                response => { var state = stateManager.GetOrCreateState(assetId); state.claimId = response.claim_id; },
                error => { pendingProposals.Remove(assetId); OnGatewayError?.Invoke(error); });
            return true;
        }

        public bool RevokeAnchor(string assetId, string reason = null)
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized) return false;
            var state = stateManager.GetState(assetId);
            if (state == null || !state.CanRevoke || pendingRevokes.ContainsKey(assetId)) return false;

            pendingRevokes[assetId] = Time.realtimeSinceStartup;
            gatewayClient.RevokeAnchor(assetId, reason,
                response => Debug.Log($"[GatewaySync] Revoke initiated, awaiting {response.required_endorser}"),
                error => { pendingRevokes.Remove(assetId); OnGatewayError?.Invoke(error); });
            return true;
        }

        public bool EndorseRevoke(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            var state = stateManager.GetState(assetId);
            if (state == null || !state.IsRevokePending || !state.RequiresMyAction(MyMspId)) return false;
            gatewayClient.EndorseRevoke(assetId, response => OnRevokeCompleted?.Invoke(assetId), error => OnGatewayError?.Invoke(error));
            return true;
        }

        public bool RejectRevoke(string assetId, string reason = null)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            var state = stateManager.GetState(assetId);
            if (state == null || !state.IsRevokePending || !state.RequiresMyAction(MyMspId)) return false;
            gatewayClient.RejectRevoke(assetId, reason, response => OnRevokeRejected?.Invoke(assetId, MyMspId), error => OnGatewayError?.Invoke(error));
            return true;
        }

        // =====================================================================
        // ANNOTATION OPERATIONS — REMOVED in Phase 6.
        // The LLM-mediated flow lives in SkillFlowController + SkillGatewayClient.
        // =====================================================================

        // =====================================================================
        // QUERY HELPERS
        // =====================================================================

        public IEnumerable<AnchorClaimState> GetPendingRevocationsRequiringMyAction()
            => stateManager.GetPendingRevocationsRequiringAction(MyMspId);

        public ClaimStatus GetClaimStatus(string assetId)
            => stateManager?.GetState(assetId)?.status ?? ClaimStatus.None;

        public AnchorClaimState GetClaimState(string assetId)
            => stateManager?.GetState(assetId);

        public bool CanPropose(string assetId)
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized || pendingProposals.ContainsKey(assetId)) return false;
            var state = stateManager.GetState(assetId);
            return state == null || state.CanPropose;
        }

        public bool CanRevoke(string assetId)
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized) return false;
            var state = stateManager.GetState(assetId);
            return state != null && state.CanRevoke;
        }

        // =====================================================================
        // POSE CONVERSION
        // =====================================================================

        private Pose ConvertToSitePose(Pose worldPose)
        {
            if (siteFrameManager == null) return worldPose;
            try
            {
                var isValidProp = siteFrameManager.GetType().GetProperty("IsValid");
                if (isValidProp != null && (bool)isValidProp.GetValue(siteFrameManager))
                {
                    var method = siteFrameManager.GetType().GetMethod("SitePoseFromWorldPose");
                    if (method != null) return (Pose)method.Invoke(siteFrameManager, new object[] { worldPose });
                }
            }
            catch (Exception e) { Debug.LogWarning($"[GatewaySync] Pose conversion failed: {e.Message}"); }
            return worldPose;
        }

        // =====================================================================
        // EVENT HANDLERS - ANCHORS
        // =====================================================================

        private void HandleStateChanged(string assetId, AnchorClaimState state)
        {
            if (state.status != ClaimStatus.Pending) pendingProposals.Remove(assetId);
            if (state.status != ClaimStatus.RevokePending) pendingRevokes.Remove(assetId);
            OnClaimStatusChanged?.Invoke(assetId, state.status);
        }

        private void HandleRevokePending(string assetId, AnchorClaimState state)
        {
            if (state.RequiresMyAction(MyMspId)) Debug.Log($"[GatewaySync] ACTION REQUIRED: Endorse or reject revocation of {assetId}");
            OnRevokePending?.Invoke(assetId, state.revokeInitiatedBy);
        }

        private void HandleRevoked(string assetId, AnchorClaimState state) => OnRevokeCompleted?.Invoke(assetId);

        // Annotation event handlers REMOVED in Phase 6.

        // =====================================================================
        // SSE HANDLERS
        // =====================================================================

        private void HandleSseConnected()
        {
            Debug.Log($"[GatewaySync] ✓ Connected as {config.organizationName}");
            OnGatewayConnected?.Invoke();
            FetchSnapshotForReconnect();
            if (config.autoProcessRevocations) CheckPendingRevocations();
        }

        private void HandleSseDisconnected() => OnGatewayDisconnected?.Invoke();
        private void HandleSseEvent(GatewayEvent evt) { }
        private void HandleSseError(string error) => OnGatewayError?.Invoke(error);

        /// <summary>
        /// Fetch snapshot on connect/reconnect. Phase 6: anchor claim states are
        /// restored by the SSE replay / state manager; annotation restore removed.
        /// </summary>
        private void FetchSnapshotForReconnect()
        {
            if (!config.fetchSnapshotOnReconnect) return;
            gatewayClient.FetchSnapshot(
                response =>
                {
                    if (response != null && response.success)
                        Debug.Log("[GatewaySync] Snapshot fetched on reconnect.");
                },
                error => Debug.LogWarning($"[GatewaySync] Snapshot fetch failed: {error}"));
        }

        private void CheckPendingRevocations()
        {
            gatewayClient.GetPendingRevocationsForMe(
                response => { if (response.count > 0) Debug.Log($"[GatewaySync] {response.count} pending revocation(s) require action"); },
                error => { });
        }
    }
}