// ============================================================================
// FILE: GatewayClient.cs
// REST client for Gateway API calls.
// EP5 FIX: Explicit Initialize() method, better logging
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

    [Serializable]
    public class SnapshotResponse
    {
        public bool success;
        public SnapshotAssetState[] assets;
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
    }
}