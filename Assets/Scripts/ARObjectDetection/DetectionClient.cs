// ============================================================================
// FILE: DetectionClient.cs
// Send frame with optional pose hints for ArUco IPPE disambiguation
// ============================================================================

using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace ARObjectDetection
{
    /// <summary>
    /// Optional pose hint for ArUco marker disambiguation.
    /// Pass the last known rvec/tvec to help server disambiguate IPPE solutions.
    /// </summary>
    public struct PoseHint
    {
        public float[] rvec;  // Rodrigues rotation vector (3 elements)
        public float[] tvec;  // Translation vector (3 elements)

        public bool IsValid => rvec != null && rvec.Length == 3 &&
                               tvec != null && tvec.Length == 3;

        public static PoseHint Invalid => new PoseHint { rvec = null, tvec = null };
    }

    public class DetectionClient
    {
        private DetectionConfig config;
        private int pendingRequests = 0;

        public DetectionClient(DetectionConfig config)
        {
            this.config = config;
        }

        public IEnumerator CheckServerHealth(Action<bool> callback)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(config.HealthEndpoint))
            {
                request.timeout = (int)config.requestTimeout;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"Server health check: OK - {request.downloadHandler.text}");
                    callback?.Invoke(true);
                }
                else
                {
                    Debug.LogError($"Server health check failed: {request.error}");
                    callback?.Invoke(false);
                }
            }
        }

        /// <summary>
        /// Send frame with metadata and optional pose hint for disambiguation
        /// </summary>
        public IEnumerator SendFrameForDetection(
            CapturedFrame capturedFrame,
            PoseHint poseHint,
            Action<DetectionResponse> onSuccess,
            Action<string> onError)
        {
            if (pendingRequests >= config.maxPendingRequests)
            {
                if (config.enablePerformanceLogging)
                    Debug.LogWarning($"Max pending requests reached ({config.maxPendingRequests}), dropping frame");

                onError?.Invoke("Max pending requests reached");
                yield break;
            }

            pendingRequests++;
            float startTime = Time.time;

            // RAW BINARY POST - Maximum performance
            using (UnityWebRequest request = new UnityWebRequest(config.DetectEndpoint, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(capturedFrame.jpegData);
                request.downloadHandler = new DownloadHandlerBuffer();

                // Essential headers
                request.SetRequestHeader("Content-Type", "image/jpeg");

                // Add pose hint headers if valid (for ArUco IPPE disambiguation)
                if (poseHint.IsValid)
                {
                    // Format: comma-separated values with invariant culture (decimal point)
                    string rvecStr = string.Format(CultureInfo.InvariantCulture,
                        "{0:F6},{1:F6},{2:F6}",
                        poseHint.rvec[0], poseHint.rvec[1], poseHint.rvec[2]);
                    string tvecStr = string.Format(CultureInfo.InvariantCulture,
                        "{0:F6},{1:F6},{2:F6}",
                        poseHint.tvec[0], poseHint.tvec[1], poseHint.tvec[2]);

                    request.SetRequestHeader("X-Hint-Rvec", rvecStr);
                    request.SetRequestHeader("X-Hint-Tvec", tvecStr);

                    if (config.enablePerformanceLogging)
                    {
                        Debug.Log($"[DetectionClient] Sending pose hint: rvec={rvecStr}, tvec={tvecStr}");
                    }
                }

                request.timeout = (int)config.requestTimeout;

                yield return request.SendWebRequest();

                pendingRequests--;
                float latency = Time.time - startTime;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string jsonResponse = request.downloadHandler.text;
                        DetectionResponse response = JsonUtility.FromJson<DetectionResponse>(jsonResponse);

                        // Set metadata (server doesn't receive these anymore, but we track them client-side)
                        response.frame_id = capturedFrame.frameId;
                        response.capture_time = capturedFrame.captureTime;

                        float age = Time.realtimeSinceStartup - response.capture_time;
                        float netPlusOverhead = latency - response.inference_time;
                        float kb = capturedFrame.jpegData.Length / 1024f;
                        Debug.Log($"[Payload] Sent: {capturedFrame.width}×{capturedFrame.height}, Size: {kb:F1} KB");
                        Debug.Log($"[Perf] frame={capturedFrame.frameId} age={age:F3}s latency={latency:F3}s inf={response.inference_time:F3}s net+oh={netPlusOverhead:F3}s size={kb:F1}KB");

                        if (config.enablePerformanceLogging)
                        {
                            Debug.Log($"Detection successful: {response.count} objects, " +
                                    $"Latency: {latency:F3}s, " +
                                    $"Server inference: {response.inference_time:F3}s");
                        }

                        onSuccess?.Invoke(response);
                    }
                    catch (Exception e)
                    {
                        string error = $"Failed to parse response: {e.Message}";
                        Debug.LogError(error);
                        onError?.Invoke(error);
                    }
                }
                else
                {
                    string error = $"Request failed: {request.error} (Code: {request.responseCode})";
                    Debug.LogError(error);
                    onError?.Invoke(error);
                }
            }
        }

        /// <summary>
        /// Send frame with retry logic (no pose hint - backwards compatible)
        /// </summary>
        public IEnumerator SendFrameWithRetry(
            CapturedFrame capturedFrame,
            Action<DetectionResponse> onSuccess,
            Action<string> onError)
        {
            // Call overload with invalid (no) hint
            yield return SendFrameWithRetry(capturedFrame, PoseHint.Invalid, onSuccess, onError);
        }

        /// <summary>
        /// Send frame with retry logic and optional pose hint
        /// </summary>
        public IEnumerator SendFrameWithRetry(
            CapturedFrame capturedFrame,
            PoseHint poseHint,
            Action<DetectionResponse> onSuccess,
            Action<string> onError)
        {
            int attempts = 0;
            bool success = false;

            while (attempts < config.maxRetryAttempts && !success)
            {
                attempts++;

                bool requestCompleted = false;
                DetectionResponse response = null;
                string error = null;

                yield return SendFrameForDetection(
                    capturedFrame,
                    poseHint,
                    (resp) => { response = resp; success = true; requestCompleted = true; },
                    (err) => { error = err; requestCompleted = true; }
                );

                while (!requestCompleted)
                    yield return null;

                if (success)
                {
                    onSuccess?.Invoke(response);
                    yield break;
                }
                else if (attempts < config.maxRetryAttempts)
                {
                    if (config.enablePerformanceLogging)
                        Debug.LogWarning($"Retry attempt {attempts}/{config.maxRetryAttempts}");

                    yield return new WaitForSeconds(0.5f * attempts);
                }
            }

            if (!success)
            {
                onError?.Invoke($"Failed after {attempts} attempts");
            }
        }

        public int GetPendingRequestCount()
        {
            return pendingRequests;
        }
    }
}