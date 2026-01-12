using UnityEngine;
using UnityEngine.Events;
using PassthroughCameraSamples;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ARObjectDetection
{
    public class ARDetectionManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private DetectionConfig config;
        [SerializeField] private TrackerConfig trackerConfig;
        [Header("References")]
        [SerializeField] private WebCamTextureManager webCamTextureManager;
        [SerializeField] private DetectionVisualizer visualizer;
        [SerializeField] private DetectionVisualizer3D visualizer3D;
        [SerializeField] private ObjectTracker tracker;

        [Header("Events")]
        public UnityEvent<DetectionResponse> OnDetectionReceived;
        public UnityEvent<string> OnDetectionError;

        private FrameCaptureService frameCaptureService;
        private DetectionClient detectionClient;

        private bool isRunning = false;
        private bool serverHealthy = false;
        private DetectionMetrics metrics = new DetectionMetrics();

        private DetectionResponse latestDetection;
        private float lastFrameTime;
        private float lastSendTime;
        private float lastMetricsLogTime = 0f;

        public DetectionMetrics Metrics => metrics;
        public DetectionResponse LatestDetection => latestDetection;
        public bool IsRunning => isRunning;
        public bool ServerHealthy => serverHealthy;
        public ObjectTracker Tracker => tracker;

        private int lastAcceptedFrameId = -1;
        public float maxResponseAgeToAccept = 0.8f; // try 0.5–0.8


        private void Awake()
        {
            if (config == null)
            {
                Debug.LogError("DetectionConfig is not assigned!");
                enabled = false;
                return;
            }

            if (trackerConfig == null)
            {
                Debug.LogError("TrackerConfig is not assigned!");
                enabled = false;
                return;
            }

            if (webCamTextureManager == null)
            {
                webCamTextureManager = FindFirstObjectByType<WebCamTextureManager>();
                if (webCamTextureManager == null)
                {
                    Debug.LogError("WebCamTextureManager not found in scene!");
                    enabled = false;
                    return;
                }
            }

            if (tracker == null)
            {
                tracker = GetComponent<ObjectTracker>();
                if (tracker == null)
                {
                    Debug.LogError("ObjectTracker component not found!");
                    enabled = false;
                    return;
                }
            }

            frameCaptureService = new FrameCaptureService(config);
            detectionClient = new DetectionClient(config);

            Debug.Log("ARDetectionManager initialized with 3D IoU tracking and backpressure");
        }

        private void Start()
        {
            StartCoroutine(CheckServerHealthAndStart());
        }

        private IEnumerator CheckServerHealthAndStart()
        {
            Debug.Log($"Checking server health at: {config.serverUrl}");

            yield return detectionClient.CheckServerHealth((healthy) =>
            {
                serverHealthy = healthy;
                if (healthy)
                {
                    Debug.Log("Server is healthy, starting detection");
                    StartDetection();
                }
                else
                {
                    Debug.LogError($"Server not reachable at {config.serverUrl}");
                }
            });
        }

        public void StartDetection()
        {
            if (isRunning)
            {
                Debug.LogWarning("Detection already running");
                return;
            }

            isRunning = true;
            metrics.Reset();
            Debug.Log("Detection started with 3D IoU tracking and strict backpressure");
        }

        public void StopDetection()
        {
            isRunning = false;
            Debug.Log("Detection stopped");
        }

        private void Update()
        {
            if (!isRunning || webCamTextureManager == null)
                return;

            WebCamTexture webCamTexture = webCamTextureManager.WebCamTexture;
            if (webCamTexture == null || !webCamTexture.isPlaying)
                return;

            if (Time.time - lastFrameTime > 0)
            {
                metrics.captureFrameRate = 1f / (Time.time - lastFrameTime);
            }
            lastFrameTime = Time.time;
            metrics.totalFramesCaptured++;

            // Check backpressure BEFORE capture/encode
            if (detectionClient.GetPendingRequestCount() >= config.maxPendingRequests)
            {
                metrics.droppedFrames++;
                return;
            }

            if (frameCaptureService.ShouldCaptureFrame())
            {
                CapturedFrame capturedFrame = frameCaptureService.CaptureFrameWithMetadata(webCamTexture);

                if (capturedFrame != null && capturedFrame.jpegData != null && capturedFrame.jpegData.Length > 0)
                {
                    metrics.framesSent++;

                    if (Time.time - lastSendTime > 0)
                    {
                        metrics.sendFrameRate = 1f / (Time.time - lastSendTime);
                    }
                    lastSendTime = Time.time;

                    StartCoroutine(SendFrameCoroutine(capturedFrame));
                }
            }
        }

        private void LateUpdate()
        {
            if (Time.time - lastMetricsLogTime > 2f)
            {
                lastMetricsLogTime = Time.time;

                if (config.enablePerformanceLogging)
                {
                    Debug.Log("=== PHASE 2 METRICS (3D IoU) ===");
                    Debug.Log($"Avg Latency: {metrics.averageLatency:F3}s ({metrics.averageLatency * 1000:F0}ms)");
                    Debug.Log($"Send Rate: {metrics.sendFrameRate:F1} Hz");
                    Debug.Log($"Success: {metrics.successfulDetections}, Failed: {metrics.failedRequests}, Dropped: {metrics.droppedFrames}");
                    Debug.Log($"Out-of-order: {metrics.outOfOrderResponses}, Late: {metrics.lateResponses}");
                    Debug.Log($"Pending Requests: {detectionClient.GetPendingRequestCount()}/{config.maxPendingRequests}");

                    if (tracker != null)
                    {
                        int activeCount = tracker.ActiveTrackCount;
                        int confirmedCount = tracker.ConfirmedTrackCount;
                        float confirmedRatio = activeCount > 0 ? (float)confirmedCount / activeCount : 0f;

                        Debug.Log($"Tracks: {activeCount} active, {confirmedCount} confirmed ({confirmedRatio:P0})");

                        if (confirmedRatio < 0.5f && activeCount > 5)
                        {
                            Debug.LogWarning("⚠️ Low confirmed ratio - check thresholds!");
                        }
                    }

                    Debug.Log("================================");
                }
            }
        }

        private IEnumerator SendFrameCoroutine(CapturedFrame capturedFrame)
        {
            float startTime = Time.time;

            yield return detectionClient.SendFrameWithRetry(
                capturedFrame,
                OnDetectionSuccess,
                OnDetectionFailed
            );

            float latency = Time.time - startTime;
            metrics.RecordLatency(latency);
        }

        private void OnDetectionSuccess(DetectionResponse response)
        {

            float responseAge = Time.realtimeSinceStartup - response.capture_time;
            Debug.Log($"[Server Response] image_size={response.image_size[0]}×{response.image_size[1]}, " +
                        $"processed_size={response.processed_size[0]}×{response.processed_size[1]}");

            // Drop stale
            if (responseAge > maxResponseAgeToAccept)
            {
                Debug.LogWarning($"[ARDetectionManager] Dropping stale response frame={response.frame_id} age={responseAge:F3}s");
                return;
            }

            // Drop out-of-order
            if (response.frame_id <= lastAcceptedFrameId)
            {
                Debug.LogWarning($"[ARDetectionManager] Dropping out-of-order response frame={response.frame_id} last={lastAcceptedFrameId}");
                return;
            }
            lastAcceptedFrameId = response.frame_id;

            if (responseAge > 0.5f)
            {
                metrics.lateResponses++;
            }

            latestDetection = response;
            metrics.successfulDetections++;
            metrics.currentDetectionCount = response.count;

            // DEBUG: Log filter status
            Debug.Log($"[Filter DEBUG] enableClassFilter={config.enableClassFilter}, classFilter={(config.classFilter != null ? config.classFilter.Length.ToString() : "null")} classes");

            // ====== CLASS FILTER (WHITELIST) - APPLIED FIRST ======
            if (config.enableClassFilter && config.classFilter != null && config.classFilter.Length > 0)
            {
                int beforeCount = response.detections.Count;

                // Remove detections NOT in whitelist
                response.detections.RemoveAll(d =>
                {
                    // Check if this detection's class is in the allowed list
                    bool isAllowed = false;
                    string detectionClass = d.class_name.ToLower().Trim();

                    foreach (string allowedClass in config.classFilter)
                    {
                        string allowedClassLower = allowedClass.ToLower().Trim();

                        if (detectionClass == allowedClassLower)
                        {
                            isAllowed = true;
                            break;
                        }
                    }

                    // Log what's being blocked/allowed
                    if (!isAllowed)
                    {
                        Debug.Log($"[Filter] BLOCKED: '{d.class_name}'");
                    }
                    else if (config.enablePerformanceLogging)
                    {
                        Debug.Log($"[Filter] ALLOWED: '{d.class_name}'");
                    }

                    return !isAllowed; // Remove if NOT allowed
                });

                response.count = response.detections.Count;

                int filteredCount = beforeCount - response.count;
                if (filteredCount > 0)
                {
                    Debug.Log($"[Filter] Blocked {filteredCount} objects. Kept: {response.count}");
                }
            }

            // Filter by confidence threshold (AFTER class filter)
            if (config.confidenceThreshold > 0.25f)
            {
                int beforeConfFilter = response.detections.Count;
                response.detections.RemoveAll(d => d.confidence < config.confidenceThreshold);
                response.count = response.detections.Count;

                int confFilteredCount = beforeConfFilter - response.count;
                if (confFilteredCount > 0)
                {
                    Debug.Log($"[Filter] Filtered {confFilteredCount} detections below confidence {config.confidenceThreshold:F2}");
                }
            }

            // Process detections through tracker
            if (tracker != null)
            {
                tracker.ProcessDetections(response);
            }

            // Show ALL active tracks (confirmed + tentative)
            if (tracker != null && tracker.ActiveTrackCount > 0)
            {
                if (visualizer3D != null)
                {
                    // Filter out Lost tracks before visualization
                    var visibleTracks = tracker.ActiveTracks
                        .Where(t => t.state != TrackState.Lost)
                        .ToList();

                    if (visibleTracks.Count > 0)
                    {
                        visualizer3D.ShowTrackedObjects(visibleTracks);
                    }
                }

                int confirmed = tracker.ConfirmedTrackCount;
                int tentative = tracker.ActiveTrackCount - confirmed;

                if (config.enablePerformanceLogging)
                {
                    Debug.Log($"[Manager] Visualizing {tracker.ActiveTrackCount} tracks " +
                             $"({confirmed} confirmed, {tentative} tentative)");
                }
            }
            else if (visualizer3D != null)
            {
                // Clear boxes when no tracks
                visualizer3D.ClearAllBoxes();
            }

            OnDetectionReceived?.Invoke(response);
        }

        private void OnDetectionFailed(string error)
        {
            metrics.failedRequests++;
            Debug.LogError($"Detection failed: {error}");
            OnDetectionError?.Invoke(error);
        }

        private void OnDestroy()
        {
            if (frameCaptureService != null)
            {
                frameCaptureService.Dispose();
            }

            StopDetection();
        }

        private void OnDisable()
        {
            StopDetection();
        }

        public void RestartDetection()
        {
            StopDetection();
            StartCoroutine(CheckServerHealthAndStart());
        }

        public TrackedObject GetTrackById(int trackId)
        {
            return tracker?.ActiveTracks.FirstOrDefault(t => t.id == trackId);
        }

        public List<TrackedObject> GetTracksByClass(string className)
        {
            if (tracker == null) return new List<TrackedObject>();
            return tracker.ActiveTracks.Where(t => t.className == className).ToList();
        }
    }
}