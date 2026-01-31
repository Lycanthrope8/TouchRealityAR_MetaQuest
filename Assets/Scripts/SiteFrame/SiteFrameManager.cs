using UnityEngine;

namespace ARObjectDetection
{
    /// <summary>
    /// SiteFrameManager - Shared Coordinate Frame for Multi-Device AR
    /// 
    /// Estimates and maintains a transform T_world_site (pose of shared SiteFrame in Unity world),
    /// derived from fiducial marker observations.
    /// </summary>
    public class SiteFrameManager : MonoBehaviour
    {
        [Header("Validity Settings")]
        [Tooltip("Number of consecutive good observations required before SiteFrame becomes valid")]
        [Range(1, 20)]
        [SerializeField] private int requiredConsecutiveObservations = 8;

        [Tooltip("(DEPRECATED - no longer used for invalidation) Previously controlled auto-invalidation timeout. Locked SiteFrame now persists indefinitely until XR tracking reset or explicit Invalidate() call.")]
        [Range(1f, 120f)]
        [SerializeField] private float graceSeconds = 15f;

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

        [Header("Jump Rejection / Outlier Hysteresis")]
        [Tooltip("Maximum position jump allowed (meters). Larger jumps are treated as outliers.")]
        [SerializeField] private float maxJumpMeters = 0.5f;

        [Tooltip("Maximum rotation jump allowed (degrees). Larger jumps are treated as outliers.")]
        [SerializeField] private float maxJumpDegrees = 45f;

        [Tooltip("Number of consecutive high-quality outliers required before invalidation")]
        [Range(2, 10)]
        [SerializeField] private int outlierCountBeforeInvalidate = 3;

        [Tooltip("Minimum confidence for an outlier to count toward invalidation")]
        [Range(0f, 1f)]
        [SerializeField] private float outlierMinConfidence = 0.5f;

        [Tooltip("Maximum reproj error for an outlier to count toward invalidation (pixels)")]
        [SerializeField] private float outlierMaxReprojError = 3.0f;

        [Header("Debug Visualization")]
        [Tooltip("Optional transform to visualize the SiteFrame pose")]
        [SerializeField] private Transform siteFrameVisual;

        [Tooltip("Enable debug logging")]
        [SerializeField] private bool enableDebugLogs = true;

        // ============================================================
        // INTERNAL STATE
        // ============================================================

        private Pose worldFromSite = Pose.identity;
        private bool isValid = false;
        private int consecutiveGoodObservations = 0;
        private float lastObservationTime = float.NegativeInfinity;
        private int lastObservedMarkerId = -1;

        private Pose candidatePose = Pose.identity;
        private bool hasCandidate = false;

        private int totalObservationsReceived = 0;
        private int observationsRejectedJump = 0;
        private int observationsRejectedConfidence = 0;
        private int lockCount = 0;
        private int relockCount = 0;

        // Outlier hysteresis state
        private int consecutiveOutlierCount = 0;
        private int outliersIgnored = 0;
        private Pose lastOutlierPose = Pose.identity;
        private float lastOutlierConfidence = 0f;
        private float lastOutlierReprojError = 0f;

        // ============================================================
        // PUBLIC PROPERTIES
        // ============================================================

        public bool IsValid => isValid;
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
                Debug.LogWarning("[SiteFrame] No siteFrameVisual assigned! Assign a Transform in the Inspector to see the SiteFrame position.");
            }
        }

        private void OnEnable()
        {
            // Subscribe to XR tracking origin changes (Quest recenter, tracking lost/regained, etc.)
            // This uses Unity's XR subsystem events when available
            UnityEngine.XR.XRDevice.deviceLoaded += OnXRDeviceLoaded;

            // For Meta Quest / OVR: subscribe to recenter events if OVRManager is available
            SubscribeToOVREvents();
        }

        private void OnDisable()
        {
            UnityEngine.XR.XRDevice.deviceLoaded -= OnXRDeviceLoaded;
            UnsubscribeFromOVREvents();
        }

        private void SubscribeToOVREvents()
        {
            // Try to find OVRManager and subscribe to tracking events
            // This is done via reflection to avoid hard dependency on Oculus SDK
            var ovrManagerType = System.Type.GetType("OVRManager, Assembly-CSharp");
            if (ovrManagerType != null)
            {
                var displayEvent = ovrManagerType.GetEvent("display_TrackingRecovered");
                var lostEvent = ovrManagerType.GetEvent("display_TrackingLost");
                var recenterEvent = ovrManagerType.GetEvent("HMDUnmounted");

                if (displayEvent != null || lostEvent != null)
                {
                    Debug.Log("[SiteFrame] OVRManager detected - subscribing to tracking events");
                }
            }

            // Alternative: Use Application.onBeforeRender or InputTracking events
            UnityEngine.XR.InputTracking.trackingAcquired += OnTrackingAcquired;
            UnityEngine.XR.InputTracking.trackingLost += OnTrackingLost;
        }

        private void UnsubscribeFromOVREvents()
        {
            UnityEngine.XR.InputTracking.trackingAcquired -= OnTrackingAcquired;
            UnityEngine.XR.InputTracking.trackingLost -= OnTrackingLost;
        }

        private void OnXRDeviceLoaded(string deviceName)
        {
            if (isValid)
            {
                Debug.LogWarning($"[SiteFrame] XR device loaded/changed ({deviceName}). Invalidating SiteFrame.");
                InvalidateSiteFrame();
            }
        }

        private void OnTrackingAcquired(UnityEngine.XR.XRNodeState state)
        {
            // Tracking recovered - but our world reference may have shifted
            // Only invalidate if we were previously locked and tracking was lost
            if (enableDebugLogs)
            {
                Debug.Log($"[SiteFrame] XR tracking acquired for {state.nodeType}");
            }
        }

        private void OnTrackingLost(UnityEngine.XR.XRNodeState state)
        {
            // Head tracking lost - this could mean world origin will shift
            if (state.nodeType == UnityEngine.XR.XRNode.Head ||
                state.nodeType == UnityEngine.XR.XRNode.CenterEye)
            {
                if (isValid)
                {
                    Debug.LogWarning($"[SiteFrame] XR head tracking LOST. Invalidating SiteFrame to prevent drift.");
                    InvalidateSiteFrame();
                }
            }
        }

        /// <summary>
        /// Call this method when the XR tracking origin is reset/recentered.
        /// This should be hooked up to OVRManager.TrackingRecovered or similar events
        /// in the scene's XR management script.
        /// </summary>
        public void OnTrackingOriginReset()
        {
            if (isValid)
            {
                Debug.LogWarning("[SiteFrame] Tracking origin reset detected. Invalidating SiteFrame.");
                InvalidateSiteFrame();
            }
        }

        private void Update()
        {
            // NOTE: We intentionally do NOT invalidate a locked SiteFrame due to marker absence.
            // Once locked, the SiteFrame persists until:
            // 1. XR tracking is lost/reset (handled via OnTrackingOriginChanged or manual call)
            // 2. Multiple consecutive high-quality outliers indicate marker actually moved
            // 3. Explicit user-triggered relock via Invalidate() or Reset()

            // Log periodic status when locked but marker not visible
            if (isValid && SecondsSinceLastSeen > 5.0f)
            {
                // Log once every 10 seconds to confirm we're holding the pose
                if (enableDebugLogs && Mathf.FloorToInt(SecondsSinceLastSeen) % 10 == 0 &&
                    Mathf.FloorToInt(SecondsSinceLastSeen - Time.deltaTime) % 10 != 0)
                {
                    Debug.Log($"[SiteFrame] LOCKED: no marker observed for {SecondsSinceLastSeen:F0}s; holding last SiteFrame pose");
                }
            }

            UpdateVisual();
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        public void SubmitTagPoseInCamera(Pose tagInCamera, float confidence, Pose cameraWorldPoseAtCapture, int markerId = 0, float reprojError = 0f)
        {
            totalObservationsReceived++;

            if (confidence < minConfidence)
            {
                observationsRejectedConfidence++;
                if (enableDebugLogs)
                {
                    Debug.Log($"[SiteFrame] Rejected observation: confidence {confidence:F2} < {minConfidence:F2}");
                }
                return;
            }

            Pose newWorldFromSite = OpenCvPoseConversion.ComputeSiteFrameWorldPose(tagInCamera, cameraWorldPoseAtCapture);

            if (enableDebugLogs && totalObservationsReceived <= 5)
            {
                Debug.Log($"[SiteFrame] Computed world pose: pos={newWorldFromSite.position}, euler={newWorldFromSite.rotation.eulerAngles}");
            }

            if (isValid)
            {
                float positionDelta = Vector3.Distance(newWorldFromSite.position, worldFromSite.position);
                float rotationDelta = Quaternion.Angle(newWorldFromSite.rotation, worldFromSite.rotation);

                bool isOutlier = positionDelta > maxJumpMeters || rotationDelta > maxJumpDegrees;

                if (isOutlier)
                {
                    observationsRejectedJump++;

                    // Store outlier info for potential invalidation
                    lastOutlierPose = newWorldFromSite;
                    lastOutlierConfidence = confidence;
                    lastOutlierReprojError = reprojError;

                    // Check if this is a "high-quality" outlier that could indicate real pose change
                    bool highQualityOutlier = confidence >= outlierMinConfidence && reprojError <= outlierMaxReprojError;

                    if (highQualityOutlier)
                    {
                        consecutiveOutlierCount++;

                        if (enableDebugLogs)
                        {
                            Debug.LogWarning($"[SiteFrame] OUTLIER ignored (HIGH-QUALITY): pos={positionDelta:F3}m, rot={rotationDelta:F1}deg, " +
                                           $"conf={confidence:F2}, reproj={reprojError:F2}px, count={consecutiveOutlierCount}/{outlierCountBeforeInvalidate}");
                        }

                        // If we've seen enough consecutive high-quality outliers, the world has actually changed
                        if (consecutiveOutlierCount >= outlierCountBeforeInvalidate)
                        {
                            if (enableDebugLogs)
                            {
                                Debug.LogWarning($"[SiteFrame] INVALIDATED after {consecutiveOutlierCount} consecutive high-quality outliers. Forcing relock.");
                            }

                            InvalidateSiteFrame();
                            relockCount++;

                            // Start new lock sequence with the outlier pose
                            candidatePose = newWorldFromSite;
                            hasCandidate = true;
                            consecutiveGoodObservations = 1;
                            lastObservedMarkerId = markerId;
                            lastObservationTime = Time.realtimeSinceStartup;
                            consecutiveOutlierCount = 0;
                        }
                    }
                    else
                    {
                        // Low-quality outlier: ignore completely, don't count toward invalidation
                        outliersIgnored++;

                        if (enableDebugLogs)
                        {
                            Debug.Log($"[SiteFrame] OUTLIER ignored (low-quality): pos={positionDelta:F3}m, rot={rotationDelta:F1}deg, " +
                                     $"conf={confidence:F2}, reproj={reprojError:F2}px (need conf>={outlierMinConfidence:F2}, reproj<={outlierMaxReprojError:F1})");
                        }
                    }
                    return;
                }

                // Good observation - reset outlier counter and apply smoothed update
                consecutiveOutlierCount = 0;
                ApplySmoothedUpdate(newWorldFromSite);
                lastObservationTime = Time.realtimeSinceStartup;
                lastObservedMarkerId = markerId;

                if (enableDebugLogs && totalObservationsReceived % 30 == 0)
                {
                    Debug.Log($"[SiteFrame] Updated (smooth). Delta: pos={positionDelta:F4}m, rot={rotationDelta:F2}deg");
                }
            }
            else
            {
                if (!hasCandidate)
                {
                    candidatePose = newWorldFromSite;
                    hasCandidate = true;
                    consecutiveGoodObservations = 1;
                    lastObservedMarkerId = markerId;
                    lastObservationTime = Time.realtimeSinceStartup;

                    if (enableDebugLogs)
                    {
                        Debug.Log($"[SiteFrame] First observation. Lock sequence (1/{requiredConsecutiveObservations})");
                    }
                    return;
                }

                float positionDelta = Vector3.Distance(newWorldFromSite.position, candidatePose.position);
                float rotationDelta = Quaternion.Angle(newWorldFromSite.rotation, candidatePose.rotation);

                if (positionDelta > maxJumpMeters || rotationDelta > maxJumpDegrees)
                {
                    if (enableDebugLogs)
                    {
                        Debug.Log($"[SiteFrame] Lock reset - inconsistent. Delta: pos={positionDelta:F3}m, rot={rotationDelta:F1}deg");
                    }

                    candidatePose = newWorldFromSite;
                    consecutiveGoodObservations = 1;
                    lastObservedMarkerId = markerId;
                    lastObservationTime = Time.realtimeSinceStartup;
                    return;
                }

                consecutiveGoodObservations++;
                lastObservationTime = Time.realtimeSinceStartup;
                lastObservedMarkerId = markerId;

                candidatePose = new Pose(
                    Vector3.Lerp(candidatePose.position, newWorldFromSite.position, positionSmoothingAlpha),
                    Quaternion.Slerp(candidatePose.rotation, newWorldFromSite.rotation, rotationSmoothingAlpha)
                );

                if (enableDebugLogs)
                {
                    Debug.Log($"[SiteFrame] Lock progress: {consecutiveGoodObservations}/{requiredConsecutiveObservations}");
                }

                if (consecutiveGoodObservations >= requiredConsecutiveObservations)
                {
                    worldFromSite = candidatePose;
                    isValid = true;
                    lockCount++;
                    consecutiveOutlierCount = 0;

                    Debug.Log($"[SiteFrame] LOCKED! Position: {worldFromSite.position}, Marker ID: {markerId}");

                    UpdateVisual();
                }
            }
        }

        public Pose SitePoseFromWorldPose(Pose worldPose)
        {
            if (!isValid)
            {
                Debug.LogWarning("[SiteFrame] SitePoseFromWorldPose called but SiteFrame is not valid!");
                return worldPose;
            }

            Pose siteFromWorld = InversePose(worldFromSite);
            return MultiplyPoses(siteFromWorld, worldPose);
        }

        public Pose WorldPoseFromSitePose(Pose sitePose)
        {
            if (!isValid)
            {
                Debug.LogWarning("[SiteFrame] WorldPoseFromSitePose called but SiteFrame is not valid!");
                return sitePose;
            }

            return MultiplyPoses(worldFromSite, sitePose);
        }

        public void Invalidate()
        {
            InvalidateSiteFrame();
        }

        public void Reset()
        {
            InvalidateSiteFrame();
            totalObservationsReceived = 0;
            observationsRejectedJump = 0;
            observationsRejectedConfidence = 0;
            lockCount = 0;
            relockCount = 0;
            consecutiveOutlierCount = 0;
            outliersIgnored = 0;
        }

        // ============================================================
        // INTERNAL METHODS
        // ============================================================

        private void InvalidateSiteFrame()
        {
            isValid = false;
            hasCandidate = false;
            consecutiveGoodObservations = 0;
            consecutiveOutlierCount = 0;
            candidatePose = Pose.identity;

            if (enableDebugLogs)
            {
                Debug.Log("[SiteFrame] Invalidated");
            }

            UpdateVisual();
        }

        private void ApplySmoothedUpdate(Pose newPose)
        {
            Vector3 smoothedPosition = Vector3.Lerp(
                worldFromSite.position,
                newPose.position,
                positionSmoothingAlpha
            );

            Quaternion smoothedRotation = Quaternion.Slerp(
                worldFromSite.rotation,
                newPose.rotation,
                rotationSmoothingAlpha
            );

            worldFromSite = new Pose(smoothedPosition, smoothedRotation);
        }

        private void UpdateVisual()
        {
            if (siteFrameVisual == null) return;

            if (isValid)
            {
                if (!siteFrameVisual.gameObject.activeSelf)
                {
                    siteFrameVisual.gameObject.SetActive(true);
                    Debug.Log($"[SiteFrame] VISUAL ACTIVATED at: {worldFromSite.position}");
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

        private void OnGUI()
        {
            if (!enableDebugLogs) return;

            GUILayout.BeginArea(new Rect(10, 440, 400, 180));
            GUILayout.Label("=== SITE FRAME ===");
            GUILayout.Label($"Valid: {(isValid ? "YES" : "NO")} | Marker: {lastObservedMarkerId}");
            GUILayout.Label($"Visual: {(siteFrameVisual != null ? (siteFrameVisual.gameObject.activeSelf ? "VISIBLE" : "hidden") : "NOT ASSIGNED")}");

            if (isValid)
            {
                GUILayout.Label($"Last seen: {SecondsSinceLastSeen:F1}s ago");
                GUILayout.Label($"Pos: {worldFromSite.position:F2}");
                GUILayout.Label($"Outlier streak: {consecutiveOutlierCount}/{outlierCountBeforeInvalidate}");
            }
            else
            {
                GUILayout.Label($"Lock: {consecutiveGoodObservations}/{requiredConsecutiveObservations}");
            }

            GUILayout.Label($"Obs: {totalObservationsReceived} | Locks: {lockCount} | Relocks: {relockCount}");
            GUILayout.Label($"Outliers ignored: {outliersIgnored} | Jump rejections: {observationsRejectedJump}");
            GUILayout.EndArea();
        }
    }
}