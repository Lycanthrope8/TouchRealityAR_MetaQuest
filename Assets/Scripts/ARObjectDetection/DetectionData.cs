// ============================================================================
// DetectionData.cs - Updated with FiducialObservation for SiteFrame support
// + Multi-marker support via additional_markers field
// 
// CRITICAL: Field names must EXACTLY match server JSON for JsonUtility to work
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

    [Serializable]
    public class FiducialObservation
    {
        public bool found;
        public int id;
        public float[] rvec;
        public float[] tvec;
        public float reproj_error;
        public float confidence;
        public string marker_type;
        public float marker_size;
        public int corner_count;
        public List<FiducialObservation> additional_markers;

        public float GetDistance()
        {
            if (tvec == null || tvec.Length != 3)
                return -1f;
            return Mathf.Sqrt(tvec[0] * tvec[0] + tvec[1] * tvec[1] + tvec[2] * tvec[2]);
        }

        public bool HasValidPose()
        {
            if (!found)
                return false;
            if (rvec == null || rvec.Length != 3)
                return false;
            if (tvec == null || tvec.Length != 3)
                return false;

            for (int i = 0; i < 3; i++)
            {
                if (float.IsNaN(rvec[i]) || float.IsInfinity(rvec[i]))
                    return false;
                if (float.IsNaN(tvec[i]) || float.IsInfinity(tvec[i]))
                    return false;
            }

            if (tvec[0] == 0f && tvec[1] == 0f && tvec[2] == 0f)
                return false;

            return true;
        }

        public float GetEffectiveConfidence()
        {
            if (confidence > 0f)
                return confidence;
            if (reproj_error > 0f)
                return Mathf.Clamp01(1f - reproj_error / 10f);
            return found ? 0.7f : 0f;
        }

        public int GetTotalMarkerCount()
        {
            int count = found ? 1 : 0;
            if (additional_markers != null)
                count += additional_markers.Count;
            return count;
        }

        public List<int> GetAllMarkerIds()
        {
            var ids = new List<int>();
            if (found)
                ids.Add(id);
            if (additional_markers != null)
            {
                foreach (var marker in additional_markers)
                {
                    if (marker != null && marker.found)
                        ids.Add(marker.id);
                }
            }
            return ids;
        }

        public List<FiducialObservation> GetAllValidMarkers()
        {
            var markers = new List<FiducialObservation>();
            if (HasValidPose())
                markers.Add(this);
            if (additional_markers != null)
            {
                foreach (var marker in additional_markers)
                {
                    if (marker != null && marker.HasValidPose())
                        markers.Add(marker);
                }
            }
            return markers;
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
        public int frame_id;
        public float capture_time;
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
        public int fiducialObservationsReceived;
        public int fiducialObservationsProcessed;
        public int multiMarkerFramesReceived;
        public int additionalMarkersProcessed;

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
            multiMarkerFramesReceived = 0;
            additionalMarkersProcessed = 0;
            recentLatencies.Clear();
        }
    }
}