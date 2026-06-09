// ============================================================================
// FILE: GatewayClient.cs
// REST client for Gateway API calls - Updated for Two-Org Model
//
// PHASE 6: The annotation service has been removed. RequestAnnotation(),
// AnnotationRequestData, and AnnotationResponse are deleted. The LLM-mediated
// flow lives in SkillGatewayClient instead. SnapshotAnnotationState remains as a
// passive DTO so existing snapshot parsing is unaffected, but it is no longer
// consumed by GatewaySync.
// ============================================================================

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway
{
    [Serializable]
    public class PoseSite
    {
        public Position position;
        public Rotation rotation;
        [Serializable] public class Position { public float x, y, z; }
        [Serializable] public class Rotation { public float qx, qy, qz, qw; }
    }

    [Serializable]
    public class QualityMetrics
    {
        public float confidence_mean;
        public float stability_rms;
        public int observation_count;
    }

    [Serializable] public class ProposeRequest { public string asset_id; public PoseSite pose_site; public QualityMetrics quality_metrics; }
    [Serializable] public class ProposeResponse { public bool success; public string claim_id; public string state; public string conflict_classification; public string payload_hash; public string error; }

    // Revocation types (unchanged)
    [Serializable] public class RevokeRequest { public string asset_id; public string reason; }
    [Serializable] public class RevokeResponse { public bool success; public string claim_id; public string state; public string initiated_by; public string required_endorser; public string event_id; public string error; }
    [Serializable] public class EndorseRevokeRequest { public string asset_id; }
    [Serializable] public class EndorseRevokeResponse { public bool success; public string claim_id; public string state; public string initiated_by; public string endorsed_by; public bool anchor_deleted; public string event_id; public string error; }
    [Serializable] public class RejectRevokeRequest { public string asset_id; public string reason; }
    [Serializable] public class RejectRevokeResponse { public bool success; public string claim_id; public string state; public string initiated_by; public string rejected_by; public string rejection_reason; public bool anchor_preserved; public string event_id; public string error; }
    [Serializable] public class PendingRevocation { public string assetId; public string claimId; public string initiatedBy; public string initiatedAt; public string reason; public string status; public string requiredEndorser; }
    [Serializable] public class PendingRevocationsResponse { public PendingRevocation[] pendingRevocations; public int count; public string forOrg; }

    // Snapshot types (v2.1: annotations include intent_type)
    [Serializable]
    public class SnapshotResponse
    {
        public bool success;
        public SnapshotAssetState[] assets;
        public SnapshotAnnotationState[] annotations;
        public string last_event_id;
    }

    [Serializable]
    public class SnapshotAssetState
    {
        public string asset_id;
        public string claim_id;
        public string state;
        public string publisher_id;
        public int endorsement_count;
    }

    [Serializable]
    public class SnapshotAnnotationState
    {
        public string asset_id;
        public string annotation_id;
        public string intent_type;      // v2.1
        public string state;
        public string tier;
        public string content_text;
        public string proposed_via_org;
        public bool endorsed_org1;
        public bool endorsed_org2;
    }

    // Annotation types REMOVED in Phase 6 (annotation service deleted).

    // =========================================================================
    // GATEWAY CLIENT
    // =========================================================================

    public class GatewayClient : MonoBehaviour
    {
        private GatewayConfig config;
        private bool isInitialized = false;
        public bool IsInitialized => isInitialized;

        public void Initialize(GatewayConfig gatewayConfig)
        {
            if (gatewayConfig == null) { Debug.LogError("[GatewayClient] Initialize called with null config!"); return; }
            config = gatewayConfig;
            isInitialized = true;
            Debug.Log($"[GatewayClient] ✓ Initialized: {config.gatewayBaseUrl}");
        }

        public static PoseSite CreatePoseSite(Pose pose)
        {
            return new PoseSite
            {
                position = new PoseSite.Position { x = pose.position.x, y = pose.position.y, z = pose.position.z },
                rotation = new PoseSite.Rotation { qx = pose.rotation.x, qy = pose.rotation.y, qz = pose.rotation.z, qw = pose.rotation.w }
            };
        }

        public static QualityMetrics CreateQualityMetrics(float confidence, float stabilityRms, int observationCount)
        {
            return new QualityMetrics { confidence_mean = confidence, stability_rms = stabilityRms, observation_count = observationCount };
        }

        // =====================================================================
        // PROPOSE ANCHOR (unchanged)
        // =====================================================================

        public void ProposeAnchor(string assetId, PoseSite poseSite, QualityMetrics qualityMetrics,
            Action<ProposeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null) { onError?.Invoke("GatewayClient not initialized"); return; }
            var request = new ProposeRequest { asset_id = assetId, pose_site = poseSite, quality_metrics = qualityMetrics };
            StartCoroutine(PostCoroutine<ProposeRequest, ProposeResponse>(config.ProposeEndpoint, request, onSuccess, onError));
        }

        // =====================================================================
        // REVOCATION WORKFLOW (unchanged)
        // =====================================================================

        public void RevokeAnchor(string assetId, string reason, Action<RevokeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null) { onError?.Invoke("GatewayClient not initialized"); return; }
            StartCoroutine(PostCoroutine<RevokeRequest, RevokeResponse>($"{config.gatewayBaseUrl}/admin/revoke",
                new RevokeRequest { asset_id = assetId, reason = reason ?? "" }, onSuccess, onError));
        }

        public void EndorseRevoke(string assetId, Action<EndorseRevokeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null) { onError?.Invoke("GatewayClient not initialized"); return; }
            StartCoroutine(PostCoroutine<EndorseRevokeRequest, EndorseRevokeResponse>($"{config.gatewayBaseUrl}/admin/endorse-revoke",
                new EndorseRevokeRequest { asset_id = assetId }, onSuccess, onError));
        }

        public void RejectRevoke(string assetId, string reason, Action<RejectRevokeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null) { onError?.Invoke("GatewayClient not initialized"); return; }
            StartCoroutine(PostCoroutine<RejectRevokeRequest, RejectRevokeResponse>($"{config.gatewayBaseUrl}/admin/reject-revoke",
                new RejectRevokeRequest { asset_id = assetId, reason = reason ?? "" }, onSuccess, onError));
        }

        public void GetPendingRevocationsForMe(Action<PendingRevocationsResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null) { onError?.Invoke("GatewayClient not initialized"); return; }
            StartCoroutine(GetCoroutine<PendingRevocationsResponse>($"{config.gatewayBaseUrl}/admin/pending-revocations/for-me", onSuccess, onError));
        }

        // =====================================================================
        // SNAPSHOT (unchanged)
        // =====================================================================

        public void FetchSnapshot(Action<SnapshotResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null) { onError?.Invoke("GatewayClient not initialized"); return; }
            StartCoroutine(GetCoroutine<SnapshotResponse>(config.SnapshotUrl, onSuccess, onError));
        }

        // =====================================================================
        // ANNOTATION REQUEST — REMOVED in Phase 6.
        // The LLM-mediated flow now lives in SkillGatewayClient.Interpret/Execute.
        // =====================================================================

        // =====================================================================
        // GENERIC HTTP HELPERS
        // =====================================================================

        private IEnumerator PostCoroutine<TReq, TRes>(string url, TReq request, Action<TRes> onSuccess, Action<string> onError)
        {
            string json = JsonUtility.ToJson(request);
            Debug.Log($"[GatewayClient] POST {url}");

            using (var webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                webRequest.SetRequestHeader("x-api-key", config.apiKey);
                webRequest.SetRequestHeader("x-org-id", config.organizationId);
                webRequest.timeout = config.requestTimeoutSeconds;

                yield return webRequest.SendWebRequest();

                string responseText = webRequest.downloadHandler?.text ?? "";

                if (webRequest.result == UnityWebRequest.Result.Success && webRequest.responseCode >= 200 && webRequest.responseCode < 300)
                {
                    try
                    {
                        var response = JsonUtility.FromJson<TRes>(responseText);
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e) { onError?.Invoke($"Parse error: {e.Message}"); }
                }
                else
                {
                    onError?.Invoke($"HTTP {webRequest.responseCode}: {webRequest.error} - {responseText}");
                }
            }
        }

        private IEnumerator GetCoroutine<TRes>(string url, Action<TRes> onSuccess, Action<string> onError)
        {
            Debug.Log($"[GatewayClient] GET {url}");

            using (var webRequest = UnityWebRequest.Get(url))
            {
                webRequest.SetRequestHeader("x-api-key", config.apiKey);
                webRequest.SetRequestHeader("x-org-id", config.organizationId);
                webRequest.timeout = config.requestTimeoutSeconds;

                yield return webRequest.SendWebRequest();

                if (webRequest.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var response = JsonUtility.FromJson<TRes>(webRequest.downloadHandler.text);
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e) { onError?.Invoke($"Parse error: {e.Message}"); }
                }
                else
                {
                    onError?.Invoke($"HTTP {webRequest.responseCode}: {webRequest.error}");
                }
            }
        }
    }
}