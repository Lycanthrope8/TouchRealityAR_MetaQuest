using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using PassthroughCameraSamples;

namespace ARObjectDetection
{
    /// <summary>
    /// PHASE 2 - 3D IoU tracking with smoothing and backpressure
    /// FIXED: Now scales bbox coordinates to match camera intrinsics resolution
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
                // NEW: Log the actual camera info
                var intrinsics = cameraIntrinsics.Value;
                Debug.Log($"[Camera Info] Resolution: {intrinsics.Resolution.x}×{intrinsics.Resolution.y}");
                Debug.Log($"[Camera Info] Focal Length: ({intrinsics.FocalLength.x:F1}, {intrinsics.FocalLength.y:F1})");
                Debug.Log($"[Camera Info] Principal Point: ({intrinsics.PrincipalPoint.x:F1}, {intrinsics.PrincipalPoint.y:F1})");

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

        private void Update()
        {
            if (activeTracks.Count == 0) return;

            float now = Time.realtimeSinceStartup;

            foreach (var track in activeTracks)
            {
                track.timeSinceLastUpdate = now - track.lastUpdateTime;

                if (config.enableMotionCompensation)
                {
                    CompensateForCameraMotion(track);
                }

                float dt = (track.stateTime > 0f) ? (now - track.stateTime) : Time.deltaTime;
                if (dt < 0f) dt = 0f;

                track.worldPosition += track.velocity * dt;
                track.stateTime = now;

                track.worldPositionSmoothed = Vector3.Lerp(
                    track.worldPositionSmoothed,
                    track.worldPosition,
                    config.smoothingAlphaPosition
                );

                track.worldBounds.center = track.worldPosition;

                if (track.timeSinceLastUpdate > 0.1f)
                {
                    track.trackConfidence *= Mathf.Exp(-dt * 2.0f);
                    track.trackConfidence = Mathf.Max(track.trackConfidence, 0.1f);
                }
            }

            int lostCount = activeTracks.RemoveAll(t => t.state == TrackState.Lost);
            if (lostCount > 0)
            {
                Debug.Log($"[ObjectTracker] 🗑️ Removed {lostCount} lost tracks");
            }
        }

        private void CompensateForCameraMotion(TrackedObject track)
        {
            if (centerEyeTransform == null || !cameraIntrinsics.HasValue)
                return;

            Pose currentPose = new Pose(
                centerEyeTransform.position,
                centerEyeTransform.rotation
            );

            Quaternion rotationDelta = currentPose.rotation * Quaternion.Inverse(track.lastCameraPose.rotation);
            Vector3 positionDelta = currentPose.position - track.lastCameraPose.position;

            if (Quaternion.Angle(rotationDelta, Quaternion.identity) < 0.5f && positionDelta.magnitude < 0.01f)
            {
                return;
            }

            Vector3 oldRayOrigin = track.centerRay.origin;
            Vector3 oldRayDir = track.centerRay.direction;

            Vector3 relativeOrigin = oldRayOrigin - track.lastCameraPose.position;
            Vector3 newRayOrigin = rotationDelta * relativeOrigin + currentPose.position;
            Vector3 newRayDir = rotationDelta * oldRayDir;

            Ray newRay = new Ray(newRayOrigin, newRayDir);
            Vector3 worldPos = newRay.origin + newRay.direction * track.depth;
            Vector2Int newPixelCenter = WorldPointToPixel(worldPos, currentPose);

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

        public void ProcessDetections(DetectionResponse response)
        {
            if (response == null || response.detections == null)
                return;

            if (!cameraIntrinsics.HasValue)
            {
                Debug.LogWarning("[ObjectTracker] ❌ No camera intrinsics available");
                return;
            }

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

            List<Detection3D> detections3D = ConvertDetectionsTo3D(response);
            if (detections3D.Count == 0)
            {
                Debug.LogWarning("[ObjectTracker] ⚠️ No valid 3D detections");
                return;
            }

            Debug.Log($"[ObjectTracker] ⏪ Aligning {activeTracks.Count} tracks to capture time");
            foreach (var track in activeTracks)
            {
                if (track.stateTime <= 0f)
                    track.stateTime = now;

                float dtToCapture = captureTime - track.stateTime;
                PredictTrack(track, dtToCapture);
                track.stateTime = captureTime;
            }

            var (costMatrix, validPairs) = BuildCostMatrix(detections3D);
            var matches = GreedyMatch(costMatrix, validPairs);

            Debug.Log($"[ObjectTracker] 🔗 Matched {matches.Count}/{Mathf.Min(activeTracks.Count, detections3D.Count)} pairs using 3D IoU");

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

            foreach (var (trackIdx, detIdx) in matches)
            {
                UpdateTrack(activeTracks[trackIdx], detections3D[detIdx], captureTime);
            }

            HandleUnmatchedTracks(matches, captureTime);
            int newTracksCreated = CreateNewTracks(matches, detections3D, captureTime);

            float forwardDt = now - captureTime;
            Debug.Log($"[ObjectTracker] ⏩ Predicting {activeTracks.Count} tracks forward {forwardDt:F3}s to present");
            foreach (var track in activeTracks)
            {
                PredictTrack(track, forwardDt);
                track.stateTime = now;
            }

            MergeDuplicateTracks();

            int tentative = activeTracks.Count(t => t.state == TrackState.Tentative);
            int confirmed = ConfirmedTrackCount;
            Debug.Log($"[ObjectTracker] 📊 Summary: {activeTracks.Count} tracks ({confirmed} confirmed, {tentative} tentative), {newTracksCreated} new");
        }

        private void PredictTrack(TrackedObject track, float dt)
        {
            if (Mathf.Abs(dt) < 0.001f) return;

            track.worldPosition += track.velocity * dt;
            track.worldBounds.center = track.worldPosition;

            if (track.velocity.magnitude > config.maxObjectVelocity)
            {
                track.velocity = track.velocity.normalized * config.maxObjectVelocity;
            }
        }

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

                    if (track.classId != detection.detection.class_id)
                    {
                        costs[i, j] = 999f;
                        continue;
                    }

                    float iou3d = CalculateIoU3D(track.worldBounds, detection.worldBounds);
                    float iou2d = CalculateIoU2D(track.bbox2D, detection.bbox2D);
                    float dist3D = Vector3.Distance(track.worldPosition, detection.worldPosition);
                    float centerDist2D = Vector2.Distance(
                        new Vector2(track.centerPixel.x, track.centerPixel.y),
                        new Vector2(detection.centerPixel.x, detection.centerPixel.y)
                    );

                    float iou3dCost = 1.0f - iou3d;
                    float iou2dCost = 1.0f - iou2d;
                    float distCost3D = Mathf.Clamp01(dist3D / config.max3DDistance);
                    float distCost2D = Mathf.Clamp01(centerDist2D / config.maxCenterDistance);

                    float cost = (iou3dCost * 0.5f) + (iou2dCost * 0.2f) + (distCost3D * 0.2f) + (distCost2D * 0.1f);
                    costs[i, j] = cost;

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

        private List<(int trackIdx, int detIdx)> GreedyMatch(
            float[,] costMatrix,
            List<(int trackIdx, int detIdx)> validPairs)
        {
            List<(int, int)> matches = new List<(int, int)>();

            if (activeTracks.Count == 0 || validPairs.Count == 0)
                return matches;

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

        private void UpdateTrack(TrackedObject track, Detection3D detection, float updateTime)
        {
            float dtMeas = updateTime - track.lastMeasuredTime;
            if (dtMeas > 0.01f)
            {
                Vector3 newVelocity = (detection.worldPosition - track.lastMeasuredPosition) / dtMeas;
                track.velocity = Vector3.Lerp(track.velocity, newVelocity, config.smoothingAlphaVelocity);
            }

            track.worldPosition = detection.worldPosition;
            track.worldSize = Vector3.Lerp(track.worldSize, detection.worldSize, config.smoothingAlphaSize);
            track.worldBounds = new Bounds(track.worldPosition, track.worldSize);

            track.depth = detection.depth;
            track.centerRay = detection.centerRay;
            track.centerPixel = detection.centerPixel;
            track.bbox2D = detection.bbox2D;
            track.confidence = detection.detection.confidence;
            track.lastUpdateTime = updateTime;
            track.stateTime = updateTime;
            track.timeSinceLastUpdate = 0f;
            track.lastCameraPose = detection.cameraPose;

            track.lastMeasuredPosition = detection.worldPosition;
            track.lastMeasuredTime = updateTime;

            track.hits++;
            track.trackConfidence = Mathf.Min(1.0f, track.trackConfidence + 0.2f);

            if (track.hits >= config.minHits && track.state == TrackState.Tentative)
            {
                track.state = TrackState.Confirmed;
                Debug.Log($"[ObjectTracker] ✅ Track {track.id} CONFIRMED ({track.className}) after {track.hits} hits");
            }

            track.AddToHistory(detection.worldPosition, updateTime);
        }

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

        private void CreateNewTrack(Detection3D detection, float createTime)
        {
            TrackedObject newTrack = new TrackedObject
            {
                id = nextTrackId++,
                classId = detection.detection.class_id,
                className = detection.detection.class_name,
                stateTime = createTime,
                lastMeasuredPosition = detection.worldPosition,
                lastMeasuredTime = createTime,
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
                displayColor = Color.yellow,
            };

            activeTracks.Add(newTrack);

            Debug.Log($"[ObjectTracker] 🆕 New track {newTrack.id} created ({newTrack.className})");
        }

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
        /// Convert 2D pixel coordinates to 3D world ray (CORRECTED)
        /// </summary>
        private Ray PixelToWorldRay(Vector2Int pixelCoords, PassthroughCameraIntrinsics intrinsics, Pose cameraPose)
        {
            float x_norm = (pixelCoords.x - intrinsics.PrincipalPoint.x) / intrinsics.FocalLength.x;
            float y_norm = (pixelCoords.y - intrinsics.PrincipalPoint.y) / intrinsics.FocalLength.y;

            Vector3 rayDirLocal = new Vector3(x_norm, y_norm, 1f).normalized;
            Vector3 rayDirWorld = cameraPose.rotation * rayDirLocal;

            Ray worldRay = new Ray(cameraPose.position, rayDirWorld);

            if (config.enableDebugLogs)
            {
                Debug.Log($"[Coord Transform] Pixel:{pixelCoords} → Norm:({x_norm:F3},{y_norm:F3}) → Ray:{rayDirWorld}");
            }

            return worldRay;
        }

        /// <summary>
        /// Convert 2D detections to 3D with CORRECTED coordinate transforms and bbox scaling
        /// FIXED: Now scales bbox coordinates from sent resolution to camera intrinsics resolution
        /// </summary>
        private List<Detection3D> ConvertDetectionsTo3D(DetectionResponse response)
        {
            List<Detection3D> detections3D = new List<Detection3D>();
            PassthroughCameraIntrinsics intrinsics = cameraIntrinsics.Value;

            // CRITICAL FIX: Get sent image size and camera intrinsics resolution
            int sentW = response.image_size[0];
            int sentH = response.image_size[1];
            int camW = intrinsics.Resolution.x;
            int camH = intrinsics.Resolution.y;

            // Calculate scaling factors
            float sx = (sentW > 0) ? (float)camW / sentW : 1f;
            float sy = (sentH > 0) ? (float)camH / sentH : 1f;

            Debug.Log($"[Coord Scale] Sent:{sentW}×{sentH} → Camera:{camW}×{camH} | sx={sx:F2}, sy={sy:F2}");

            Pose currentCameraPose = new Pose(
                centerEyeTransform.position,
                centerEyeTransform.rotation
            );

            foreach (var detection in response.detections)
            {
                if (detection.bbox == null || detection.bbox.Length != 4)
                    continue;

                // CRITICAL FIX: Scale bbox from sent resolution to camera intrinsics resolution
                float x1_sent = detection.bbox[0];
                float y1_sent = detection.bbox[1];
                float x2_sent = detection.bbox[2];
                float y2_sent = detection.bbox[3];

                // Scale to camera resolution
                float x1_cam = x1_sent * sx;
                float y1_cam = y1_sent * sy;
                float x2_cam = x2_sent * sx;
                float y2_cam = y2_sent * sy;

                // Flip Y-axis (now in camera resolution space)
                float y1 = camH - y1_cam;
                float y2 = camH - y2_cam;

                float y_min = Mathf.Min(y1, y2);
                float y_max = Mathf.Max(y1, y2);

                float x1 = x1_cam;
                float x2 = x2_cam;

                // Y-offset correction
                float bboxHeightPixels = y_max - y_min;
                float yOffsetAdjustment = 0f;

                if (autoCorrectYOffset)
                {
                    yOffsetAdjustment = bboxHeightPixels * yOffsetPercentage;
                }

                yOffsetAdjustment += manualYOffsetPixels;

                Vector2Int centerPixel = new Vector2Int(
                    Mathf.RoundToInt((x1 + x2) / 2f),
                    Mathf.RoundToInt((y_min + y_max) / 2f + yOffsetAdjustment)
                );

                if (config.enableDebugLogs)
                {
                    Debug.Log($"[Coord Debug] {detection.class_name}:\n" +
                             $"  Sent bbox: ({x1_sent:F1}, {y1_sent:F1}, {x2_sent:F1}, {y2_sent:F1})\n" +
                             $"  Scaled bbox: ({x1_cam:F1}, {y1_cam:F1}, {x2_cam:F1}, {y2_cam:F1})\n" +
                             $"  Y-flipped: ({x1:F1}, {y_min:F1}, {x2:F1}, {y_max:F1})\n" +
                             $"  Center pixel: {centerPixel}");
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

            Vector2Int topLeft = new Vector2Int(Mathf.RoundToInt(x1), Mathf.RoundToInt(y2));
            Vector2Int topRight = new Vector2Int(Mathf.RoundToInt(x2), Mathf.RoundToInt(y2));
            Vector2Int bottomLeft = new Vector2Int(Mathf.RoundToInt(x1), Mathf.RoundToInt(y1));
            Vector2Int bottomRight = new Vector2Int(Mathf.RoundToInt(x2), Mathf.RoundToInt(y1));

            Ray rayTL = PixelToWorldRay(topLeft, intrinsics, cameraPose);
            Ray rayTR = PixelToWorldRay(topRight, intrinsics, cameraPose);
            Ray rayBL = PixelToWorldRay(bottomLeft, intrinsics, cameraPose);
            Ray rayBR = PixelToWorldRay(bottomRight, intrinsics, cameraPose);

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

            Vector3 localPos = Quaternion.Inverse(cameraPose.rotation) * (worldPos - cameraPose.position);

            if (Mathf.Abs(localPos.z) < 0.01f)
            {
                Debug.LogWarning($"[WorldToPixel] Point behind camera: {worldPos}");
                return Vector2Int.zero;
            }

            float x_norm = localPos.x / localPos.z;
            float y_norm = localPos.y / localPos.z;

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