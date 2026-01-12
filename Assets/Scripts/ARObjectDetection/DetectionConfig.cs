using UnityEngine;

namespace ARObjectDetection
{
          /// <summary>
          /// Configuration for AR object detection system
          /// Create via: Assets > Create > AR Detection > Detection Config
          /// UPDATED: targetResolution now properly used for GPU downscaling
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

                    [Header("CRITICAL: Resolution sent to server (GPU downscaled)")]
                    [Tooltip("Target resolution for captured frames - MUST maintain camera aspect ratio!\n" +
                             "Camera is typically 1280×720 (16:9), so use 640×360, 512×288, etc.\n" +
                             "SMALLER = faster encode + less bandwidth + faster inference")]
                    public Vector2Int targetResolution = new Vector2Int(640, 640);

                    [Tooltip("JPEG compression quality (0-100, higher = better quality, larger size)")]
                    [Range(1, 100)]
                    public int jpegQuality = 60;

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
            "bottle",
            "tv"
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

                    /// <summary>
                    /// Validate configuration on inspector change
                    /// </summary>
                    private void OnValidate()
                    {
                              // Warn if aspect ratio doesn't match typical camera (16:9)
                              float targetAspect = (float)targetResolution.x / targetResolution.y;
                              float cameraAspect = 16f / 9f; // Typical Quest camera

                              if (Mathf.Abs(targetAspect - cameraAspect) > 0.1f)
                              {
                                        Debug.LogWarning($"[DetectionConfig] Target resolution aspect ratio ({targetAspect:F2}) " +
                                                       $"doesn't match camera ({cameraAspect:F2}). " +
                                                       $"This may cause distortion. Recommended: 640×360, 512×288, or 1280×720");
                              }

                              // Ensure resolution is reasonable
                              if (targetResolution.x < 320 || targetResolution.y < 180)
                              {
                                        Debug.LogWarning("[DetectionConfig] Target resolution is very low - detection quality may suffer");
                              }

                              if (targetResolution.x > 1280 || targetResolution.y > 720)
                              {
                                        Debug.LogWarning("[DetectionConfig] Target resolution is high - this wastes bandwidth. " +
                                                       "Server will downscale to 640×640 anyway.");
                              }
                    }
          }
}