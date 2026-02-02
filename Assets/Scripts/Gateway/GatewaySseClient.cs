// ============================================================================
// FILE: GatewaySseClient.cs
// Server-Sent Events (SSE) client for Unity.
// Uses UnityWebRequest with DownloadHandlerScript for streaming.
// Handles reconnection, Last-Event-ID, and event parsing.
// 
// FIX: Added SetConfig() and SetGatewayClient() methods
// ============================================================================

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// SSE client for receiving real-time events from Gateway.
    /// </summary>
    public class GatewaySseClient : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private GatewayConfig config;
        [SerializeField] private GatewayClient gatewayClient;

        [Header("State Manager")]
        [SerializeField] private bool autoCreateStateManager = true;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;

        // State
        private AnchorClaimStateManager stateManager;
        private UnityWebRequest activeRequest;
        private SseDownloadHandler sseHandler;
        private Coroutine connectionCoroutine;
        private bool isConnecting = false;
        private bool isConnected = false;
        private bool shouldReconnect = true;
        private int reconnectAttempts = 0;
        private float lastHeartbeatTime;
        private float lastEventTime;

        // Events
        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<GatewayEvent> OnEventReceived;
        public event Action<string> OnError;

        // Public properties
        public bool IsConnected => isConnected;
        public bool IsConnecting => isConnecting;
        public AnchorClaimStateManager StateManager => stateManager;
        public float SecondsSinceLastEvent => Time.realtimeSinceStartup - lastEventTime;

        private void Awake()
        {
            // Config and gatewayClient will be set by GatewaySync via SetConfig/SetGatewayClient
        }

        private void OnEnable()
        {
            shouldReconnect = true;
            // Only connect if config is available
            if (config != null)
            {
                Connect();
            }
        }

        private void OnDisable()
        {
            shouldReconnect = false;
            Disconnect();
        }

        private void OnDestroy()
        {
            shouldReconnect = false;
            Disconnect();
        }

        private void Update()
        {
            // Check for heartbeat timeout
            if (isConnected && config != null)
            {
                float timeSinceLastEvent = Time.realtimeSinceStartup - lastEventTime;
                if (timeSinceLastEvent > config.heartbeatTimeoutSeconds)
                {
                    Debug.LogWarning($"[GatewaySseClient] Heartbeat timeout ({timeSinceLastEvent:F1}s), reconnecting...");
                    Reconnect();
                }
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                // App paused - disconnect cleanly
                Disconnect();
                // Save PlayerPrefs (includes lastEventId)
                PlayerPrefs.Save();
            }
            else
            {
                // App resumed - reconnect
                if (shouldReconnect && config != null)
                {
                    Connect();
                }
            }
        }

        /// <summary>
        /// Set the configuration (called by GatewaySync)
        /// </summary>
        public void SetConfig(GatewayConfig newConfig)
        {
            config = newConfig;
            if (enableDebugLogs && config != null)
            {
                Debug.Log($"[GatewaySseClient] Config set: {config.gatewayBaseUrl}");
            }
        }

        /// <summary>
        /// Set the gateway client for snapshot fetching (called by GatewaySync)
        /// </summary>
        public void SetGatewayClient(GatewayClient client)
        {
            gatewayClient = client;
        }

        /// <summary>
        /// Set the state manager (called by GatewaySync)
        /// </summary>
        public void SetStateManager(AnchorClaimStateManager manager)
        {
            stateManager = manager;
        }

        /// <summary>
        /// Connect to the SSE stream
        /// </summary>
        public void Connect()
        {
            if (isConnecting || isConnected)
                return;

            if (config == null)
            {
                Debug.LogError("[GatewaySseClient] GatewayConfig not configured!");
                return;
            }

            if (connectionCoroutine != null)
            {
                StopCoroutine(connectionCoroutine);
            }

            connectionCoroutine = StartCoroutine(ConnectCoroutine());
        }

        /// <summary>
        /// Disconnect from the SSE stream
        /// </summary>
        public void Disconnect()
        {
            shouldReconnect = false;

            if (connectionCoroutine != null)
            {
                StopCoroutine(connectionCoroutine);
                connectionCoroutine = null;
            }

            if (activeRequest != null)
            {
                activeRequest.Abort();
                activeRequest.Dispose();
                activeRequest = null;
            }

            if (isConnected)
            {
                isConnected = false;
                OnDisconnected?.Invoke();
                Debug.Log("[GatewaySseClient] Disconnected");
            }

            isConnecting = false;
        }

        /// <summary>
        /// Force reconnect
        /// </summary>
        public void Reconnect()
        {
            Disconnect();
            shouldReconnect = true;
            reconnectAttempts++;
            Connect();
        }

        private IEnumerator ConnectCoroutine()
        {
            isConnecting = true;

            // Calculate reconnect delay with exponential backoff
            float delay = 0f;
            if (reconnectAttempts > 0)
            {
                delay = Mathf.Min(
                    config.reconnectDelaySeconds * Mathf.Pow(2, reconnectAttempts - 1),
                    config.maxReconnectDelaySeconds
                );
                Debug.Log($"[GatewaySseClient] Reconnect attempt {reconnectAttempts}, waiting {delay:F1}s...");
                yield return new WaitForSeconds(delay);
            }

            // Build URL
            string url = config.EventStreamUrl;

            // Add Last-Event-ID as query param if available
            string lastEventId = stateManager?.GetLastEventId();
            if (!string.IsNullOrEmpty(lastEventId))
            {
                url += (url.Contains("?") ? "&" : "?") + "last_event_id=" + UnityWebRequest.EscapeURL(lastEventId);
                Debug.Log($"[GatewaySseClient] Connecting with Last-Event-ID: {lastEventId}");
            }

            if (enableDebugLogs || config.enableDebugLogs)
            {
                Debug.Log($"[GatewaySseClient] Connecting to: {url}");
            }

            // Create request
            activeRequest = new UnityWebRequest(url, "GET");
            sseHandler = new SseDownloadHandler(this);
            activeRequest.downloadHandler = sseHandler;
            activeRequest.SetRequestHeader("Accept", "text/event-stream");
            activeRequest.SetRequestHeader("Cache-Control", "no-cache");
            activeRequest.SetRequestHeader("x-api-key", config.apiKey);

            // Add Last-Event-ID header
            if (!string.IsNullOrEmpty(lastEventId))
            {
                activeRequest.SetRequestHeader("Last-Event-ID", lastEventId);
            }

            // Start request (don't wait for completion - it's a stream)
            var operation = activeRequest.SendWebRequest();

            // Wait a bit to check initial response
            yield return new WaitForSeconds(0.5f);

            if (activeRequest.result == UnityWebRequest.Result.ConnectionError ||
                activeRequest.result == UnityWebRequest.Result.ProtocolError)
            {
                string error = $"Connection failed: {activeRequest.error} (HTTP {activeRequest.responseCode})";
                Debug.LogError($"[GatewaySseClient] {error}");

                isConnecting = false;
                OnError?.Invoke(error);

                // Try snapshot as fallback
                if (config.fetchSnapshotOnReconnect && gatewayClient != null)
                {
                    Debug.Log("[GatewaySseClient] Fetching snapshot as fallback...");
                    gatewayClient.FetchSnapshot(
                        snapshot =>
                        {
                            stateManager?.ApplySnapshot(snapshot.assets);
                        },
                        err => Debug.LogWarning($"[GatewaySseClient] Snapshot failed: {err}")
                    );
                }

                // Schedule reconnect
                if (shouldReconnect)
                {
                    connectionCoroutine = StartCoroutine(ConnectCoroutine());
                }
                yield break;
            }

            // Connection established
            isConnecting = false;
            isConnected = true;
            reconnectAttempts = 0;
            lastEventTime = Time.realtimeSinceStartup;
            lastHeartbeatTime = Time.realtimeSinceStartup;

            Debug.Log($"[GatewaySseClient] ✓ Connected to SSE stream");
            OnConnected?.Invoke();

            // Keep connection alive until it fails or is aborted
            while (!operation.isDone && activeRequest != null)
            {
                yield return null;
            }

            // Connection ended
            if (isConnected)
            {
                isConnected = false;
                OnDisconnected?.Invoke();

                string reason = activeRequest?.error ?? "Connection closed";
                Debug.Log($"[GatewaySseClient] Connection ended: {reason}");

                // Schedule reconnect
                if (shouldReconnect)
                {
                    reconnectAttempts++;
                    connectionCoroutine = StartCoroutine(ConnectCoroutine());
                }
            }
        }

        /// <summary>
        /// Process received SSE data (called from DownloadHandler)
        /// </summary>
        internal void ProcessSseData(string line)
        {
            // Ensure we're on main thread for Unity API calls
            MainThreadDispatcher.RunOnMainThread(() => ProcessSseDataOnMainThread(line));
        }

        private void ProcessSseDataOnMainThread(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            lastEventTime = Time.realtimeSinceStartup;

            // Parse SSE format
            // id: <eventId>
            // event: <eventType>
            // data: <json>

            if (line.StartsWith("id:"))
            {
                string eventId = line.Substring(3).Trim();
                // Will be associated with the next data line
                sseHandler.CurrentEventId = eventId;
                return;
            }

            if (line.StartsWith("event:"))
            {
                string eventType = line.Substring(6).Trim();
                sseHandler.CurrentEventType = eventType;
                return;
            }

            if (line.StartsWith("data:"))
            {
                string json = line.Substring(5).Trim();
                ProcessEventData(json, sseHandler.CurrentEventId, sseHandler.CurrentEventType);

                // Reset for next event
                sseHandler.CurrentEventId = null;
                sseHandler.CurrentEventType = null;
                return;
            }

            // Comment or unknown line - ignore
            if (line.StartsWith(":"))
            {
                // SSE comment/keepalive
                return;
            }
        }

        private void ProcessEventData(string json, string eventId, string eventType)
        {
            if (string.IsNullOrEmpty(json))
                return;

            try
            {
                // Parse the JSON
                GatewayEvent evt = JsonUtility.FromJson<GatewayEvent>(json);

                if (evt == null)
                {
                    Debug.LogWarning($"[GatewaySseClient] Failed to parse event JSON: {json}");
                    return;
                }

                // Set event ID from SSE line if not in JSON
                if (string.IsNullOrEmpty(evt.eventId) && !string.IsNullOrEmpty(eventId))
                {
                    evt.eventId = eventId;
                }

                // Set event type from SSE line if not in JSON
                if (string.IsNullOrEmpty(evt.type) && !string.IsNullOrEmpty(eventType))
                {
                    evt.type = eventType;
                }

                if (enableDebugLogs || (config != null && config.logAllEvents))
                {
                    Debug.Log($"[GatewaySseClient] Event: type={evt.type}, assetId={evt.assetId}, " +
                             $"claimId={evt.claimId}, eventId={evt.eventId}");
                }

                // Special handling for CONNECTED event
                if (evt.type == "CONNECTED")
                {
                    Debug.Log($"[GatewaySseClient] Server connection confirmed, clientId={json}");
                    return;
                }

                // Special handling for HEARTBEAT
                if (evt.type == "HEARTBEAT")
                {
                    lastHeartbeatTime = Time.realtimeSinceStartup;
                    return;
                }

                // Apply to state manager
                if (stateManager != null)
                {
                    bool applied = stateManager.ApplyEvent(evt);
                    if (!applied && enableDebugLogs)
                    {
                        Debug.Log($"[GatewaySseClient] Event was duplicate/skipped");
                    }
                }

                // Notify listeners
                OnEventReceived?.Invoke(evt);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GatewaySseClient] Error processing event: {e.Message}\nJSON: {json}");
            }
        }

        /// <summary>
        /// Custom DownloadHandler for streaming SSE data
        /// </summary>
        private class SseDownloadHandler : DownloadHandlerScript
        {
            private GatewaySseClient client;
            private StringBuilder lineBuffer = new StringBuilder();

            public string CurrentEventId { get; set; }
            public string CurrentEventType { get; set; }

            public SseDownloadHandler(GatewaySseClient client) : base(new byte[1024])
            {
                this.client = client;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0 || client == null)
                    return true;

                string text = Encoding.UTF8.GetString(data, 0, dataLength);

                // Process each character
                foreach (char c in text)
                {
                    if (c == '\n')
                    {
                        // End of line - process it
                        string line = lineBuffer.ToString();
                        lineBuffer.Clear();

                        if (!string.IsNullOrEmpty(line))
                        {
                            client.ProcessSseData(line);
                        }
                    }
                    else if (c != '\r')
                    {
                        lineBuffer.Append(c);
                    }
                }

                return true;
            }

            protected override void CompleteContent()
            {
                // Process any remaining data in buffer
                if (lineBuffer.Length > 0)
                {
                    string line = lineBuffer.ToString();
                    lineBuffer.Clear();
                    if (!string.IsNullOrEmpty(line))
                    {
                        client.ProcessSseData(line);
                    }
                }
            }
        }
    }
}