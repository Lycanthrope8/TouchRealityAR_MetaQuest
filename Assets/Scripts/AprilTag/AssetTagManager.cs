// ============================================================================
// FILE: AssetTagManager.cs
// Main manager for AprilTag-based asset identity.
// 
// FIXES IMPLEMENTED:
// - Tag latching does NOT depend on SiteFrame validity (identity latches immediately)
// - Site-frame poses are computed/stored when SiteFrame becomes valid
// - Tags remain sticky after latching (never auto-unlatch on occlusion)
// - Only manual reset or ClearAll can remove latched tags
// - OnGUI REMOVED to eliminate NullReferenceException spam
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace ARObjectDetection.AprilTag
{
    public class AssetTagManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private AprilTagConfig config;

        [Header("References")]
        [SerializeField] private SiteFrameManager siteFrameManager;
        [SerializeField] private ObjectTracker objectTracker;
        [SerializeField] private Camera mainCamera;

        [Header("Latching Behavior")]
        [Tooltip("Number of consecutive stable observations to latch (overrides config if set)")]
        [Range(2, 10)]
        [SerializeField] private int requiredStableObservations = 3;

        [Tooltip("If true, tags can latch even when SiteFrame is invalid (pose stored later)")]
        [SerializeField] private bool allowLatchWithoutSiteFrame = true;

        [Header("Events")]
        public UnityEvent<int> OnTagLatched;
        public UnityEvent<int, Pose, float> OnTagSeen;
        public UnityEvent<int> OnTagConflict;
        public UnityEvent<int> OnTagConflictResolved;
        public UnityEvent<int> OnAnchorRemoved;

        // Internal state
        private Dictionary<int, LatchedTagState> tagStates = new Dictionary<int, LatchedTagState>();
        private Dictionary<int, float> conflictCooldowns = new Dictionary<int, float>();

        // Pending site-frame pose updates (for tags that latched before SiteFrame was valid)
        private HashSet<int> tagsPendingSiteFramePose = new HashSet<int>();

        // Track which tags were detected this tracker frame
        private HashSet<int> tagsDetectedThisFrame = new HashSet<int>();
        private int lastProcessedTrackerFrame = -1;

        // Metrics (kept for potential future use / debugging via code)
        private int totalDetectionsReceived = 0;
        private int detectionsSkippedSiteFrameMarker = 0;
        private int detectionsSkippedLowQuality = 0;
        private int detectionsSkippedCooldown = 0;
        private int tagsLatched = 0;
        private int conflictsTriggered = 0;
        private int conflictsResolved = 0;
        private int tagsLatchedWithoutSiteFrame = 0;
        private int tagsPoseUpdatedAfterSiteFrame = 0;
        private int anchorsRemoved = 0;

        // Public properties
        public IReadOnlyDictionary<int, LatchedTagState> AllTagStates => tagStates;
        public int LatchedTagCount => tagStates.Values.Count(s => s.IsLatched);
        public int CandidateTagCount => tagStates.Values.Count(s => s.Status == TagStatus.Candidate);
        public int ConflictTagCount => tagStates.Values.Count(s => s.IsInConflict);
        public AprilTagConfig Config => config;
        public bool IsSiteFrameValid => siteFrameManager != null && siteFrameManager.IsValid;
        public SiteFrameManager SiteFrame => siteFrameManager;
        public int RequiredStableObservations => requiredStableObservations;

        // Metrics accessors for InfoPanel
        public int TotalDetectionsReceived => totalDetectionsReceived;
        public int TagsLatchedCount => tagsLatched;
        public int ConflictsTriggered => conflictsTriggered;
        public int AnchorsRemovedCount => anchorsRemoved;

        private void Awake()
        {
            if (config == null)
            {
                Debug.LogWarning("[AssetTagManager] AprilTagConfig not assigned, using defaults");
            }

            if (siteFrameManager == null)
                siteFrameManager = FindFirstObjectByType<SiteFrameManager>();

            if (objectTracker == null)
                objectTracker = FindFirstObjectByType<ObjectTracker>();

            if (mainCamera == null)
                mainCamera = Camera.main;

            if (OnAnchorRemoved == null)
                OnAnchorRemoved = new UnityEvent<int>();

            Debug.Log($"[AssetTagManager] Initialized. RequiredStableObservations={requiredStableObservations}, " +
                     $"AllowLatchWithoutSiteFrame={allowLatchWithoutSiteFrame}, " +
                     $"AnchorRemoval={config?.EnableAnchorRemoval ?? false}");
        }

        private void Update()
        {
            // Update conflict cooldowns
            var expired = new List<int>();
            foreach (var kvp in conflictCooldowns)
            {
                if (Time.realtimeSinceStartup > kvp.Value)
                    expired.Add(kvp.Key);
            }
            foreach (int tagId in expired)
                conflictCooldowns.Remove(tagId);

            // Check if SiteFrame just became valid and update pending tags
            if (IsSiteFrameValid && tagsPendingSiteFramePose.Count > 0)
            {
                UpdatePendingSiteFramePoses();
            }

            // Visibility-aware anchor removal
            if (config != null && config.EnableAnchorRemoval && mainCamera != null)
            {
                ProcessAnchorRemoval();
            }
        }

        /// <summary>
        /// Update site-frame poses for tags that latched before SiteFrame was valid
        /// </summary>
        private void UpdatePendingSiteFramePoses()
        {
            var pending = tagsPendingSiteFramePose.ToList();
            foreach (int tagId in pending)
            {
                if (tagStates.TryGetValue(tagId, out LatchedTagState state))
                {
                    Pose sitePose = siteFrameManager.SitePoseFromWorldPose(state.LastTagPoseWorld);
                    state.UpdateSiteFramePose(sitePose);
                    tagsPoseUpdatedAfterSiteFrame++;

                    Debug.Log($"[AssetTagManager] Tag #{tagId}: Site-frame pose updated now that SiteFrame is valid");
                }
                tagsPendingSiteFramePose.Remove(tagId);
            }
        }

        /// <summary>
        /// Process an AprilTag detection observation.
        /// </summary>
        public void ProcessDetection(AprilTagDetection detection, Pose cameraPoseAtCapture)
        {
            if (!detection.IsValid)
                return;

            totalDetectionsReceived++;

            tagsDetectedThisFrame.Add(detection.TagId);

            int siteFrameMarkerId = config != null ? config.SiteFrameMarkerId : 0;
            if (detection.TagId == siteFrameMarkerId)
            {
                detectionsSkippedSiteFrameMarker++;
                return;
            }

            Pose tagPoseWorld = OpenCvPoseConversion.ComputeTagWorldPose(detection.PoseInCamera, cameraPoseAtCapture);

            Pose tagPoseSite = tagPoseWorld;
            bool hasSiteFramePose = false;

            if (IsSiteFrameValid)
            {
                tagPoseSite = siteFrameManager.SitePoseFromWorldPose(tagPoseWorld);
                hasSiteFramePose = true;
            }

            OnTagSeen?.Invoke(detection.TagId, tagPoseWorld, detection.Confidence);

            if (!tagStates.TryGetValue(detection.TagId, out LatchedTagState state))
            {
                if (!MeetsLatchQuality(detection.Quality))
                {
                    detectionsSkippedLowQuality++;
                    return;
                }

                state = LatchedTagState.CreateCandidate(
                    detection.TagId,
                    tagPoseSite,
                    tagPoseWorld,
                    detection.Quality,
                    detection.Confidence,
                    detection.ReprojError,
                    hasSiteFramePose
                );
                tagStates[detection.TagId] = state;

                Debug.Log($"[AssetTagManager] New candidate tag #{detection.TagId} " +
                         $"(hasSiteFramePose={hasSiteFramePose})");
            }
            else
            {
                ProcessExistingTag(state, detection, tagPoseSite, tagPoseWorld, hasSiteFramePose);
            }
        }

        private bool MeetsLatchQuality(DetectionQuality quality)
        {
            if (config != null)
                return config.MeetsLatchQuality(quality);
            return quality >= DetectionQuality.Fair;
        }

        private void ProcessExistingTag(LatchedTagState state, AprilTagDetection detection,
            Pose poseSite, Pose poseWorld, bool hasSiteFramePose)
        {
            switch (state.Status)
            {
                case TagStatus.Candidate:
                    ProcessCandidateTag(state, detection, poseSite, poseWorld, hasSiteFramePose);
                    break;
                case TagStatus.Latched:
                    ProcessLatchedTag(state, detection, poseSite, poseWorld, hasSiteFramePose);
                    break;
                case TagStatus.Conflict:
                    ProcessConflictTag(state, detection, poseSite, poseWorld);
                    break;
            }
        }

        private void ProcessCandidateTag(LatchedTagState state, AprilTagDetection detection,
            Pose poseSite, Pose poseWorld, bool hasSiteFramePose)
        {
            Pose referenceNewPose = hasSiteFramePose ? poseSite : poseWorld;
            Pose referenceOldPose = state.HasSiteFramePose ? state.LastTagPoseSite : state.LastTagPoseWorld;

            float maxPosDelta = config != null ? config.MaxStablePositionDelta : 0.05f;
            float maxRotDelta = config != null ? config.MaxStableRotationDelta : 15f;

            float positionDelta = Vector3.Distance(referenceNewPose.position, referenceOldPose.position);
            float rotationDelta = Quaternion.Angle(referenceNewPose.rotation, referenceOldPose.rotation);

            bool isStable = positionDelta <= maxPosDelta && rotationDelta <= maxRotDelta;

            if (!MeetsLatchQuality(detection.Quality))
            {
                detectionsSkippedLowQuality++;
                return;
            }

            if (isStable)
            {
                state.UpdateFromDetection(poseSite, poseWorld, detection.Quality,
                    detection.Confidence, detection.ReprojError, true, hasSiteFramePose);

                if (state.ConsecutiveStableDetections >= requiredStableObservations)
                {
                    state.Latch();
                    tagsLatched++;

                    if (!hasSiteFramePose)
                    {
                        tagsLatchedWithoutSiteFrame++;
                        tagsPendingSiteFramePose.Add(state.TagId);
                        Debug.Log($"[AssetTagManager] ★ TAG #{state.TagId} LATCHED (identity confirmed) - " +
                                 $"SiteFrame pose PENDING (will update when SiteFrame valid)");
                    }
                    else
                    {
                        Debug.Log($"[AssetTagManager] ★ TAG #{state.TagId} LATCHED with full site-frame pose");
                    }

                    OnTagLatched?.Invoke(state.TagId);
                }
                else
                {
                    Debug.Log($"[AssetTagManager] Tag #{state.TagId}: {state.ConsecutiveStableDetections}/{requiredStableObservations}");
                }
            }
            else
            {
                state.UpdateFromDetection(poseSite, poseWorld, detection.Quality,
                    detection.Confidence, detection.ReprojError, false, hasSiteFramePose);
            }
        }

        private void ProcessLatchedTag(LatchedTagState state, AprilTagDetection detection,
            Pose poseSite, Pose poseWorld, bool hasSiteFramePose)
        {
            if (!MeetsLatchQuality(detection.Quality))
            {
                detectionsSkippedLowQuality++;
                return;
            }

            if (conflictCooldowns.TryGetValue(state.TagId, out float cooldownEnd))
            {
                if (Time.realtimeSinceStartup < cooldownEnd)
                {
                    detectionsSkippedCooldown++;
                    state.LastSeenTime = Time.realtimeSinceStartup;
                    return;
                }
            }

            state.UpdateFromDetection(poseSite, poseWorld, detection.Quality,
                detection.Confidence, detection.ReprojError, true, hasSiteFramePose);

            if (hasSiteFramePose && tagsPendingSiteFramePose.Contains(state.TagId))
            {
                state.UpdateSiteFramePose(poseSite);
                tagsPendingSiteFramePose.Remove(state.TagId);
                tagsPoseUpdatedAfterSiteFrame++;
                Debug.Log($"[AssetTagManager] Tag #{state.TagId}: Site-frame pose now available");
            }

            if (state.HasLinkedTrack && objectTracker != null && hasSiteFramePose)
            {
                CheckForConflict(state, poseSite, poseWorld);
            }
        }

        private void ProcessConflictTag(LatchedTagState state, AprilTagDetection detection,
            Pose poseSite, Pose poseWorld)
        {
            state.LastSeenTime = Time.realtimeSinceStartup;
            state.LastConfidence = detection.Confidence;
            state.LastQuality = detection.Quality;
            state.ConflictTagPoseWorld = poseWorld;
        }

        private void CheckForConflict(LatchedTagState state, Pose poseSite, Pose poseWorld)
        {
            TrackedObject track = objectTracker.ActiveTracks.FirstOrDefault(t => t.id == state.LinkedTrackId);
            if (track == null)
                return;

            float conflictThreshold = config != null ? config.ConflictThreshold : 0.25f;

            Pose trackedPoseWorld = new Pose(track.worldPositionSmoothed, track.worldRotation);
            Pose trackedPoseSite = IsSiteFrameValid
                ? siteFrameManager.SitePoseFromWorldPose(trackedPoseWorld)
                : trackedPoseWorld;

            Pose objectPoseFromTagSite = new Pose(
                poseSite.position + poseSite.rotation * state.TagToObjectOffset.position,
                poseSite.rotation * state.TagToObjectOffset.rotation
            );

            float positionErrorSite = Vector3.Distance(objectPoseFromTagSite.position, trackedPoseSite.position);

            if (positionErrorSite > conflictThreshold)
            {
                state.SetConflict(positionErrorSite, poseWorld, trackedPoseWorld);
                conflictsTriggered++;
                OnTagConflict?.Invoke(state.TagId);

                Debug.LogWarning($"[AssetTagManager] CONFLICT for tag #{state.TagId}: error={positionErrorSite:F3}m");
            }
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        public bool TryGetLatchedTag(int tagId, out LatchedTagState state)
        {
            if (tagStates.TryGetValue(tagId, out state))
                return state.IsLatched;
            state = null;
            return false;
        }

        public IEnumerable<LatchedTagState> GetAllLatchedTags()
        {
            return tagStates.Values.Where(s => s.IsLatched);
        }

        public IEnumerable<LatchedTagState> GetConflictTags()
        {
            return tagStates.Values.Where(s => s.IsInConflict);
        }

        public LatchedTagState GetTagState(int tagId)
        {
            tagStates.TryGetValue(tagId, out LatchedTagState state);
            return state;
        }

        public void AcceptConflict(int tagId)
        {
            if (!tagStates.TryGetValue(tagId, out LatchedTagState state) || !state.IsInConflict)
                return;

            state.AcceptTagPose();
            conflictsResolved++;

            float cooldown = config != null ? config.SlowCorrectionDuration * 2f : 2f;
            conflictCooldowns[tagId] = Time.realtimeSinceStartup + cooldown;

            OnTagConflictResolved?.Invoke(tagId);
            Debug.Log($"[AssetTagManager] Conflict ACCEPTED for tag #{tagId}");
        }

        public void RejectConflict(int tagId)
        {
            if (!tagStates.TryGetValue(tagId, out LatchedTagState state) || !state.IsInConflict)
                return;

            state.RejectTagPose();
            conflictsResolved++;

            float cooldown = config != null ? config.SlowCorrectionDuration * 2f : 2f;
            conflictCooldowns[tagId] = Time.realtimeSinceStartup + cooldown;

            OnTagConflictResolved?.Invoke(tagId);
            Debug.Log($"[AssetTagManager] Conflict REJECTED for tag #{tagId}");
        }

        /// <summary>
        /// Clear a single tag (manual unlatch)
        /// </summary>
        public void ClearTag(int tagId)
        {
            if (tagStates.Remove(tagId))
            {
                tagsPendingSiteFramePose.Remove(tagId);
                conflictCooldowns.Remove(tagId);
                Debug.Log($"[AssetTagManager] Tag #{tagId} cleared");
            }
        }

        /// <summary>
        /// Clear all tags (manual reset)
        /// </summary>
        public void ClearAllTags()
        {
            tagStates.Clear();
            tagsPendingSiteFramePose.Clear();
            conflictCooldowns.Clear();
            Debug.Log("[AssetTagManager] All tag states cleared");
        }

        public Pose WorldToSitePose(Pose worldPose)
        {
            if (!IsSiteFrameValid)
                return worldPose;
            return siteFrameManager.SitePoseFromWorldPose(worldPose);
        }

        public Pose SiteToWorldPose(Pose sitePose)
        {
            if (!IsSiteFrameValid)
                return sitePose;
            return siteFrameManager.WorldPoseFromSitePose(sitePose);
        }

        // ============================================================
        // VISIBILITY-AWARE ANCHOR REMOVAL
        // ============================================================

        /// <summary>
        /// Process anchor removal based on visibility and detection state.
        /// Called every Unity Update frame.
        /// </summary>
        private void ProcessAnchorRemoval()
        {
            if (config == null) return;

            var toRemove = new List<int>();

            foreach (var state in tagStates.Values)
            {
                if (!state.IsLatched) continue;

                // Check for anchor confirmation
                if (!state.IsAnchorConfirmed)
                {
                    if (state.ConsecutiveDetectionFrames >= config.AnchorConfirmationFrames)
                    {
                        state.ConfirmAnchor();
                        Debug.Log($"[AssetTagManager] ✅ Anchor #{state.TagId} CONFIRMED after {state.ConsecutiveDetectionFrames} detection frames");
                    }
                }

                // Check for removal (only confirmed anchors)
                if (state.ShouldRemoveAnchor(config.MaxMissesWhileInView))
                {
                    toRemove.Add(state.TagId);
                }
            }

            // Remove anchors that exceeded miss threshold
            foreach (int tagId in toRemove)
            {
                if (tagStates.Remove(tagId))
                {
                    tagsPendingSiteFramePose.Remove(tagId);
                    conflictCooldowns.Remove(tagId);
                    anchorsRemoved++;
                    OnAnchorRemoved?.Invoke(tagId);
                    Debug.Log($"[AssetTagManager] 🗑️ Removed anchor #{tagId} (exceeded {config.MaxMissesWhileInView} consecutive misses while in view)");
                }
            }
        }

        /// <summary>
        /// Check if a world position is in the camera's frustum (visible).
        /// </summary>
        private bool IsInCameraFOV(Vector3 worldPos)
        {
            if (mainCamera == null) return false;

            Vector3 viewportPos = mainCamera.WorldToViewportPoint(worldPos);

            return viewportPos.z > 0 &&
                   viewportPos.x >= 0 && viewportPos.x <= 1 &&
                   viewportPos.y >= 0 && viewportPos.y <= 1;
        }

        /// <summary>
        /// PUBLIC API: Called by AprilTagIntegration at the END of each detection/tracker frame.
        /// </summary>
        public void OnTrackerFrameComplete(int frameId)
        {
            if (frameId == lastProcessedTrackerFrame)
                return;
            lastProcessedTrackerFrame = frameId;

            if (config == null || !config.EnableAnchorRemoval || mainCamera == null)
            {
                tagsDetectedThisFrame.Clear();
                return;
            }

            foreach (var state in tagStates.Values)
            {
                if (!state.IsLatched) continue;

                bool detectedThisFrame = tagsDetectedThisFrame.Contains(state.TagId);

                if (detectedThisFrame)
                {
                    state.ResetMissCounter();
                }
                else
                {
                    bool inFOV = IsInCameraFOV(state.LastTagPoseWorld.position);

                    if (inFOV)
                    {
                        state.RecordMissWhileInView();
                    }
                    else
                    {
                        state.ResetMissCounter();
                    }
                }
            }

            tagsDetectedThisFrame.Clear();
        }

        // NOTE: OnGUI() method has been REMOVED to eliminate NullReferenceException spam.
        // Debug information can be viewed via the InfoPanel or via code-based logging.
    }
}