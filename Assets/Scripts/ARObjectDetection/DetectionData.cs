using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection
{
    /// <summary>
    /// Represents a single object detection
    /// </summary>
    [Serializable]
    public class Detection
    {
        public float[] bbox;  // [x1, y1, x2, y2] - IN ORIGINAL IMAGE COORDINATES
        public float confidence;
        public string class_name;
        public int class_id;

        /// <summary>
        /// Get bounding box as Rect (x, y, width, height)
        /// </summary>
        public Rect GetRect()
        {
            if (bbox == null || bbox.Length != 4)
                return Rect.zero;

            return new Rect(
                bbox[0],
                bbox[1],
                bbox[2] - bbox[0],  // width
                bbox[3] - bbox[1]   // height
            );
        }

        /// <summary>
        /// Get center point of bounding box
        /// </summary>
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

    /// <summary>
    /// Preprocessing transform metadata from server
    /// </summary>
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
        public string method;  // "resize", "letterbox", "crop"
    }

    /// <summary>
    /// Server response from detection endpoint
    /// </summary>
    [Serializable]
    public class DetectionResponse
    {
        public bool success;
        public List<Detection> detections;
        public int count;
        public float inference_time;
        public int[] image_size;  // [width, height] of ORIGINAL image
        public int[] processed_size;  // [width, height] sent to YOLO
        public PreprocessingTransform transform;  // How coordinates were transformed
        public string timestamp;
    }

    /// <summary>
    /// Processed detection with 3D information
    /// </summary>
    public class ProcessedDetection
    {
        public Detection detection;
        public Ray worldRay;  // Ray from camera through detection center
        public Vector3? worldPosition;  // If raycasted to surface
        public float timestamp;  // Time when detected

        public ProcessedDetection(Detection detection, Ray worldRay)
        {
            this.detection = detection;
            this.worldRay = worldRay;
            this.timestamp = Time.time;
        }
    }

    /// <summary>
    /// Performance metrics
    /// </summary>
    public class DetectionMetrics
    {
        public int totalFramesCaptured;
        public int framesSent;
        public int successfulDetections;
        public int failedRequests;
        public float averageLatency;
        public float lastLatency;
        public int currentDetectionCount;
        public float captureFrameRate;
        public float sendFrameRate;

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
            averageLatency = 0f;
            lastLatency = 0f;
            currentDetectionCount = 0;
            recentLatencies.Clear();
        }
    }
}