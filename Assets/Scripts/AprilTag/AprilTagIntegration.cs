// ============================================================================
// FILE: AprilTagIntegration.cs
// Integration component connecting AprilTag system to ARDetectionManager.
// 
// KEY FEATURES:
// - NO REFLECTION: Uses ARDetectionManager.TryGetCameraPoseForFrame() public API
// - STRICT TIMING: If pose buffer lookup fails, detection is SKIPPED
// - MULTI-MARKER: Processes primary fiducial + additional_markers array
// - ROBUSTNESS: Guards against null/invalid rvec/tvec arrays
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.AprilTag
{
    [RequireComponent(typeof(ARDetectionManager))]
    public class AprilTagIntegration : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private AssetTagManager assetTagManager;
        [SerializeField] private SiteFrameManager siteFrameManager;

        [Header("Settings")]
        [Tooltip("Enable processing of AprilTag detections for asset identity")]
        [SerializeField] private bool enableAprilTagProcessing = true;

        [Tooltip("SiteFrame marker ID (will NOT be processed for asset identity)")]
        [SerializeField] private int siteFrameMarkerId = 0;

        [Tooltip("Maximum response age to process (seconds)")]
        [SerializeField] private float maxResponseAge = 0.8f;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;

        [Tooltip("Interval for rate-limited multi-marker debug logs (seconds)")]
        [SerializeField] private float multiMarkerLogInterval = 2.0f;

        [Tooltip("Interval for rate-limited buffer miss warnings (seconds)")]
        [SerializeField] private float bufferMissLogInterval = 5.0f;

        private ARDetectionManager detectionManager;

        private int fiducialsProcessed = 0;
        private int fiducialsSkippedNoBuffer = 0;
        private int fiducialsSkippedSiteFrame = 0;
        private int fiducialsSkippedAge = 0;
        private int fiducialsSkippedInvalidPose = 0;
        private int multiMarkerFrames = 0;
        private int additionalMarkersReceived = 0;
        private int assetTagsProcessed = 0;

        private float lastBufferMissLogTime = 0f;
        private float lastMultiMarkerLogTime = 0f;

        private void Awake()
        {
            detectionManager = GetComponent<ARDetectionManager>();

            if (detectionManager == null)
            {
                Debug.LogError("[AprilTagIntegration] ARDetectionManager not found!");
                enabled = false;
                return;
            }

            if (assetTagManager == null)
                assetTagManager = FindFirstObjectByType<AssetTagManager>();

            if (siteFrameManager == null)
                siteFrameManager = FindFirstObjectByType<SiteFrameManager>();

            if (assetTagManager == null)
            {
                Debug.LogWarning("[AprilTagIntegration] AssetTagManager not found - asset tag processing disabled");
            }
        }

        private void OnEnable()
        {
            if (detectionManager != null)
            {
                detectionManager.OnDetectionReceived.AddListener(OnDetectionReceived);
            }

            Debug.Log($"[AprilTagIntegration] Enabled. Processing={enableAprilTagProcessing}, " +
                     $"SiteFrameMarker={siteFrameMarkerId}, Using public API (no reflection)");
        }

        private void OnDisable()
        {
            if (detectionManager != null)
            {
                detectionManager.OnDetectionReceived.RemoveListener(OnDetectionReceived);
            }
        }

        private void OnDetectionReceived(DetectionResponse response)
        {
            if (!enableAprilTagProcessing)
                return;

            List<FiducialObservation> markersToProcess = new List<FiducialObservation>();
            List<int> allMarkerIds = new List<int>();
            int siteFrameCount = 0;

            // Check primary fiducial
            if (response.fiducial != null && response.fiducial.HasValidPose())
            {
                allMarkerIds.Add(response.fiducial.id);

                if (response.fiducial.id == siteFrameMarkerId)
                {
                    siteFrameCount++;
                    fiducialsSkippedSiteFrame++;
                }
                else if (assetTagManager != null)
                {
                    markersToProcess.Add(response.fiducial);
                }
            }

            // Check additional markers (multi-marker support)
            if (response.fiducial?.additional_markers != null && response.fiducial.additional_markers.Count > 0)
            {
                multiMarkerFrames++;
                additionalMarkersReceived += response.fiducial.additional_markers.Count;

                foreach (var marker in response.fiducial.additional_markers)
                {
                    if (marker == null)
                        continue;

                    if (!marker.HasValidPose())
                    {
                        fiducialsSkippedInvalidPose++;
                        continue;
                    }

                    allMarkerIds.Add(marker.id);

                    if (marker.id == siteFrameMarkerId)
                    {
                        siteFrameCount++;
                        fiducialsSkippedSiteFrame++;
                    }
                    else if (assetTagManager != null)
                    {
                        markersToProcess.Add(marker);
                    }
                }
            }

            // Multi-marker debug logging (rate-limited)
            float currentTime = Time.realtimeSinceStartup;
            if (enableDebugLogs && allMarkerIds.Count > 0 &&
                (currentTime - lastMultiMarkerLogTime) > multiMarkerLogInterval)
            {
                lastMultiMarkerLogTime = currentTime;

                string idsStr = string.Join(", ", allMarkerIds);
                int assetTagCount = markersToProcess.Count;

                Debug.Log($"[AprilTagIntegration] MULTI-MARKER FRAME:\n" +
                         $"  Total markers received: {allMarkerIds.Count}\n" +
                         $"  Marker IDs: [{idsStr}]\n" +
                         $"  SiteFrame (ID {siteFrameMarkerId}): {siteFrameCount}\n" +
                         $"  Asset tags (ID != {siteFrameMarkerId}): {assetTagCount} to process\n" +
                         $"  Cumulative: multiMarkerFrames={multiMarkerFrames}, additionalMarkersTotal={additionalMarkersReceived}");
            }

            if (markersToProcess.Count == 0)
                return;

            // Process each asset tag marker
            foreach (var marker in markersToProcess)
            {
                ProcessSingleMarker(marker, response.frame_id, response.capture_time);
            }

            // FIX #4: Notify AssetTagManager that this tracker frame is complete
            // This finalizes visibility-based miss counters for anchor removal
            if (assetTagManager != null)
            {
                assetTagManager.OnTrackerFrameComplete(response.frame_id);
            }
        }

        private void ProcessSingleMarker(FiducialObservation marker, int frameId, float captureTime)
        {
            // ROBUSTNESS: Validate marker data
            if (marker == null)
                return;

            if (!marker.HasValidPose())
            {
                fiducialsSkippedInvalidPose++;
                if (enableDebugLogs && fiducialsSkippedInvalidPose % 10 == 1)
                {
                    Debug.LogWarning($"[AprilTagIntegration] SKIPPING marker #{marker.id} - invalid pose data " +
                                   $"(rvec={marker.rvec != null}, tvec={marker.tvec != null})");
                }
                return;
            }

            float currentTime = Time.realtimeSinceStartup;
            float responseAge = currentTime - captureTime;

            // Check response age
            if (responseAge > maxResponseAge)
            {
                fiducialsSkippedAge++;
                if (enableDebugLogs && fiducialsSkippedAge % 10 == 1)
                {
                    Debug.Log($"[AprilTagIntegration] Skipping stale marker #{marker.id} (age={responseAge:F3}s)");
                }
                return;
            }

            // GET CAMERA POSE VIA PUBLIC API (NO REFLECTION!)
            if (!detectionManager.TryGetCameraPoseForFrame(frameId, out Pose cameraPoseAtCapture))
            {
                fiducialsSkippedNoBuffer++;

                if (enableDebugLogs && (currentTime - lastBufferMissLogTime) > bufferMissLogInterval)
                {
                    lastBufferMissLogTime = currentTime;
                    Debug.LogWarning($"[AprilTagIntegration] SKIPPING marker #{marker.id} - " +
                                    $"pose buffer miss for frameId={frameId}. " +
                                    $"Total skipped: {fiducialsSkippedNoBuffer}.");
                }
                return;
            }

            // Convert OpenCV pose to Unity (with robustness check)
            Pose tagInCamera;
            try
            {
                tagInCamera = OpenCvPoseConversion.TagInCamera_FromOpenCvRvecTvec(
                    marker.rvec,
                    marker.tvec
                );

                // Check for identity pose (indicates conversion failure)
                if (tagInCamera.position == Vector3.zero &&
                    Quaternion.Angle(tagInCamera.rotation, Quaternion.identity) < 0.01f)
                {
                    fiducialsSkippedInvalidPose++;
                    if (enableDebugLogs)
                    {
                        Debug.LogWarning($"[AprilTagIntegration] SKIPPING marker #{marker.id} - " +
                                       $"pose conversion returned identity");
                    }
                    return;
                }
            }
            catch (System.Exception e)
            {
                fiducialsSkippedInvalidPose++;
                Debug.LogError($"[AprilTagIntegration] Exception converting pose for marker #{marker.id}: {e.Message}");
                return;
            }

            // Create AprilTagDetection struct
            AprilTagDetection detection = AprilTagDetection.FromFiducialObservation(
                marker,
                frameId,
                captureTime,
                tagInCamera
            );

            if (!detection.IsValid)
            {
                fiducialsSkippedInvalidPose++;
                return;
            }

            // Send to AssetTagManager (if available)
            if (assetTagManager != null)
            {
                assetTagManager.ProcessDetection(detection, cameraPoseAtCapture);
            }

            fiducialsProcessed++;
            assetTagsProcessed++;

            if (enableDebugLogs && assetTagsProcessed % 30 == 1)
            {
                Debug.Log($"[AprilTagIntegration] Processed ASSET TAG #{detection.TagId} " +
                         $"(quality={detection.Quality}, conf={detection.Confidence:F2}, " +
                         $"frameId={frameId}) - Total: {assetTagsProcessed}");
            }
        }

        public bool EnableAprilTagProcessing
        {
            get => enableAprilTagProcessing;
            set => enableAprilTagProcessing = value;
        }

        public void ResetMetrics()
        {
            fiducialsProcessed = 0;
            fiducialsSkippedNoBuffer = 0;
            fiducialsSkippedSiteFrame = 0;
            fiducialsSkippedAge = 0;
            fiducialsSkippedInvalidPose = 0;
            multiMarkerFrames = 0;
            additionalMarkersReceived = 0;
            assetTagsProcessed = 0;
        }

        private void OnGUI()
        {
            if (!enableDebugLogs) return;

            GUILayout.BeginArea(new Rect(420, 440, 400, 120));
            GUILayout.Label("─── APRILTAG INTEGRATION ───");
            GUILayout.Label($"Asset tags processed: {assetTagsProcessed}");
            GUILayout.Label($"Multi-marker frames: {multiMarkerFrames} | Additional markers: {additionalMarkersReceived}");
            GUILayout.Label($"Skipped: buffer={fiducialsSkippedNoBuffer}, " +
                           $"siteframe={fiducialsSkippedSiteFrame}, " +
                           $"age={fiducialsSkippedAge}, " +
                           $"invalid={fiducialsSkippedInvalidPose}");
            GUILayout.EndArea();
        }
    }
}