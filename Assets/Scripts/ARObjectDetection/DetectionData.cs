// ============================================================================
// DetectionData.cs - Updated with 3D Bounds
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
        public int droppedFrames;  // NEW: Track frames dropped due to backpressure
        public float averageLatency;
        public float lastLatency;
        public int currentDetectionCount;
        public float captureFrameRate;
        public float sendFrameRate;
        public int outOfOrderResponses;
        public int lateResponses;

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
            recentLatencies.Clear();
        }
    }
}