// ============================================================================
// FILE: GatewaySseClient.cs
// SSE client for Unity - receives real-time events from Gateway.
// EP5 FIX: Explicit Initialize(), guaranteed Connect() call, clear logging
// ============================================================================

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway
{
    public class GatewaySseClient : MonoBehaviour
    {
        private GatewayConfig config;
        private AnchorClaimStateManager stateManager;
        private GatewayClient gatewayClient;

        private UnityWebRequest activeRequest;
        private SseDownloadHandler sseHandler;
        private Coroutine connectionCoroutine;
        private bool isInitialized = false;
        private bool isConnecting = false;
        private bool isConnected = false;
        private bool shouldReconnect = true;
        private int reconnectAttempts = 0;
        private float lastEventTime;

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<GatewayEvent> OnEventReceived;
        public event Action<string> OnError;

        public bool IsConnected => isConnected;
        public bool IsConnecting => isConnecting;
        public float SecondsSinceLastEvent => Time.realtimeSinceStartup - lastEventTime;

        public void Initialize(GatewayConfig gatewayConfig, AnchorClaimStateManager stateMgr, GatewayClient client)
        {
            if (gatewayConfig == null)
            {
                Debug.LogError("[GatewaySseClient] Initialize called with null config!");
                return;
            }

            config = gatewayConfig;
            stateManager = stateMgr;
            gatewayClient = client;
            isInitialized = true;

            Debug.Log($"[GatewaySseClient] ✓ Initialized: {config.EventStreamUrl}");
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
                Disconnect();
                PlayerPrefs.Save();
            }
            else if (shouldReconnect && isInitialized)
            {
                Connect();
            }
        }

        public void Connect()
        {
            if (!isInitialized)
            {
                Debug.LogError("[GatewaySseClient] Cannot connect - not initialized!");
                return;
            }

            if (isConnecting || isConnected)
            {
                Debug.Log("[GatewaySseClient] Already connecting/connected");
                return;
            }

            if (connectionCoroutine != null)
                StopCoroutine(connectionCoroutine);

            shouldReconnect = true;
            connectionCoroutine = StartCoroutine(ConnectCoroutine());
        }

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
            }

            isConnecting = false;
        }

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

            // Exponential backoff
            if (reconnectAttempts > 0)
            {
                float delay = Mathf.Min(
                    config.reconnectDelaySeconds * Mathf.Pow(2, reconnectAttempts - 1),
                    config.maxReconnectDelaySeconds
                );
                Debug.Log($"[GatewaySseClient] Reconnect attempt {reconnectAttempts}, waiting {delay:F1}s...");
                yield return new WaitForSeconds(delay);
            }

            // Build URL with Last-Event-ID
            string url = config.EventStreamUrl;
            string lastEventId = stateManager?.GetLastEventId();
            if (!string.IsNullOrEmpty(lastEventId))
            {
                url += (url.Contains("?") ? "&" : "?") + "last_event_id=" + UnityWebRequest.EscapeURL(lastEventId);
            }

            Debug.Log($"[GatewaySseClient] Connecting SSE: {url}");

            activeRequest = new UnityWebRequest(url, "GET");
            sseHandler = new SseDownloadHandler(this);
            activeRequest.downloadHandler = sseHandler;
            activeRequest.SetRequestHeader("Accept", "text/event-stream");
            activeRequest.SetRequestHeader("Cache-Control", "no-cache");
            activeRequest.SetRequestHeader("x-api-key", config.apiKey);

            if (!string.IsNullOrEmpty(lastEventId))
                activeRequest.SetRequestHeader("Last-Event-ID", lastEventId);

            var operation = activeRequest.SendWebRequest();

            // Wait briefly for initial response
            yield return new WaitForSeconds(1.0f);

            if (activeRequest.result == UnityWebRequest.Result.ConnectionError ||
                activeRequest.result == UnityWebRequest.Result.ProtocolError)
            {
                string error = $"SSE connection failed: {activeRequest.error} (HTTP {activeRequest.responseCode})";
                Debug.LogError($"[GatewaySseClient] {error}");
                isConnecting = false;
                OnError?.Invoke(error);

                if (shouldReconnect)
                {
                    reconnectAttempts++;
                    connectionCoroutine = StartCoroutine(ConnectCoroutine());
                }
                yield break;
            }

            // Connected!
            isConnecting = false;
            isConnected = true;
            reconnectAttempts = 0;
            lastEventTime = Time.realtimeSinceStartup;

            Debug.Log($"[GatewaySseClient] ✓ SSE connected to {config.gatewayBaseUrl}");
            OnConnected?.Invoke();

            // Keep alive until disconnected
            while (!operation.isDone && activeRequest != null)
            {
                yield return null;
            }

            // Connection ended
            if (isConnected)
            {
                isConnected = false;
                OnDisconnected?.Invoke();
                Debug.Log($"[GatewaySseClient] SSE connection closed: {activeRequest?.error ?? "unknown"}");

                if (shouldReconnect)
                {
                    reconnectAttempts++;
                    connectionCoroutine = StartCoroutine(ConnectCoroutine());
                }
            }
        }

        internal void ProcessSseData(string line)
        {
            MainThreadDispatcher.RunOnMainThread(() => ProcessSseDataOnMainThread(line));
        }

        private void ProcessSseDataOnMainThread(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            lastEventTime = Time.realtimeSinceStartup;

            if (line.StartsWith("id:"))
            {
                sseHandler.CurrentEventId = line.Substring(3).Trim();
                return;
            }

            if (line.StartsWith("event:"))
            {
                sseHandler.CurrentEventType = line.Substring(6).Trim();
                return;
            }

            if (line.StartsWith("data:"))
            {
                string json = line.Substring(5).Trim();
                ProcessEventData(json, sseHandler.CurrentEventId, sseHandler.CurrentEventType);
                sseHandler.CurrentEventId = null;
                sseHandler.CurrentEventType = null;
                return;
            }
        }

        private void ProcessEventData(string json, string eventId, string eventType)
        {
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                GatewayEvent evt = JsonUtility.FromJson<GatewayEvent>(json);
                if (evt == null) return;

                if (string.IsNullOrEmpty(evt.eventId) && !string.IsNullOrEmpty(eventId))
                    evt.eventId = eventId;
                if (string.IsNullOrEmpty(evt.type) && !string.IsNullOrEmpty(eventType))
                    evt.type = eventType;

                Debug.Log($"[GatewaySseClient] Received event: id={evt.eventId}, type={evt.type}, assetId={evt.assetId}");

                if (evt.type == "CONNECTED" || evt.type == "HEARTBEAT")
                    return;

                if (stateManager != null)
                    stateManager.ApplyEvent(evt);

                OnEventReceived?.Invoke(evt);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GatewaySseClient] Error processing event: {e.Message}");
            }
        }

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
                if (data == null || dataLength == 0 || client == null) return true;

                string text = Encoding.UTF8.GetString(data, 0, dataLength);

                foreach (char c in text)
                {
                    if (c == '\n')
                    {
                        string line = lineBuffer.ToString();
                        lineBuffer.Clear();
                        if (!string.IsNullOrEmpty(line))
                            client.ProcessSseData(line);
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
                if (lineBuffer.Length > 0)
                {
                    client.ProcessSseData(lineBuffer.ToString());
                    lineBuffer.Clear();
                }
            }
        }
    }
}