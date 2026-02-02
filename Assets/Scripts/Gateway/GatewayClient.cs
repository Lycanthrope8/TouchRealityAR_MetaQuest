// ============================================================================
// FILE: GatewayClient.cs
// REST client for Gateway API calls (propose, resolve, snapshot).
// Uses UnityWebRequest - no third-party dependencies.
// ============================================================================

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// REST client for Gateway API.
    /// </summary>
    public class GatewayClient : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private GatewayConfig config;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;

        // Callbacks
        public event Action<ProposeResponse> OnProposeSuccess;
        public event Action<string> OnProposeError;
        public event Action<ResolveResponse> OnResolveSuccess;
        public event Action<string> OnResolveError;
        public event Action<SnapshotResponse> OnSnapshotSuccess;
        public event Action<string> OnSnapshotError;

        // Metrics
        private int totalRequests = 0;
        private int successfulRequests = 0;
        private int failedRequests = 0;

        public int TotalRequests => totalRequests;
        public int SuccessfulRequests => successfulRequests;
        public int FailedRequests => failedRequests;

        private void Awake()
        {
            if (config == null)
            {
                config = FindFirstObjectByType<GatewayConfig>();
                if (config == null)
                {
                    Debug.LogError("[GatewayClient] GatewayConfig not assigned!");
                }
            }
        }

        /// <summary>
        /// Set configuration at runtime
        /// </summary>
        public void SetConfig(GatewayConfig newConfig)
        {
            config = newConfig;
        }

        // ============================================================
        // PROPOSE CLAIM
        // ============================================================

        /// <summary>
        /// Propose an anchor claim to the gateway.
        /// </summary>
        /// <param name="assetId">Asset ID (e.g., "laptop_TAG_5")</param>
        /// <param name="poseSite">Pose in site frame coordinates</param>
        /// <param name="qualityMetrics">Quality metrics from detection</param>
        /// <param name="onSuccess">Success callback</param>
        /// <param name="onError">Error callback</param>
        public void ProposeAnchor(string assetId, PoseSite poseSite, QualityMetrics qualityMetrics,
            Action<ProposeResponse> onSuccess = null, Action<string> onError = null)
        {
            if (config == null)
            {
                string error = "GatewayConfig not configured";
                Debug.LogError($"[GatewayClient] {error}");
                onError?.Invoke(error);
                return;
            }

            StartCoroutine(ProposeAnchorCoroutine(assetId, poseSite, qualityMetrics, onSuccess, onError));
        }

        private IEnumerator ProposeAnchorCoroutine(string assetId, PoseSite poseSite, QualityMetrics qualityMetrics,
            Action<ProposeResponse> onSuccess, Action<string> onError)
        {
            totalRequests++;
            string url = config.ProposeEndpoint;

            // Build request body
            ProposeRequest requestBody = new ProposeRequest
            {
                asset_id = assetId,
                pose_site = poseSite,
                quality_metrics = qualityMetrics
            };

            string jsonBody = JsonUtility.ToJson(requestBody);

            if (enableDebugLogs || config.enableDebugLogs)
            {
                Debug.Log($"[GatewayClient] POST {url}");
                Debug.Log($"[GatewayClient] Body: {jsonBody}");
            }

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-api-key", config.apiKey);
                request.timeout = (int)config.requestTimeoutSeconds;

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    successfulRequests++;
                    string responseText = request.downloadHandler.text;

                    if (enableDebugLogs || config.enableDebugLogs)
                    {
                        Debug.Log($"[GatewayClient] Response ({request.responseCode}): {responseText}");
                    }

                    try
                    {
                        ProposeResponse response = JsonUtility.FromJson<ProposeResponse>(responseText);
                        
                        if (response.success)
                        {
                            Debug.Log($"[GatewayClient] ✓ Propose SUCCESS: claimId={response.claim_id}, state={response.state}");
                            onSuccess?.Invoke(response);
                            OnProposeSuccess?.Invoke(response);
                        }
                        else
                        {
                            string error = response.error ?? "Unknown error";
                            Debug.LogWarning($"[GatewayClient] Propose failed: {error}");
                            onError?.Invoke(error);
                            OnProposeError?.Invoke(error);
                        }
                    }
                    catch (Exception e)
                    {
                        string error = $"Failed to parse response: {e.Message}";
                        Debug.LogError($"[GatewayClient] {error}");
                        onError?.Invoke(error);
                        OnProposeError?.Invoke(error);
                    }
                }
                else
                {
                    failedRequests++;
                    string error = $"HTTP {request.responseCode}: {request.error}";
                    
                    // Try to extract error from response body
                    if (!string.IsNullOrEmpty(request.downloadHandler?.text))
                    {
                        try
                        {
                            var errorResponse = JsonUtility.FromJson<ErrorResponse>(request.downloadHandler.text);
                            if (!string.IsNullOrEmpty(errorResponse.error))
                            {
                                error = errorResponse.error;
                            }
                        }
                        catch { }
                    }

                    Debug.LogError($"[GatewayClient] Propose error: {error}");
                    onError?.Invoke(error);
                    OnProposeError?.Invoke(error);
                }
            }
        }

        // ============================================================
        // RESOLVE ASSET
        // ============================================================

        /// <summary>
        /// Resolve the active anchor for an asset.
        /// </summary>
        public void ResolveAsset(string assetId, Action<ResolveResponse> onSuccess = null, Action<string> onError = null)
        {
            if (config == null)
            {
                string error = "GatewayConfig not configured";
                Debug.LogError($"[GatewayClient] {error}");
                onError?.Invoke(error);
                return;
            }

            StartCoroutine(ResolveAssetCoroutine(assetId, onSuccess, onError));
        }

        private IEnumerator ResolveAssetCoroutine(string assetId, Action<ResolveResponse> onSuccess, Action<string> onError)
        {
            totalRequests++;
            string url = config.GetAssetResolveUrl(assetId);

            if (enableDebugLogs || config.enableDebugLogs)
            {
                Debug.Log($"[GatewayClient] GET {url}");
            }

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("x-api-key", config.apiKey);
                request.timeout = (int)config.requestTimeoutSeconds;

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    successfulRequests++;
                    string responseText = request.downloadHandler.text;

                    if (enableDebugLogs || config.enableDebugLogs)
                    {
                        Debug.Log($"[GatewayClient] Response ({request.responseCode}): {responseText}");
                    }

                    try
                    {
                        ResolveResponse response = JsonUtility.FromJson<ResolveResponse>(responseText);
                        onSuccess?.Invoke(response);
                        OnResolveSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        string error = $"Failed to parse response: {e.Message}";
                        Debug.LogError($"[GatewayClient] {error}");
                        onError?.Invoke(error);
                        OnResolveError?.Invoke(error);
                    }
                }
                else
                {
                    failedRequests++;
                    string error = $"HTTP {request.responseCode}: {request.error}";
                    Debug.LogError($"[GatewayClient] Resolve error: {error}");
                    onError?.Invoke(error);
                    OnResolveError?.Invoke(error);
                }
            }
        }

        // ============================================================
        // FETCH SNAPSHOT
        // ============================================================

        /// <summary>
        /// Fetch status snapshot for catch-up after reconnect.
        /// </summary>
        public void FetchSnapshot(Action<SnapshotResponse> onSuccess = null, Action<string> onError = null)
        {
            if (config == null)
            {
                string error = "GatewayConfig not configured";
                Debug.LogError($"[GatewayClient] {error}");
                onError?.Invoke(error);
                return;
            }

            StartCoroutine(FetchSnapshotCoroutine(onSuccess, onError));
        }

        private IEnumerator FetchSnapshotCoroutine(Action<SnapshotResponse> onSuccess, Action<string> onError)
        {
            totalRequests++;
            string url = config.SnapshotUrl;

            if (enableDebugLogs || config.enableDebugLogs)
            {
                Debug.Log($"[GatewayClient] GET {url} (snapshot)");
            }

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("x-api-key", config.apiKey);
                request.timeout = (int)config.requestTimeoutSeconds;

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    successfulRequests++;
                    string responseText = request.downloadHandler.text;

                    if (enableDebugLogs || config.enableDebugLogs)
                    {
                        Debug.Log($"[GatewayClient] Snapshot response: {responseText}");
                    }

                    try
                    {
                        SnapshotResponse response = JsonUtility.FromJson<SnapshotResponse>(responseText);
                        Debug.Log($"[GatewayClient] ✓ Snapshot received: {response.assets?.Count ?? 0} assets");
                        onSuccess?.Invoke(response);
                        OnSnapshotSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        string error = $"Failed to parse snapshot: {e.Message}";
                        Debug.LogError($"[GatewayClient] {error}");
                        onError?.Invoke(error);
                        OnSnapshotError?.Invoke(error);
                    }
                }
                else
                {
                    failedRequests++;
                    string error = $"HTTP {request.responseCode}: {request.error}";
                    Debug.LogError($"[GatewayClient] Snapshot error: {error}");
                    onError?.Invoke(error);
                    OnSnapshotError?.Invoke(error);
                }
            }
        }

        // ============================================================
        // DATA MODELS
        // ============================================================

        [Serializable]
        public class ProposeRequest
        {
            public string asset_id;
            public PoseSite pose_site;
            public QualityMetrics quality_metrics;
        }

        [Serializable]
        public class PoseSite
        {
            public Position position;
            public Rotation rotation;
        }

        [Serializable]
        public class Position
        {
            public float x;
            public float y;
            public float z;
        }

        [Serializable]
        public class Rotation
        {
            public float qx;
            public float qy;
            public float qz;
            public float qw;
        }

        [Serializable]
        public class QualityMetrics
        {
            public float confidence_mean;
            public float stability_rms;
            public int observation_count;
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

        [Serializable]
        public class ResolveResponse
        {
            public bool success;
            public string asset_id;
            public string claim_id;
            public string state;
            public string activated_at;
            public string publisher_id;
            public int endorsement_count;
            public string message;
        }

        [Serializable]
        public class ErrorResponse
        {
            public bool success;
            public string error;
        }

        // ============================================================
        // HELPER METHODS
        // ============================================================

        /// <summary>
        /// Create PoseSite from Unity Pose
        /// </summary>
        public static PoseSite CreatePoseSite(Pose pose)
        {
            return new PoseSite
            {
                position = new Position
                {
                    x = pose.position.x,
                    y = pose.position.y,
                    z = pose.position.z
                },
                rotation = new Rotation
                {
                    qx = pose.rotation.x,
                    qy = pose.rotation.y,
                    qz = pose.rotation.z,
                    qw = pose.rotation.w
                }
            };
        }

        /// <summary>
        /// Create QualityMetrics from detection data
        /// </summary>
        public static QualityMetrics CreateQualityMetrics(float confidence, float stabilityRms, int observations)
        {
            return new QualityMetrics
            {
                confidence_mean = confidence,
                stability_rms = stabilityRms,
                observation_count = observations
            };
        }
    }
}
