// ============================================================================
// FILE: GatewayClient.cs
// REST client for Gateway API calls - Updated for Two-Org Model
// Supports revocation workflow: initiate, endorse, reject
// v2.0: Added annotation request support
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

        [Serializable]
        public class Position { public float x, y, z; }
        [Serializable]
        public class Rotation { public float qx, qy, qz, qw; }
    }

    [Serializable]
    public class QualityMetrics
    {
        public float confidence_mean;
        public float stability_rms;
        public int observation_count;
    }

    [Serializable]
    public class ProposeRequest
    {
        public string asset_id;
        public PoseSite pose_site;
        public QualityMetrics quality_metrics;
    }

    [Serializable]
    public class ProposeResponse
    {
        public bool success;
        public string claim_id;
        public string state;
        public string conflict_classification;
        public string payload_hash;
        public string error;
    }

    // =========================================================================
    // REVOCATION TYPES
    // =========================================================================

    [Serializable]
    public class RevokeRequest
    {
        public string asset_id;
        public string reason;
    }

    [Serializable]
    public class RevokeResponse
    {
        public bool success;
        public string claim_id;
        public string state;
        public string initiated_by;
        public string required_endorser;
        public string event_id;
        public string error;
    }

    [Serializable]
    public class EndorseRevokeRequest
    {
        public string asset_id;
    }

    [Serializable]
    public class EndorseRevokeResponse
    {
        public bool success;
        public string claim_id;
        public string state;
        public string initiated_by;
        public string endorsed_by;
        public bool anchor_deleted;
        public string event_id;
        public string error;
    }

    [Serializable]
    public class RejectRevokeRequest
    {
        public string asset_id;
        public string reason;
    }

    [Serializable]
    public class RejectRevokeResponse
    {
        public bool success;
        public string claim_id;
        public string state;
        public string initiated_by;
        public string rejected_by;
        public string rejection_reason;
        public bool anchor_preserved;
        public string event_id;
        public string error;
    }

    [Serializable]
    public class PendingRevocation
    {
        public string assetId;
        public string claimId;
        public string initiatedBy;
        public string initiatedAt;
        public string reason;
        public string status;
        public string requiredEndorser;
    }

    [Serializable]
    public class PendingRevocationsResponse
    {
        public PendingRevocation[] pendingRevocations;
        public int count;
        public string forOrg;
    }

    // =========================================================================
    // SNAPSHOT TYPES (v2.0: includes annotations)
    // =========================================================================

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
        public string state;
        public string tier;
        public string content_text;
        public string proposed_via_org;
        public bool endorsed_org1;
        public bool endorsed_org2;
    }

    // =========================================================================
    // ANNOTATION TYPES (v2.0)
    // =========================================================================

    [Serializable]
    public class AnnotationRequestData
    {
        public string asset_id;
        public string tier;
        public string class_name;
        public float confidence;
    }

    [Serializable]
    public class AnnotationResponse
    {
        public bool success;
        public string annotation_id;
        public string asset_id;
        public string state;
        public string tier;
        public string content_text;
        public string anchor_claim_id;
        public string proposed_via_org;
        public string activation_method;
        public string error;
    }

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
            if (gatewayConfig == null)
            {
                Debug.LogError("[GatewayClient] Initialize called with null config!");
                return;
            }
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
            return new QualityMetrics
            {
                confidence_mean = confidence,
                stability_rms = stabilityRms,
                observation_count = observationCount
            };
        }

        // =====================================================================
        // PROPOSE ANCHOR
        // =====================================================================

        public void ProposeAnchor(string assetId, PoseSite poseSite, QualityMetrics qualityMetrics,
            Action<ProposeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null)
            {
                Debug.LogError("[GatewayClient] Not initialized! Call Initialize() first.");
                onError?.Invoke("GatewayClient not initialized");
                return;
            }

            var request = new ProposeRequest
            {
                asset_id = assetId,
                pose_site = poseSite,
                quality_metrics = qualityMetrics
            };

            StartCoroutine(ProposeAnchorCoroutine(request, onSuccess, onError));
        }

        private IEnumerator ProposeAnchorCoroutine(ProposeRequest request,
            Action<ProposeResponse> onSuccess, Action<string> onError)
        {
            string url = config.ProposeEndpoint;
            string json = JsonUtility.ToJson(request);

            Debug.Log($"[GatewayClient] POST {url}");
            Debug.Log($"[GatewayClient] Body: {json}");

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
                Debug.Log($"[GatewayClient] Response ({webRequest.responseCode}): {responseText}");

                if (webRequest.result == UnityWebRequest.Result.Success && webRequest.responseCode >= 200 && webRequest.responseCode < 300)
                {
                    try
                    {
                        var response = JsonUtility.FromJson<ProposeResponse>(responseText);
                        Debug.Log($"[GatewayClient] ✓ Propose SUCCESS: claimId={response.claim_id}, state={response.state}");
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error: {e.Message}");
                    }
                }
                else
                {
                    string error = $"HTTP {webRequest.responseCode}: {webRequest.error} - {responseText}";
                    Debug.LogError($"[GatewayClient] Propose FAILED: {error}");
                    onError?.Invoke(error);
                }
            }
        }

        // =====================================================================
        // REVOCATION WORKFLOW
        // =====================================================================

        /// <summary>
        /// Initiate anchor revocation. Sets state to REVOKE_PENDING.
        /// Requires endorsement from the other organization to complete.
        /// </summary>
        public void RevokeAnchor(string assetId, string reason,
            Action<RevokeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null)
            {
                onError?.Invoke("GatewayClient not initialized");
                return;
            }

            var request = new RevokeRequest
            {
                asset_id = assetId,
                reason = reason ?? ""
            };

            StartCoroutine(RevokeAnchorCoroutine(request, onSuccess, onError));
        }

        private IEnumerator RevokeAnchorCoroutine(RevokeRequest request,
            Action<RevokeResponse> onSuccess, Action<string> onError)
        {
            string url = $"{config.gatewayBaseUrl}/admin/revoke";
            string json = JsonUtility.ToJson(request);

            Debug.Log($"[GatewayClient] POST {url} (Revoke)");

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
                        var response = JsonUtility.FromJson<RevokeResponse>(responseText);
                        Debug.Log($"[GatewayClient] ✓ Revoke initiated: state={response.state}, requiredEndorser={response.required_endorser}");
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error: {e.Message}");
                    }
                }
                else
                {
                    onError?.Invoke($"HTTP {webRequest.responseCode}: {webRequest.error} - {responseText}");
                }
            }
        }

        /// <summary>
        /// Endorse a pending revocation. Completes the revocation and deletes the anchor.
        /// Must be called by different organization than initiator.
        /// </summary>
        public void EndorseRevoke(string assetId,
            Action<EndorseRevokeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null)
            {
                onError?.Invoke("GatewayClient not initialized");
                return;
            }

            var request = new EndorseRevokeRequest { asset_id = assetId };
            StartCoroutine(EndorseRevokeCoroutine(request, onSuccess, onError));
        }

        private IEnumerator EndorseRevokeCoroutine(EndorseRevokeRequest request,
            Action<EndorseRevokeResponse> onSuccess, Action<string> onError)
        {
            string url = $"{config.gatewayBaseUrl}/admin/endorse-revoke";
            string json = JsonUtility.ToJson(request);

            Debug.Log($"[GatewayClient] POST {url} (EndorseRevoke)");

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
                        var response = JsonUtility.FromJson<EndorseRevokeResponse>(responseText);
                        Debug.Log($"[GatewayClient] ✓ Revoke endorsed: state={response.state}, anchorDeleted={response.anchor_deleted}");
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error: {e.Message}");
                    }
                }
                else
                {
                    onError?.Invoke($"HTTP {webRequest.responseCode}: {webRequest.error} - {responseText}");
                }
            }
        }

        /// <summary>
        /// Reject a pending revocation. Keeps anchor in ACTIVE state.
        /// Must be called by different organization than initiator.
        /// </summary>
        public void RejectRevoke(string assetId, string reason,
            Action<RejectRevokeResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null)
            {
                onError?.Invoke("GatewayClient not initialized");
                return;
            }

            var request = new RejectRevokeRequest { asset_id = assetId, reason = reason ?? "" };
            StartCoroutine(RejectRevokeCoroutine(request, onSuccess, onError));
        }

        private IEnumerator RejectRevokeCoroutine(RejectRevokeRequest request,
            Action<RejectRevokeResponse> onSuccess, Action<string> onError)
        {
            string url = $"{config.gatewayBaseUrl}/admin/reject-revoke";
            string json = JsonUtility.ToJson(request);

            Debug.Log($"[GatewayClient] POST {url} (RejectRevoke)");

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
                        var response = JsonUtility.FromJson<RejectRevokeResponse>(responseText);
                        Debug.Log($"[GatewayClient] ✓ Revoke rejected: state={response.state}, anchorPreserved={response.anchor_preserved}");
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error: {e.Message}");
                    }
                }
                else
                {
                    onError?.Invoke($"HTTP {webRequest.responseCode}: {webRequest.error} - {responseText}");
                }
            }
        }

        /// <summary>
        /// Get pending revocations that require this organization's action
        /// </summary>
        public void GetPendingRevocationsForMe(Action<PendingRevocationsResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null)
            {
                onError?.Invoke("GatewayClient not initialized");
                return;
            }
            StartCoroutine(GetPendingRevocationsCoroutine(onSuccess, onError));
        }

        private IEnumerator GetPendingRevocationsCoroutine(Action<PendingRevocationsResponse> onSuccess, Action<string> onError)
        {
            string url = $"{config.gatewayBaseUrl}/admin/pending-revocations/for-me";

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
                        var response = JsonUtility.FromJson<PendingRevocationsResponse>(webRequest.downloadHandler.text);
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error: {e.Message}");
                    }
                }
                else
                {
                    onError?.Invoke($"HTTP {webRequest.responseCode}: {webRequest.error}");
                }
            }
        }

        // =====================================================================
        // SNAPSHOT
        // =====================================================================

        public void FetchSnapshot(Action<SnapshotResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null)
            {
                onError?.Invoke("GatewayClient not initialized");
                return;
            }
            StartCoroutine(FetchSnapshotCoroutine(onSuccess, onError));
        }

        private IEnumerator FetchSnapshotCoroutine(Action<SnapshotResponse> onSuccess, Action<string> onError)
        {
            string url = config.SnapshotUrl;
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
                        var response = JsonUtility.FromJson<SnapshotResponse>(webRequest.downloadHandler.text);
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error: {e.Message}");
                    }
                }
                else
                {
                    onError?.Invoke($"HTTP {webRequest.responseCode}: {webRequest.error}");
                }
            }
        }

        // =====================================================================
        // ANNOTATION REQUEST (v2.0)
        // =====================================================================

        /// <summary>
        /// Request an AI-generated annotation for an asset.
        /// Calls POST /annotations/request on the gateway.
        /// The gateway orchestrates: mock/LLM generation → chaincode → SSE.
        /// </summary>
        public void RequestAnnotation(string assetId, string tier, string className, float confidence,
            Action<AnnotationResponse> onSuccess, Action<string> onError)
        {
            if (!isInitialized || config == null)
            {
                onError?.Invoke("GatewayClient not initialized");
                return;
            }

            var request = new AnnotationRequestData
            {
                asset_id = assetId,
                tier = tier,
                class_name = className,
                confidence = confidence
            };

            StartCoroutine(RequestAnnotationCoroutine(request, onSuccess, onError));
        }

        private IEnumerator RequestAnnotationCoroutine(AnnotationRequestData request,
            Action<AnnotationResponse> onSuccess, Action<string> onError)
        {
            string url = $"{config.gatewayBaseUrl}/annotations/request";
            string json = JsonUtility.ToJson(request);

            Debug.Log($"[GatewayClient] POST {url} (RequestAnnotation)");
            Debug.Log($"[GatewayClient] Body: {json}");

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
                Debug.Log($"[GatewayClient] Annotation Response ({webRequest.responseCode}): {responseText}");

                if (webRequest.result == UnityWebRequest.Result.Success && webRequest.responseCode >= 200 && webRequest.responseCode < 300)
                {
                    try
                    {
                        var response = JsonUtility.FromJson<AnnotationResponse>(responseText);
                        Debug.Log($"[GatewayClient] ✓ Annotation: state={response.state}, tier={response.tier}");
                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Parse error: {e.Message}");
                    }
                }
                else
                {
                    string error = $"HTTP {webRequest.responseCode}: {webRequest.error} - {responseText}";
                    Debug.LogError($"[GatewayClient] Annotation FAILED: {error}");
                    onError?.Invoke(error);
                }
            }
        }
    }
}