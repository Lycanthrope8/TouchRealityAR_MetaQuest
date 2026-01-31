// ============================================================================
// DetectionData.cs - Updated with FiducialObservation for SiteFrame support
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection
{
    [Serializable]
    public class Detection
    {
        public float[] bbox;
        public float confidence;
        public string class_name;
        public int class_id;

        public Rect GetRect()
        {
            if (bbox == null || bbox.Length != 4)
                return Rect.zero;

            return new Rect(
                bbox[0],
                bbox[1],
                bbox[2] - bbox[0],
                bbox[3] - bbox[1]
            );
        }

        public Vector2 GetCenter()
        {
            if (bbox == null || bbox.Length != 4)
                return Vector2.zero;

            return new Vector2(
                (bbox[0] + bbox[2]) / 2f,
                (bbox[1] + bbox[3]) / 2f
            );
        }
    }

    [Serializable]
    public class PreprocessingTransform
    {
        public int original_width;
        public int original_height;
        public int processed_width;
        public int processed_height;
        public float scale_x;
        public float scale_y;
        public float offset_x;
        public float offset_y;
        public string method;
    }

    /// <summary>
    /// Fiducial/marker observation from server-side detection.
    /// Used by SiteFrameManager to establish shared coordinate frame.
    /// 
    /// This is OPTIONAL - if the server doesn't send fiducial data,
    /// the field will be null and the system continues to work without it.
    /// </summary>
    [Serializable]
    public class FiducialObservation
    {
        /// <summary>True if a fiducial marker was detected in this frame</summary>
        public bool found;

        /// <summary>Marker/tag ID (e.g., AprilTag ID, ArUco ID)</summary>
        public int id;

        /// <summary>
        /// Rodrigues rotation vector from solvePnP (axis-angle, radians).
        /// Length 3: [rx, ry, rz]
        /// Represents rotation of marker in camera coordinates.
        /// </summary>
        public float[] rvec;

        /// <summary>
        /// Translation vector from solvePnP (meters).
        /// Length 3: [tx, ty, tz]
        /// Represents position of marker in camera coordinates.
        /// </summary>
        public float[] tvec;

        /// <summary>
        /// Optional: Reprojection error from solvePnP (pixels).
        /// Lower values indicate better pose estimation.
        /// </summary>
        public float reproj_error;

        /// <summary>
        /// Optional: Confidence score (0-1) for the detection.
        /// Can be derived from reproj_error, corner detection quality, etc.
        /// </summary>
        public float confidence;

        /// <summary>
        /// Optional: Marker family/type (e.g., "apriltag_36h11", "aruco_4x4")
        /// </summary>
        public string marker_type;

        /// <summary>
        /// Optional: Physical size of the marker in meters (for solvePnP).
        /// </summary>
        public float marker_size;

        /// <summary>
        /// Optional: Number of detected corners (typically 4 for ArUco/AprilTag).
        /// </summary>
        public int corner_count;

        /// <summary>
        /// Get the distance from camera to marker (tvec magnitude) in meters.
        /// </summary>
        public float GetDistance()
        {
            if (tvec == null || tvec.Length != 3)
                return -1f;
            return Mathf.Sqrt(tvec[0] * tvec[0] + tvec[1] * tvec[1] + tvec[2] * tvec[2]);
        }

        /// <summary>
        /// Check if this observation has valid pose data.
        /// </summary>
        public bool HasValidPose()
        {
            return found &&
                   rvec != null && rvec.Length == 3 &&
                   tvec != null && tvec.Length == 3;
        }

        /// <summary>
        /// Get effective confidence (uses explicit confidence if set, otherwise derives from reproj_error).
        /// </summary>
        public float GetEffectiveConfidence()
        {
            if (confidence > 0f)
                return confidence;

            // Derive confidence from reprojection error if available
            // Lower reproj_error = higher confidence
            if (reproj_error > 0f)
            {
                // Map reproj_error to confidence: 0px = 1.0, 5px = 0.5, 10px = 0.0
                return Mathf.Clamp01(1f - reproj_error / 10f);
            }

            // Default to medium confidence if marker was found but no quality metrics
            return found ? 0.7f : 0f;
        }
    }

    [Serializable]
    public class DetectionResponse
    {
        public bool success;
        public List<Detection> detections;
        public int count;
        public float inference_time;
        public int[] image_size;
        public int[] processed_size;
        public PreprocessingTransform transform;
        public string timestamp;

        // Client metadata
        public int frame_id;
        public float capture_time;

        /// <summary>
        /// Optional fiducial marker observation for SiteFrame.
        /// Will be null if server doesn't detect/send fiducial data.
        /// </summary>
        public FiducialObservation fiducial;
    }

    public class ProcessedDetection
    {
        public Detection detection;
        public Ray worldRay;
        public Vector3? worldPosition;
        public float timestamp;

        public ProcessedDetection(Detection detection, Ray worldRay)
        {
            this.detection = detection;
            this.worldRay = worldRay;
            this.timestamp = Time.time;
        }
    }

    public class DetectionMetrics
    {
        public int totalFramesCaptured;
        public int framesSent;
        public int successfulDetections;
        public int failedRequests;
        public int droppedFrames;
        public float averageLatency;
        public float lastLatency;
        public int currentDetectionCount;
        public float captureFrameRate;
        public float sendFrameRate;
        public int outOfOrderResponses;
        public int lateResponses;

        // SiteFrame metrics
        public int fiducialObservationsReceived;
        public int fiducialObservationsProcessed;

        private List<float> recentLatencies = new List<float>();
        private const int maxLatencySamples = 50;

        public void RecordLatency(float latency)
        {
            lastLatency = latency;
            recentLatencies.Add(latency);

            if (recentLatencies.Count > maxLatencySamples)
                recentLatencies.RemoveAt(0);

            float sum = 0f;
            foreach (var l in recentLatencies)
                sum += l;
            averageLatency = sum / recentLatencies.Count;
        }

        public void Reset()
        {
            totalFramesCaptured = 0;
            framesSent = 0;
            successfulDetections = 0;
            failedRequests = 0;
            droppedFrames = 0;
            averageLatency = 0f;
            lastLatency = 0f;
            currentDetectionCount = 0;
            outOfOrderResponses = 0;
            lateResponses = 0;
            fiducialObservationsReceived = 0;
            fiducialObservationsProcessed = 0;
            recentLatencies.Clear();
        }
    }
}