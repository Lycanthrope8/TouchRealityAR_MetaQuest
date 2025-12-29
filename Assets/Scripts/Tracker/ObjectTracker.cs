using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using PassthroughCameraSamples;

namespace ARObjectDetection
{
    /// <summary>
    /// PHASE 2 - 3D IoU tracking with smoothing and backpressure
    /// </summary>
    public class ObjectTracker : MonoBehaviour
    {
        // === SINGLETON ===
        private static ObjectTracker instance;
        public static ObjectTracker Instance => instance;

        [Header("Configuration")]
        [SerializeField] private TrackerConfig config;
        [SerializeField] private DetectionConfig detectionConfig;
        [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

        [Header("Depth Settings")]
        [SerializeField] private float defaultDepth = 2.0f;
        [SerializeField] private LayerMask raycastLayers = ~0;
        [SerializeField] private float maxRaycastDistance = 10f;

        [Header("Debug Y-Offset Compensation")]
        [Tooltip("Manual Y-offset adjustment in pixels (positive = shift down)")]
        [SerializeField] private float manualYOffsetPixels = 0f;

        [Tooltip("Enable automatic Y-offset correction based on bbox height")]
        [SerializeField] private bool autoCorrectYOffset = true;

        [Tooltip("Y-offset as percentage of bbox height (0.5 = half bbox height)")]
        [Range(0f, 1f)]
        [SerializeField] private float yOffsetPercentage = 0.0f;


        // Active tracks
        private List<TrackedObject> activeTracks = new List<TrackedObject>();
        private int nextTrackId = 0;

        // Camera
        private PassthroughCameraIntrinsics? cameraIntrinsics;
        private Transform centerEyeTransform;

        // Frame tracking
        private int lastProcessedFrameId = -1;

        // Metrics
        public int ActiveTrackCount => activeTracks.Count;
        public int ConfirmedTrackCount => activeTracks.Count(t => t.state == TrackState.Confirmed);
        public List<TrackedObject> ActiveTracks => activeTracks;

        private void Awake()
        {
            // === ENFORCE SINGLETON ===
            if (instance != null && instance != this)
            {
                Debug.LogWarning($"[ObjectTracker] Duplicate instance detected! Destroying {gameObject.name}");
                Destroy(this);
                return;
            }

            instance = this;

            // Validate config
            if (config == null)
            {
                Debug.LogError("[ObjectTracker] TrackerConfig not assigned!");
                enabled = false;
                return;
            }

            // Get camera intrinsics
            try
            {
                cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(cameraEye);
                Debug.Log($"[ObjectTracker] Camera intrinsics loaded: {cameraIntrinsics.Value.Resolution}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ObjectTracker] Failed to get camera intrinsics: {e.Message}");
            }

            // Get center eye transform
            if (OVRManager.instance != null)
            {
                centerEyeTransform = OVRManager.instance.GetComponentInChildren<Camera>().transform;
            }

            Debug.Log($"[ObjectTracker] ✅ Phase 2 initialized - 3D IoU={config.iou3DThreshold}, posAlpha={config.smoothingAlphaPosition}, sizeAlpha={config.smoothingAlphaSize}");
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }

        /// <summary>
        /// Update all tracks every frame (prediction + motion compensation)
        /// </summary>
        private void Update()
        {
            if (activeTracks.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            float dtFrame = Time.deltaTime;

            foreach (var track in activeTracks)
            {
                // Update time since last detection match
                track.timeSinceLastUpdate = now - track.lastUpdateTime;

                // 1. Apply motion compensation FIRST (if enabled)
                if (config.enableMotionCompensation)
                {
                    CompensateForCameraMotion(track);
                }

                // 2. Predict with velocity
                track.worldPosition += track.velocity * dtFrame;

                // 3. Smooth for display (EMA)
                track.worldPositionSmoothed = Vector3.Lerp(
                    track.worldPositionSmoothed,
                    track.worldPosition,
                    config.smoothingAlphaPosition
                );

                // 4. Update bounds center
                track.worldBounds.center = track.worldPosition;

                // 5. Decay confidence if no updates
                if (track.timeSinceLastUpdate > 0.1f)
                {
                    track.trackConfidence *= Mathf.Exp(-dtFrame * 2.0f);
                    track.trackConfidence = Mathf.Max(track.trackConfidence, 0.1f);
                }
            }

            // Remove lost tracks
            int lostCount = activeTracks.RemoveAll(t => t.state == TrackState.Lost);
            if (lostCount > 0)
            {
                Debug.Log($"[ObjectTracker] 🗑️ Removed {lostCount} lost tracks");
            }
        }

        /// <summary>
        /// Compensate for camera motion using ray reprojection
        /// </summary>
        private void CompensateForCameraMotion(TrackedObject track)
        {
            if (centerEyeTransform == null || !cameraIntrinsics.HasValue)
                return;

            // Get current camera pose
            Pose currentPose = new Pose(
                centerEyeTransform.position,
                centerEyeTransform.rotation
            );

            // Calculate pose delta
            Quaternion rotationDelta = currentPose.rotation * Quaternion.Inverse(track.lastCameraPose.rotation);
            Vector3 positionDelta = currentPose.position - track.lastCameraPose.position;

            // Only compensate if there's significant motion
            if (Quaternion.Angle(rotationDelta, Quaternion.identity) < 0.5f && positionDelta.magnitude < 0.01f)
            {
                return; // Skip if camera barely moved
            }

            // Transform the center ray by the pose change
            Vector3 oldRayOrigin = track.centerRay.origin;
            Vector3 oldRayDir = track.centerRay.direction;

            // Rotate ray origin around old camera position
            Vector3 relativeOrigin = oldRayOrigin - track.lastCameraPose.position;
            Vector3 newRayOrigin = rotationDelta * relativeOrigin + currentPose.position;

            // Rotate ray direction
            Vector3 newRayDir = rotationDelta * oldRayDir;

            // Create new ray
            Ray newRay = new Ray(newRayOrigin, newRayDir);

            // Project to depth plane
            Vector3 worldPos = newRay.origin + newRay.direction * track.depth;

            // Convert back to pixel coordinates
            Vector2Int newPixelCenter = WorldPointToPixel(worldPos, currentPose);

            // Update track
            track.centerPixel = newPixelCenter;
            track.centerRay = newRay;
            track.worldPosition = worldPos;
            track.worldBounds.center = worldPos;
            track.lastCameraPose = currentPose;

            if (config.enableDebugLogs)
            {
                Debug.Log($"[Motion Comp] Track {track.id}: Moved {Vector2Int.Distance(track.centerPixel, newPixelCenter)}px");
            }
        }


        /// <summary>
        /// Process detections with 3D IoU and temporal alignment
        /// </summary>
        public void ProcessDetections(DetectionResponse response)
        {
            if (response == null || response.detections == null)
                return;

            if (!cameraIntrinsics.HasValue)
            {
                Debug.LogWarning("[ObjectTracker] ❌ No camera intrinsics available");
                return;
            }

            // STEP 0: Check frame order
            if (response.frame_id <= lastProcessedFrameId)
            {
                Debug.LogWarning($"[ObjectTracker] ⏭️ Dropping out-of-order frame {response.frame_id} (last: {lastProcessedFrameId})");
                return;
            }

            lastProcessedFrameId = response.frame_id;

            float captureTime = response.capture_time;
            float now = Time.realtimeSinceStartup;
            float responseAge = now - captureTime;

            Debug.Log($"[ObjectTracker] 📦 Processing frame {response.frame_id}, age: {responseAge:F3}s, detections: {response.detections.Count}");

            // Convert detections to 3D with AABB bounds
            List<Detection3D> detections3D = ConvertDetectionsTo3D(response);
            if (detections3D.Count == 0)
            {
                Debug.LogWarning("[ObjectTracker] ⚠️ No valid 3D detections");
                return;
            }

            // STEP 1: Predict all tracks BACKWARD to detection capture time
            Debug.Log($"[ObjectTracker] ⏪ Predicting {activeTracks.Count} tracks backward to capture time");
            foreach (var track in activeTracks)
            {
                float dt = captureTime - track.lastUpdateTime;
                PredictTrack(track, dt);
            }

            // STEP 2: Associate detections with tracks using 3D IoU
            var (costMatrix, validPairs) = BuildCostMatrix(detections3D);
            var matches = GreedyMatch(costMatrix, validPairs);

            Debug.Log($"[ObjectTracker] 🔗 Matched {matches.Count}/{Mathf.Min(activeTracks.Count, detections3D.Count)} pairs using 3D IoU");

            // DEBUG: Log matching details
            if (config.enableDebugLogs && matches.Count > 0)
            {
                foreach (var (trackIdx, detIdx) in matches)
                {
                    var track = activeTracks[trackIdx];
                    var det = detections3D[detIdx];
                    float iou3d = CalculateIoU3D(track.worldBounds, det.worldBounds);
                    float iou2d = CalculateIoU2D(track.bbox2D, det.bbox2D);
                    float dist3D = Vector3.Distance(track.worldPosition, det.worldPosition);
                    Debug.Log($"  ✅ Match: Track#{track.id}({track.className}) ↔ Det({det.detection.class_name}) | 3D IoU={iou3d:F2} 2D IoU={iou2d:F2} dist={dist3D:F2}m");
                }
            }

            // STEP 3: Update matched tracks with smoothing
            foreach (var (trackIdx, detIdx) in matches)
            {
                UpdateTrack(activeTracks[trackIdx], detections3D[detIdx], captureTime);
            }

            // STEP 4: Handle unmatched tracks
            HandleUnmatchedTracks(matches, captureTime);

            // STEP 5: Create new tracks
            int newTracksCreated = CreateNewTracks(matches, detections3D, captureTime);

            // STEP 6: Predict all tracks FORWARD to now
            float forwardDt = now - captureTime;
            Debug.Log($"[ObjectTracker] ⏩ Predicting {activeTracks.Count} tracks forward {forwardDt:F3}s to present");
            foreach (var track in activeTracks)
            {
                PredictTrack(track, forwardDt);
            }

            // STEP 7: Merge duplicates
            MergeDuplicateTracks();

            // SUMMARY
            int tentative = activeTracks.Count(t => t.state == TrackState.Tentative);
            int confirmed = ConfirmedTrackCount;
            Debug.Log($"[ObjectTracker] 📊 Summary: {activeTracks.Count} tracks ({confirmed} confirmed, {tentative} tentative), {newTracksCreated} new");
        }

        /// <summary>
        /// Predict track state by delta time
        /// </summary>
        private void PredictTrack(TrackedObject track, float dt)
        {
            if (Mathf.Abs(dt) < 0.001f) return;

            // Predict position
            track.worldPosition += track.velocity * dt;
            track.worldBounds.center = track.worldPosition;

            // Clamp velocity
            if (track.velocity.magnitude > config.maxObjectVelocity)
            {
                track.velocity = track.velocity.normalized * config.maxObjectVelocity;
            }
        }

        /// <summary>
        /// Build cost matrix using 3D IoU
        /// </summary>
        private (float[,] costs, List<(int trackIdx, int detIdx)> validPairs) BuildCostMatrix(List<Detection3D> detections)
        {
            float[,] costs = new float[activeTracks.Count, detections.Count];
            List<(int, int)> validPairs = new List<(int, int)>();

            Debug.Log($"[ObjectTracker] 🧮 Building 3D IoU cost matrix: {activeTracks.Count} tracks × {detections.Count} detections");

            for (int i = 0; i < activeTracks.Count; i++)
            {
                TrackedObject track = activeTracks[i];

                for (int j = 0; j < detections.Count; j++)
                {
                    Detection3D detection = detections[j];

                    // Hard reject class mismatches
                    if (track.classId != detection.detection.class_id)
                    {
                        costs[i, j] = 999f;
                        continue;
                    }

                    // Calculate 3D IoU (PRIMARY METRIC)
                    float iou3d = CalculateIoU3D(track.worldBounds, detection.worldBounds);

                    // Calculate 2D IoU (SECONDARY METRIC)
                    float iou2d = CalculateIoU2D(track.bbox2D, detection.bbox2D);

                    // Calculate 3D distance
                    float dist3D = Vector3.Distance(track.worldPosition, detection.worldPosition);

                    // Calculate 2D center distance
                    float centerDist2D = Vector2.Distance(
                        new Vector2(track.centerPixel.x, track.centerPixel.y),
                        new Vector2(detection.centerPixel.x, detection.centerPixel.y)
                    );

                    // Combined cost (weighted toward 3D IoU)
                    float iou3dCost = 1.0f - iou3d;
                    float iou2dCost = 1.0f - iou2d;
                    float distCost3D = Mathf.Clamp01(dist3D / config.max3DDistance);
                    float distCost2D = Mathf.Clamp01(centerDist2D / config.maxCenterDistance);

                    float cost = (iou3dCost * 0.5f) + (iou2dCost * 0.2f) + (distCost3D * 0.2f) + (distCost2D * 0.1f);
                    costs[i, j] = cost;

                    // Gating (relaxed for 3D IoU due to depth noise)
                    bool passesGating = (iou3d >= config.iou3DThreshold || iou2d >= config.iou2DThreshold) &&
                                       dist3D <= config.max3DDistance &&
                                       centerDist2D <= config.maxCenterDistance;

                    if (passesGating)
                    {
                        validPairs.Add((i, j));
                        if (config.enableDebugLogs)
                        {
                            Debug.Log($"  ✓ Track#{track.id}({track.className}) ↔ Det({detection.detection.class_name}): 3D IoU={iou3d:F2} 2D IoU={iou2d:F2} dist={dist3D:F2}m cost={cost:F2}");
                        }
                    }
                    else
                    {
                        costs[i, j] = 999f;
                        if (config.enableDebugLogs && track.classId == detection.detection.class_id)
                        {
                            Debug.Log($"  ✗ REJECT Track#{track.id} ↔ Det: 3D IoU={iou3d:F2}(need>{config.iou3DThreshold}) 2D IoU={iou2d:F2}(need>{config.iou2DThreshold}) dist={dist3D:F2}m");
                        }
                    }
                }
            }

            Debug.Log($"[ObjectTracker] 🎯 Found {validPairs.Count} valid pairs after 3D IoU gating");
            return (costs, validPairs);
        }

        /// <summary>
        /// Calculate 3D IoU (AABB version)
        /// </summary>
        private float CalculateIoU3D(Bounds a, Bounds b)
        {
            Vector3 min = Vector3.Max(a.min, b.min);
            Vector3 max = Vector3.Min(a.max, b.max);
            Vector3 d = max - min;

            if (d.x <= 0f || d.y <= 0f || d.z <= 0f) return 0f;

            float intersection = d.x * d.y * d.z;
            float volumeA = a.size.x * a.size.y * a.size.z;
            float volumeB = b.size.x * b.size.y * b.size.z;
            float union = volumeA + volumeB - intersection;

            return union > 0f ? (intersection / union) : 0f;
        }

        /// <summary>
        /// Calculate 2D IoU (fallback metric)
        /// </summary>
        private float CalculateIoU2D(Rect a, Rect b)
        {
            float x1 = Mathf.Max(a.xMin, b.xMin);
            float y1 = Mathf.Max(a.yMin, b.yMin);
            float x2 = Mathf.Min(a.xMax, b.xMax);
            float y2 = Mathf.Min(a.yMax, b.yMax);

            float intersection = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
            float union = a.width * a.height + b.width * b.height - intersection;

            return union > 0 ? intersection / union : 0f;
        }

        /// <summary>
        /// Greedy matching algorithm
        /// </summary>
        private List<(int trackIdx, int detIdx)> GreedyMatch(
            float[,] costMatrix,
            List<(int trackIdx, int detIdx)> validPairs)
        {
            List<(int, int)> matches = new List<(int, int)>();

            if (activeTracks.Count == 0 || validPairs.Count == 0)
                return matches;

            // Sort by cost
            var sortedPairs = validPairs
                .OrderBy(p => costMatrix[p.trackIdx, p.detIdx])
                .ToList();

            HashSet<int> usedTracks = new HashSet<int>();
            HashSet<int> usedDetections = new HashSet<int>();

            foreach (var (trackIdx, detIdx) in sortedPairs)
            {
                if (usedTracks.Contains(trackIdx) || usedDetections.Contains(detIdx))
                    continue;

                matches.Add((trackIdx, detIdx));
                usedTracks.Add(trackIdx);
                usedDetections.Add(detIdx);
            }

            return matches;
        }

        /// <summary>
        /// Update track with new detection (with EMA smoothing)
        /// </summary>
        private void UpdateTrack(TrackedObject track, Detection3D detection, float updateTime)
        {
            float dt = updateTime - track.lastUpdateTime;

            // Update velocity
            if (dt > 0.01f)
            {
                Vector3 newVelocity = (detection.worldPosition - track.worldPosition) / dt;
                track.velocity = Vector3.Lerp(track.velocity, newVelocity, config.smoothingAlphaVelocity);
            }

            // SMOOTH position and size (EMA)
            track.worldPosition = Vector3.Lerp(track.worldPosition, detection.worldPosition, config.smoothingAlphaPosition);
            track.worldSize = Vector3.Lerp(track.worldSize, detection.worldSize, config.smoothingAlphaSize);

            // Update bounds
            track.worldBounds = new Bounds(track.worldPosition, track.worldSize);

            track.depth = detection.depth;
            track.centerRay = detection.centerRay;
            track.centerPixel = detection.centerPixel;
            track.bbox2D = detection.bbox2D;
            track.confidence = detection.detection.confidence;
            track.lastUpdateTime = updateTime;
            track.timeSinceLastUpdate = 0f;
            track.lastCameraPose = detection.cameraPose;

            // Update lifecycle
            track.hits++;
            track.trackConfidence = Mathf.Min(1.0f, track.trackConfidence + 0.2f);

            if (track.hits >= config.minHits && track.state == TrackState.Tentative)
            {
                track.state = TrackState.Confirmed;
                Debug.Log($"[ObjectTracker] ✅ Track {track.id} CONFIRMED ({track.className}) after {track.hits} hits");
            }

            track.AddToHistory(detection.worldPosition, updateTime);
        }

        /// <summary>
        /// Handle tracks without detection matches
        /// </summary>
        private void HandleUnmatchedTracks(List<(int trackIdx, int detIdx)> matches, float currentTime)
        {
            var unmatchedTrackIndices = Enumerable.Range(0, activeTracks.Count)
                .Except(matches.Select(m => m.trackIdx))
                .ToList();

            if (unmatchedTrackIndices.Count > 0)
            {
                Debug.Log($"[ObjectTracker] 👻 {unmatchedTrackIndices.Count} unmatched tracks");
            }

            foreach (int idx in unmatchedTrackIndices)
            {
                var track = activeTracks[idx];

                float maxAge = track.state == TrackState.Confirmed
                    ? config.maxMissTimeSec
                    : config.maxMissTimeSec * 0.5f;

                if (track.timeSinceLastUpdate > maxAge)
                {
                    track.state = TrackState.Lost;
                    Debug.Log($"[ObjectTracker] ❌ Track {track.id} LOST ({track.className}) - no updates for {track.timeSinceLastUpdate:F2}s");
                }
            }
        }

        /// <summary>
        /// Create new tracks from unmatched detections
        /// </summary>
        private int CreateNewTracks(List<(int trackIdx, int detIdx)> matches, List<Detection3D> detections, float createTime)
        {
            var unmatchedDetIndices = Enumerable.Range(0, detections.Count)
                .Except(matches.Select(m => m.detIdx))
                .ToList();

            if (unmatchedDetIndices.Count > 0)
            {
                Debug.Log($"[ObjectTracker] 🆕 {unmatchedDetIndices.Count} unmatched detections → creating new tracks");
            }

            int created = 0;
            foreach (int idx in unmatchedDetIndices)
            {
                Detection3D detection = detections[idx];

                // Anti-duplication: check distance to existing tracks
                bool tooClose = activeTracks.Any(t =>
                    t.className == detection.detection.class_name &&
                    Vector3.Distance(t.worldPosition, detection.worldPosition) < 0.3f
                );

                if (tooClose)
                {
                    Debug.Log($"[ObjectTracker] ⚠️ Rejected new {detection.detection.class_name} (too close to existing track)");
                    continue;
                }

                CreateNewTrack(detection, createTime);
                created++;
            }

            return created;
        }

        /// <summary>
        /// Create a new track
        /// </summary>
        private void CreateNewTrack(Detection3D detection, float createTime)
        {
            TrackedObject newTrack = new TrackedObject
            {
                id = nextTrackId++,
                classId = detection.detection.class_id,
                className = detection.detection.class_name,
                worldPosition = detection.worldPosition,
                worldPositionSmoothed = detection.worldPosition,
                worldSize = detection.worldSize,
                worldBounds = detection.worldBounds,
                worldRotation = Quaternion.identity,
                velocity = Vector3.zero,
                velocitySmoothed = Vector3.zero,
                centerRay = detection.centerRay,
                depth = detection.depth,
                centerPixel = detection.centerPixel,
                bbox2D = detection.bbox2D,
                state = TrackState.Tentative,
                hits = 1,
                timeSinceLastUpdate = 0f,
                lastUpdateTime = createTime,
                confidence = detection.detection.confidence,
                lastCameraPose = detection.cameraPose,
                trackConfidence = 1.0f,
                displayColor = Color.yellow
            };

            activeTracks.Add(newTrack);

            Debug.Log($"[ObjectTracker] 🆕 New track {newTrack.id} created ({newTrack.className})");
        }

        /// <summary>
        /// Merge duplicate tracks
        /// </summary>
        private void MergeDuplicateTracks()
        {
            int mergedCount = 0;

            for (int i = 0; i < activeTracks.Count; i++)
            {
                for (int j = i + 1; j < activeTracks.Count; j++)
                {
                    var track1 = activeTracks[i];
                    var track2 = activeTracks[j];

                    if (track1.className == track2.className)
                    {
                        float dist = Vector3.Distance(track1.worldPosition, track2.worldPosition);

                        if (dist < 0.25f)
                        {
                            TrackedObject keepTrack, removeTrack;

                            if (track1.state == TrackState.Confirmed && track2.state != TrackState.Confirmed)
                            {
                                keepTrack = track1;
                                removeTrack = track2;
                            }
                            else if (track2.state == TrackState.Confirmed && track1.state != TrackState.Confirmed)
                            {
                                keepTrack = track2;
                                removeTrack = track1;
                            }
                            else
                            {
                                keepTrack = track1.id < track2.id ? track1 : track2;
                                removeTrack = track1.id < track2.id ? track2 : track1;
                            }

                            removeTrack.state = TrackState.Lost;
                            mergedCount++;

                            Debug.Log($"[ObjectTracker] 🔀 Merged track {removeTrack.id} into {keepTrack.id} ({keepTrack.className}, dist={dist:F2}m)");
                        }
                    }
                }
            }

            if (mergedCount > 0)
            {
                Debug.Log($"[ObjectTracker] 🔀 Total merges: {mergedCount}");
            }
        }

        /// <summary>
        /// Build AABB from 8 corner points (4 bbox corners × near/far depth)
        /// </summary>
        // CRITICAL FIX: Proper 2D pixel → 3D world coordinate transformation
        // Based on pinhole camera model with camera intrinsics

        /// <summary>
        /// Convert 2D pixel coordinates to 3D world ray (CORRECTED)
        /// </summary>
        private Ray PixelToWorldRay(Vector2Int pixelCoords, PassthroughCameraIntrinsics intrinsics, Pose cameraPose)
        {
            // Step 1: Convert pixel coordinates to normalized camera coordinates
            // Using pinhole camera projection model
            float x_norm = (pixelCoords.x - intrinsics.PrincipalPoint.x) / intrinsics.FocalLength.x;
            float y_norm = (pixelCoords.y - intrinsics.PrincipalPoint.y) / intrinsics.FocalLength.y;

            // Step 2: Create ray direction in camera local space (Z-forward)
            Vector3 rayDirLocal = new Vector3(x_norm, y_norm, 1f).normalized;

            // Step 3: Transform ray to world space using camera pose
            Vector3 rayDirWorld = cameraPose.rotation * rayDirLocal;

            // Step 4: Ray starts at camera position
            Ray worldRay = new Ray(cameraPose.position, rayDirWorld);

            if (config.enableDebugLogs)
            {
                Debug.Log($"[Coord Transform] Pixel:{pixelCoords} → Norm:({x_norm:F3},{y_norm:F3}) → Ray:{rayDirWorld}");
            }

            return worldRay;
        }
        /// <summary>
        /// Convert 2D detections to 3D with CORRECTED coordinate transforms
        /// </summary>
        private List<Detection3D> ConvertDetectionsTo3D(DetectionResponse response)
        {
            List<Detection3D> detections3D = new List<Detection3D>();
            PassthroughCameraIntrinsics intrinsics = cameraIntrinsics.Value;

            int imageWidth = response.image_size[0];
            int imageHeight = response.image_size[1];

            Pose currentCameraPose = new Pose(
                centerEyeTransform.position,
                centerEyeTransform.rotation
            );

            foreach (var detection in response.detections)
            {
                if (detection.bbox == null || detection.bbox.Length != 4)
                    continue;

                float x1_orig = detection.bbox[0];
                float y1_orig = detection.bbox[1];
                float x2_orig = detection.bbox[2];
                float y2_orig = detection.bbox[3];

                // Flip Y-axis
                float y1 = imageHeight - y1_orig;
                float y2 = imageHeight - y2_orig;

                float y_min = Mathf.Min(y1, y2);
                float y_max = Mathf.Max(y1, y2);

                float x1 = x1_orig;
                float x2 = x2_orig;

                // ====== Y-OFFSET CORRECTION ======
                float bboxHeightPixels = y_max - y_min;
                float yOffsetAdjustment = 0f;

                if (autoCorrectYOffset)
                {
                    // Apply percentage-based offset
                    yOffsetAdjustment = bboxHeightPixels * yOffsetPercentage;
                }

                // Add manual offset
                yOffsetAdjustment += manualYOffsetPixels;

                // Calculate center pixel WITH offset
                Vector2Int centerPixel = new Vector2Int(
                    Mathf.RoundToInt((x1 + x2) / 2f),
                    Mathf.RoundToInt((y_min + y_max) / 2f + yOffsetAdjustment)
                );

                if (config.enableDebugLogs)
                {
                    Debug.Log($"[Y-Offset Debug] {detection.class_name}:\n" +
                             $"  Bbox Height: {bboxHeightPixels}px\n" +
                             $"  Auto Offset: {bboxHeightPixels * yOffsetPercentage:F1}px\n" +
                             $"  Manual Offset: {manualYOffsetPixels}px\n" +
                             $"  Total Offset: {yOffsetAdjustment:F1}px\n" +
                             $"  Original Center: ({(x1 + x2) / 2f:F0}, {(y_min + y_max) / 2f:F0})\n" +
                             $"  Adjusted Center: {centerPixel}");
                }

                Ray centerRay = PixelToWorldRay(centerPixel, intrinsics, currentCameraPose);

                float depth = defaultDepth;
                if (Physics.Raycast(centerRay, out RaycastHit hit, maxRaycastDistance, raycastLayers))
                {
                    depth = hit.distance;
                }

                Vector3 worldPosition = centerRay.origin + centerRay.direction * depth;

                float thickness = Mathf.Clamp(
                    depth * config.depthThicknessFraction,
                    config.minBoundsThickness,
                    config.maxBoundsThickness
                );

                Bounds bounds = BuildBoundsFrom8PointsFixed(
                    x1, y_min, x2, y_max,
                    depth, thickness,
                    intrinsics, currentCameraPose
                );

                float pixelWidth = x2 - x1;
                float pixelHeight = y_max - y_min;

                float worldWidth = (pixelWidth * depth) / intrinsics.FocalLength.x;
                float worldHeight = (pixelHeight * depth) / intrinsics.FocalLength.y;

                Detection3D det3D = new Detection3D
                {
                    detection = detection,
                    worldPosition = worldPosition,
                    worldSize = new Vector3(worldWidth, worldHeight, thickness),
                    worldBounds = bounds,
                    depth = depth,
                    centerRay = centerRay,
                    centerPixel = centerPixel,
                    bbox2D = new Rect(x1, y_min, x2 - x1, pixelHeight),
                    cameraPose = currentCameraPose
                };

                detections3D.Add(det3D);
            }

            return detections3D;
        }

        /// <summary>
        /// Build AABB from 8 corner points (FIXED coordinate transform)
        /// </summary>
        private Bounds BuildBoundsFrom8PointsFixed(
            float x1, float y1, float x2, float y2,
            float centerDepth, float thickness,
            PassthroughCameraIntrinsics intrinsics,
            Pose cameraPose)
        {
            float nearDepth = Mathf.Max(0.1f, centerDepth - thickness * 0.5f);
            float farDepth = centerDepth + thickness * 0.5f;

            // 4 corner pixels (Y already flipped and corrected)
            // Note: y1 is bottom, y2 is top in camera space now
            Vector2Int topLeft = new Vector2Int(Mathf.RoundToInt(x1), Mathf.RoundToInt(y2));
            Vector2Int topRight = new Vector2Int(Mathf.RoundToInt(x2), Mathf.RoundToInt(y2));
            Vector2Int bottomLeft = new Vector2Int(Mathf.RoundToInt(x1), Mathf.RoundToInt(y1));
            Vector2Int bottomRight = new Vector2Int(Mathf.RoundToInt(x2), Mathf.RoundToInt(y1));

            // FIXED: Convert pixels to rays using proper transform
            Ray rayTL = PixelToWorldRay(topLeft, intrinsics, cameraPose);
            Ray rayTR = PixelToWorldRay(topRight, intrinsics, cameraPose);
            Ray rayBL = PixelToWorldRay(bottomLeft, intrinsics, cameraPose);
            Ray rayBR = PixelToWorldRay(bottomRight, intrinsics, cameraPose);

            // 8 world points (4 corners × 2 depths)
            Vector3[] points = new Vector3[8]
            {
            rayTL.GetPoint(nearDepth),
            rayTR.GetPoint(nearDepth),
            rayBL.GetPoint(nearDepth),
            rayBR.GetPoint(nearDepth),
            rayTL.GetPoint(farDepth),
            rayTR.GetPoint(farDepth),
            rayBL.GetPoint(farDepth),
            rayBR.GetPoint(farDepth)
            };

            // Create bounds
            Bounds bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++)
            {
                bounds.Encapsulate(points[i]);
            }

            return bounds;
        }       
         /// <summary>
         /// Convert 3D world point back to 2D pixel coordinates (inverse transform)
         /// Used for motion compensation
         /// </summary>
        private Vector2Int WorldPointToPixel(Vector3 worldPos, Pose cameraPose)
        {
            if (!cameraIntrinsics.HasValue) return Vector2Int.zero;

            var intrinsics = cameraIntrinsics.Value;

            // Step 1: Transform world point to camera local space
            Vector3 localPos = Quaternion.Inverse(cameraPose.rotation) * (worldPos - cameraPose.position);

            // Step 2: Prevent division by zero
            if (Mathf.Abs(localPos.z) < 0.01f)
            {
                Debug.LogWarning($"[WorldToPixel] Point behind camera: {worldPos}");
                return Vector2Int.zero;
            }

            // Step 3: Pinhole camera projection (3D → 2D)
            float x_norm = localPos.x / localPos.z;
            float y_norm = localPos.y / localPos.z;

            // Step 4: Apply camera intrinsics
            float x_pixel = x_norm * intrinsics.FocalLength.x + intrinsics.PrincipalPoint.x;
            float y_pixel = y_norm * intrinsics.FocalLength.y + intrinsics.PrincipalPoint.y;

            Vector2Int pixel = new Vector2Int(Mathf.RoundToInt(x_pixel), Mathf.RoundToInt(y_pixel));

            if (config.enableDebugLogs)
            {
                Debug.Log($"[WorldToPixel] {worldPos} → Local:{localPos} → Norm:({x_norm:F3},{y_norm:F3}) → Pixel:{pixel}");
            }

            return pixel;
        }
    }

    /// <summary>
    /// Detection with 3D information and bounds
    /// </summary>
    public class Detection3D
    {
        public Detection detection;
        public Vector3 worldPosition;
        public Vector3 worldSize;
        public Bounds worldBounds;  
        public float depth;
        public Ray centerRay;
        public Vector2Int centerPixel;
        public Rect bbox2D;
        public Pose cameraPose;
    }
}