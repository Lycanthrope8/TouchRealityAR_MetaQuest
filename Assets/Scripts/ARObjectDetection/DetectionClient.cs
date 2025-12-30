// ============================================================================
// FILE 3: DetectionClient.cs
// Send frame_id and capture_time to server
// ============================================================================

using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

namespace ARObjectDetection
{
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
        /// Send frame with metadata
        /// </summary>
        public IEnumerator SendFrameForDetection(
    CapturedFrame capturedFrame,
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
        /// Send frame with retry logic
        /// </summary>
        public IEnumerator SendFrameWithRetry(
            CapturedFrame capturedFrame,
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
