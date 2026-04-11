// ============================================================================
// FILE: GatewaySync.cs
// Main orchestrator for Unity <-> Gateway communication
// Updated for Two-Org Model with revocation workflow support
// v2.0: Added annotation request + state management
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

        [Header("Annotation Events (v2.0)")]
        public UnityEvent<string, AnnotationStatus> OnAnnotationStatusChanged;
        public UnityEvent<string, string> OnAnnotationActive;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;

        private AnchorClaimStateManager stateManager;
        private AnnotationStateManager annotationStateManager;
        private bool isInitialized = false;
        private Dictionary<string, float> pendingProposals = new Dictionary<string, float>();
        private Dictionary<string, float> pendingRevokes = new Dictionary<string, float>();
        private Dictionary<string, float> pendingAnnotationRequests = new Dictionary<string, float>();
        private const float PENDING_TIMEOUT_SECONDS = 30f;

        private static GatewaySync instance;
        public static GatewaySync Instance => instance;

        public bool IsConnected => sseClient != null && sseClient.IsConnected;
        public AnchorClaimStateManager StateManager => stateManager;
        public AnnotationStateManager AnnotationManager => annotationStateManager;
        public GatewayClient Client => gatewayClient;
        public GatewayConfig Config => config;
        public string MyOrgId => config?.organizationId ?? "org1";
        public string MyMspId => config?.MspId ?? "Org1MSP";

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start() => Initialize();

        private void Initialize()
        {
            if (isInitialized) return;
            if (config == null)
            {
                Debug.LogError("[GatewaySync] GatewayConfig not assigned!");
                return;
            }

            Debug.Log($"[GatewaySync] Initializing as {config.organizationName} ({config.MspId})");

            // Anchor state manager
            stateManager = new AnchorClaimStateManager();
            stateManager.OnStateChanged += HandleStateChanged;
            stateManager.OnRevokePending += HandleRevokePending;
            stateManager.OnRevoked += HandleRevoked;

            // Annotation state manager (v2.0)
            annotationStateManager = new AnnotationStateManager();
            annotationStateManager.OnStateChanged += HandleAnnotationStateChanged;
            annotationStateManager.OnAnnotationActive += HandleAnnotationActive;

            // Gateway client
            if (gatewayClient == null)
            {
                gatewayClient = GetComponent<GatewayClient>() ?? gameObject.AddComponent<GatewayClient>();
            }
            gatewayClient.Initialize(config);

            // SSE client
            if (sseClient == null)
            {
                sseClient = GetComponent<GatewaySseClient>() ?? gameObject.AddComponent<GatewaySseClient>();
            }
            sseClient.Initialize(config, stateManager, gatewayClient);
            sseClient.SetAnnotationStateManager(annotationStateManager);
            sseClient.OnConnected += HandleSseConnected;
            sseClient.OnDisconnected += HandleSseDisconnected;
            sseClient.OnEventReceived += HandleSseEvent;
            sseClient.OnError += HandleSseError;

            isInitialized = true;
            Debug.Log($"[GatewaySync] ✓ Initialized as {config.organizationName} (annotations enabled)");
            sseClient.Connect();
        }

        private void OnDestroy()
        {
            if (stateManager != null)
            {
                stateManager.OnStateChanged -= HandleStateChanged;
                stateManager.OnRevokePending -= HandleRevokePending;
                stateManager.OnRevoked -= HandleRevoked;
            }
            if (annotationStateManager != null)
            {
                annotationStateManager.OnStateChanged -= HandleAnnotationStateChanged;
                annotationStateManager.OnAnnotationActive -= HandleAnnotationActive;
            }
            if (sseClient != null)
            {
                sseClient.OnConnected -= HandleSseConnected;
                sseClient.OnDisconnected -= HandleSseDisconnected;
                sseClient.OnEventReceived -= HandleSseEvent;
                sseClient.OnError -= HandleSseError;
            }
            if (instance == this) instance = null;
        }

        private void Update()
        {
            if (!isInitialized) return;
            float now = Time.realtimeSinceStartup;
            CheckTimeouts(pendingProposals, now, "Proposal");
            CheckTimeouts(pendingRevokes, now, "Revoke");
            CheckTimeouts(pendingAnnotationRequests, now, "Annotation");
        }

        private void CheckTimeouts(Dictionary<string, float> pending, float now, string opType)
        {
            List<string> timedOut = new List<string>();
            foreach (var kvp in pending)
                if (now - kvp.Value > PENDING_TIMEOUT_SECONDS)
                    timedOut.Add(kvp.Key);
            foreach (string id in timedOut)
            {
                pending.Remove(id);
                Debug.LogWarning($"[GatewaySync] {opType} timeout for {id}");
            }
        }

        // =====================================================================
        // ANCHOR OPERATIONS (existing)
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
                response =>
                {
                    var state = stateManager.GetOrCreateState(assetId);
                    state.claimId = response.claim_id;
                },
                error =>
                {
                    pendingProposals.Remove(assetId);
                    OnGatewayError?.Invoke(error);
                });

            return true;
        }

        public bool RevokeAnchor(string assetId, string reason = null)
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized) return false;
            var state = stateManager.GetState(assetId);
            if (state == null || !state.CanRevoke) return false;
            if (pendingRevokes.ContainsKey(assetId)) return false;

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

            gatewayClient.EndorseRevoke(assetId,
                response => OnRevokeCompleted?.Invoke(assetId),
                error => OnGatewayError?.Invoke(error));
            return true;
        }

        public bool RejectRevoke(string assetId, string reason = null)
        {
            if (string.IsNullOrEmpty(assetId)) return false;
            var state = stateManager.GetState(assetId);
            if (state == null || !state.IsRevokePending || !state.RequiresMyAction(MyMspId)) return false;

            gatewayClient.RejectRevoke(assetId, reason,
                response => OnRevokeRejected?.Invoke(assetId, MyMspId),
                error => OnGatewayError?.Invoke(error));
            return true;
        }

        // =====================================================================
        // ANNOTATION OPERATIONS (v2.0)
        // =====================================================================

        /// <summary>
        /// Request an AI-generated annotation for an asset.
        /// The asset must have an ACTIVE anchor on-chain.
        /// </summary>
        public bool RequestAnnotation(string assetId, string className, float confidence, string tier = "ADVISORY")
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized) return false;
            if (pendingAnnotationRequests.ContainsKey(assetId)) return false;

            // Check if annotation can be requested
            var annState = annotationStateManager.GetState(assetId);
            if (annState != null && !annState.CanRequest) return false;

            annotationStateManager.SetRequesting(assetId);
            pendingAnnotationRequests[assetId] = Time.realtimeSinceStartup;

            Debug.Log($"[GatewaySync] Requesting annotation: {assetId} (tier={tier}, class={className})");

            gatewayClient.RequestAnnotation(assetId, tier, className, confidence,
                response =>
                {
                    pendingAnnotationRequests.Remove(assetId);
                    Debug.Log($"[GatewaySync] ✓ Annotation request accepted: {assetId} → {response.state}");
                },
                error =>
                {
                    pendingAnnotationRequests.Remove(assetId);
                    // Reset to None so user can try again
                    var s = annotationStateManager.GetOrCreateState(assetId);
                    s.status = AnnotationStatus.None;
                    Debug.LogError($"[GatewaySync] Annotation request failed: {error}");
                    OnGatewayError?.Invoke(error);
                });

            return true;
        }

        /// <summary>
        /// Get annotation state for an asset
        /// </summary>
        public AnnotationState GetAnnotationState(string assetId)
        {
            return annotationStateManager?.GetState(assetId);
        }

        /// <summary>
        /// Get annotation status for an asset
        /// </summary>
        public AnnotationStatus GetAnnotationStatus(string assetId)
        {
            return annotationStateManager?.GetState(assetId)?.status ?? AnnotationStatus.None;
        }

        /// <summary>
        /// Check if an annotation can be requested for this asset
        /// </summary>
        public bool CanRequestAnnotation(string assetId)
        {
            if (string.IsNullOrEmpty(assetId) || !isInitialized) return false;
            if (pendingAnnotationRequests.ContainsKey(assetId)) return false;

            // Must have an active anchor
            var claimState = stateManager.GetState(assetId);
            if (claimState == null || !claimState.IsActive) return false;

            // Must not have an active/pending annotation
            var annState = annotationStateManager.GetState(assetId);
            return annState == null || annState.CanRequest;
        }

        // =====================================================================
        // QUERY HELPERS (existing)
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
        // POSE CONVERSION (existing)
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
        // EVENT HANDLERS - ANCHORS (existing)
        // =====================================================================

        private void HandleStateChanged(string assetId, AnchorClaimState state)
        {
            if (state.status != ClaimStatus.Pending) pendingProposals.Remove(assetId);
            if (state.status != ClaimStatus.RevokePending) pendingRevokes.Remove(assetId);
            OnClaimStatusChanged?.Invoke(assetId, state.status);
        }

        private void HandleRevokePending(string assetId, AnchorClaimState state)
        {
            if (state.RequiresMyAction(MyMspId))
                Debug.Log($"[GatewaySync] ACTION REQUIRED: Endorse or reject revocation of {assetId}");
            OnRevokePending?.Invoke(assetId, state.revokeInitiatedBy);
        }

        private void HandleRevoked(string assetId, AnchorClaimState state)
            => OnRevokeCompleted?.Invoke(assetId);

        // =====================================================================
        // EVENT HANDLERS - ANNOTATIONS (v2.0)
        // =====================================================================

        private void HandleAnnotationStateChanged(string assetId, AnnotationState state)
        {
            pendingAnnotationRequests.Remove(assetId);
            OnAnnotationStatusChanged?.Invoke(assetId, state.status);
        }

        private void HandleAnnotationActive(string assetId, AnnotationState state)
        {
            Debug.Log($"[GatewaySync] 🤖 Annotation ACTIVE for {assetId}: {state.contentText}");
            OnAnnotationActive?.Invoke(assetId, state.contentText);
        }

        // =====================================================================
        // SSE HANDLERS (existing + snapshot extension)
        // =====================================================================

        private void HandleSseConnected()
        {
            Debug.Log($"[GatewaySync] ✓ Connected as {config.organizationName}");
            OnGatewayConnected?.Invoke();

            // Fetch snapshot to restore both anchor and annotation state
            FetchSnapshotForReconnect();

            if (config.autoProcessRevocations) CheckPendingRevocations();
        }

        private void HandleSseDisconnected() => OnGatewayDisconnected?.Invoke();
        private void HandleSseEvent(GatewayEvent evt) { }
        private void HandleSseError(string error) => OnGatewayError?.Invoke(error);

        /// <summary>
        /// Fetch snapshot on connect/reconnect to restore annotation state (v2.0)
        /// </summary>
        private void FetchSnapshotForReconnect()
        {
            if (!config.fetchSnapshotOnReconnect) return;

            gatewayClient.FetchSnapshot(
                response =>
                {
                    // Restore annotation state from snapshot
                    if (response.annotations != null)
                    {
                        int count = 0;
                        foreach (var ann in response.annotations)
                        {
                            if (!string.IsNullOrEmpty(ann.asset_id))
                            {
                                annotationStateManager.LoadFromSnapshot(
                                    ann.asset_id,
                                    ann.annotation_id,
                                    ann.state,
                                    ann.content_text,
                                    ann.tier,
                                    ann.endorsed_org1,
                                    ann.endorsed_org2
                                );
                                count++;
                            }
                        }
                        if (count > 0)
                            Debug.Log($"[GatewaySync] Restored {count} annotation(s) from snapshot");
                    }
                },
                error => Debug.LogWarning($"[GatewaySync] Snapshot fetch failed: {error}")
            );
        }

        private void CheckPendingRevocations()
        {
            gatewayClient.GetPendingRevocationsForMe(
                response =>
                {
                    if (response.count > 0)
                        Debug.Log($"[GatewaySync] {response.count} pending revocation(s) require action");
                },
                error => { });
        }
    }
}