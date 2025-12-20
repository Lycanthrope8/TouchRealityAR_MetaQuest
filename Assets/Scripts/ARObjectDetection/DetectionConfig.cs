using UnityEngine;

namespace ARObjectDetection
{
          /// <summary>
          /// Configuration for AR object detection system
          /// Create via: Assets > Create > AR Detection > Detection Config
          /// </summary>
          [CreateAssetMenu(fileName = "DetectionConfig", menuName = "AR Detection/Detection Config")]
          public class DetectionConfig : ScriptableObject
          {
                    [Header("Server Settings")]
                    [Tooltip("Server URL (e.g., http://192.168.1.100:5000)")]
                    public string serverUrl = "http://192.168.1.100:5000";

                    [Tooltip("Request timeout in seconds")]
                    public float requestTimeout = 5f;

                    [Tooltip("Maximum retry attempts for failed requests")]
                    public int maxRetryAttempts = 2;

                    [Header("Frame Capture Settings")]
                    [Tooltip("Send every Nth frame to server (higher = less frequent, lower bandwidth)")]
                    [Range(1, 10)]
                    public int frameCaptureInterval = 3;

                    [Tooltip("Target resolution for captured frames")]
                    public Vector2Int targetResolution = new Vector2Int(640, 640);

                    [Tooltip("JPEG compression quality (0-100, higher = better quality, larger size)")]
                    [Range(1, 100)]
                    public int jpegQuality = 75;

                    [Header("Detection Settings")]
                    [Tooltip("Minimum confidence threshold for detections (0-1)")]
                    [Range(0f, 1f)]
                    public float confidenceThreshold = 0.25f;

                    [Tooltip("Filter detections to specific classes (empty = all classes)")]
                    public string[] classFilter = new string[0];

                    [Header("Performance")]
                    [Tooltip("Maximum number of pending requests")]
                    public int maxPendingRequests = 3;

                    [Tooltip("Enable performance logging")]
                    public bool enablePerformanceLogging = true;

                    [Header("Visualization")]
                    [Tooltip("Show bounding boxes")]
                    public bool showBoundingBoxes = true;

                    [Tooltip("Show class labels")]
                    public bool showLabels = true;

                    [Tooltip("Show confidence scores")]
                    public bool showConfidence = true;

                    /// <summary>
                    /// Get the full detection endpoint URL
                    /// </summary>
                    public string DetectEndpoint => $"{serverUrl}/detect";

                    /// <summary>
                    /// Get the health check endpoint URL
                    /// </summary>
                    public string HealthEndpoint => $"{serverUrl}/health";
          }
}