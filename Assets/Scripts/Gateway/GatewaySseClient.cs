// ============================================================================
// FILE: GatewaySseClient.cs
// SSE Client for receiving real-time events from the Gateway
// FIXED: Self-contained main thread dispatch, compatible with existing code
// v2.0: Added annotation event handling
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// Event data received from SSE stream
    /// v2.0: Added annotation fields
    /// </summary>
    [Serializable]
    public class GatewayEvent
    {
        public string event_id;
        public string type;
        public string asset_id;
        public string claim_id;
        public string state;
        public string timestamp;

        // CamelCase versions (Unity-friendly)
        public string assetId;
        public string claimId;

        // Endorsement info
        public string endorsed_by;
        public string endorsedBy;
        public string final_endorser;
        public string finalEndorser;
        public string proposed_via_org;
        public string proposedViaOrg;
        public bool is_fully_endorsed;

        // Rejection info
        public string rejected_by;
        public string rejectedBy;
        public string reason;

        // Revocation info
        public string initiated_by;
        public string initiatedBy;
        public string required_endorser;
        public string requiredEndorser;
        public bool anchor_deleted;
        public bool anchorDeleted;
        public bool anchor_preserved;
        public bool anchorPreserved;

        // Endorsements object (for dual endorsement tracking)
        public EndorsementsData endorsements;

        // =========================================================
        // ANNOTATION FIELDS (v2.0)
        // =========================================================
        public string annotation_id;
        public string annotationId;
        public string content_text;
        public string contentText;
        public string tier;
        public string activation_method;
        public string activationMethod;
        public string anchor_claim_id;
        public string anchorClaimId;
        public string revoked_by;
        public string revokedBy;

        /// <summary>
        /// Get the asset ID (tries both camelCase and snake_case)
        /// </summary>
        public string GetAssetId()
        {
            return !string.IsNullOrEmpty(assetId) ? assetId : asset_id;
        }

        /// <summary>
        /// Get the claim ID (tries both camelCase and snake_case)
        /// </summary>
        public string GetClaimId()
        {
            return !string.IsNullOrEmpty(claimId) ? claimId : claim_id;
        }

        /// <summary>
        /// Get the annotation ID (tries both formats)
        /// </summary>
        public string GetAnnotationId()
        {
            return !string.IsNullOrEmpty(annotationId) ? annotationId : annotation_id;
        }

        /// <summary>
        /// Get annotation content text (tries both formats)
        /// </summary>
        public string GetContentText()
        {
            return !string.IsNullOrEmpty(contentText) ? contentText : content_text;
        }

        /// <summary>
        /// Get activation method (tries both formats)
        /// </summary>
        public string GetActivationMethod()
        {
            return !string.IsNullOrEmpty(activationMethod) ? activationMethod : activation_method;
        }
    }

    [Serializable]
    public class EndorsementsData
    {
        public bool Org1MSP;
        public bool Org2MSP;
    }

    public class GatewaySseClient : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private float reconnectDelay = 5f;
        [SerializeField] private bool autoReconnect = true;
        [SerializeField] private bool enableDebugLogs = true;

        // SSE endpoint path (appended to base URL)
        private const string SSE_ENDPOINT = "/events/stream";

        // Events
        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<GatewayEvent> OnEventReceived;
        public event Action<string> OnError;

        // State
        private GatewayConfig config;
        private AnchorClaimStateManager stateManager;
        private AnnotationStateManager annotationStateManager;
        private GatewayClient gatewayClient;
        private UnityWebRequest currentRequest;
        private bool isConnected = false;
        private bool shouldReconnect = true;
        private string lastEventId = null;
        private Coroutine sseCoroutine;

        // Main thread action queue
        private readonly Queue<Action> mainThreadActions = new Queue<Action>();
        private readonly object queueLock = new object();

        public bool IsConnected => isConnected;

        public void Initialize(GatewayConfig config, AnchorClaimStateManager stateManager, GatewayClient client)
        {
            this.config = config;
            this.stateManager = stateManager;
            this.gatewayClient = client;
        }

        /// <summary>
        /// Set the annotation state manager (called from GatewaySync after initialization)
        /// </summary>
        public void SetAnnotationStateManager(AnnotationStateManager manager)
        {
            this.annotationStateManager = manager;
        }

        public void Connect()
        {
            if (config == null)
            {
                Debug.LogError("[GatewaySseClient] Not initialized. Call Initialize() first.");
                return;
            }

            shouldReconnect = true;
            if (sseCoroutine != null)
            {
                StopCoroutine(sseCoroutine);
            }
            sseCoroutine = StartCoroutine(SseConnectionCoroutine());
        }

        public void Disconnect()
        {
            shouldReconnect = false;
            if (sseCoroutine != null)
            {
                StopCoroutine(sseCoroutine);
                sseCoroutine = null;
            }
            if (currentRequest != null)
            {
                currentRequest.Abort();
                currentRequest.Dispose();
                currentRequest = null;
            }
            isConnected = false;
            OnDisconnected?.Invoke();
        }

        /// <summary>
        /// Process queued main thread actions
        /// </summary>
        private void Update()
        {
            lock (queueLock)
            {
                while (mainThreadActions.Count > 0)
                {
                    var action = mainThreadActions.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[GatewaySseClient] Error executing main thread action: {e.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Enqueue action to run on main thread
        /// </summary>
        private void EnqueueMainThread(Action action)
        {
            lock (queueLock)
            {
                mainThreadActions.Enqueue(action);
            }
        }

        private IEnumerator SseConnectionCoroutine()
        {
            while (shouldReconnect)
            {
                // Build SSE URL from config
                string baseUrl = config.gatewayBaseUrl;
                if (baseUrl.EndsWith("/"))
                {
                    baseUrl = baseUrl.TrimEnd('/');
                }
                string url = baseUrl + SSE_ENDPOINT;

                if (enableDebugLogs)
                    Debug.Log($"[GatewaySseClient] Connecting to SSE: {url}");

                currentRequest = new UnityWebRequest(url, "GET");
                currentRequest.downloadHandler = new SseDownloadHandler(this);
                currentRequest.SetRequestHeader("Accept", "text/event-stream");
                currentRequest.SetRequestHeader("Cache-Control", "no-cache");

                if (!string.IsNullOrEmpty(lastEventId))
                {
                    currentRequest.SetRequestHeader("Last-Event-ID", lastEventId);
                }

                var operation = currentRequest.SendWebRequest();

                // Wait for headers to be received (connection established)
                float timeout = 10f;
                float elapsed = 0f;
                while (!operation.isDone && elapsed < timeout)
                {
                    if (currentRequest.downloadedBytes > 0 && !isConnected)
                    {
                        isConnected = true;
                        Debug.Log("[GatewaySseClient] ✓ SSE Connected");
                        OnConnected?.Invoke();
                    }
                    elapsed += Time.deltaTime;
                    yield return null;
                }

                // Keep connection alive
                while (!operation.isDone && shouldReconnect)
                {
                    yield return null;
                }

                // Connection ended
                isConnected = false;

                if (currentRequest.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[GatewaySseClient] Connection error: {currentRequest.error}");
                    OnError?.Invoke(currentRequest.error);
                }

                currentRequest.Dispose();
                currentRequest = null;
                OnDisconnected?.Invoke();

                if (shouldReconnect && autoReconnect)
                {
                    Debug.Log($"[GatewaySseClient] Reconnecting in {reconnectDelay}s...");
                    yield return new WaitForSeconds(reconnectDelay);
                }
            }
        }

        /// <summary>
        /// Process SSE data received from the stream
        /// Called from SseDownloadHandler on background thread
        /// </summary>
        public void ProcessSseData(string data)
        {
            // Must dispatch to main thread for Unity operations
            EnqueueMainThread(() => ProcessSseDataOnMainThread(data));
        }

        private void ProcessSseDataOnMainThread(string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            try
            {
                // Parse SSE format: "id: xxx\nevent: xxx\ndata: {...}\n\n"
                string eventId = null;
                string eventType = null;
                string eventData = null;

                string[] lines = data.Split('\n');
                foreach (string line in lines)
                {
                    if (line.StartsWith("id:"))
                    {
                        eventId = line.Substring(3).Trim();
                    }
                    else if (line.StartsWith("event:"))
                    {
                        eventType = line.Substring(6).Trim();
                    }
                    else if (line.StartsWith("data:"))
                    {
                        eventData = line.Substring(5).Trim();
                    }
                }

                if (!string.IsNullOrEmpty(eventId))
                {
                    lastEventId = eventId;
                }

                if (!string.IsNullOrEmpty(eventType) && !string.IsNullOrEmpty(eventData))
                {
                    ProcessEventData(eventId, eventType, eventData);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GatewaySseClient] Error parsing SSE data: {e.Message}");
            }
        }

        private void ProcessEventData(string eventId, string eventType, string jsonData)
        {
            try
            {
                // Parse the JSON data
                GatewayEvent evt = JsonUtility.FromJson<GatewayEvent>(jsonData);
                evt.type = eventType; // Ensure type is set

                // Get asset ID (tries both formats)
                string assetId = evt.GetAssetId();

                if (enableDebugLogs)
                {
                    Debug.Log($"[GatewaySseClient] Received event: id={eventId}, type={eventType}, assetId={assetId}");
                }

                // Skip heartbeat events for state processing
                if (eventType == "HEARTBEAT" || eventType == "CONNECTED")
                {
                    OnEventReceived?.Invoke(evt);
                    return;
                }

                // Update anchor state manager based on event type
                if (stateManager != null && !string.IsNullOrEmpty(assetId))
                {
                    ProcessAnchorStateUpdate(eventType, evt, assetId);
                }

                // Update annotation state manager based on event type
                if (annotationStateManager != null && !string.IsNullOrEmpty(assetId))
                {
                    ProcessAnnotationStateUpdate(eventType, evt, assetId);
                }

                // Notify listeners
                OnEventReceived?.Invoke(evt);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GatewaySseClient] Error processing event data: {e.Message}\nData: {jsonData}");
            }
        }

        private void ProcessAnchorStateUpdate(string eventType, GatewayEvent evt, string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return;

            string claimId = evt.GetClaimId();
            string proposedViaOrg = !string.IsNullOrEmpty(evt.proposedViaOrg) ? evt.proposedViaOrg : evt.proposed_via_org;
            string rejectedBy = !string.IsNullOrEmpty(evt.rejectedBy) ? evt.rejectedBy : evt.rejected_by;
            string initiatedBy = !string.IsNullOrEmpty(evt.initiatedBy) ? evt.initiatedBy : evt.initiated_by;
            string requiredEndorser = !string.IsNullOrEmpty(evt.requiredEndorser) ? evt.requiredEndorser : evt.required_endorser;

            switch (eventType)
            {
                case "CLAIM_PROPOSED":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} is now PROPOSED");
                    stateManager.SetProposed(assetId, claimId, proposedViaOrg);
                    break;

                case "CLAIM_ENDORSED_ORG1":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} endorsed by Org1");
                    stateManager.SetEndorsedOrg1(assetId);
                    break;

                case "CLAIM_ENDORSED_ORG2":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} endorsed by Org2");
                    stateManager.SetEndorsedOrg2(assetId);
                    break;

                case "CLAIM_ACTIVATED":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} is now ACTIVE!");
                    stateManager.SetActive(assetId);
                    break;

                case "CLAIM_REJECTED":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} was REJECTED by {rejectedBy}");
                    stateManager.SetRejected(assetId, rejectedBy, evt.reason);
                    break;

                case "REVOKE_INITIATED":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} revocation initiated by {initiatedBy}");
                    stateManager.SetRevokePending(assetId, initiatedBy, requiredEndorser, evt.reason);
                    break;

                case "CLAIM_REVOKED":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} has been REVOKED");
                    stateManager.SetRevoked(assetId);
                    break;

                case "REVOKE_REJECTED":
                    Debug.Log($"[GatewaySseClient] → State Update: {assetId} revocation rejected, back to ACTIVE");
                    stateManager.SetActive(assetId);
                    break;
            }
        }

        /// <summary>
        /// Process annotation-specific SSE events (v2.0)
        /// </summary>
        private void ProcessAnnotationStateUpdate(string eventType, GatewayEvent evt, string assetId)
        {
            string contentText = evt.GetContentText();
            string annotationId = evt.GetAnnotationId();
            string rejectedBy = !string.IsNullOrEmpty(evt.rejectedBy) ? evt.rejectedBy : evt.rejected_by;
            string revokedBy = !string.IsNullOrEmpty(evt.revokedBy) ? evt.revokedBy : evt.revoked_by;
            string activationMethod = evt.GetActivationMethod();

            switch (eventType)
            {
                case "ANNOTATION_PROPOSED":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId} ANN_PROPOSED (tier={evt.tier})");
                    annotationStateManager.SetProposed(assetId, annotationId, contentText, evt.tier);
                    break;

                case "ANNOTATION_ENDORSED_ORG1":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId} endorsed by Org1");
                    annotationStateManager.SetEndorsedOrg1(assetId);
                    break;

                case "ANNOTATION_ENDORSED_ORG2":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId} endorsed by Org2");
                    annotationStateManager.SetEndorsedOrg2(assetId);
                    break;

                case "ANNOTATION_ACTIVE":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId} ANN_ACTIVE! ({activationMethod})");
                    annotationStateManager.SetActive(assetId, contentText, evt.tier, activationMethod);
                    break;

                case "ANNOTATION_REJECTED":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId} ANN_REJECTED by {rejectedBy}");
                    annotationStateManager.SetRejected(assetId, rejectedBy, evt.reason);
                    break;

                case "ANNOTATION_REVOKED":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId} ANN_REVOKED by {revokedBy}");
                    annotationStateManager.SetRevoked(assetId, revokedBy, evt.reason);
                    break;
            }
        }

        private void OnDestroy()
        {
            Disconnect();
        }
    }

    /// <summary>
    /// Custom download handler for SSE streaming
    /// </summary>
    public class SseDownloadHandler : DownloadHandlerScript
    {
        private GatewaySseClient client;
        private StringBuilder buffer = new StringBuilder();

        public SseDownloadHandler(GatewaySseClient client) : base()
        {
            this.client = client;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return true;

            string chunk = Encoding.UTF8.GetString(data, 0, dataLength);
            buffer.Append(chunk);

            // Process complete events (separated by double newline)
            string content = buffer.ToString();
            int eventEnd;
            while ((eventEnd = content.IndexOf("\n\n")) != -1)
            {
                string eventData = content.Substring(0, eventEnd);
                content = content.Substring(eventEnd + 2);

                if (!string.IsNullOrWhiteSpace(eventData))
                {
                    client.ProcessSseData(eventData);
                }
            }

            buffer.Clear();
            buffer.Append(content);

            return true;
        }
    }
}