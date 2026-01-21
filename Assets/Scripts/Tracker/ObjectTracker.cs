using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using PassthroughCameraSamples;

namespace ARObjectDetection
{
    /// <summary>
    /// OBJECT TRACKER - LONG-TERM MR EDITION
    /// 
    /// Design Philosophy:
    /// - Track IDs are nearly permanent during a session
    /// - Objects don't "disappear", they get occluded
    /// - Lost tracks are retained for long periods (60-120s default)
    /// - Lost tracks can always be revived via 3D-first matching
    /// - Smooth position output via worldPositionSmoothed
    /// 
    /// Key Features:
    /// - 3D-first association (2D is cost factor only, never blocks)
    /// - Lost track retention with configurable duration
    /// - Lost track motion compensation (keeps 2D state fresh)
    /// - Revival via main matching or second-pass nearest-neighbor
    /// - Continuous worldPositionSmoothed for overlay systems
    /// </summary>
    public class ObjectTracker : MonoBehaviour
    {
        // ============================================================
        // SINGLETON
        // ============================================================
        private static ObjectTracker instance;
        public static ObjectTracker Instance => instance;

        // ============================================================
        // CONFIGURATION
        // ============================================================
        [Header("Configuration")]
        [SerializeField] private TrackerConfig config;
        [SerializeField] private DetectionConfig detectionConfig;
        [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

        [Header("Depth Settings")]
        [SerializeField] private float defaultDepth = 2.0f;
        [SerializeField] private LayerMask raycastLayers = ~0;
        [SerializeField] private float maxRaycastDistance = 10f;

        [Header("Y-Offset Correction")]
        [SerializeField] private float manualYOffsetPixels = 0f;
        [SerializeField] private bool autoCorrectYOffset = true;
        [Range(0f, 1f)]
        [SerializeField] private float yOffsetPercentage = 0.0f;

        // ============================================================
        // TRACK STORAGE
        // ============================================================
        private List<TrackedObject> allTracks = new List<TrackedObject>();
        private int nextTrackId = 0;

        // ============================================================
        // CAMERA
        // ============================================================
        private PassthroughCameraIntrinsics? cameraIntrinsics;
        private Transform centerEyeTransform;

        // ============================================================
        // FRAME TRACKING
        // ============================================================
        private int lastProcessedFrameId = -1;

        // ============================================================
        // METRICS
        // ============================================================
        private int reviveCountMainMatch = 0;
        private int reviveCountSecondPass = 0;
        private int newTrackCount = 0;
        private int lostPrunedCount = 0;
        private int mergeCount = 0;
        private float lastMetricsLogTime = 0f;

        // ============================================================
        // PUBLIC PROPERTIES
        // ============================================================

        /// <summary>Count of non-Lost tracks (Tentative + Confirmed)</summary>
        public int ActiveTrackCount => allTracks.Count(t => t.state != TrackState.Lost);

        /// <summary>Count of Lost tracks (retained for revival)</summary>
        public int LostTrackCount => allTracks.Count(t => t.state == TrackState.Lost);

        /// <summary>Total track count including Lost</summary>
        public int TotalTrackCount => allTracks.Count;

        /// <summary>Count of Confirmed tracks</summary>
        public int ConfirmedTrackCount => allTracks.Count(t => t.state == TrackState.Confirmed);

        /// <summary>
        /// All tracks including Lost. Use for internal systems that need full state.
        /// WARNING: Filter by state if you don't want Lost tracks.
        /// </summary>
        public List<TrackedObject> ActiveTracks => allTracks;

        /// <summary>
        /// Only non-Lost tracks (Tentative + Confirmed).
        /// USE THIS for visualization/anchoring systems.
        /// </summary>
        public IEnumerable<TrackedObject> VisibleTracks => allTracks.Where(t => t.state != TrackState.Lost);

        /// <summary>Only Confirmed tracks</summary>
        public IEnumerable<TrackedObject> ConfirmedTracks => allTracks.Where(t => t.state == TrackState.Confirmed);

        /// <summary>Lost tracks that are eligible for revival</summary>
        public IEnumerable<TrackedObject> RevivableTracks =>
            allTracks.Where(t => t.IsEligibleForRevival(config.reacquireWindowSec));

        // Metrics
        public int ReviveCountMainMatch => reviveCountMainMatch;
        public int ReviveCountSecondPass => reviveCountSecondPass;
        public int NewTrackCount => newTrackCount;
        public int LostPrunedCount => lostPrunedCount;
        public int MergeCount => mergeCount;

        // ============================================================
        // UNITY LIFECYCLE
        // ============================================================

        private void Awake()
        {
            // Singleton enforcement
            if (instance != null && instance != this)
            {
                Debug.LogWarning($"[ObjectTracker] Duplicate instance! Destroying {gameObject.name}");
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
                var intrinsics = cameraIntrinsics.Value;
                Debug.Log($"[ObjectTracker] Camera: {intrinsics.Resolution.x}×{intrinsics.Resolution.y}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ObjectTracker] Camera intrinsics failed: {e.Message}");
            }

            // Get center eye transform
            if (OVRManager.instance != null)
            {
                centerEyeTransform = OVRManager.instance.GetComponentInChildren<Camera>().transform;
            }

            Debug.Log($"[ObjectTracker] ✅ LONG-TERM MR TRACKER initialized");
            Debug.Log($"[ObjectTracker] Retention={config.lostRetentionTimeSec}s, " +
                     $"ReacquireWindow={config.reacquireWindowSec}s, " +
                     $"ConfirmedMiss={config.confirmedMaxMissTimeSec}s");
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void Update()
        {
            if (allTracks.Count == 0) return;

            float now = Time.realtimeSinceStartup;

            // Update all tracks
            foreach (var track in allTracks)
            {
                track.timeSinceLastUpdate = now - track.lastUpdateTime;

                UpdateTrackMotion(track, now);
            }

            // Prune only tracks that exceeded retention period
            PruneLostTracks(now);

            // Periodic metrics logging
            if (config.metricsSummaryIntervalSec > 0 &&
                now - lastMetricsLogTime > config.metricsSummaryIntervalSec)
            {
                lastMetricsLogTime = now;
                LogMetricsSummary();
            }
        }

        /// <summary>
        /// Update track motion: smoothing, prediction, motion compensation
        /// </summary>
        private void UpdateTrackMotion(TrackedObject track, float now)
        {
            // Motion compensation (including Lost tracks within reacquire window)
            bool shouldCompensate = config.enableMotionCompensation &&
                (track.state != TrackState.Lost ||
                 (config.motionCompensateLostTracks && track.IsEligibleForRevival(config.reacquireWindowSec)));

            if (shouldCompensate)
            {
                CompensateForCameraMotion(track);
            }

            // For non-Lost tracks: update smoothed position (for display)
            // worldPosition is only updated when we get a NEW detection measurement
            if (track.state != TrackState.Lost)
            {
                float dt = Time.deltaTime;

                // Smooth position interpolates towards worldPosition (which only changes on detection)
                track.worldPositionSmoothed = Vector3.Lerp(
                    track.worldPositionSmoothed,
                    track.worldPosition,
                    config.smoothingAlphaPosition
                );

                // Update bounds center to match
                track.worldBounds.center = track.worldPosition;

                // Decay confidence when not seen recently
                if (track.timeSinceLastUpdate > 0.2f)
                {
                    track.trackConfidence *= Mathf.Exp(-dt * 1.5f);
                    track.trackConfidence = Mathf.Max(track.trackConfidence, 0.1f);
                }
            }
            else if (config.predictLostTrackMotion && track.IsEligibleForRevival(config.reacquireWindowSec))
            {
                // Optional: continue predicting Lost track motion (usually disabled)
                float dt = Time.deltaTime;
                track.worldPosition += track.velocity * dt;
                track.worldBounds.center = track.worldPosition;
                track.worldPositionSmoothed = track.worldPosition;
            }
        }

        /// <summary>
        /// Compensate track 2D state for camera motion
        /// </summary>
        private void CompensateForCameraMotion(TrackedObject track)
        {
            if (centerEyeTransform == null || !cameraIntrinsics.HasValue) return;

            Pose currentPose = new Pose(centerEyeTransform.position, centerEyeTransform.rotation);

            // Check if camera moved significantly
            Quaternion rotDelta = currentPose.rotation * Quaternion.Inverse(track.lastCameraPose.rotation);
            Vector3 posDelta = currentPose.position - track.lastCameraPose.position;

            if (Quaternion.Angle(rotDelta, Quaternion.identity) < 0.5f && posDelta.magnitude < 0.01f)
                return;

            // For ALL tracks (Lost or Active):
            // World position stays FIXED in world space
            // Only update 2D projection (centerPixel, bbox2D, ray) for the new camera pose

            // Project the fixed world position into new camera frame
            Vector2Int newPixel = WorldPointToPixel(track.worldPosition, currentPose);

            Vector2 pixelDelta = new Vector2(
                newPixel.x - track.centerPixel.x,
                newPixel.y - track.centerPixel.y
            );

            track.bbox2D = new Rect(
                track.bbox2D.x + pixelDelta.x,
                track.bbox2D.y + pixelDelta.y,
                track.bbox2D.width,
                track.bbox2D.height
            );
            track.centerPixel = newPixel;

            // Update ray to point from new camera position to the FIXED world position
            Vector3 toWorld = track.worldPosition - currentPose.position;
            if (toWorld.magnitude > 0.1f)
            {
                track.centerRay = new Ray(currentPose.position, toWorld.normalized);
                track.depth = toWorld.magnitude;
            }

            track.lastCameraPose = currentPose;
        }

        /// <summary>
        /// Prune Lost tracks that have exceeded retention period
        /// </summary>
        private void PruneLostTracks(float now)
        {
            int beforeCount = allTracks.Count;
            allTracks.RemoveAll(t => t.ShouldPrune(now, config.lostRetentionTimeSec));
            int pruned = beforeCount - allTracks.Count;

            if (pruned > 0)
            {
                lostPrunedCount += pruned;
                Debug.Log($"[ObjectTracker] 🗑️ Pruned {pruned} tracks (retention={config.lostRetentionTimeSec}s expired)");
            }
        }

        // ============================================================
        // DETECTION PROCESSING
        // ============================================================

        public void ProcessDetections(DetectionResponse response)
        {
            if (response == null || response.detections == null) return;
            if (!cameraIntrinsics.HasValue)
            {
                Debug.LogWarning("[ObjectTracker] No camera intrinsics!");
                return;
            }

            // Frame ordering check
            if (response.frame_id <= lastProcessedFrameId)
            {
                Debug.LogWarning($"[ObjectTracker] Dropping out-of-order frame {response.frame_id}");
                return;
            }
            lastProcessedFrameId = response.frame_id;

            float captureTime = response.capture_time;
            float now = Time.realtimeSinceStartup;

            if (config.enableDebugLogs)
            {
                Debug.Log($"[ObjectTracker] 📦 Frame {response.frame_id}, {response.detections.Count} detections, " +
                         $"age={(now - captureTime) * 1000:F0}ms");
            }

            // Convert 2D detections to 3D
            List<Detection3D> detections3D = ConvertDetectionsTo3D(response);
            if (detections3D.Count == 0)
            {
                if (config.enableDebugLogs)
                    Debug.LogWarning("[ObjectTracker] No valid 3D detections");
                return;
            }

            // Align active tracks to capture time (for matching purposes)
            // NOTE: We no longer predict position here - just update stateTime
            foreach (var track in allTracks)
            {
                if (track.state == TrackState.Lost) continue;
                if (track.stateTime <= 0f) track.stateTime = now;
                track.stateTime = captureTime;
            }

            // === MAIN MATCHING (includes Lost tracks) ===
            var (costMatrix, validPairs) = BuildCostMatrix3DFirst(detections3D);
            var matches = GreedyMatch(costMatrix, validPairs);

            if (config.enableDebugLogs)
            {
                Debug.Log($"[ObjectTracker] 🔗 Matched {matches.Count} pairs");
            }

            // Process matches
            HashSet<int> matchedDetIndices = new HashSet<int>();
            foreach (var (trackIdx, detIdx) in matches)
            {
                var track = allTracks[trackIdx];
                bool wasLost = track.state == TrackState.Lost;

                UpdateTrackFromDetection(track, detections3D[detIdx], captureTime);
                matchedDetIndices.Add(detIdx);

                if (wasLost)
                {
                    track.Revive(config.minHits);
                    reviveCountMainMatch++;
                    Debug.Log($"[ObjectTracker] 🔄 REVIVED #{track.id} ({track.className}) via main match " +
                             $"[revival #{track.revivalCount}]");
                }
            }

            // Handle unmatched tracks (may transition to Lost)
            HandleUnmatchedTracks(matches, now);

            // Create new tracks or revive via second pass
            ProcessUnmatchedDetections(detections3D, matchedDetIndices, captureTime);

            // Update stateTime to now (no prediction)
            foreach (var track in allTracks)
            {
                if (track.state == TrackState.Lost) continue;
                track.stateTime = now;
            }

            // Merge duplicates
            MergeDuplicateTracks(now);

            if (config.enableDebugLogs)
            {
                Debug.Log($"[ObjectTracker] 📊 {ActiveTrackCount} active, {LostTrackCount} lost, " +
                         $"{ConfirmedTrackCount} confirmed");
            }
        }

        /// <summary>
        /// Build cost matrix with 3D distance as ONLY hard gate.
        /// 2D metrics are cost factors only - they never block matches.
        /// Includes Lost tracks with relaxed thresholds.
        /// </summary>
        private (float[,], List<(int, int)>) BuildCostMatrix3DFirst(List<Detection3D> detections)
        {
            float[,] costs = new float[allTracks.Count, detections.Count];
            List<(int, int)> validPairs = new List<(int, int)>();

            for (int i = 0; i < allTracks.Count; i++)
            {
                TrackedObject track = allTracks[i];

                // Determine thresholds based on track state
                bool isRevivalCandidate = track.IsEligibleForRevival(config.reacquireWindowSec);
                float max3DDist = isRevivalCandidate ? config.reacquireMax3DDistance : config.max3DDistance;

                for (int j = 0; j < detections.Count; j++)
                {
                    Detection3D det = detections[j];

                    // Class must match
                    if (track.classId != det.detection.class_id)
                    {
                        costs[i, j] = 999f;
                        continue;
                    }

                    // Calculate 3D distance (PRIMARY)
                    float dist3D = Vector3.Distance(track.worldPosition, det.worldPosition);

                    // 3D DISTANCE IS THE ONLY HARD GATE
                    if (dist3D > max3DDist)
                    {
                        costs[i, j] = 999f;
                        continue;
                    }

                    // Calculate other metrics (for cost, not gating)
                    float iou3d = CalculateIoU3D(track.worldBounds, det.worldBounds);
                    float iou2d = CalculateIoU2D(track.bbox2D, det.bbox2D);
                    float centerDist2D = Vector2.Distance(
                        new Vector2(track.centerPixel.x, track.centerPixel.y),
                        new Vector2(det.centerPixel.x, det.centerPixel.y)
                    );

                    // Build cost (lower = better match)
                    float dist3DCost = dist3D / max3DDist;
                    float iou3dCost = 1f - iou3d;
                    float iou2dCost = 1f - iou2d;
                    float center2DCost = Mathf.Clamp01(centerDist2D / 500f);

                    // Weighted cost - 3D dominates
                    float cost = (dist3DCost * 0.50f) +
                                (iou3dCost * 0.25f) +
                                (iou2dCost * 0.15f) +
                                (center2DCost * 0.10f);

                    // Small penalty for Lost tracks (prefer active in ties)
                    if (isRevivalCandidate) cost += 0.05f;

                    costs[i, j] = cost;
                    validPairs.Add((i, j));

                    if (config.enableDebugLogs)
                    {
                        string tag = isRevivalCandidate ? " [LOST]" : "";
                        Debug.Log($"  ✓ #{track.id}{tag} ↔ {det.detection.class_name}: " +
                                 $"dist3D={dist3D:F2}m cost={cost:F3}");
                    }
                }
            }

            return (costs, validPairs);
        }

        /// <summary>
        /// Greedy matching: assign each detection to lowest-cost track
        /// </summary>
        private List<(int, int)> GreedyMatch(float[,] costs, List<(int, int)> validPairs)
        {
            var matches = new List<(int, int)>();
            if (allTracks.Count == 0 || validPairs.Count == 0) return matches;

            var sorted = validPairs.OrderBy(p => costs[p.Item1, p.Item2]).ToList();
            var usedTracks = new HashSet<int>();
            var usedDets = new HashSet<int>();

            foreach (var (ti, di) in sorted)
            {
                if (usedTracks.Contains(ti) || usedDets.Contains(di)) continue;
                matches.Add((ti, di));
                usedTracks.Add(ti);
                usedDets.Add(di);
            }

            return matches;
        }

        /// <summary>
        /// Update track state from detection
        /// </summary>
        private void UpdateTrackFromDetection(TrackedObject track, Detection3D det, float updateTime)
        {
            // Update velocity from position change
            float dtMeas = updateTime - track.lastMeasuredTime;
            if (dtMeas > 0.01f && dtMeas < 2f)
            {
                Vector3 newVel = (det.worldPosition - track.lastMeasuredPosition) / dtMeas;
                if (newVel.magnitude < config.maxObjectVelocity)
                {
                    track.velocity = Vector3.Lerp(track.velocity, newVel, config.smoothingAlphaVelocity);
                }
            }

            // Update state
            track.worldPosition = det.worldPosition;
            track.worldSize = Vector3.Lerp(track.worldSize, det.worldSize, config.smoothingAlphaSize);
            track.worldBounds = new Bounds(track.worldPosition, track.worldSize);
            track.depth = det.depth;
            track.centerRay = det.centerRay;
            track.centerPixel = det.centerPixel;
            track.bbox2D = det.bbox2D;
            track.confidence = det.detection.confidence;
            track.lastUpdateTime = updateTime;
            track.stateTime = updateTime;
            track.timeSinceLastUpdate = 0f;
            track.lastCameraPose = det.cameraPose;
            track.lastMeasuredPosition = det.worldPosition;
            track.lastMeasuredTime = updateTime;

            track.hits++;
            track.trackConfidence = Mathf.Min(1f, track.trackConfidence + 0.2f);

            // Promote to Confirmed if enough hits
            if (track.hits >= config.minHits && track.state == TrackState.Tentative)
            {
                track.state = TrackState.Confirmed;
                Debug.Log($"[ObjectTracker] ✅ #{track.id} CONFIRMED ({track.className})");
            }

            track.AddToHistory(det.worldPosition, updateTime);
        }

        /// <summary>
        /// Handle unmatched tracks - transition to Lost if miss threshold exceeded
        /// </summary>
        private void HandleUnmatchedTracks(List<(int, int)> matches, float now)
        {
            var matchedIndices = matches.Select(m => m.Item1).ToHashSet();

            for (int i = 0; i < allTracks.Count; i++)
            {
                if (matchedIndices.Contains(i)) continue;

                var track = allTracks[i];
                if (track.state == TrackState.Lost) continue;

                float maxMiss = track.state == TrackState.Confirmed
                    ? config.confirmedMaxMissTimeSec
                    : config.tentativeMaxMissTimeSec;

                if (track.timeSinceLastUpdate > maxMiss)
                {
                    track.MarkLost(now);
                    Debug.Log($"[ObjectTracker] ⚠️ #{track.id} ({track.className}) → LOST " +
                             $"(miss={track.timeSinceLastUpdate:F1}s > {maxMiss:F1}s)");
                }
            }
        }

        /// <summary>
        /// Process unmatched detections: try revival first, then create new
        /// </summary>
        private void ProcessUnmatchedDetections(List<Detection3D> detections, HashSet<int> matchedIndices, float createTime)
        {
            for (int i = 0; i < detections.Count; i++)
            {
                if (matchedIndices.Contains(i)) continue;

                Detection3D det = detections[i];

                // Try to revive a Lost track first (3D-only matching)
                TrackedObject reviveCandidate = FindRevivalCandidate(det);
                if (reviveCandidate != null)
                {
                    UpdateTrackFromDetection(reviveCandidate, det, createTime);
                    reviveCandidate.Revive(config.minHits);
                    reviveCountSecondPass++;
                    Debug.Log($"[ObjectTracker] 🔄 REVIVED #{reviveCandidate.id} ({reviveCandidate.className}) " +
                             $"via second-pass [revival #{reviveCandidate.revivalCount}]");
                    continue;
                }

                // Check if too close to existing active track
                bool tooClose = allTracks.Any(t =>
                    t.state != TrackState.Lost &&
                    t.classId == det.detection.class_id &&
                    Vector3.Distance(t.worldPosition, det.worldPosition) < config.minTrackSeparation
                );

                if (tooClose)
                {
                    if (config.enableDebugLogs)
                        Debug.Log($"[ObjectTracker] ⚠️ Rejected {det.detection.class_name} (too close to active)");
                    continue;
                }

                // Create new track
                CreateNewTrack(det, createTime);
            }
        }

        /// <summary>
        /// Find best Lost track to revive (3D distance only)
        /// </summary>
        private TrackedObject FindRevivalCandidate(Detection3D det)
        {
            TrackedObject best = null;
            float bestDist = config.reacquireMax3DDistance;

            foreach (var track in allTracks)
            {
                if (!track.IsEligibleForRevival(config.reacquireWindowSec)) continue;
                if (track.classId != det.detection.class_id) continue;

                float dist = Vector3.Distance(track.worldPosition, det.worldPosition);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = track;
                }
            }

            return best;
        }

        /// <summary>
        /// Create a new track from detection
        /// </summary>
        private void CreateNewTrack(Detection3D det, float createTime)
        {
            var track = new TrackedObject
            {
                id = nextTrackId++,
                classId = det.detection.class_id,
                className = det.detection.class_name,
                creationTime = createTime,
                stateTime = createTime,
                lastMeasuredPosition = det.worldPosition,
                lastMeasuredTime = createTime,
                worldPosition = det.worldPosition,
                worldPositionSmoothed = det.worldPosition,
                worldSize = det.worldSize,
                worldBounds = det.worldBounds,
                worldRotation = Quaternion.identity,
                velocity = Vector3.zero,
                velocitySmoothed = Vector3.zero,
                centerRay = det.centerRay,
                depth = det.depth,
                centerPixel = det.centerPixel,
                bbox2D = det.bbox2D,
                state = TrackState.Tentative,
                hits = 1,
                timeSinceLastUpdate = 0f,
                lastUpdateTime = createTime,
                confidence = det.detection.confidence,
                lastCameraPose = det.cameraPose,
                trackConfidence = 1f,
                displayColor = Color.yellow,
            };

            allTracks.Add(track);
            newTrackCount++;

            Debug.Log($"[ObjectTracker] 🆕 New track #{track.id} ({track.className})");
        }

        /// <summary>
        /// Predict track position forward/backward in time
        /// </summary>
        private void PredictTrackPosition(TrackedObject track, float dt)
        {
            if (Mathf.Abs(dt) < 0.001f) return;

            track.worldPosition += track.velocity * dt;
            track.worldBounds.center = track.worldPosition;

            // Clamp velocity
            if (track.velocity.magnitude > config.maxObjectVelocity)
            {
                track.velocity = track.velocity.normalized * config.maxObjectVelocity;
            }
        }

        /// <summary>
        /// Merge duplicate tracks (same class, too close together)
        /// </summary>
        private void MergeDuplicateTracks(float now)
        {
            for (int i = 0; i < allTracks.Count; i++)
            {
                var t1 = allTracks[i];
                if (t1.state == TrackState.Lost) continue;

                for (int j = i + 1; j < allTracks.Count; j++)
                {
                    var t2 = allTracks[j];
                    if (t2.state == TrackState.Lost) continue;
                    if (t1.classId != t2.classId) continue;

                    float dist = Vector3.Distance(t1.worldPosition, t2.worldPosition);
                    if (dist < config.minTrackSeparation)
                    {
                        // Decide which to keep
                        TrackedObject keep, remove;

                        if (t1.state == TrackState.Confirmed && t2.state != TrackState.Confirmed)
                        {
                            keep = t1; remove = t2;
                        }
                        else if (t2.state == TrackState.Confirmed && t1.state != TrackState.Confirmed)
                        {
                            keep = t2; remove = t1;
                        }
                        else if (config.preferOlderTrackOnMerge)
                        {
                            keep = t1.creationTime < t2.creationTime ? t1 : t2;
                            remove = t1.creationTime < t2.creationTime ? t2 : t1;
                        }
                        else
                        {
                            keep = t1.hits > t2.hits ? t1 : t2;
                            remove = t1.hits > t2.hits ? t2 : t1;
                        }

                        remove.MarkLost(now);
                        mergeCount++;

                        Debug.Log($"[ObjectTracker] 🔀 Merged #{remove.id} into #{keep.id} ({keep.className})");
                    }
                }
            }
        }

        // ============================================================
        // UTILITY METHODS
        // ============================================================

        private float CalculateIoU3D(Bounds a, Bounds b)
        {
            Vector3 min = Vector3.Max(a.min, b.min);
            Vector3 max = Vector3.Min(a.max, b.max);
            Vector3 d = max - min;

            if (d.x <= 0 || d.y <= 0 || d.z <= 0) return 0f;

            float inter = d.x * d.y * d.z;
            float volA = a.size.x * a.size.y * a.size.z;
            float volB = b.size.x * b.size.y * b.size.z;
            float union = volA + volB - inter;

            return union > 0 ? inter / union : 0f;
        }

        private float CalculateIoU2D(Rect a, Rect b)
        {
            float x1 = Mathf.Max(a.xMin, b.xMin);
            float y1 = Mathf.Max(a.yMin, b.yMin);
            float x2 = Mathf.Min(a.xMax, b.xMax);
            float y2 = Mathf.Min(a.yMax, b.yMax);

            float inter = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
            float union = a.width * a.height + b.width * b.height - inter;

            return union > 0 ? inter / union : 0f;
        }

        private Ray PixelToWorldRay(Vector2Int pixel, PassthroughCameraIntrinsics intrinsics, Pose camPose)
        {
            float xn = (pixel.x - intrinsics.PrincipalPoint.x) / intrinsics.FocalLength.x;
            float yn = (pixel.y - intrinsics.PrincipalPoint.y) / intrinsics.FocalLength.y;
            Vector3 dirLocal = new Vector3(xn, yn, 1f).normalized;
            Vector3 dirWorld = camPose.rotation * dirLocal;
            return new Ray(camPose.position, dirWorld);
        }

        private Vector2Int WorldPointToPixel(Vector3 worldPos, Pose camPose)
        {
            if (!cameraIntrinsics.HasValue) return Vector2Int.zero;

            var intrinsics = cameraIntrinsics.Value;
            Vector3 local = Quaternion.Inverse(camPose.rotation) * (worldPos - camPose.position);

            if (Mathf.Abs(local.z) < 0.01f) return Vector2Int.zero;

            float xn = local.x / local.z;
            float yn = local.y / local.z;
            float xp = xn * intrinsics.FocalLength.x + intrinsics.PrincipalPoint.x;
            float yp = yn * intrinsics.FocalLength.y + intrinsics.PrincipalPoint.y;

            return new Vector2Int(Mathf.RoundToInt(xp), Mathf.RoundToInt(yp));
        }

        private List<Detection3D> ConvertDetectionsTo3D(DetectionResponse response)
        {
            var result = new List<Detection3D>();
            var intrinsics = cameraIntrinsics.Value;

            int sentW = response.image_size[0];
            int sentH = response.image_size[1];
            int camW = intrinsics.Resolution.x;
            int camH = intrinsics.Resolution.y;

            float sx = sentW > 0 ? (float)camW / sentW : 1f;
            float sy = sentH > 0 ? (float)camH / sentH : 1f;

            Pose camPose = new Pose(centerEyeTransform.position, centerEyeTransform.rotation);

            foreach (var det in response.detections)
            {
                if (det.bbox == null || det.bbox.Length != 4) continue;

                // Scale bbox to camera resolution
                float x1 = det.bbox[0] * sx;
                float y1 = det.bbox[1] * sy;
                float x2 = det.bbox[2] * sx;
                float y2 = det.bbox[3] * sy;

                // Flip Y
                float y1f = camH - y1;
                float y2f = camH - y2;
                float yMin = Mathf.Min(y1f, y2f);
                float yMax = Mathf.Max(y1f, y2f);

                // Y offset correction
                float bboxH = yMax - yMin;
                float yOff = autoCorrectYOffset ? bboxH * yOffsetPercentage : 0f;
                yOff += manualYOffsetPixels;

                Vector2Int center = new Vector2Int(
                    Mathf.RoundToInt((x1 + x2) / 2f),
                    Mathf.RoundToInt((yMin + yMax) / 2f + yOff)
                );

                // Get depth via raycast
                Ray ray = PixelToWorldRay(center, intrinsics, camPose);
                float depth = defaultDepth;
                if (Physics.Raycast(ray, out RaycastHit hit, maxRaycastDistance, raycastLayers))
                {
                    depth = hit.distance;
                }

                Vector3 worldPos = ray.origin + ray.direction * depth;

                // Estimate world size
                float thickness = Mathf.Clamp(
                    depth * config.depthThicknessFraction,
                    config.minBoundsThickness,
                    config.maxBoundsThickness
                );

                float pw = x2 - x1;
                float ph = yMax - yMin;
                float ww = (pw * depth) / intrinsics.FocalLength.x;
                float wh = (ph * depth) / intrinsics.FocalLength.y;

                result.Add(new Detection3D
                {
                    detection = det,
                    worldPosition = worldPos,
                    worldSize = new Vector3(ww, wh, thickness),
                    worldBounds = new Bounds(worldPos, new Vector3(ww, wh, thickness)),
                    depth = depth,
                    centerRay = ray,
                    centerPixel = center,
                    bbox2D = new Rect(x1, yMin, pw, ph),
                    cameraPose = camPose
                });
            }

            return result;
        }

        // ============================================================
        // DEBUG / METRICS
        // ============================================================

        private void LogMetricsSummary()
        {
            Debug.Log($"[ObjectTracker] ═══════════════════════════════════");
            Debug.Log($"[ObjectTracker] TRACKS: {ActiveTrackCount} active, {LostTrackCount} lost, " +
                     $"{ConfirmedTrackCount} confirmed, {TotalTrackCount} total");
            Debug.Log($"[ObjectTracker] REVIVALS: main={reviveCountMainMatch}, secondPass={reviveCountSecondPass}");
            Debug.Log($"[ObjectTracker] NEW: {newTrackCount}, PRUNED: {lostPrunedCount}, MERGED: {mergeCount}");
            Debug.Log($"[ObjectTracker] ═══════════════════════════════════");
        }

        public void ResetMetrics()
        {
            reviveCountMainMatch = 0;
            reviveCountSecondPass = 0;
            newTrackCount = 0;
            lostPrunedCount = 0;
            mergeCount = 0;
        }

        private void OnGUI()
        {
            if (!config.showRevivalMetrics) return;

            GUILayout.BeginArea(new Rect(10, 130, 500, 160));
            GUILayout.Label("═══ LONG-TERM MR TRACKER ═══");
            GUILayout.Label($"Active: {ActiveTrackCount} | Lost: {LostTrackCount} | Confirmed: {ConfirmedTrackCount}");
            GUILayout.Label($"Revivals: main={reviveCountMainMatch} + secondPass={reviveCountSecondPass}");
            GUILayout.Label($"New: {newTrackCount} | Pruned: {lostPrunedCount} | Merged: {mergeCount}");
            GUILayout.Label($"Retention: {config.lostRetentionTimeSec}s | ReacquireWindow: {config.reacquireWindowSec}s");
            GUILayout.EndArea();
        }
    }

    // ============================================================
    // DETECTION 3D
    // ============================================================

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