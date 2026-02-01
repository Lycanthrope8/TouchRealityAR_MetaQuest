// ============================================================================
// FILE: AprilTagDetectionTypes.cs
// Core data types for AprilTag asset identity system.
// 
// FIXES IMPLEMENTED:
// - LatchedTagState tracks HasSiteFramePose flag
// - UpdateSiteFramePose() for deferred pose computation
// - Sticky latching: tags remain latched even during occlusion
// ============================================================================

using System;
using UnityEngine;

namespace ARObjectDetection.AprilTag
{
    /// <summary>
    /// Status of an AprilTag in the asset identity system.
    /// </summary>
    public enum TagStatus
    {
        /// <summary>Tag has been seen but not yet latched (needs more stable observations)</summary>
        Candidate,

        /// <summary>Tag has been latched - identity is confirmed for this session</summary>
        Latched,

        /// <summary>Tag pose conflicts with tracked object pose - requires manual resolution</summary>
        Conflict
    }

    /// <summary>
    /// Detection quality level.
    /// </summary>
    public enum DetectionQuality
    {
        Poor,
        Fair,
        Good,
        Excellent
    }

    /// <summary>
    /// Parsed AprilTag detection from server fiducial observation.
    /// </summary>
    public struct AprilTagDetection
    {
        public int TagId;
        public int FrameId;
        public float Timestamp;
        public Pose PoseInCamera;
        public float Confidence;
        public float ReprojError;
        public DetectionQuality Quality;

        public bool IsValid => TagId >= 0 && FrameId >= 0 && !IsPoseIdentity(PoseInCamera);

        private static bool IsPoseIdentity(Pose pose)
        {
            return pose.position == Vector3.zero &&
                   Quaternion.Angle(pose.rotation, Quaternion.identity) < 0.01f;
        }

        public static AprilTagDetection FromFiducialObservation(
            FiducialObservation fiducial,
            int frameId,
            float timestamp,
            Pose poseInCamera)
        {
            // Validate pose data
            if (fiducial == null || !fiducial.HasValidPose())
            {
                return Invalid;
            }

            float confidence = fiducial.GetEffectiveConfidence();
            float reproj = fiducial.reproj_error;

            DetectionQuality quality;
            if (confidence >= 0.9f && reproj < 2f)
                quality = DetectionQuality.Excellent;
            else if (confidence >= 0.7f && reproj < 5f)
                quality = DetectionQuality.Good;
            else if (confidence >= 0.5f && reproj < 8f)
                quality = DetectionQuality.Fair;
            else
                quality = DetectionQuality.Poor;

            return new AprilTagDetection
            {
                TagId = fiducial.id,
                FrameId = frameId,
                Timestamp = timestamp,
                PoseInCamera = poseInCamera,
                Confidence = confidence,
                ReprojError = reproj,
                Quality = quality
            };
        }

        public static AprilTagDetection Invalid => new AprilTagDetection { TagId = -1, FrameId = -1 };
    }

    /// <summary>
    /// Persistent state for a latched AprilTag.
    /// Supports deferred SiteFrame pose computation.
    /// </summary>
    public class LatchedTagState
    {
        public int TagId { get; private set; }
        public TagStatus Status { get; private set; }

        // Poses in different coordinate spaces
        public Pose LastTagPoseSite { get; set; }
        public Pose LastTagPoseWorld { get; set; }
        public Pose LatchedPoseSite { get; private set; }
        public Pose LatchedPoseWorld { get; private set; }

        // NEW: Track whether we have a valid site-frame pose
        public bool HasSiteFramePose { get; private set; }

        // Track association
        public int LinkedTrackId { get; private set; } = -1;
        public string LinkedClassName { get; private set; } = "";
        public Pose TagToObjectOffset { get; set; } = Pose.identity;

        // Quality metrics
        public DetectionQuality LastQuality { get; set; }
        public float LastConfidence { get; set; }
        public float LastReprojError { get; set; }

        // Timing
        public float FirstSeenTime { get; private set; }
        public float LastSeenTime { get; set; }
        public float LatchedTime { get; private set; }

        // Counters
        public int TotalDetections { get; private set; }
        public int ConsecutiveStableDetections { get; private set; }

        // Visibility-aware anchor removal (FIX #4)
        public int ConsecutiveMissesWhileInView { get; private set; }
        public int ConsecutiveDetectionFrames { get; private set; }
        public bool IsAnchorConfirmed { get; private set; }

        // Conflict state
        public float ConflictErrorMeters { get; private set; }
        public Pose ConflictTagPoseWorld { get; set; }
        public Pose ConflictTrackedPoseWorld { get; private set; }

        public bool IsLatched => Status == TagStatus.Latched || Status == TagStatus.Conflict;
        public bool IsInConflict => Status == TagStatus.Conflict;
        public bool HasLinkedTrack => LinkedTrackId >= 0;
        public float SecondsSinceLastSeen => Time.realtimeSinceStartup - LastSeenTime;

        public static LatchedTagState CreateCandidate(int tagId, Pose poseSite, Pose poseWorld,
            DetectionQuality quality, float confidence, float reproj, bool hasSiteFramePose = true)
        {
            float now = Time.realtimeSinceStartup;
            return new LatchedTagState
            {
                TagId = tagId,
                Status = TagStatus.Candidate,
                LastTagPoseSite = poseSite,
                LastTagPoseWorld = poseWorld,
                HasSiteFramePose = hasSiteFramePose,
                LastQuality = quality,
                LastConfidence = confidence,
                LastReprojError = reproj,
                FirstSeenTime = now,
                LastSeenTime = now,
                TotalDetections = 1,
                ConsecutiveStableDetections = 1
            };
        }

        public void UpdateFromDetection(Pose poseSite, Pose poseWorld, DetectionQuality quality,
            float confidence, float reproj, bool isStable, bool hasSiteFramePose = true)
        {
            LastTagPoseSite = poseSite;
            LastTagPoseWorld = poseWorld;
            LastQuality = quality;
            LastConfidence = confidence;
            LastReprojError = reproj;
            LastSeenTime = Time.realtimeSinceStartup;
            TotalDetections++;

            // Update site-frame pose availability
            if (hasSiteFramePose)
            {
                HasSiteFramePose = true;
            }

            if (isStable)
                ConsecutiveStableDetections++;
            else
                ConsecutiveStableDetections = 1;

            // FIX #4: Anchor confirmation tracking (detection frames, not Unity Update)
            if (IsLatched && !IsAnchorConfirmed)
            {
                ConsecutiveDetectionFrames++;
            }

            // Reset visibility miss counter on successful detection
            ConsecutiveMissesWhileInView = 0;
        }

        /// <summary>
        /// Update the site-frame pose after SiteFrame becomes valid.
        /// Called for tags that latched before SiteFrame was available.
        /// </summary>
        public void UpdateSiteFramePose(Pose sitePose)
        {
            LastTagPoseSite = sitePose;
            if (IsLatched)
            {
                LatchedPoseSite = sitePose;
            }
            HasSiteFramePose = true;
        }

        public void Latch()
        {
            Status = TagStatus.Latched;
            LatchedPoseSite = LastTagPoseSite;
            LatchedPoseWorld = LastTagPoseWorld;
            LatchedTime = Time.realtimeSinceStartup;
        }

        public void LinkToTrack(int trackId, string className)
        {
            LinkedTrackId = trackId;
            LinkedClassName = className;
        }

        public void UnlinkTrack()
        {
            LinkedTrackId = -1;
            LinkedClassName = "";
        }

        public void SetConflict(float errorMeters, Pose tagPoseWorld, Pose trackedPoseWorld)
        {
            Status = TagStatus.Conflict;
            ConflictErrorMeters = errorMeters;
            ConflictTagPoseWorld = tagPoseWorld;
            ConflictTrackedPoseWorld = trackedPoseWorld;
        }

        public void AcceptTagPose()
        {
            Status = TagStatus.Latched;
            LatchedPoseSite = LastTagPoseSite;
            LatchedPoseWorld = LastTagPoseWorld;
            ConflictErrorMeters = 0f;
        }

        public void RejectTagPose()
        {
            Status = TagStatus.Latched;
            ConflictErrorMeters = 0f;
        }

        // ============================================================
        // FIX #4: VISIBILITY-AWARE ANCHOR REMOVAL
        // ============================================================

        /// <summary>
        /// Called when tag not detected during a detection/tracker frame.
        /// Only increments miss counter if tag position is in camera FOV.
        /// </summary>
        public void RecordMissWhileInView()
        {
            ConsecutiveMissesWhileInView++;
        }

        /// <summary>
        /// Reset miss counter (called when tag detected or when not in view).
        /// </summary>
        public void ResetMissCounter()
        {
            ConsecutiveMissesWhileInView = 0;
        }

        /// <summary>
        /// Confirm this anchor (called when consecutive detection frames reaches threshold).
        /// </summary>
        public void ConfirmAnchor()
        {
            IsAnchorConfirmed = true;
        }

        /// <summary>
        /// Check if anchor should be removed due to prolonged absence while visible.
        /// </summary>
        public bool ShouldRemoveAnchor(int maxMissesWhileInView)
        {
            // Only confirmed anchors can be removed this way
            if (!IsAnchorConfirmed)
                return false;

            return ConsecutiveMissesWhileInView > maxMissesWhileInView;
        }
    }

    /// <summary>
    /// Configuration ScriptableObject for AprilTag asset identity system.
    /// </summary>
    [CreateAssetMenu(fileName = "AprilTagConfig", menuName = "AR Detection/AprilTag Config")]
    public class AprilTagConfig : ScriptableObject
    {
        [Header("Latching Settings")]
        [Tooltip("Number of consecutive stable detections required to latch a tag")]
        [Range(2, 20)]
        public int RequiredConsecutiveDetections = 3;

        [Tooltip("Maximum position delta between frames to consider stable (meters)")]
        public float MaxStablePositionDelta = 0.05f;

        [Tooltip("Maximum rotation delta between frames to consider stable (degrees)")]
        public float MaxStableRotationDelta = 15f;

        [Tooltip("Minimum detection quality to consider for latching")]
        public DetectionQuality MinQualityForLatch = DetectionQuality.Fair;

        [Header("Correction Thresholds")]
        [Tooltip("Position error threshold for gentle correction (meters)")]
        public float RefineThreshold = 0.05f;

        [Tooltip("Position error threshold to trigger conflict (meters)")]
        public float ConflictThreshold = 0.25f;

        [Tooltip("Duration of gentle correction (seconds)")]
        public float GentleCorrectionDuration = 0.3f;

        [Tooltip("Duration of slow correction (seconds)")]
        public float SlowCorrectionDuration = 1.0f;

        [Header("SiteFrame Integration")]
        [Tooltip("Marker ID reserved for SiteFrame (will NOT be used for asset identity)")]
        public int SiteFrameMarkerId = 0;

        [Tooltip("Require valid SiteFrame before processing asset tags (DEPRECATED - tags now latch independently)")]
        public bool RequireSiteFrame = false;

        [Header("Anchor Removal (FIX #4)")]
        [Tooltip("Consecutive detection/tracker frames needed to confirm anchor (after latching)")]
        [Range(5, 50)]
        public int AnchorConfirmationFrames = 15;

        [Tooltip("Max consecutive misses while in FOV before removing confirmed anchor")]
        [Range(10, 100)]
        public int MaxMissesWhileInView = 20;

        [Tooltip("Enable visibility-aware anchor removal (only removes when in FOV and not detected)")]
        public bool EnableAnchorRemoval = true;

        [Header("Debug")]
        public bool EnableDebugLogs = true;
        public bool ShowDebugGUI = true;

        public bool IsSiteFrameMarker(int tagId) => tagId == SiteFrameMarkerId;

        public bool MeetsLatchQuality(DetectionQuality quality) => quality >= MinQualityForLatch;
    }
}