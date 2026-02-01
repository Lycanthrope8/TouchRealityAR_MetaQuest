using UnityEngine;

namespace ARObjectDetection
{
    /// <summary>
    /// SiteFrameManager - Shared Coordinate Frame for Multi-Device AR
    /// 
    /// BEHAVIOR:
    /// - Once SiteFrame becomes VALID at least once, it NEVER becomes INVALID again during the session.
    /// - When marker is visible: pose updates/refines normally with smoothing.
    /// - When marker is occluded: holds the last good pose (no updates, no invalidation).
    /// </summary>
    public class SiteFrameManager : MonoBehaviour
    {
        [Header("Validity Settings")]
        [Tooltip("Number of consecutive good observations required before SiteFrame becomes valid")]
        [Range(1, 20)]
        [SerializeField] private int requiredConsecutiveObservations = 8;

        [Tooltip("Seconds without observations before considering marker occluded")]
        [Range(0.1f, 5f)]
        [SerializeField] private float occlusionThresholdSeconds = 0.5f;

        [Tooltip("Minimum confidence from tag detection to accept observation")]
        [Range(0f, 1f)]
        [SerializeField] private float minConfidence = 0.2f;

        [Header("Smoothing")]
        [Tooltip("EMA alpha for position smoothing (lower = smoother, higher = more responsive)")]
        [Range(0.01f, 1f)]
        [SerializeField] private float positionSmoothingAlpha = 0.3f;

        [Tooltip("Slerp factor for rotation smoothing")]
        [Range(0.01f, 1f)]
        [SerializeField] private float rotationSmoothingAlpha = 0.3f;

        [Header("Jump Rejection")]
        [Tooltip("Maximum position jump allowed (meters). Larger jumps are treated as outliers.")]
        [SerializeField] private float maxJumpMeters = 0.5f;

        [Tooltip("Maximum rotation jump allowed (degrees). Larger jumps are treated as outliers.")]
        [SerializeField] private float maxJumpDegrees = 45f;

        [Header("Debug Visualization")]
        [Tooltip("Optional transform to visualize the SiteFrame pose")]
        [SerializeField] private Transform siteFrameVisual;

        [Tooltip("Enable debug logging")]
        [SerializeField] private bool enableDebugLogs = true;

        // ============================================================
        // INTERNAL STATE
        // ============================================================

        private Pose worldFromSite = Pose.identity;
        private int consecutiveGoodObservations = 0;
        private float lastObservationTime = float.NegativeInfinity;
        private int lastObservedMarkerId = -1;

        private Pose candidatePose = Pose.identity;
        private bool hasCandidate = false;

        private int totalObservationsReceived = 0;
        private int observationsRejectedJump = 0;
        private int observationsRejectedConfidence = 0;

        // ============================================================
        // OCCLUSION-TOLERANT STATE
        // ============================================================

        /// <summary>
        /// True once SiteFrame has been valid at least once this session.
        /// Once true, IsValid will always return true (never invalidates).
        /// </summary>
        private bool siteFrameEverValidated = false;

        /// <summary>
        /// True if we received a valid marker observation this frame or very recently.
        /// Used to decide whether to update/refine pose or hold.
        /// </summary>
        private bool markerCurrentlyVisible = false;

        /// <summary>
        /// Timestamp when siteFrameEverValidated became true.
        /// </summary>
        private float firstValidationTime = 0f;

        /// <summary>
        /// Track whether we logged the occlusion message (to avoid spam).
        /// </summary>
        private bool loggedOcclusionHold = false;

        /// <summary>
        /// Track whether we logged the refining message after occlusion.
        /// </summary>
        private bool loggedRefiningAfterOcclusion = false;

        // ============================================================
        // PUBLIC PROPERTIES
        // ============================================================

        /// <summary>
        /// Returns true if SiteFrame has ever been validated this session.
        /// Once valid, always valid (never invalidates due to occlusion).
        /// </summary>
        public bool IsValid => siteFrameEverValidated;

        /// <summary>
        /// True if marker is currently visible (receiving fresh observations).
        /// </summary>
        public bool IsMarkerVisible => markerCurrentlyVisible;

        /// <summary>
        /// True if SiteFrame has been validated at least once.
        /// </summary>
        public bool HasEverBeenValid => siteFrameEverValidated;

        public float SecondsSinceLastSeen => Time.realtimeSinceStartup - lastObservationTime;
        public Pose WorldFromSite => worldFromSite;
        public int LastObservedMarkerId => lastObservedMarkerId;
        public int ConsecutiveGoodObservations => consecutiveGoodObservations;
        public int TotalObservationsReceived => totalObservationsReceived;

        // ============================================================
        // UNITY LIFECYCLE
        // ============================================================

        private void Awake()
        {
            if (siteFrameVisual != null)
            {
                Debug.Log($"[SiteFrame] Visual assigned: {siteFrameVisual.name}");
                siteFrameVisual.gameObject.SetActive(false);
            }
            else
            {
                Debug.LogWarning("[SiteFrame] No siteFrameVisual assigned!");
            }

            Debug.Log("[SiteFrame] Initialized - occlusion-tolerant mode (refines when visible, holds when occluded)");
        }

        private void OnEnable()
        {
            UnityEngine.XR.XRDevice.deviceLoaded += OnXRDeviceLoaded;
            UnityEngine.XR.InputTracking.trackingAcquired += OnTrackingAcquired;
            UnityEngine.XR.InputTracking.trackingLost += OnTrackingLost;
        }

        private void OnDisable()
        {
            UnityEngine.XR.XRDevice.deviceLoaded -= OnXRDeviceLoaded;
            UnityEngine.XR.InputTracking.trackingAcquired -= OnTrackingAcquired;
            UnityEngine.XR.InputTracking.trackingLost -= OnTrackingLost;
        }

        private void OnXRDeviceLoaded(string deviceName)
        {
            if (enableDebugLogs)
                Debug.Log($"[SiteFrame] XR device loaded: {deviceName}");
        }

        private void OnTrackingAcquired(UnityEngine.XR.XRNodeState state)
        {
            if (enableDebugLogs)
                Debug.Log($"[SiteFrame] XR tracking acquired for {state.nodeType}");
        }

        private void OnTrackingLost(UnityEngine.XR.XRNodeState state)
        {
            if (enableDebugLogs)
                Debug.Log($"[SiteFrame] XR tracking lost for {state.nodeType}");
        }

        private void Update()
        {
            // Determine if marker is currently visible based on time since last observation
            float timeSinceLastObs = Time.realtimeSinceStartup - lastObservationTime;
            bool wasVisible = markerCurrentlyVisible;
            markerCurrentlyVisible = (timeSinceLastObs <= occlusionThresholdSeconds);

            // Log state transitions
            if (siteFrameEverValidated)
            {
                if (wasVisible && !markerCurrentlyVisible)
                {
                    // Transition: visible -> occluded
                    if (enableDebugLogs && !loggedOcclusionHold)
                    {
                        Debug.Log($"[SiteFrame] Marker occluded – holding last pose at {worldFromSite.position:F2}");
                        loggedOcclusionHold = true;
                        loggedRefiningAfterOcclusion = false;
                    }
                }
                else if (!wasVisible && markerCurrentlyVisible)
                {
                    // Transition: occluded -> visible
                    if (enableDebugLogs && !loggedRefiningAfterOcclusion)
                    {
                        Debug.Log($"[SiteFrame] Marker visible – refining pose");
                        loggedRefiningAfterOcclusion = true;
                        loggedOcclusionHold = false;
                    }
                }
            }

            // Update visual
            UpdateVisual();
        }

        // ============================================================
        // OBSERVATION SUBMISSION
        // ============================================================

        /// <summary>
        /// Submit a new tag observation (pose of tag relative to camera).
        /// </summary>
        public void SubmitTagPoseInCamera(Pose tagInCamera, float confidence, Pose cameraPose, int markerId = 0, float reprojError = -1f)
        {
            totalObservationsReceived++;

            if (confidence < minConfidence)
            {
                observationsRejectedConfidence++;
                return;
            }

            // Mark observation time (for visibility tracking)
            lastObservationTime = Time.realtimeSinceStartup;
            lastObservedMarkerId = markerId;

            Pose newWorldFromSite = ComputeWorldFromSite(tagInCamera, cameraPose);

            if (siteFrameEverValidated)
            {
                // Already validated - refine pose with smoothing
                ProcessRefinementObservation(newWorldFromSite, markerId);
            }
            else
            {
                // Not yet validated - building initial lock
                ProcessInitialLockObservation(newWorldFromSite, markerId);
            }
        }

        /// <summary>
        /// Process observation when building initial lock (not yet validated).
        /// </summary>
        private void ProcessInitialLockObservation(Pose newWorldFromSite, int markerId)
        {
            if (!hasCandidate)
            {
                candidatePose = newWorldFromSite;
                hasCandidate = true;
                consecutiveGoodObservations = 1;

                if (enableDebugLogs)
                    Debug.Log($"[SiteFrame] First observation from marker #{markerId}. Lock sequence (1/{requiredConsecutiveObservations})");
                return;
            }

            float positionDelta = Vector3.Distance(newWorldFromSite.position, candidatePose.position);
            float rotationDelta = Quaternion.Angle(newWorldFromSite.rotation, candidatePose.rotation);

            if (positionDelta > maxJumpMeters || rotationDelta > maxJumpDegrees)
            {
                if (enableDebugLogs)
                    Debug.Log($"[SiteFrame] Lock reset - inconsistent. Delta: pos={positionDelta:F3}m, rot={rotationDelta:F1}deg");

                candidatePose = newWorldFromSite;
                consecutiveGoodObservations = 1;
                return;
            }

            consecutiveGoodObservations++;

            // Smooth the candidate
            candidatePose = new Pose(
                Vector3.Lerp(candidatePose.position, newWorldFromSite.position, positionSmoothingAlpha),
                Quaternion.Slerp(candidatePose.rotation, newWorldFromSite.rotation, rotationSmoothingAlpha)
            );

            if (enableDebugLogs)
                Debug.Log($"[SiteFrame] Lock progress from marker #{markerId}: {consecutiveGoodObservations}/{requiredConsecutiveObservations}");

            if (consecutiveGoodObservations >= requiredConsecutiveObservations)
            {
                // FIRST VALIDATION
                worldFromSite = candidatePose;
                siteFrameEverValidated = true;
                firstValidationTime = Time.realtimeSinceStartup;
                markerCurrentlyVisible = true;
                loggedRefiningAfterOcclusion = true;  // Prevent immediate "refining" log

                Debug.Log($"[SiteFrame] ★★★ SiteFrame VALID (first time) ★★★ Position: {worldFromSite.position:F2}, Marker #{markerId}");
                Debug.Log($"[SiteFrame] SiteFrame will remain VALID for the rest of the session. Pose refines when visible, holds when occluded.");

                UpdateVisual();
            }
        }

        /// <summary>
        /// Process observation when already validated - refine pose with smoothing.
        /// </summary>
        private void ProcessRefinementObservation(Pose newWorldFromSite, int markerId)
        {
            float positionDelta = Vector3.Distance(newWorldFromSite.position, worldFromSite.position);
            float rotationDelta = Quaternion.Angle(newWorldFromSite.rotation, worldFromSite.rotation);

            // Check for outliers/jumps
            if (positionDelta > maxJumpMeters || rotationDelta > maxJumpDegrees)
            {
                observationsRejectedJump++;

                if (enableDebugLogs && Time.frameCount % 60 == 0)
                    Debug.Log($"[SiteFrame] Outlier rejected: pos={positionDelta:F3}m, rot={rotationDelta:F1}deg (thresh: {maxJumpMeters}m/{maxJumpDegrees}deg)");

                return;
            }

            // Apply smoothed update
            Vector3 smoothedPosition = Vector3.Lerp(worldFromSite.position, newWorldFromSite.position, positionSmoothingAlpha);
            Quaternion smoothedRotation = Quaternion.Slerp(worldFromSite.rotation, newWorldFromSite.rotation, rotationSmoothingAlpha);
            worldFromSite = new Pose(smoothedPosition, smoothedRotation);

            if (enableDebugLogs && Time.frameCount % 120 == 0)
                Debug.Log($"[SiteFrame] Pose refined. Delta: pos={positionDelta:F4}m, rot={rotationDelta:F2}deg");

            UpdateVisual();
        }

        private Pose ComputeWorldFromSite(Pose tagInCamera, Pose cameraPose)
        {
            Pose worldFromTag = MultiplyPoses(cameraPose, tagInCamera);
            return worldFromTag;
        }

        // ============================================================
        // COORDINATE TRANSFORMS
        // ============================================================

        public Pose SitePoseFromWorldPose(Pose worldPose)
        {
            if (!siteFrameEverValidated)
            {
                Debug.LogWarning("[SiteFrame] SitePoseFromWorldPose called but SiteFrame not yet valid!");
                return worldPose;
            }

            Pose siteFromWorld = InversePose(worldFromSite);
            return MultiplyPoses(siteFromWorld, worldPose);
        }

        public Pose WorldPoseFromSitePose(Pose sitePose)
        {
            if (!siteFrameEverValidated)
            {
                Debug.LogWarning("[SiteFrame] WorldPoseFromSitePose called but SiteFrame not yet valid!");
                return sitePose;
            }

            return MultiplyPoses(worldFromSite, sitePose);
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        /// <summary>
        /// Full reset - clears validation state. Use only if you truly need to restart.
        /// </summary>
        public void Reset()
        {
            siteFrameEverValidated = false;
            firstValidationTime = 0f;
            markerCurrentlyVisible = false;
            loggedOcclusionHold = false;
            loggedRefiningAfterOcclusion = false;

            hasCandidate = false;
            consecutiveGoodObservations = 0;
            candidatePose = Pose.identity;
            worldFromSite = Pose.identity;

            totalObservationsReceived = 0;
            observationsRejectedJump = 0;
            observationsRejectedConfidence = 0;

            UpdateVisual();
            Debug.Log("[SiteFrame] FULL RESET - will re-acquire on next marker detection");
        }

        /// <summary>
        /// Invalidate is a no-op after first validation (SiteFrame never invalidates).
        /// Kept for API compatibility.
        /// </summary>
        public void Invalidate()
        {
            if (siteFrameEverValidated)
            {
                Debug.Log("[SiteFrame] Invalidate() called but SiteFrame is permanently valid - ignoring");
                return;
            }

            hasCandidate = false;
            consecutiveGoodObservations = 0;
            candidatePose = Pose.identity;
            UpdateVisual();
        }

        // ============================================================
        // INTERNAL METHODS
        // ============================================================

        private void UpdateVisual()
        {
            if (siteFrameVisual == null) return;

            if (siteFrameEverValidated)
            {
                if (!siteFrameVisual.gameObject.activeSelf)
                {
                    siteFrameVisual.gameObject.SetActive(true);
                    Debug.Log($"[SiteFrame] VISUAL ACTIVATED at: {worldFromSite.position:F2}");
                }
                siteFrameVisual.SetPositionAndRotation(worldFromSite.position, worldFromSite.rotation);
            }
            else
            {
                if (siteFrameVisual.gameObject.activeSelf)
                {
                    siteFrameVisual.gameObject.SetActive(false);
                    Debug.Log("[SiteFrame] VISUAL DEACTIVATED");
                }
            }
        }

        private static Pose InversePose(Pose pose)
        {
            Quaternion invRot = Quaternion.Inverse(pose.rotation);
            Vector3 invPos = invRot * (-pose.position);
            return new Pose(invPos, invRot);
        }

        private static Pose MultiplyPoses(Pose a, Pose b)
        {
            Vector3 pos = a.position + a.rotation * b.position;
            Quaternion rot = a.rotation * b.rotation;
            return new Pose(pos, rot);
        }

        // ============================================================
        // DEBUG GUI
        // ============================================================

        private void OnGUI()
        {
            if (!enableDebugLogs) return;

            GUILayout.BeginArea(new Rect(10, 440, 400, 180));
            GUILayout.Label("=== SITE FRAME ===");

            string validStatus = siteFrameEverValidated ? "★ VALID" : "Not yet valid";
            string visibilityStatus = markerCurrentlyVisible ? "Marker VISIBLE (refining)" : "Marker OCCLUDED (holding)";

            GUILayout.Label($"Status: {validStatus}");

            if (siteFrameEverValidated)
            {
                GUILayout.Label($"{visibilityStatus}");
                GUILayout.Label($"Last seen: {SecondsSinceLastSeen:F1}s ago | Marker #{lastObservedMarkerId}");
                GUILayout.Label($"Pos: {worldFromSite.position:F2}");

                float validDuration = Time.realtimeSinceStartup - firstValidationTime;
                GUILayout.Label($"Valid for: {validDuration:F0}s");
            }
            else
            {
                GUILayout.Label($"Lock progress: {consecutiveGoodObservations}/{requiredConsecutiveObservations}");
            }

            GUILayout.Label($"Observations: {totalObservationsReceived} | Rejected: jump={observationsRejectedJump}, conf={observationsRejectedConfidence}");
            GUILayout.EndArea();
        }
    }
}