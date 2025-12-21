using UnityEngine;
using UnityEngine.Events;
using PassthroughCameraSamples;
using System.Collections;

namespace ARObjectDetection
{
    /// <summary>
    /// Main manager for AR object detection system
    /// Orchestrates frame capture, detection, and visualization
    /// </summary>
    public class ARDetectionManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private DetectionConfig config;

        [Header("References")]
        [SerializeField] private WebCamTextureManager webCamTextureManager;
        [SerializeField] private DetectionVisualizer visualizer; // 2D visualizer (optional)
        [SerializeField] private DetectionVisualizer3D visualizer3D; // 3D visualizer (recommended for VR)

        [Header("Events")]
        public UnityEvent<DetectionResponse> OnDetectionReceived;
        public UnityEvent<string> OnDetectionError;

        // Services
        private FrameCaptureService frameCaptureService;
        private DetectionClient detectionClient;

        // State
        private bool isRunning = false;
        private bool serverHealthy = false;
        private DetectionMetrics metrics = new DetectionMetrics();

        // Current detection data
        private DetectionResponse latestDetection;
        private float lastFrameTime;
        private float lastSendTime;

        public DetectionMetrics Metrics => metrics;
        public DetectionResponse LatestDetection => latestDetection;
        public bool IsRunning => isRunning;
        public bool ServerHealthy => serverHealthy;

        private void Awake()
        {
            // Validate configuration
            if (config == null)
            {
                Debug.LogError("DetectionConfig is not assigned!");
                enabled = false;
                return;
            }

            // Find WebCamTextureManager if not assigned
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

            // Initialize services
            frameCaptureService = new FrameCaptureService(config);
            detectionClient = new DetectionClient(config);

            Debug.Log("ARDetectionManager initialized");
        }

        private void Start()
        {
            // Check server health before starting
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
                    Debug.LogError($"Server not reachable at {config.serverUrl}. Please check:\n" +
                                 "1. Server is running (python server.py)\n" +
                                 "2. Correct IP address in DetectionConfig\n" +
                                 "3. Quest and PC on same network\n" +
                                 "4. Firewall allows port 5000");
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

            // Update capture frame rate
            if (Time.time - lastFrameTime > 0)
            {
                metrics.captureFrameRate = 1f / (Time.time - lastFrameTime);
            }
            lastFrameTime = Time.time;
            metrics.totalFramesCaptured++;

            // Check if we should capture this frame
            if (frameCaptureService.ShouldCaptureFrame())
            {
                // Capture and send frame
                byte[] jpegData = frameCaptureService.CaptureFrame(webCamTexture);

                if (jpegData != null && jpegData.Length > 0)
                {
                    metrics.framesSent++;

                    // Update send frame rate
                    if (Time.time - lastSendTime > 0)
                    {
                        metrics.sendFrameRate = 1f / (Time.time - lastSendTime);
                    }
                    lastSendTime = Time.time;

                    // Send to server
                    StartCoroutine(SendFrameCoroutine(jpegData));
                }
            }
        }

        private IEnumerator SendFrameCoroutine(byte[] jpegData)
        {
            float startTime = Time.time;

            yield return detectionClient.SendFrameWithRetry(
                jpegData,
                OnDetectionSuccess,
                OnDetectionFailed
            );

            float latency = Time.time - startTime;
            metrics.RecordLatency(latency);
        }

        private void OnDetectionSuccess(DetectionResponse response)
        {
            latestDetection = response;
            metrics.successfulDetections++;
            metrics.currentDetectionCount = response.count;

            // Filter by confidence threshold if needed
            if (config.confidenceThreshold > 0.25f)  // Server already filters at 0.25
            {
                response.detections.RemoveAll(d => d.confidence < config.confidenceThreshold);
                response.count = response.detections.Count;
            }

            // Filter by class if specified
            if (config.classFilter != null && config.classFilter.Length > 0)
            {
                response.detections.RemoveAll(d => System.Array.IndexOf(config.classFilter, d.class_name) == -1);
                response.count = response.detections.Count;
            }

            // Visualize detections - prioritize 3D visualizer for VR
            if (visualizer3D != null)
            {
                visualizer3D.ShowDetections(response);
            }
            else if (visualizer != null)
            {
                visualizer.ShowDetections(response);
            }

            // Invoke event
            OnDetectionReceived?.Invoke(response);

            if (config.enablePerformanceLogging && response.count > 0)
            {
                Debug.Log($"Detections: {response.count} objects");
                foreach (var det in response.detections)
                {
                    Debug.Log($"  - {det.class_name} ({det.confidence:F2})");
                }
            }
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

        // Public API for manual control
        public void RestartDetection()
        {
            StopDetection();
            StartCoroutine(CheckServerHealthAndStart());
        }

        public void UpdateConfig(DetectionConfig newConfig)
        {
            if (newConfig != null)
            {
                config = newConfig;
                Debug.Log("Configuration updated");
            }
        }
    }
}