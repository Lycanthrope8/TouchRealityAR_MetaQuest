// ============================================================================
// FILE: GatewayConfig.cs
// ScriptableObject configuration for Gateway connectivity.
// Create via: Assets > Create > AR Detection > Gateway Config
// ============================================================================

using UnityEngine;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// Configuration for Gateway connectivity.
    /// </summary>
    [CreateAssetMenu(fileName = "GatewayConfig", menuName = "AR Detection/Gateway Config")]
    public class GatewayConfig : ScriptableObject
    {
        [Header("Server Connection")]
        [Tooltip("Gateway base URL (e.g., http://192.168.1.100:3000)")]
        public string gatewayBaseUrl = "http://192.168.1.100:3000";

        [Tooltip("API key for authentication (proposer role)")]
        public string apiKey = "proposer-key-001";

        [Tooltip("Request timeout in seconds")]
        public float requestTimeoutSeconds = 10f;

        [Header("SSE Event Stream")]
        [Tooltip("Endpoint for headset event stream (relative to base URL)")]
        public string eventStreamEndpoint = "/events/stream";

        [Tooltip("Reconnect delay after disconnect (seconds)")]
        public float reconnectDelaySeconds = 3f;

        [Tooltip("Maximum reconnect delay with exponential backoff (seconds)")]
        public float maxReconnectDelaySeconds = 30f;

        [Tooltip("Heartbeat timeout - consider disconnected if no heartbeat (seconds)")]
        public float heartbeatTimeoutSeconds = 45f;

        [Header("Snapshot/Catch-Up")]
        [Tooltip("Endpoint for status snapshot (relative to base URL)")]
        public string snapshotEndpoint = "/events/snapshot";

        [Tooltip("Fetch snapshot on reconnect if Last-Event-ID replay fails")]
        public bool fetchSnapshotOnReconnect = true;

        [Header("Debug")]
        [Tooltip("Enable verbose logging")]
        public bool enableDebugLogs = true;

        [Tooltip("Log all SSE events received")]
        public bool logAllEvents = false;

        /// <summary>
        /// Get full URL for claims propose endpoint
        /// </summary>
        public string ProposeEndpoint => $"{gatewayBaseUrl}/claims/propose";

        /// <summary>
        /// Get full URL for event stream
        /// </summary>
        public string EventStreamUrl => $"{gatewayBaseUrl}{eventStreamEndpoint}";

        /// <summary>
        /// Get full URL for status snapshot
        /// </summary>
        public string SnapshotUrl => $"{gatewayBaseUrl}{snapshotEndpoint}";

        /// <summary>
        /// Get full URL for asset resolve
        /// </summary>
        public string GetAssetResolveUrl(string assetId) => 
            $"{gatewayBaseUrl}/assets/{UnityEngine.Networking.UnityWebRequest.EscapeURL(assetId)}/resolve";

        /// <summary>
        /// Get full URL for asset claims list
        /// </summary>
        public string GetAssetClaimsUrl(string assetId) =>
            $"{gatewayBaseUrl}/assets/{UnityEngine.Networking.UnityWebRequest.EscapeURL(assetId)}/claims";

        private void OnValidate()
        {
            // Ensure URL doesn't end with slash
            if (gatewayBaseUrl.EndsWith("/"))
            {
                gatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');
            }

            // Ensure endpoints start with slash
            if (!eventStreamEndpoint.StartsWith("/"))
            {
                eventStreamEndpoint = "/" + eventStreamEndpoint;
            }
            if (!snapshotEndpoint.StartsWith("/"))
            {
                snapshotEndpoint = "/" + snapshotEndpoint;
            }

            // Warn if using default/example values
            if (apiKey == "proposer-key-001")
            {
                Debug.LogWarning("[GatewayConfig] Using default API key. Update for production.");
            }
        }
    }
}
