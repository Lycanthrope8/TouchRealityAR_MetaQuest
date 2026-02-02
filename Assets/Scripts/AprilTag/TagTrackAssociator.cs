// ============================================================================
// FILE: TagTrackAssociator.cs
// Associates AprilTags with tracked objects from ObjectTracker.
// 
// FIXES IMPLEMENTED:
// - Hysteresis: Once a pending candidate is selected, do NOT switch unless new
//   candidate is significantly better (prevents jitter-based switching)
// - RequiredFrames=1 confirms IMMEDIATELY (no expiration possible)
// - Debug logging shows world poses and site poses for diagnosis
// - Coordinate frame validation
// - OnGUI REMOVED to eliminate debug spam
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ARObjectDetection.AprilTag
{
    public class PendingAssociation
    {
        public int TagId;
        public int TrackId;
        public string ClassName;
        public int ConfirmationCount;
        public float LastSeenTime;
        public float DistanceSite;
        public float BestScoreSeen;
    }

    public class TagTrackAssociator : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private AssetTagManager assetTagManager;
        [SerializeField] private ObjectTracker objectTracker;
        [SerializeField] private SiteFrameManager siteFrameManager;

        [Header("Association Settings")]
        [Tooltip("Maximum distance to associate tag with track (meters) - DESK SCALE")]
        [SerializeField] private float maxAssociationDistance = 0.75f;

        [Tooltip("Score bonus for keeping existing association")]
        [SerializeField] private float existingAssociationBonus = 0.2f;

        [Tooltip("Score bonus for confirmed tracks")]
        [SerializeField] private float confirmedTrackBonus = 0.1f;

        [Tooltip("Minimum improvement required to switch pending candidate (hysteresis)")]
        [SerializeField] private float pendingSwitchThreshold = 0.20f;

        [Header("Temporal Confirmation")]
        [Range(1, 30)]
        [SerializeField] private int requiredConfirmationFrames = 15;

        [Tooltip("Timeout for pending associations (seconds) - only applies if RequiredFrames > 1")]
        [SerializeField] private float pendingTimeout = 5.0f;

        [Header("Sticky Behavior")]
        [Tooltip("If true, once a tag is associated with a track, it cannot be reassigned automatically")]
        [SerializeField] private bool stickyAssociations = false;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;
        [SerializeField] private bool enableVerbosePoseLogs = false;

        // Internal state - ONE-TO-ONE MAPPINGS
        private Dictionary<int, int> trackToTag = new Dictionary<int, int>();
        private Dictionary<int, int> tagToTrack = new Dictionary<int, int>();
        private Dictionary<int, PendingAssociation> pendingAssociations = new Dictionary<int, PendingAssociation>();

        // Metrics
        private int associationAttempts = 0;
        private int successfulAssociations = 0;
        private int rejectedTooFar = 0;
        private int rejectedNoTracks = 0;
        private int rejectedConflict = 0;
        private int eventsFired = 0;
        private int hysteresisPreventedSwitch = 0;

        // Public accessors for metrics (can be used by InfoPanel)
        public int AssociationAttempts => associationAttempts;
        public int SuccessfulAssociations => successfulAssociations;
        public int ActiveAssociationCount => trackToTag.Count;
        public int PendingCount => pendingAssociations.Count;

        private void Awake()
        {
            if (assetTagManager == null)
                assetTagManager = FindFirstObjectByType<AssetTagManager>();
            if (objectTracker == null)
                objectTracker = FindFirstObjectByType<ObjectTracker>();
            if (siteFrameManager == null)
                siteFrameManager = FindFirstObjectByType<SiteFrameManager>();

            if (assetTagManager == null)
            {
                Debug.LogError("[TagTrackAssociator] AssetTagManager not found!");
                enabled = false;
                return;
            }

            assetTagManager.OnTagSeen.AddListener(OnTagSeen);
            assetTagManager.OnTagLatched.AddListener(OnTagLatched);

            Debug.Log($"[TagTrackAssociator] Initialized. MaxDist={maxAssociationDistance}m, " +
                     $"StickyAssociations={stickyAssociations}, RequiredFrames={requiredConfirmationFrames}, " +
                     $"PendingSwitchThreshold={pendingSwitchThreshold}m");
        }

        private void OnDestroy()
        {
            if (assetTagManager != null)
            {
                assetTagManager.OnTagSeen.RemoveListener(OnTagSeen);
                assetTagManager.OnTagLatched.RemoveListener(OnTagLatched);
            }
        }

        private void Update()
        {
            // Only expire pending associations if RequiredFrames > 1
            if (requiredConfirmationFrames <= 1)
                return;

            var expired = new List<int>();
            float now = Time.realtimeSinceStartup;
            foreach (var kvp in pendingAssociations)
            {
                if (now - kvp.Value.LastSeenTime > pendingTimeout)
                    expired.Add(kvp.Key);
            }
            foreach (int tagId in expired)
            {
                if (enableDebugLogs)
                    Debug.Log($"[TagTrackAssociator] Pending association for tag #{tagId} expired");
                pendingAssociations.Remove(tagId);
            }
        }

        private void OnTagSeen(int tagId, Pose tagPoseWorld, float confidence)
        {
            eventsFired++;

            LatchedTagState state = assetTagManager.GetTagState(tagId);
            if (state == null || !state.IsLatched)
                return;

            TryAssociateTag(tagId, state);
        }

        private void OnTagLatched(int tagId)
        {
            eventsFired++;
            Debug.Log($"[TagTrackAssociator] OnTagLatched event received for tag #{tagId}");

            LatchedTagState state = assetTagManager.GetTagState(tagId);
            if (state != null)
            {
                TryAssociateTag(tagId, state);
            }
            else
            {
                Debug.LogWarning($"[TagTrackAssociator] Tag #{tagId} state is null after OnTagLatched!");
            }
        }

        public void TryAssociateTag(int tagId, LatchedTagState tagState)
        {
            associationAttempts++;

            if (objectTracker == null)
            {
                if (enableDebugLogs)
                    Debug.LogWarning("[TagTrackAssociator] ObjectTracker is null!");
                return;
            }

            if (tagState == null)
            {
                if (enableDebugLogs)
                    Debug.LogWarning($"[TagTrackAssociator] Tag #{tagId} state is null!");
                return;
            }

            int activeTrackCount = objectTracker.ActiveTracks.Count(t => t.state != TrackState.Lost);
            if (activeTrackCount == 0)
            {
                rejectedNoTracks++;
                if (enableDebugLogs)
                    Debug.Log($"[TagTrackAssociator] Tag #{tagId}: No active tracks to associate with");
                return;
            }

            int currentLinkedTrackId = tagState.LinkedTrackId;

            if (stickyAssociations && tagToTrack.ContainsKey(tagId))
            {
                int existingTrackId = tagToTrack[tagId];
                TrackedObject existingTrack = objectTracker.ActiveTracks.FirstOrDefault(t => t.id == existingTrackId);
                if (existingTrack != null && existingTrack.state != TrackState.Lost)
                {
                    return;
                }
                if (enableDebugLogs)
                    Debug.Log($"[TagTrackAssociator] Tag #{tagId}: Previous track #{existingTrackId} gone, allowing reassociation");
            }

            Vector3 tagWorldPos = tagState.LastTagPoseWorld.position;

            if (enableDebugLogs)
            {
                Debug.Log($"[TagTrackAssociator] === ASSOCIATION ATTEMPT for Tag #{tagId} ===");
                Debug.Log($"[TagTrackAssociator] Tag #{tagId} World Pos: {tagWorldPos:F3}");
                if (tagState.HasSiteFramePose)
                    Debug.Log($"[TagTrackAssociator] Tag #{tagId} Site Pos: {tagState.LastTagPoseSite.position:F3}");
                Debug.Log($"[TagTrackAssociator] Available tracks:");
                foreach (var t in objectTracker.ActiveTracks.Where(t => t.state != TrackState.Lost))
                {
                    Debug.Log($"[TagTrackAssociator]   Track #{t.id} ({t.className}): pos={t.worldPositionSmoothed:F3}, dist={Vector3.Distance(tagWorldPos, t.worldPositionSmoothed):F3}m");
                }
            }

            if (enableVerbosePoseLogs)
            {
                Debug.Log($"[TagTrackAssociator] Tag #{tagId} WorldPos: {tagWorldPos}");
                if (tagState.HasSiteFramePose)
                    Debug.Log($"[TagTrackAssociator] Tag #{tagId} SitePos: {tagState.LastTagPoseSite.position}");
            }

            var candidates = new List<(TrackedObject track, float distance, float score)>();

            if (enableDebugLogs)
                Debug.Log($"[TagTrackAssociator] Tag #{tagId}: Searching {activeTrackCount} active tracks");

            foreach (var track in objectTracker.ActiveTracks)
            {
                if (track.state == TrackState.Lost)
                    continue;

                if (stickyAssociations && trackToTag.TryGetValue(track.id, out int existingTagId))
                {
                    if (existingTagId != tagId)
                        continue;
                }

                Vector3 trackWorldPos = track.worldPositionSmoothed;
                float distance = Vector3.Distance(tagWorldPos, trackWorldPos);

                if (enableDebugLogs)
                {
                    Debug.Log($"[TagTrackAssociator] Tag #{tagId} -> Track #{track.id} ({track.className}): " +
                             $"distance={distance:F3}m (max={maxAssociationDistance}m) " +
                             $"tagPos={tagWorldPos:F2} trackPos={trackWorldPos:F2}");
                }

                if (distance > maxAssociationDistance)
                    continue;

                float score = distance;
                if (track.state == TrackState.Confirmed)
                    score -= confirmedTrackBonus;
                if (track.id == currentLinkedTrackId)
                    score -= existingAssociationBonus;

                candidates.Add((track, distance, score));
            }

            if (candidates.Count == 0)
            {
                rejectedTooFar++;
                if (enableDebugLogs)
                    Debug.Log($"[TagTrackAssociator] Tag #{tagId}: No tracks within {maxAssociationDistance}m");
                return;
            }

            candidates.Sort((a, b) => a.score.CompareTo(b.score));
            var best = candidates[0];

            if (enableDebugLogs)
                Debug.Log($"[TagTrackAssociator] Tag #{tagId}: Best candidate is Track #{best.track.id} ({best.track.className}) at {best.distance:F3}m, score={best.score:F3}");

            if (stickyAssociations && currentLinkedTrackId >= 0 && best.track.id != currentLinkedTrackId)
            {
                var current = candidates.FirstOrDefault(c => c.track.id == currentLinkedTrackId);
                if (current.track != null)
                {
                    if (enableDebugLogs)
                    {
                        Debug.Log($"[TagTrackAssociator] Tag #{tagId} keeping existing track #{currentLinkedTrackId} " +
                                 $"(sticky mode, not switching to #{best.track.id})");
                    }
                    return;
                }
            }

            ApplyTemporalConfirmationWithHysteresis(tagId, tagState, best.track, best.distance, best.score, currentLinkedTrackId);
        }

        private void ApplyTemporalConfirmationWithHysteresis(int tagId, LatchedTagState tagState,
            TrackedObject bestTrack, float distance, float score, int currentLinkedTrackId)
        {
            float now = Time.realtimeSinceStartup;

            if (pendingAssociations.TryGetValue(tagId, out PendingAssociation pending))
            {
                if (pending.TrackId == bestTrack.id)
                {
                    pending.ConfirmationCount++;
                    pending.LastSeenTime = now;
                    pending.DistanceSite = distance;

                    if (score < pending.BestScoreSeen)
                        pending.BestScoreSeen = score;

                    if (enableDebugLogs)
                        Debug.Log($"[TagTrackAssociator] Tag #{tagId} -> Track #{bestTrack.id}: confirmation {pending.ConfirmationCount}/{requiredConfirmationFrames}");

                    if (pending.ConfirmationCount >= requiredConfirmationFrames)
                    {
                        FinalizeAssociation(tagId, tagState, bestTrack, currentLinkedTrackId);
                        pendingAssociations.Remove(tagId);
                    }
                    return;
                }
                else
                {
                    float improvement = pending.BestScoreSeen - score;

                    if (improvement < pendingSwitchThreshold)
                    {
                        hysteresisPreventedSwitch++;
                        pending.LastSeenTime = now;

                        if (enableDebugLogs)
                        {
                            Debug.Log($"[TagTrackAssociator] Tag #{tagId}: Hysteresis prevented switch from Track #{pending.TrackId} " +
                                     $"to Track #{bestTrack.id} (improvement={improvement:F3}m < threshold={pendingSwitchThreshold}m)");
                        }
                        return;
                    }

                    if (enableDebugLogs)
                    {
                        Debug.Log($"[TagTrackAssociator] Tag #{tagId}: Track changed from #{pending.TrackId} to #{bestTrack.id} " +
                                 $"(improvement={improvement:F3}m >= threshold={pendingSwitchThreshold}m)");
                    }
                    pendingAssociations.Remove(tagId);
                }
            }

            pendingAssociations[tagId] = new PendingAssociation
            {
                TagId = tagId,
                TrackId = bestTrack.id,
                ClassName = bestTrack.className,
                ConfirmationCount = 1,
                LastSeenTime = now,
                DistanceSite = distance,
                BestScoreSeen = score
            };

            if (enableDebugLogs)
                Debug.Log($"[TagTrackAssociator] Tag #{tagId} -> Track #{bestTrack.id}: starting confirmation 1/{requiredConfirmationFrames}");

            if (requiredConfirmationFrames <= 1)
            {
                FinalizeAssociation(tagId, tagState, bestTrack, currentLinkedTrackId);
                pendingAssociations.Remove(tagId);
            }
        }

        private void FinalizeAssociation(int tagId, LatchedTagState tagState, TrackedObject track, int previousLinkedTrackId)
        {
            if (trackToTag.TryGetValue(track.id, out int existingTagId) && existingTagId != tagId)
            {
                rejectedConflict++;
                Debug.LogWarning($"[TagTrackAssociator] CONFLICT: Track #{track.id} already has tag #{existingTagId}, " +
                               $"cannot associate with tag #{tagId}");
                return;
            }

            if (previousLinkedTrackId >= 0)
            {
                trackToTag.Remove(previousLinkedTrackId);
            }
            if (tagToTrack.TryGetValue(tagId, out int oldTrackId))
            {
                trackToTag.Remove(oldTrackId);
            }

            tagState.LinkToTrack(track.id, track.className);
            trackToTag[track.id] = tagId;
            tagToTrack[tagId] = track.id;
            successfulAssociations++;

            Debug.Log($"[TagTrackAssociator] ★ Association CONFIRMED: Tag #{tagId} ↔ Track #{track.id} ({track.className})");
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        public int GetTagForTrack(int trackId)
        {
            return trackToTag.TryGetValue(trackId, out int tagId) ? tagId : -1;
        }

        public int GetTrackForTag(int tagId)
        {
            return tagToTrack.TryGetValue(tagId, out int trackId) ? trackId : -1;
        }

        public bool TrackHasTag(int trackId)
        {
            return trackToTag.ContainsKey(trackId);
        }

        public bool TagHasTrack(int tagId)
        {
            return tagToTrack.ContainsKey(tagId);
        }

        public void ClearAssociation(int tagId)
        {
            LatchedTagState state = assetTagManager.GetTagState(tagId);
            if (state != null && state.HasLinkedTrack)
            {
                trackToTag.Remove(state.LinkedTrackId);
                state.UnlinkTrack();
            }
            tagToTrack.Remove(tagId);
            pendingAssociations.Remove(tagId);

            Debug.Log($"[TagTrackAssociator] Cleared association for tag #{tagId}");
        }

        public void ClearAllAssociations()
        {
            trackToTag.Clear();
            tagToTrack.Clear();
            pendingAssociations.Clear();
            foreach (var state in assetTagManager.AllTagStates.Values)
                state.UnlinkTrack();

            Debug.Log("[TagTrackAssociator] All associations cleared");
        }

        // NOTE: OnGUI() method has been REMOVED to eliminate debug spam.
        // Metrics can be accessed via public properties for InfoPanel display.
    }
}