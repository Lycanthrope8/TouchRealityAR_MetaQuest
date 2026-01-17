using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using PassthroughCameraSamples;

namespace ARObjectDetection
{
    /// <summary>
    /// PHASE 2.2 - 3D-FIRST tracking with PROPER Lost retention and revival
    /// 
    /// KEY FIXES:
    /// 1. 3D distance is PRIMARY gate - 2D is ONLY a cost factor, never blocks
    /// 2. Lost tracks are retained for lostRetentionTimeSec (default 8s)
    /// 3. Lost tracks within reacquireWindow are motion-compensated
    /// 4. Revival uses 3D-only matching
    /// </summary>
    public class ObjectTracker : MonoBehaviour
    {
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

        [Header("Debug Y-Offset")]
        [SerializeField] private float manualYOffsetPixels = 0f;
        [SerializeField] private bool autoCorrectYOffset = true;
        [Range(0f, 1f)]
        [SerializeField] private float yOffsetPercentage = 0.0f;

        // Track lists
        private List<TrackedObject> activeTracks = new List<TrackedObject>();
        private int nextTrackId = 0;

        // Camera
        private PassthroughCameraIntrinsics? cameraIntrinsics;
        private Transform centerEyeTransform;

        // Frame tracking
        private int lastProcessedFrameId = -1;

        // === DEBUG METRICS ===
        private int reviveCountMainMatch = 0;
        private int reviveCountSecondPass = 0;
        private int newTrackCreatedWhileRecentLostExists = 0;
        private int blockedBy2DGateCount = 0;  // Should stay 0 with 3D-first
        private int lostPrunedCount = 0;
        private float lastMetricsLogTime = 0f;

        // Public metrics
        public int ActiveTrackCount => activeTracks.Count(t => t.state != TrackState.Lost);
        public int LostTrackCount => activeTracks.Count(t => t.state == TrackState.Lost);
        public int TotalTrackCount => activeTracks.Count;
        public int ConfirmedTrackCount => activeTracks.Count(t => t.state == TrackState.Confirmed);

        /// <summary>
        /// All tracks including Lost ones (for internal use / advanced consumers).
        /// WARNING: Consumers should filter by state or use NonLostTracks.
        /// </summary>
        public List<TrackedObject> ActiveTracks => activeTracks;

        /// <summary>
        /// Only non-Lost tracks (Tentative + Confirmed). Use this for visualization/anchoring.
        /// </summary>
        public IEnumerable<TrackedObject> NonLostTracks => activeTracks.Where(t => t.state != TrackState.Lost);

        public int ReviveCountMainMatch => reviveCountMainMatch;
        public int ReviveCountSecondPass => reviveCountSecondPass;
        public int NewTrackCreatedWhileRecentLostExists => newTrackCreatedWhileRecentLostExists;
        public int LostPrunedCount => lostPrunedCount;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Debug.LogWarning($"[ObjectTracker] Duplicate! Destroying {gameObject.name}");
                Destroy(this);
                return;
            }
            instance = this;

            if (config == null)
            {
                Debug.LogError("[ObjectTracker] TrackerConfig not assigned!");
                enabled = false;
                return;
            }

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

            if (OVRManager.instance != null)
            {
                centerEyeTransform = OVRManager.instance.GetComponentInChildren<Camera>().transform;
            }

            Debug.Log($"[ObjectTracker] ✅ PHASE 2.2 - 3D-FIRST with Lost retention");
            Debug.Log($"[ObjectTracker] Settings: max3D={config.max3DDistance}m, " +
                     $"reacquire3D={config.reacquireMax3DDistance}m, " +
                     $"reacquireWindow={config.reacquireWindowSec}s, " +
                     $"lostRetention={config.lostRetentionTimeSec}s");
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void Update()
        {
            if (activeTracks.Count == 0) return;

            float now = Time.realtimeSinceStartup;

            foreach (var track in activeTracks)
            {
                track.timeSinceLastUpdate = now - track.lastUpdateTime;

                // === FIX: Motion-compensate Lost tracks within reacquire window ===
                bool shouldMotionCompensate = config.enableMotionCompensation &&
                    (track.state != TrackState.Lost ||
                     (config.motionCompensateLostTracks && track.IsEligibleForRevival(config.reacquireWindowSec)));

                if (shouldMotionCompensate)
                {
                    CompensateForCameraMotion(track);
                }

                // Predict motion for non-Lost tracks
                if (track.state != TrackState.Lost)
                {
                    float dt = (track.stateTime > 0f) ? (now - track.stateTime) : Time.deltaTime;
                    if (dt > 0f)
                    {
                        track.worldPosition += track.velocity * dt;
                        track.stateTime = now;
                        track.worldBounds.center = track.worldPosition;

                        track.worldPositionSmoothed = Vector3.Lerp(
                            track.worldPositionSmoothed,
                            track.worldPosition,
                            config.smoothingAlphaPosition
                        );

                        if (track.timeSinceLastUpdate > 0.1f)
                        {
                            track.trackConfidence *= Mathf.Exp(-dt * 2.0f);
                            track.trackConfidence = Mathf.Max(track.trackConfidence, 0.1f);
                        }
                    }
                }
            }

            // === PRUNE: Only remove Lost tracks after retention expires ===
            int beforeCount = activeTracks.Count;
            activeTracks.RemoveAll(t => t.ShouldPrune(now, config.lostRetentionTimeSec));
            int pruned = beforeCount - activeTracks.Count;
            if (pruned > 0)
            {
                lostPrunedCount += pruned;
                Debug.Log($"[ObjectTracker] 🗑️ Pruned {pruned} Lost tracks (retention={config.lostRetentionTimeSec}s expired)");
            }

            // Periodic metrics summary
            if (config.metricsSummaryIntervalSec > 0 && now - lastMetricsLogTime > config.metricsSummaryIntervalSec)
            {
                lastMetricsLogTime = now;
                LogMetricsSummary();
            }
        }

        private void CompensateForCameraMotion(TrackedObject track)
        {
            if (centerEyeTransform == null || !cameraIntrinsics.HasValue) return;

            Pose currentPose = new Pose(centerEyeTransform.position, centerEyeTransform.rotation);

            Quaternion rotationDelta = currentPose.rotation * Quaternion.Inverse(track.lastCameraPose.rotation);
            Vector3 positionDelta = currentPose.position - track.lastCameraPose.position;

            if (Quaternion.Angle(rotationDelta, Quaternion.identity) < 0.5f && positionDelta.magnitude < 0.01f)
                return;

            // For Lost tracks: project stored worldPosition into new camera frame to update 2D
            // (don't re-derive world position from stale depth)
            if (track.state == TrackState.Lost)
            {
                // Lost tracks keep their world position fixed; only update 2D projection
                Vector2Int lostPixelCenter = WorldPointToPixel(track.worldPosition, currentPose);

                // Shift bbox2D based on pixel delta
                Vector2 lostPixelDelta = new Vector2(
                    lostPixelCenter.x - track.centerPixel.x,
                    lostPixelCenter.y - track.centerPixel.y
                );
                track.bbox2D = new Rect(
                    track.bbox2D.x + lostPixelDelta.x,
                    track.bbox2D.y + lostPixelDelta.y,
                    track.bbox2D.width,
                    track.bbox2D.height
                );
                track.centerPixel = lostPixelCenter;

                // Update ray to point from new camera position to fixed world position
                Vector3 toWorld = track.worldPosition - currentPose.position;
                track.centerRay = new Ray(currentPose.position, toWorld.normalized);
                track.depth = toWorld.magnitude;

                track.lastCameraPose = currentPose;
                return;
            }

            // For non-Lost tracks: standard motion compensation
            Vector3 relativeOrigin = track.centerRay.origin - track.lastCameraPose.position;
            Vector3 newRayOrigin = rotationDelta * relativeOrigin + currentPose.position;
            Vector3 newRayDir = rotationDelta * track.centerRay.direction;
            Ray newRay = new Ray(newRayOrigin, newRayDir);

            Vector3 worldPos = newRay.origin + newRay.direction * track.depth;

            // Update 2D pixel center
            Vector2Int newPixelCenter = WorldPointToPixel(worldPos, currentPose);

            // Shift bbox2D
            Vector2 pixelDelta = new Vector2(
                newPixelCenter.x - track.centerPixel.x,
                newPixelCenter.y - track.centerPixel.y
            );
            track.bbox2D = new Rect(
                track.bbox2D.x + pixelDelta.x,
                track.bbox2D.y + pixelDelta.y,
                track.bbox2D.width,
                track.bbox2D.height
            );

            track.centerPixel = newPixelCenter;
            track.centerRay = newRay;
            track.worldPosition = worldPos;
            track.worldBounds.center = worldPos;
            track.lastCameraPose = currentPose;
        }

        public void ProcessDetections(DetectionResponse response)
        {
            if (response == null || response.detections == null) return;
            if (!cameraIntrinsics.HasValue)
            {
                Debug.LogWarning("[ObjectTracker] No camera intrinsics!");
                return;
            }

            if (response.frame_id <= lastProcessedFrameId)
            {
                Debug.LogWarning($"[ObjectTracker] Dropping out-of-order frame {response.frame_id}");
                return;
            }
            lastProcessedFrameId = response.frame_id;

            float captureTime = response.capture_time;
            float now = Time.realtimeSinceStartup;

            Debug.Log($"[ObjectTracker] 📦 Frame {response.frame_id}, {response.detections.Count} detections");

            List<Detection3D> detections3D = ConvertDetectionsTo3D(response);
            if (detections3D.Count == 0)
            {
                Debug.LogWarning("[ObjectTracker] No valid 3D detections");
                return;
            }

            // Align tracks to capture time
            foreach (var track in activeTracks)
            {
                if (track.state == TrackState.Lost) continue;
                if (track.stateTime <= 0f) track.stateTime = now;
                float dtToCapture = captureTime - track.stateTime;
                PredictTrack(track, dtToCapture);
                track.stateTime = captureTime;
            }

            // === 3D-FIRST MATCHING (includes Lost tracks) ===
            var (costMatrix, validPairs) = Build3DFirstCostMatrix(detections3D);
            var matches = GreedyMatch(costMatrix, validPairs);

            Debug.Log($"[ObjectTracker] 🔗 Matched {matches.Count} pairs");

            // Process matches (including revivals)
            HashSet<int> matchedDetIndices = new HashSet<int>();
            foreach (var (trackIdx, detIdx) in matches)
            {
                var track = activeTracks[trackIdx];
                bool wasLost = track.state == TrackState.Lost;

                UpdateTrack(track, detections3D[detIdx], captureTime);
                matchedDetIndices.Add(detIdx);

                if (wasLost)
                {
                    track.Revive();
                    reviveCountMainMatch++;
                    Debug.Log($"[ObjectTracker] 🔄 REVIVED Track {track.id} ({track.className}) via main match - revival #{track.revivalCount}");
                }
            }

            // Handle unmatched active tracks
            HandleUnmatchedTracks(matches, now);

            // === CREATE NEW TRACKS / SECOND-PASS REVIVAL ===
            CreateNewTracksOrRevive(detections3D, matchedDetIndices, captureTime);

            // Forward predict to present
            float forwardDt = now - captureTime;
            foreach (var track in activeTracks)
            {
                if (track.state == TrackState.Lost) continue;
                PredictTrack(track, forwardDt);
                track.stateTime = now;
            }

            MergeDuplicateTracks();

            Debug.Log($"[ObjectTracker] 📊 {TotalTrackCount} tracks ({ConfirmedTrackCount} confirmed, {LostTrackCount} lost)");
        }

        /// <summary>
        /// 3D-FIRST cost matrix: 3D distance is the ONLY hard gate.
        /// 2D metrics are cost factors only.
        /// </summary>
        private (float[,] costs, List<(int, int)> validPairs) Build3DFirstCostMatrix(List<Detection3D> detections)
        {
            float[,] costs = new float[activeTracks.Count, detections.Count];
            List<(int, int)> validPairs = new List<(int, int)>();

            for (int i = 0; i < activeTracks.Count; i++)
            {
                TrackedObject track = activeTracks[i];

                // Determine if this is a revival candidate (Lost but within window)
                bool isRevivalCandidate = track.IsEligibleForRevival(config.reacquireWindowSec);

                // 3D distance threshold - use relaxed threshold for revival candidates
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

                    // Calculate metrics
                    float dist3D = Vector3.Distance(track.worldPosition, det.worldPosition);
                    float iou3d = CalculateIoU3D(track.worldBounds, det.worldBounds);
                    float iou2d = CalculateIoU2D(track.bbox2D, det.bbox2D);
                    float centerDist2D = Vector2.Distance(
                        new Vector2(track.centerPixel.x, track.centerPixel.y),
                        new Vector2(det.centerPixel.x, det.centerPixel.y)
                    );

                    // === 3D IS THE ONLY HARD GATE ===
                    bool passes3DGate = dist3D <= max3DDist;

                    if (!passes3DGate)
                    {
                        costs[i, j] = 999f;
                        continue;
                    }

                    // === 2D metrics are COST FACTORS only, never block ===
                    float dist3DCost = dist3D / max3DDist;  // 0-1 normalized
                    float iou3dCost = 1.0f - iou3d;
                    float iou2dCost = 1.0f - iou2d;
                    float centerDist2DCost = Mathf.Clamp01(centerDist2D / 500f);  // Normalize but don't block

                    // Weight: 3D dominates
                    float cost = (dist3DCost * 0.5f) +
                                (iou3dCost * 0.25f) +
                                (iou2dCost * 0.15f) +
                                (centerDist2DCost * 0.10f);

                    // Small penalty for Lost tracks (prefer active tracks in ties)
                    if (isRevivalCandidate) cost += 0.05f;

                    costs[i, j] = cost;
                    validPairs.Add((i, j));

                    if (config.enableDebugLogs)
                    {
                        string tag = isRevivalCandidate ? " [LOST-revivable]" : "";
                        Debug.Log($"  ✓ Track#{track.id}({track.className}){tag} ↔ Det: " +
                                 $"dist3D={dist3D:F2}m IoU3D={iou3d:F2} cost={cost:F3}");
                    }
                }
            }

            Debug.Log($"[ObjectTracker] 🎯 {validPairs.Count} valid pairs (3D-first gate)");
            return (costs, validPairs);
        }

        private List<(int, int)> GreedyMatch(float[,] costMatrix, List<(int, int)> validPairs)
        {
            var matches = new List<(int, int)>();
            if (activeTracks.Count == 0 || validPairs.Count == 0) return matches;

            var sorted = validPairs.OrderBy(p => costMatrix[p.Item1, p.Item2]).ToList();
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

        private void UpdateTrack(TrackedObject track, Detection3D det, float updateTime)
        {
            float dtMeas = updateTime - track.lastMeasuredTime;
            if (dtMeas > 0.01f)
            {
                Vector3 newVel = (det.worldPosition - track.lastMeasuredPosition) / dtMeas;
                track.velocity = Vector3.Lerp(track.velocity, newVel, config.smoothingAlphaVelocity);
            }

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
            track.trackConfidence = Mathf.Min(1.0f, track.trackConfidence + 0.2f);

            if (track.hits >= config.minHits && track.state == TrackState.Tentative)
            {
                track.state = TrackState.Confirmed;
                Debug.Log($"[ObjectTracker] ✅ Track {track.id} CONFIRMED ({track.className})");
            }

            track.AddToHistory(det.worldPosition, updateTime);
        }

        private void HandleUnmatchedTracks(List<(int, int)> matches, float now)
        {
            var matchedTrackIndices = matches.Select(m => m.Item1).ToHashSet();

            for (int i = 0; i < activeTracks.Count; i++)
            {
                if (matchedTrackIndices.Contains(i)) continue;

                var track = activeTracks[i];
                if (track.state == TrackState.Lost) continue;  // Already Lost

                float maxMiss = track.state == TrackState.Confirmed
                    ? config.confirmedMaxMissTimeSec
                    : config.tentativeMaxMissTimeSec;

                if (track.timeSinceLastUpdate > maxMiss)
                {
                    track.MarkLost(now);
                    Debug.Log($"[ObjectTracker] ⚠️ Track {track.id} ({track.className}) → LOST " +
                             $"(miss={track.timeSinceLastUpdate:F2}s > {maxMiss:F1}s)");
                }
            }
        }

        /// <summary>
        /// For unmatched detections: try to revive a Lost track first, else create new.
        /// Uses 3D-ONLY matching for revival.
        /// </summary>
        private void CreateNewTracksOrRevive(List<Detection3D> detections, HashSet<int> matchedDetIndices, float createTime)
        {
            for (int detIdx = 0; detIdx < detections.Count; detIdx++)
            {
                if (matchedDetIndices.Contains(detIdx)) continue;

                Detection3D det = detections[detIdx];

                // === TRY REVIVAL FIRST (3D-only) ===
                TrackedObject reviveCandidate = FindRevivalCandidate3DOnly(det);
                if (reviveCandidate != null)
                {
                    UpdateTrack(reviveCandidate, det, createTime);
                    reviveCandidate.Revive();
                    reviveCountSecondPass++;
                    Debug.Log($"[ObjectTracker] 🔄 REVIVED Track {reviveCandidate.id} ({reviveCandidate.className}) " +
                             $"via second-pass - revival #{reviveCandidate.revivalCount}");
                    continue;
                }

                // === CHECK: Is there a recent Lost track nearby that we SHOULD have revived? ===
                bool recentLostExists = activeTracks.Any(t =>
                    t.IsEligibleForRevival(config.reacquireWindowSec) &&
                    t.classId == det.detection.class_id &&
                    Vector3.Distance(t.worldPosition, det.worldPosition) < config.reacquireMax3DDistance * 1.5f
                );

                // === CHECK: Too close to an ACTIVE track? ===
                bool tooCloseToActive = activeTracks.Any(t =>
                    t.state != TrackState.Lost &&
                    t.classId == det.detection.class_id &&
                    Vector3.Distance(t.worldPosition, det.worldPosition) < 0.3f
                );

                if (tooCloseToActive)
                {
                    Debug.Log($"[ObjectTracker] ⚠️ Rejected {det.detection.class_name} (too close to active)");
                    continue;
                }

                // === CREATE NEW TRACK ===
                if (recentLostExists)
                {
                    newTrackCreatedWhileRecentLostExists++;
                    Debug.LogWarning($"[ObjectTracker] ⚠️ Creating NEW track while recent Lost exists! " +
                                   $"(class={det.detection.class_name}) - check thresholds");
                }

                CreateNewTrack(det, createTime);
            }
        }

        /// <summary>
        /// Find best Lost track to revive using 3D distance ONLY.
        /// </summary>
        private TrackedObject FindRevivalCandidate3DOnly(Detection3D det)
        {
            TrackedObject best = null;
            float bestDist = config.reacquireMax3DDistance;

            foreach (var track in activeTracks)
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

        private void CreateNewTrack(Detection3D det, float createTime)
        {
            var track = new TrackedObject
            {
                id = nextTrackId++,
                classId = det.detection.class_id,
                className = det.detection.class_name,
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
                trackConfidence = 1.0f,
                displayColor = Color.yellow,
            };

            activeTracks.Add(track);
            Debug.Log($"[ObjectTracker] 🆕 New track {track.id} ({track.className})");
        }

        private void PredictTrack(TrackedObject track, float dt)
        {
            if (Mathf.Abs(dt) < 0.001f) return;
            track.worldPosition += track.velocity * dt;
            track.worldBounds.center = track.worldPosition;
            if (track.velocity.magnitude > config.maxObjectVelocity)
                track.velocity = track.velocity.normalized * config.maxObjectVelocity;
        }

        private void MergeDuplicateTracks()
        {
            for (int i = 0; i < activeTracks.Count; i++)
            {
                var t1 = activeTracks[i];
                if (t1.state == TrackState.Lost) continue;

                for (int j = i + 1; j < activeTracks.Count; j++)
                {
                    var t2 = activeTracks[j];
                    if (t2.state == TrackState.Lost) continue;
                    if (t1.className != t2.className) continue;

                    float dist = Vector3.Distance(t1.worldPosition, t2.worldPosition);
                    if (dist < 0.25f)
                    {
                        var keep = t1.state == TrackState.Confirmed ? t1 :
                                  t2.state == TrackState.Confirmed ? t2 :
                                  (t1.id < t2.id ? t1 : t2);
                        var remove = keep == t1 ? t2 : t1;

                        remove.MarkLost(Time.realtimeSinceStartup);
                        Debug.Log($"[ObjectTracker] 🔀 Merged {remove.id} into {keep.id} ({keep.className})");
                    }
                }
            }
        }

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

        private Ray PixelToWorldRay(Vector2Int pixel, PassthroughCameraIntrinsics intrinsics, Pose cameraPose)
        {
            float xn = (pixel.x - intrinsics.PrincipalPoint.x) / intrinsics.FocalLength.x;
            float yn = (pixel.y - intrinsics.PrincipalPoint.y) / intrinsics.FocalLength.y;
            Vector3 dirLocal = new Vector3(xn, yn, 1f).normalized;
            Vector3 dirWorld = cameraPose.rotation * dirLocal;
            return new Ray(cameraPose.position, dirWorld);
        }

        private Vector2Int WorldPointToPixel(Vector3 worldPos, Pose cameraPose)
        {
            if (!cameraIntrinsics.HasValue) return Vector2Int.zero;
            var intrinsics = cameraIntrinsics.Value;
            Vector3 local = Quaternion.Inverse(cameraPose.rotation) * (worldPos - cameraPose.position);
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

                float x1 = det.bbox[0] * sx;
                float y1 = det.bbox[1] * sy;
                float x2 = det.bbox[2] * sx;
                float y2 = det.bbox[3] * sy;

                float y1f = camH - y1;
                float y2f = camH - y2;
                float yMin = Mathf.Min(y1f, y2f);
                float yMax = Mathf.Max(y1f, y2f);

                float bboxH = yMax - yMin;
                float yOff = autoCorrectYOffset ? bboxH * yOffsetPercentage : 0f;
                yOff += manualYOffsetPixels;

                Vector2Int center = new Vector2Int(
                    Mathf.RoundToInt((x1 + x2) / 2f),
                    Mathf.RoundToInt((yMin + yMax) / 2f + yOff)
                );

                Ray ray = PixelToWorldRay(center, intrinsics, camPose);
                float depth = defaultDepth;
                if (Physics.Raycast(ray, out RaycastHit hit, maxRaycastDistance, raycastLayers))
                    depth = hit.distance;

                Vector3 worldPos = ray.origin + ray.direction * depth;

                float thickness = Mathf.Clamp(depth * config.depthThicknessFraction,
                    config.minBoundsThickness, config.maxBoundsThickness);

                float pw = x2 - x1;
                float ph = yMax - yMin;
                float ww = (pw * depth) / intrinsics.FocalLength.x;
                float wh = (ph * depth) / intrinsics.FocalLength.y;

                Bounds bounds = new Bounds(worldPos, new Vector3(ww, wh, thickness));

                result.Add(new Detection3D
                {
                    detection = det,
                    worldPosition = worldPos,
                    worldSize = new Vector3(ww, wh, thickness),
                    worldBounds = bounds,
                    depth = depth,
                    centerRay = ray,
                    centerPixel = center,
                    bbox2D = new Rect(x1, yMin, pw, ph),
                    cameraPose = camPose
                });
            }

            return result;
        }

        private void LogMetricsSummary()
        {
            Debug.Log($"[ObjectTracker] === METRICS SUMMARY ===");
            Debug.Log($"  Tracks: {ActiveTrackCount} active, {LostTrackCount} lost (retained), {TotalTrackCount} total");
            Debug.Log($"  Revivals: mainMatch={reviveCountMainMatch}, secondPass={reviveCountSecondPass}");
            Debug.Log($"  NewWhileLostExists: {newTrackCreatedWhileRecentLostExists} (should be ~0)");
            Debug.Log($"  LostPruned: {lostPrunedCount}");
        }

        public void ResetMetrics()
        {
            reviveCountMainMatch = 0;
            reviveCountSecondPass = 0;
            newTrackCreatedWhileRecentLostExists = 0;
            blockedBy2DGateCount = 0;
            lostPrunedCount = 0;
        }

        private void OnGUI()
        {
            if (!config.showRevivalMetrics) return;

            GUILayout.BeginArea(new Rect(10, 130, 450, 140));
            GUILayout.Label("=== 3D-FIRST TRACKER METRICS ===");
            GUILayout.Label($"Active: {ActiveTrackCount} | Lost (retained): {LostTrackCount} | Total: {TotalTrackCount}");
            GUILayout.Label($"Confirmed: {ConfirmedTrackCount}");
            GUILayout.Label($"Revivals: main={reviveCountMainMatch} + secondPass={reviveCountSecondPass}");
            GUILayout.Label($"NewWhileLostExists: {newTrackCreatedWhileRecentLostExists} (should be ~0)");
            GUILayout.Label($"LostPruned: {lostPrunedCount}");
            GUILayout.EndArea();
        }
    }

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