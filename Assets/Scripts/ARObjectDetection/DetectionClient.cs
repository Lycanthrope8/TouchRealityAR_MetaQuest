using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

namespace ARObjectDetection
{
          /// <summary>
          /// Handles network communication with YOLO detection server
          /// </summary>
          public class DetectionClient
          {
                    private DetectionConfig config;
                    private int pendingRequests = 0;
                    private Queue<Action> requestQueue = new Queue<Action>();

                    public DetectionClient(DetectionConfig config)
                    {
                              this.config = config;
                    }

                    /// <summary>
                    /// Check server health
                    /// </summary>
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
                    /// Send frame for detection
                    /// </summary>
                    public IEnumerator SendFrameForDetection(
                        byte[] jpegData,
                        Action<DetectionResponse> onSuccess,
                        Action<string> onError)
                    {
                              // Check if we're at max pending requests
                              if (pendingRequests >= config.maxPendingRequests)
                              {
                                        if (config.enablePerformanceLogging)
                                                  Debug.LogWarning($"Max pending requests reached ({config.maxPendingRequests}), dropping frame");

                                        onError?.Invoke("Max pending requests reached");
                                        yield break;
                              }

                              pendingRequests++;
                              float startTime = Time.time;

                              // Create multipart form data
                              List<IMultipartFormSection> formData = new List<IMultipartFormSection>();
                              formData.Add(new MultipartFormFileSection("file", jpegData, "frame.jpg", "image/jpeg"));

                              using (UnityWebRequest request = UnityWebRequest.Post(config.DetectEndpoint, formData))
                              {
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
                        byte[] jpegData,
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
                                            jpegData,
                                            (resp) => { response = resp; success = true; requestCompleted = true; },
                                            (err) => { error = err; requestCompleted = true; }
                                        );

                                        // Wait for request to complete
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

                                                  yield return new WaitForSeconds(0.5f * attempts);  // Exponential backoff
                                        }
                              }

                              // All retries failed
                              if (!success)
                              {
                                        onError?.Invoke($"Failed after {attempts} attempts");
                              }
                    }

                    /// <summary>
                    /// Get number of pending requests
                    /// </summary>
                    public int GetPendingRequestCount()
                    {
                              return pendingRequests;
                    }
          }
}