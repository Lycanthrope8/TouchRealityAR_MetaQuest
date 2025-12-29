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
                    public string serverUrl = "http://10.0.0.93:5000";

                    [Tooltip("Request timeout in seconds")]
                    public float requestTimeout = 5f;

                    [Tooltip("Maximum retry attempts for failed requests")]
                    public int maxRetryAttempts = 2;

                    [Header("Frame Capture Settings")]
                    [Tooltip("Send every Nth frame to server (higher = less frequent, lower bandwidth)")]
                    [Range(1, 30)]
                    public int frameCaptureInterval = 6;

                    [Tooltip("Target resolution for captured frames")]
                    public Vector2Int targetResolution = new Vector2Int(640, 640);

                    [Tooltip("JPEG compression quality (0-100, higher = better quality, larger size)")]
                    [Range(1, 100)]
                    public int jpegQuality = 75;

                    [Header("Detection Settings")]
                    [Tooltip("Minimum confidence threshold for detections (0-1)")]
                    [Range(0f, 1f)]
                    public float confidenceThreshold = 0.25f;

                    [Header("Class Filter (Whitelist)")]
                    [Tooltip("Enable class whitelist filtering")]
                    public bool enableClassFilter = true;

                    [Tooltip("Only detect these classes (leave empty to detect all)")]
                    public string[] classFilter = new string[]
                    {
                    "laptop",
                    "mouse",
                    "keyboard",
                    "cell phone",
                    "cup",
                    "potted plant",
                    "bed",
                    "car",
                    "book",
                    "bottle"
                    };

                    [Header("Performance")]
                    [Tooltip("Maximum number of pending requests (keep at 1 for best latency)")]
                    [Range(1, 5)]
                    public int maxPendingRequests = 1;

                    [Tooltip("Enable performance logging")]
                    public bool enablePerformanceLogging = true;

                    [Header("Visualization")]
                    [Tooltip("Show 2D bounding boxes (canvas overlay)")]
                    public bool show2DBoundingBoxes = false;

                    [Tooltip("Show 3D bounding boxes (world space - recommended for VR)")]
                    public bool show3DBoundingBoxes = true;

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