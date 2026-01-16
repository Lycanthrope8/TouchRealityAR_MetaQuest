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
        [SerializeField] private DepthAnchorSystem depthAnchorSystem;  // NEW: Depth-based anchor system

        [Header("Visualization Options")]
        [Tooltip("Enable 3D bounding box visualization")]
        [SerializeField] private bool enable3DBoundingBoxes = false;  // OFF by default

        [Tooltip("Enable depth-based anchor placement (most accurate)")]
        [SerializeField] private bool enableDepthAnchors = true;  // ON by default

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
        public DepthAnchorSystem DepthAnchors => depthAnchorSystem;

        private int lastAcceptedFrameId = -1;
        public float maxResponseAgeToAccept = 0.8f;

        // Runtime toggles
        public bool Enable3DBoundingBoxes
        {
            get => enable3DBoundingBoxes;
            set
            {
                enable3DBoundingBoxes = value;
                if (!value && visualizer3D != null)
                {
                    visualizer3D.ClearAllBoxes();
                }
            }
        }

        public bool EnableDepthAnchors
        {
            get => enableDepthAnchors;
            set
            {
                enableDepthAnchors = value;
                if (depthAnchorSystem != null)
                {
                    depthAnchorSystem.enabled = value;
                    if (!value)
                    {
                        depthAnchorSystem.ClearAllAnchors();
                    }
                }
            }
        }

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

            // Find depth anchor system
            if (depthAnchorSystem == null)
            {
                depthAnchorSystem = GetComponent<DepthAnchorSystem>();
                if (depthAnchorSystem == null)
                {
                    depthAnchorSystem = FindFirstObjectByType<DepthAnchorSystem>();
                }
            }

            // Apply initial state
            if (depthAnchorSystem != null)
            {
                depthAnchorSystem.enabled = enableDepthAnchors;
            }

            frameCaptureService = new FrameCaptureService(config);
            detectionClient = new DetectionClient(config);

            Debug.Log("ARDetectionManager initialized with depth-based anchor placement");
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
            Debug.Log("Detection started");
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

            if (Time.frameCount % 300 == 0)
            {
                Debug.Log($"[WebCam] Actual resolution: {webCamTexture.width}×{webCamTexture.height}");
            }

            if (Time.time - lastFrameTime > 0)
            {
                metrics.captureFrameRate = 1f / (Time.time - lastFrameTime);
            }
            lastFrameTime = Time.time;
            metrics.totalFramesCaptured++;

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
                    Debug.Log("=== DETECTION METRICS ===");
                    Debug.Log($"Avg Latency: {metrics.averageLatency:F3}s ({metrics.averageLatency * 1000:F0}ms)");
                    Debug.Log($"Send Rate: {metrics.sendFrameRate:F1} Hz");
                    Debug.Log($"Success: {metrics.successfulDetections}, Failed: {metrics.failedRequests}, Dropped: {metrics.droppedFrames}");
                    Debug.Log($"Pending Requests: {detectionClient.GetPendingRequestCount()}/{config.maxPendingRequests}");

                    if (tracker != null)
                    {
                        int activeCount = tracker.ActiveTrackCount;
                        int confirmedCount = tracker.ConfirmedTrackCount;
                        Debug.Log($"Tracks: {activeCount} active, {confirmedCount} confirmed");
                    }

                    if (depthAnchorSystem != null && enableDepthAnchors)
                    {
                        Debug.Log($"Anchors: {depthAnchorSystem.ActiveAnchorCount} active, " +
                                 $"Depth Hits: {depthAnchorSystem.SuccessfulDepthHits}, " +
                                 $"Fallbacks: {depthAnchorSystem.FallbacksUsed}");
                    }

                    Debug.Log("========================");
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

            if (responseAge > maxResponseAgeToAccept)
            {
                Debug.LogWarning($"[ARDetectionManager] Dropping stale response frame={response.frame_id} age={responseAge:F3}s");
                return;
            }

            if (response.frame_id <= lastAcceptedFrameId)
            {
                Debug.LogWarning($"[ARDetectionManager] Dropping out-of-order response frame={response.frame_id}");
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

            // Class filter
            if (config.enableClassFilter && config.classFilter != null && config.classFilter.Length > 0)
            {
                response.detections.RemoveAll(d =>
                {
                    string detectionClass = d.class_name.ToLower().Trim();
                    foreach (string allowedClass in config.classFilter)
                    {
                        if (detectionClass == allowedClass.ToLower().Trim())
                            return false;
                    }
                    return true;
                });
                response.count = response.detections.Count;
            }

            // Confidence filter
            if (config.confidenceThreshold > 0.25f)
            {
                response.detections.RemoveAll(d => d.confidence < config.confidenceThreshold);
                response.count = response.detections.Count;
            }

            // Process through tracker
            if (tracker != null)
            {
                tracker.ProcessDetections(response);
            }

            // 3D Bounding boxes (optional)
            if (enable3DBoundingBoxes && visualizer3D != null && tracker != null)
            {
                var visibleTracks = tracker.ActiveTracks
                    .Where(t => t.state != TrackState.Lost)
                    .ToList();

                if (visibleTracks.Count > 0)
                {
                    visualizer3D.ShowTrackedObjects(visibleTracks);
                }
            }
            else if (!enable3DBoundingBoxes && visualizer3D != null)
            {
                visualizer3D.ClearAllBoxes();
            }

            // NOTE: DepthAnchorSystem handles anchor placement automatically in its Update()
            // by reading from ObjectTracker.ActiveTracks

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