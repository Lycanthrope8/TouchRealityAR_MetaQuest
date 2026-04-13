// ============================================================================
// FILE: GatewaySseClient.cs
// SSE Client for receiving real-time events from the Gateway
// v2.1: Annotation events include intentType — passed to AnnotationStateManager
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway
{
    [Serializable]
    public class GatewayEvent
    {
        public string event_id;
        public string type;
        public string asset_id;
        public string claim_id;
        public string state;
        public string timestamp;

        public string assetId;
        public string claimId;

        public string endorsed_by;
        public string endorsedBy;
        public string final_endorser;
        public string finalEndorser;
        public string proposed_via_org;
        public string proposedViaOrg;
        public bool is_fully_endorsed;

        public string rejected_by;
        public string rejectedBy;
        public string reason;

        public string initiated_by;
        public string initiatedBy;
        public string required_endorser;
        public string requiredEndorser;
        public bool anchor_deleted;
        public bool anchorDeleted;
        public bool anchor_preserved;
        public bool anchorPreserved;

        public EndorsementsData endorsements;

        // Annotation fields (v2.1: intentType added)
        public string annotation_id;
        public string annotationId;
        public string content_text;
        public string contentText;
        public string tier;
        public string intent_type;      // v2.1
        public string intentType;       // v2.1
        public string activation_method;
        public string activationMethod;
        public string anchor_claim_id;
        public string anchorClaimId;
        public string revoked_by;
        public string revokedBy;

        public string GetAssetId() => !string.IsNullOrEmpty(assetId) ? assetId : asset_id;
        public string GetClaimId() => !string.IsNullOrEmpty(claimId) ? claimId : claim_id;
        public string GetAnnotationId() => !string.IsNullOrEmpty(annotationId) ? annotationId : annotation_id;
        public string GetContentText() => !string.IsNullOrEmpty(contentText) ? contentText : content_text;
        public string GetActivationMethod() => !string.IsNullOrEmpty(activationMethod) ? activationMethod : activation_method;
        public string GetIntentType() => !string.IsNullOrEmpty(intentType) ? intentType : intent_type;
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

        private const string SSE_ENDPOINT = "/events/stream";

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<GatewayEvent> OnEventReceived;
        public event Action<string> OnError;

        private GatewayConfig config;
        private AnchorClaimStateManager stateManager;
        private AnnotationStateManager annotationStateManager;
        private GatewayClient gatewayClient;
        private UnityWebRequest currentRequest;
        private bool isConnected = false;
        private bool shouldReconnect = true;
        private string lastEventId = null;
        private Coroutine sseCoroutine;

        private readonly Queue<Action> mainThreadActions = new Queue<Action>();
        private readonly object queueLock = new object();

        public bool IsConnected => isConnected;

        public void Initialize(GatewayConfig config, AnchorClaimStateManager stateManager, GatewayClient client)
        {
            this.config = config;
            this.stateManager = stateManager;
            this.gatewayClient = client;
        }

        public void SetAnnotationStateManager(AnnotationStateManager manager)
        {
            this.annotationStateManager = manager;
        }

        public void Connect()
        {
            if (config == null) { Debug.LogError("[GatewaySseClient] Not initialized."); return; }
            shouldReconnect = true;
            if (sseCoroutine != null) StopCoroutine(sseCoroutine);
            sseCoroutine = StartCoroutine(SseConnectionCoroutine());
        }

        public void Disconnect()
        {
            shouldReconnect = false;
            if (sseCoroutine != null) { StopCoroutine(sseCoroutine); sseCoroutine = null; }
            if (currentRequest != null) { currentRequest.Abort(); currentRequest.Dispose(); currentRequest = null; }
            isConnected = false;
            OnDisconnected?.Invoke();
        }

        private void Update()
        {
            lock (queueLock)
            {
                while (mainThreadActions.Count > 0)
                {
                    try { mainThreadActions.Dequeue()?.Invoke(); }
                    catch (Exception e) { Debug.LogError($"[GatewaySseClient] Main thread action error: {e.Message}"); }
                }
            }
        }

        private void EnqueueMainThread(Action action)
        {
            lock (queueLock) { mainThreadActions.Enqueue(action); }
        }

        private IEnumerator SseConnectionCoroutine()
        {
            while (shouldReconnect)
            {
                string baseUrl = config.gatewayBaseUrl.TrimEnd('/');
                string url = baseUrl + SSE_ENDPOINT;
                if (enableDebugLogs) Debug.Log($"[GatewaySseClient] Connecting to SSE: {url}");

                currentRequest = new UnityWebRequest(url, "GET");
                currentRequest.downloadHandler = new SseDownloadHandler(this);
                currentRequest.SetRequestHeader("Accept", "text/event-stream");
                currentRequest.SetRequestHeader("Cache-Control", "no-cache");
                if (!string.IsNullOrEmpty(lastEventId)) currentRequest.SetRequestHeader("Last-Event-ID", lastEventId);

                var operation = currentRequest.SendWebRequest();

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

                while (!operation.isDone && shouldReconnect) yield return null;

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

        public void ProcessSseData(string data)
        {
            EnqueueMainThread(() => ProcessSseDataOnMainThread(data));
        }

        private void ProcessSseDataOnMainThread(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            try
            {
                string eventId = null, eventType = null, eventData = null;
                foreach (string line in data.Split('\n'))
                {
                    if (line.StartsWith("id:")) eventId = line.Substring(3).Trim();
                    else if (line.StartsWith("event:")) eventType = line.Substring(6).Trim();
                    else if (line.StartsWith("data:")) eventData = line.Substring(5).Trim();
                }
                if (!string.IsNullOrEmpty(eventId)) lastEventId = eventId;
                if (!string.IsNullOrEmpty(eventType) && !string.IsNullOrEmpty(eventData))
                    ProcessEventData(eventId, eventType, eventData);
            }
            catch (Exception e) { Debug.LogError($"[GatewaySseClient] Error parsing SSE: {e.Message}"); }
        }

        private void ProcessEventData(string eventId, string eventType, string jsonData)
        {
            try
            {
                GatewayEvent evt = JsonUtility.FromJson<GatewayEvent>(jsonData);
                evt.type = eventType;
                string assetId = evt.GetAssetId();

                if (enableDebugLogs) Debug.Log($"[GatewaySseClient] Event: type={eventType}, assetId={assetId}");

                if (eventType == "HEARTBEAT" || eventType == "CONNECTED") { OnEventReceived?.Invoke(evt); return; }

                if (stateManager != null && !string.IsNullOrEmpty(assetId))
                    ProcessAnchorStateUpdate(eventType, evt, assetId);

                if (annotationStateManager != null && !string.IsNullOrEmpty(assetId))
                    ProcessAnnotationStateUpdate(eventType, evt, assetId);

                OnEventReceived?.Invoke(evt);
            }
            catch (Exception e) { Debug.LogError($"[GatewaySseClient] Error processing event: {e.Message}\nData: {jsonData}"); }
        }

        private void ProcessAnchorStateUpdate(string eventType, GatewayEvent evt, string assetId)
        {
            string claimId = evt.GetClaimId();
            string proposedViaOrg = !string.IsNullOrEmpty(evt.proposedViaOrg) ? evt.proposedViaOrg : evt.proposed_via_org;
            string rejectedBy = !string.IsNullOrEmpty(evt.rejectedBy) ? evt.rejectedBy : evt.rejected_by;
            string initiatedBy = !string.IsNullOrEmpty(evt.initiatedBy) ? evt.initiatedBy : evt.initiated_by;
            string requiredEndorser = !string.IsNullOrEmpty(evt.requiredEndorser) ? evt.requiredEndorser : evt.required_endorser;

            switch (eventType)
            {
                case "CLAIM_PROPOSED": stateManager.SetProposed(assetId, claimId, proposedViaOrg); break;
                case "CLAIM_ENDORSED_ORG1": stateManager.SetEndorsedOrg1(assetId); break;
                case "CLAIM_ENDORSED_ORG2": stateManager.SetEndorsedOrg2(assetId); break;
                case "CLAIM_ACTIVATED": stateManager.SetActive(assetId); break;
                case "CLAIM_REJECTED": stateManager.SetRejected(assetId, rejectedBy, evt.reason); break;
                case "REVOKE_INITIATED": stateManager.SetRevokePending(assetId, initiatedBy, requiredEndorser, evt.reason); break;
                case "CLAIM_REVOKED": stateManager.SetRevoked(assetId); break;
                case "REVOKE_REJECTED": stateManager.SetActive(assetId); break;
            }
        }

        /// <summary>
        /// Process annotation SSE events (v2.1: intentType extracted and passed)
        /// </summary>
        private void ProcessAnnotationStateUpdate(string eventType, GatewayEvent evt, string assetId)
        {
            string contentText = evt.GetContentText();
            string annotationId = evt.GetAnnotationId();
            string intentType = evt.GetIntentType();
            string rejectedBy = !string.IsNullOrEmpty(evt.rejectedBy) ? evt.rejectedBy : evt.rejected_by;
            string revokedBy = !string.IsNullOrEmpty(evt.revokedBy) ? evt.revokedBy : evt.revoked_by;
            string activationMethod = evt.GetActivationMethod();

            // v2.1: intentType is required for all annotation state updates
            if (string.IsNullOrEmpty(intentType))
            {
                // Skip annotation processing if intentType missing (not an annotation event)
                return;
            }

            switch (eventType)
            {
                case "ANNOTATION_PROPOSED":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId}:{intentType} ANN_PROPOSED (tier={evt.tier})");
                    annotationStateManager.SetProposed(assetId, intentType, annotationId, contentText, evt.tier);
                    break;

                case "ANNOTATION_ENDORSED_ORG1":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId}:{intentType} endorsed by Org1");
                    annotationStateManager.SetEndorsedOrg1(assetId, intentType);
                    break;

                case "ANNOTATION_ENDORSED_ORG2":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId}:{intentType} endorsed by Org2");
                    annotationStateManager.SetEndorsedOrg2(assetId, intentType);
                    break;

                case "ANNOTATION_ACTIVE":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId}:{intentType} ANN_ACTIVE! ({activationMethod})");
                    annotationStateManager.SetActive(assetId, intentType, contentText, evt.tier, activationMethod);
                    break;

                case "ANNOTATION_REJECTED":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId}:{intentType} ANN_REJECTED by {rejectedBy}");
                    annotationStateManager.SetRejected(assetId, intentType, rejectedBy, evt.reason);
                    break;

                case "ANNOTATION_REVOKED":
                    Debug.Log($"[GatewaySseClient] → Annotation: {assetId}:{intentType} ANN_REVOKED by {revokedBy}");
                    annotationStateManager.SetRevoked(assetId, intentType, revokedBy, evt.reason);
                    break;
            }
        }

        private void OnDestroy() { Disconnect(); }
    }

    public class SseDownloadHandler : DownloadHandlerScript
    {
        private GatewaySseClient client;
        private StringBuilder buffer = new StringBuilder();

        public SseDownloadHandler(GatewaySseClient client) : base() { this.client = client; }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return true;
            buffer.Append(Encoding.UTF8.GetString(data, 0, dataLength));
            string content = buffer.ToString();
            int eventEnd;
            while ((eventEnd = content.IndexOf("\n\n")) != -1)
            {
                string eventData = content.Substring(0, eventEnd);
                content = content.Substring(eventEnd + 2);
                if (!string.IsNullOrWhiteSpace(eventData)) client.ProcessSseData(eventData);
            }
            buffer.Clear();
            buffer.Append(content);
            return true;
        }
    }
}
