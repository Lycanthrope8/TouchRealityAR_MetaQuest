// ============================================================================
// FILE: GatewayConfig.cs
// ScriptableObject for Gateway configuration.
// Create via: Assets > Create > AR Detection > Gateway Config
// ============================================================================

using UnityEngine;

namespace ARObjectDetection.Gateway
{
    [CreateAssetMenu(fileName = "GatewayConfig", menuName = "AR Detection/Gateway Config", order = 1)]
    public class GatewayConfig : ScriptableObject
    {
        [Header("Gateway Connection")]
        [Tooltip("Base URL of the gateway (e.g., http://192.168.1.100:3000 or https://xxx.ngrok-free.app)")]
        public string gatewayBaseUrl = "http://localhost:3000";

        [Tooltip("API key for authentication (proposer role)")]
        public string apiKey = "proposer-key-001";

        [Header("Endpoints")]
        [Tooltip("SSE event stream endpoint path")]
        public string eventStreamEndpoint = "/events/stream";

        [Tooltip("Snapshot endpoint path")]
        public string snapshotEndpoint = "/events/snapshot";

        [Tooltip("Propose endpoint path")]
        public string proposeEndpoint = "/claims/propose";

        [Header("Timeouts")]
        [Tooltip("HTTP request timeout in seconds")]
        public int requestTimeoutSeconds = 10;

        [Tooltip("SSE reconnect delay in seconds (base)")]
        public float reconnectDelaySeconds = 3f;

        [Tooltip("Maximum SSE reconnect delay in seconds")]
        public float maxReconnectDelaySeconds = 30f;

        [Tooltip("SSE heartbeat timeout in seconds")]
        public float heartbeatTimeoutSeconds = 45f;

        [Header("Behavior")]
        [Tooltip("Fetch snapshot on reconnect if Last-Event-ID replay fails")]
        public bool fetchSnapshotOnReconnect = true;

        [Tooltip("Enable debug logging")]
        public bool enableDebugLogs = true;

        [Tooltip("Log all SSE events (verbose)")]
        public bool logAllEvents = false;

        // Computed URLs
        public string ProposeEndpoint => $"{gatewayBaseUrl}{proposeEndpoint}";
        public string EventStreamUrl => $"{gatewayBaseUrl}{eventStreamEndpoint}";
        public string SnapshotUrl => $"{gatewayBaseUrl}{snapshotEndpoint}";

        private void OnValidate()
        {
            // Ensure base URL doesn't end with slash
            if (!string.IsNullOrEmpty(gatewayBaseUrl) && gatewayBaseUrl.EndsWith("/"))
            {
                gatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');
            }

            // Ensure endpoints start with slash
            if (!string.IsNullOrEmpty(eventStreamEndpoint) && !eventStreamEndpoint.StartsWith("/"))
            {
                eventStreamEndpoint = "/" + eventStreamEndpoint;
            }
            if (!string.IsNullOrEmpty(snapshotEndpoint) && !snapshotEndpoint.StartsWith("/"))
            {
                snapshotEndpoint = "/" + snapshotEndpoint;
            }
            if (!string.IsNullOrEmpty(proposeEndpoint) && !proposeEndpoint.StartsWith("/"))
            {
                proposeEndpoint = "/" + proposeEndpoint;
            }
        }
    }
}