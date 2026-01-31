using UnityEngine;
using System.Collections.Generic;

namespace ARObjectDetection
{
    /// <summary>
    /// Entry storing camera pose along with capture timestamp.
    /// </summary>
    public struct PoseBufferEntry
    {
        public Pose CameraPose;
        public float CaptureTime;  // Time.realtimeSinceStartup when stored

        public PoseBufferEntry(Pose pose, float time)
        {
            CameraPose = pose;
            CaptureTime = time;
        }
    }

    /// <summary>
    /// A lightweight buffer that stores camera world poses keyed by frameId.
    /// Critical for using the camera pose at capture time (not at response time),
    /// which avoids network-latency errors when computing marker poses.
    /// 
    /// Now includes timestamp tracking for diagnostic purposes.
    /// </summary>
    public class SiteFramePoseBuffer
    {
        private readonly int maxEntries;
        private readonly float maxAgeSeconds;
        private readonly Dictionary<int, PoseBufferEntry> entriesByFrameId = new Dictionary<int, PoseBufferEntry>();
        private readonly Queue<int> frameIdOrder = new Queue<int>();

        /// <summary>
        /// Create a pose buffer with the specified capacity and max age.
        /// </summary>
        /// <param name="maxEntries">Maximum entries to retain (default 160)</param>
        /// <param name="maxAgeSeconds">Maximum age in seconds before entries can be pruned (default 5.0)</param>
        public SiteFramePoseBuffer(int maxEntries = 160, float maxAgeSeconds = 5.0f)
        {
            this.maxEntries = maxEntries;
            this.maxAgeSeconds = maxAgeSeconds;
        }

        /// <summary>
        /// Store a camera world pose for a given frameId.
        /// </summary>
        /// <param name="frameId">The frame ID from CapturedFrame</param>
        /// <param name="cameraWorldPose">The camera's world pose at capture time</param>
        public void Store(int frameId, Pose cameraWorldPose)
        {
            float currentTime = Time.realtimeSinceStartup;
            PoseBufferEntry entry = new PoseBufferEntry(cameraWorldPose, currentTime);

            // If already exists, update it
            if (entriesByFrameId.ContainsKey(frameId))
            {
                entriesByFrameId[frameId] = entry;
                return;
            }

            // Add new entry
            entriesByFrameId[frameId] = entry;
            frameIdOrder.Enqueue(frameId);

            // Evict oldest entries if over capacity
            while (frameIdOrder.Count > maxEntries)
            {
                int oldestFrameId = frameIdOrder.Dequeue();
                entriesByFrameId.Remove(oldestFrameId);
            }

            // Also prune entries older than maxAgeSeconds (optional aggressive pruning)
            PruneOldEntries(currentTime);
        }

        /// <summary>
        /// Try to retrieve the camera world pose for a given frameId.
        /// </summary>
        /// <param name="frameId">The frame ID to look up</param>
        /// <param name="cameraWorldPose">Output: the camera's world pose at capture time</param>
        /// <returns>True if found, false otherwise</returns>
        public bool TryGet(int frameId, out Pose cameraWorldPose)
        {
            if (entriesByFrameId.TryGetValue(frameId, out PoseBufferEntry entry))
            {
                cameraWorldPose = entry.CameraPose;
                return true;
            }
            cameraWorldPose = Pose.identity;
            return false;
        }

        /// <summary>
        /// Try to retrieve the full entry including timestamp for a given frameId.
        /// </summary>
        /// <param name="frameId">The frame ID to look up</param>
        /// <param name="entry">Output: the full buffer entry with pose and timestamp</param>
        /// <returns>True if found, false otherwise</returns>
        public bool TryGetEntry(int frameId, out PoseBufferEntry entry)
        {
            return entriesByFrameId.TryGetValue(frameId, out entry);
        }

        /// <summary>
        /// Get the age in seconds of a specific frameId entry.
        /// Returns -1 if not found.
        /// </summary>
        public float GetEntryAge(int frameId)
        {
            if (entriesByFrameId.TryGetValue(frameId, out PoseBufferEntry entry))
            {
                return Time.realtimeSinceStartup - entry.CaptureTime;
            }
            return -1f;
        }

        /// <summary>
        /// Check if a frameId exists in the buffer.
        /// </summary>
        public bool Contains(int frameId)
        {
            return entriesByFrameId.ContainsKey(frameId);
        }

        /// <summary>
        /// Get the number of entries currently stored.
        /// </summary>
        public int Count => entriesByFrameId.Count;

        /// <summary>
        /// Clear all stored poses.
        /// </summary>
        public void Clear()
        {
            entriesByFrameId.Clear();
            frameIdOrder.Clear();
        }

        /// <summary>
        /// Prune entries older than maxAgeSeconds.
        /// Called automatically during Store(), but can be called manually.
        /// </summary>
        private void PruneOldEntries(float currentTime)
        {
            // Only prune from the front of the queue (oldest entries)
            while (frameIdOrder.Count > 0)
            {
                int oldestFrameId = frameIdOrder.Peek();
                if (entriesByFrameId.TryGetValue(oldestFrameId, out PoseBufferEntry entry))
                {
                    float age = currentTime - entry.CaptureTime;
                    if (age > maxAgeSeconds)
                    {
                        frameIdOrder.Dequeue();
                        entriesByFrameId.Remove(oldestFrameId);
                    }
                    else
                    {
                        break; // Remaining entries are newer
                    }
                }
                else
                {
                    // Entry was already removed, clean up queue
                    frameIdOrder.Dequeue();
                }
            }
        }

        /// <summary>
        /// Get diagnostic info about buffer state.
        /// </summary>
        public string GetDiagnosticInfo()
        {
            if (entriesByFrameId.Count == 0)
                return "Buffer empty";

            float currentTime = Time.realtimeSinceStartup;
            float oldestAge = float.MinValue;
            float newestAge = float.MaxValue;
            int oldestFrameId = -1;
            int newestFrameId = -1;

            foreach (var kvp in entriesByFrameId)
            {
                float age = currentTime - kvp.Value.CaptureTime;
                if (age > oldestAge)
                {
                    oldestAge = age;
                    oldestFrameId = kvp.Key;
                }
                if (age < newestAge)
                {
                    newestAge = age;
                    newestFrameId = kvp.Key;
                }
            }

            return $"count={entriesByFrameId.Count}, oldest=frame{oldestFrameId}({oldestAge:F2}s), newest=frame{newestFrameId}({newestAge:F2}s)";
        }
    }
}