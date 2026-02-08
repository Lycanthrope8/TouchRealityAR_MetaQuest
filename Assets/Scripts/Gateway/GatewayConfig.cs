// ============================================================================
// FILE: GatewayConfig.cs
// ScriptableObject for Gateway configuration - Updated for Two-Org Model
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

        [Header("Organization Identity")]
        [Tooltip("Organization ID for this client (org1 or org2)")]
        public string organizationId = "org1";

        [Tooltip("Organization display name")]
        public string organizationName = "Organization 1";

        [Header("Endpoints")]
        [Tooltip("SSE event stream endpoint path")]
        public string eventStreamEndpoint = "/events/stream";

        [Tooltip("Snapshot endpoint path")]
        public string snapshotEndpoint = "/events/snapshot";

        [Tooltip("Propose endpoint path")]
        public string proposeEndpoint = "/claims/propose";

        [Tooltip("Revoke endpoint path")]
        public string revokeEndpoint = "/admin/revoke";

        [Tooltip("Endorse revoke endpoint path")]
        public string endorseRevokeEndpoint = "/admin/endorse-revoke";

        [Tooltip("Reject revoke endpoint path")]
        public string rejectRevokeEndpoint = "/admin/reject-revoke";

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

        [Tooltip("Auto-process pending revocations on connect")]
        public bool autoProcessRevocations = false;

        // Computed URLs
        public string ProposeEndpoint => $"{gatewayBaseUrl}{proposeEndpoint}";
        public string EventStreamUrl => $"{gatewayBaseUrl}{eventStreamEndpoint}";
        public string SnapshotUrl => $"{gatewayBaseUrl}{snapshotEndpoint}";
        public string RevokeUrl => $"{gatewayBaseUrl}{revokeEndpoint}";
        public string EndorseRevokeUrl => $"{gatewayBaseUrl}{endorseRevokeEndpoint}";
        public string RejectRevokeUrl => $"{gatewayBaseUrl}{rejectRevokeEndpoint}";

        // Organization helpers
        public bool IsOrg1 => organizationId.ToLower() == "org1";
        public bool IsOrg2 => organizationId.ToLower() == "org2";
        public string MspId => IsOrg1 ? "Org1MSP" : "Org2MSP";
        public string OtherOrgId => IsOrg1 ? "org2" : "org1";
        public string OtherMspId => IsOrg1 ? "Org2MSP" : "Org1MSP";

        private void OnValidate()
        {
            // Ensure base URL doesn't end with slash
            if (!string.IsNullOrEmpty(gatewayBaseUrl) && gatewayBaseUrl.EndsWith("/"))
            {
                gatewayBaseUrl = gatewayBaseUrl.TrimEnd('/');
            }

            // Ensure endpoints start with slash
            EnsureStartsWithSlash(ref eventStreamEndpoint);
            EnsureStartsWithSlash(ref snapshotEndpoint);
            EnsureStartsWithSlash(ref proposeEndpoint);
            EnsureStartsWithSlash(ref revokeEndpoint);
            EnsureStartsWithSlash(ref endorseRevokeEndpoint);
            EnsureStartsWithSlash(ref rejectRevokeEndpoint);

            // Validate organization ID
            if (!string.IsNullOrEmpty(organizationId))
            {
                organizationId = organizationId.ToLower();
                if (organizationId != "org1" && organizationId != "org2")
                {
                    Debug.LogWarning($"[GatewayConfig] organizationId should be 'org1' or 'org2', got '{organizationId}'");
                }
            }
        }

        private void EnsureStartsWithSlash(ref string endpoint)
        {
            if (!string.IsNullOrEmpty(endpoint) && !endpoint.StartsWith("/"))
            {
                endpoint = "/" + endpoint;
            }
        }
    }
}
