using UnityEngine;
using UnityEngine.Events;
using PassthroughCameraSamples;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ARObjectDetection
{
    public class ARDetectionManager : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private DetectionConfig config;
        [SerializeField] private TrackerConfig trackerConfig;

        [Header("References")]
        [SerializeField] private WebCamTextureManager webCamTextureManager;
        [SerializeField] private DetectionVisualizer visualizer;
        [SerializeField] private DetectionVisualizer3D visualizer3D;
        [SerializeField] private ObjectTracker tracker;
        [SerializeField] private DepthAnchorSystem depthAnchorSystem;

        [Header("SiteFrame (Shared Coordinate Frame)")]
        [Tooltip("Optional SiteFrameManager for shared coordinate frame support")]
        [SerializeField] private SiteFrameManager siteFrameManager;

        [Tooltip("Enable processing of fiducial observations for SiteFrame")]
        [SerializeField] private bool enableSiteFrame = true;

        [Tooltip("Enable verbose diagnostic logging for SiteFrame debugging")]
        [SerializeField] private bool verboseSiteFrameLogs = true;

        [Header("Visualization Options")]
        [Tooltip("Enable 3D bounding box visualization")]
        [SerializeField] private bool enable3DBoundingBoxes = false;

        [Tooltip("Enable depth-based anchor placement (most accurate)")]
        [SerializeField] private bool enableDepthAnchors = true;

        [Header("Events")]
        public UnityEvent<DetectionResponse> OnDetectionReceived;
        public UnityEvent<string> OnDetectionError;

        /// <summary>
        /// Event fired when SiteFrame becomes valid (locked on fiducial).
        /// </summary>
        public UnityEvent OnSiteFrameLocked;

        /// <summary>
        /// Event fired when SiteFrame becomes invalid (lost fiducial).
        /// </summary>
        public UnityEvent OnSiteFrameLost;

        private FrameCaptureService frameCaptureService;
        private DetectionClient detectionClient;

        private bool isRunning = false;
        private bool serverHealthy = false;
        private DetectionMetrics metrics = new DetectionMetrics();

        private DetectionResponse latestDetection;
        private float lastFrameTime;
        private float lastSendTime;
        private float lastMetricsLogTime = 0f;

        // === SITEFRAME: Pose buffer to store camera poses by frameId ===
        private SiteFramePoseBuffer cameraPoseBuffer;
        private Transform captureCamera;
        private bool lastSiteFrameValid = false;

        // === SITEFRAME: Diagnostic counters ===
        private int fiducialsMissingPose = 0;
        private int fiducialsStaleResponse = 0;

        // === SITEFRAME: Last valid pose hint for ArUco disambiguation ===
        // Stores the most recent valid rvec/tvec to send to server for pose continuity
        private PoseHint? lastValidPoseHint = null;

        public DetectionMetrics Metrics => metrics;
        public DetectionResponse LatestDetection => latestDetection;
        public bool IsRunning => isRunning;
        public bool ServerHealthy => serverHealthy;
        public ObjectTracker Tracker => tracker;
        public DepthAnchorSystem DepthAnchors => depthAnchorSystem;
        public SiteFrameManager SiteFrame => siteFrameManager;

        private int lastAcceptedFrameId = -1;
        public float maxResponseAgeToAccept = 0.8f;

        // Runtime toggles
        public bool Enable3DBoundingBoxes
        {
            get => enable3DBoundingBoxes;
            set
            {
                enable3DBoundingBoxes = value;
                if (!value && visualizer3D != null)
                {
                    visualizer3D.ClearAllBoxes();
                }
            }
        }

        public bool EnableDepthAnchors
        {
            get => enableDepthAnchors;
            set
            {
                enableDepthAnchors = value;
                if (depthAnchorSystem != null)
                {
                    depthAnchorSystem.enabled = value;
                    if (!value)
                    {
                        depthAnchorSystem.ClearAllAnchors();
                    }
                }
            }
        }

        public bool EnableSiteFrame
        {
            get => enableSiteFrame;
            set => enableSiteFrame = value;
        }

        public bool VerboseSiteFrameLogs
        {
            get => verboseSiteFrameLogs;
            set => verboseSiteFrameLogs = value;
        }

        private void Awake()
        {
            if (config == null)
            {
                Debug.LogError("DetectionConfig is not assigned!");
                enabled = false;
                return;
            }

            if (trackerConfig == null)
            {
                Debug.LogError("TrackerConfig is not assigned!");
                enabled = false;
                return;
            }

            if (webCamTextureManager == null)
            {
                webCamTextureManager = FindFirstObjectByType<WebCamTextureManager>();
                if (webCamTextureManager == null)
                {
                    Debug.LogError("WebCamTextureManager not found in scene!");
                    enabled = false;
                    return;
                }
            }

            if (tracker == null)
            {
                tracker = GetComponent<ObjectTracker>();
                if (tracker == null)
                {
                    Debug.LogError("ObjectTracker component not found!");
                    enabled = false;
                    return;
                }
            }

            // Find depth anchor system
            if (depthAnchorSystem == null)
            {
                depthAnchorSystem = GetComponent<DepthAnchorSystem>();
                if (depthAnchorSystem == null)
                {
                    depthAnchorSystem = FindFirstObjectByType<DepthAnchorSystem>();
                }
            }

            // Find SiteFrameManager (optional)
            if (siteFrameManager == null)
            {
                siteFrameManager = GetComponent<SiteFrameManager>();
                if (siteFrameManager == null)
                {
                    siteFrameManager = FindFirstObjectByType<SiteFrameManager>();
                }
            }

            // Apply initial state
            if (depthAnchorSystem != null)
            {
                depthAnchorSystem.enabled = enableDepthAnchors;
            }

            frameCaptureService = new FrameCaptureService(config);
            detectionClient = new DetectionClient(config);

            // === SITEFRAME: Initialize pose buffer (160 entries, 5 second max age) ===
            cameraPoseBuffer = new SiteFramePoseBuffer(160, 5.0f);

            // Find capture camera
            if (OVRManager.instance != null)
            {
                captureCamera = OVRManager.instance.GetComponentInChildren<Camera>()?.transform;
            }
            if (captureCamera == null)
            {
                captureCamera = Camera.main?.transform;
            }

            string siteFrameStatus = siteFrameManager != null ? "enabled" : "not found (fiducial data will be ignored)";
            Debug.Log($"ARDetectionManager initialized with depth-based anchor placement. SiteFrame: {siteFrameStatus}");
        }

        private void Start()
        {
            StartCoroutine(CheckServerHealthAndStart());
        }

        private IEnumerator CheckServerHealthAndStart()
        {
            Debug.Log($"Checking server health at: {config.serverUrl}");

            yield return detectionClient.CheckServerHealth((healthy) =>
            {
                serverHealthy = healthy;
                if (healthy)
                {
                    Debug.Log("Server is healthy, starting detection");
                    StartDetection();
                }
                else
                {
                    Debug.LogError($"Server not reachable at {config.serverUrl}");
                }
            });
        }

        public void StartDetection()
        {
            if (isRunning)
            {
                Debug.LogWarning("Detection already running");
                return;
            }

            isRunning = true;
            metrics.Reset();
            cameraPoseBuffer?.Clear();
            fiducialsMissingPose = 0;
            fiducialsStaleResponse = 0;
            Debug.Log("Detection started");
        }

        public void StopDetection()
        {
            isRunning = false;
            Debug.Log("Detection stopped");
        }

        private void Update()
        {
            if (!isRunning || webCamTextureManager == null)
                return;

            WebCamTexture webCamTexture = webCamTextureManager.WebCamTexture;
            if (webCamTexture == null || !webCamTexture.isPlaying)
                return;

            if (Time.frameCount % 300 == 0)
            {
                Debug.Log($"[WebCam] Actual resolution: {webCamTexture.width}×{webCamTexture.height}");
            }

            if (Time.time - lastFrameTime > 0)
            {
                metrics.captureFrameRate = 1f / (Time.time - lastFrameTime);
            }
            lastFrameTime = Time.time;
            metrics.totalFramesCaptured++;

            if (detectionClient.GetPendingRequestCount() >= config.maxPendingRequests)
            {
                metrics.droppedFrames++;
                return;
            }

            if (frameCaptureService.ShouldCaptureFrame())
            {
                CapturedFrame capturedFrame = frameCaptureService.CaptureFrameWithMetadata(webCamTexture);

                if (capturedFrame != null && capturedFrame.jpegData != null && capturedFrame.jpegData.Length > 0)
                {
                    // === SITEFRAME: Store camera pose for this frameId BEFORE sending ===
                    StoreCameraPoseForFrame(capturedFrame.frameId);

                    metrics.framesSent++;

                    if (Time.time - lastSendTime > 0)
                    {
                        metrics.sendFrameRate = 1f / (Time.time - lastSendTime);
                    }
                    lastSendTime = Time.time;

                    StartCoroutine(SendFrameCoroutine(capturedFrame));
                }
            }

            // === SITEFRAME: Check for validity state changes to fire events ===
            CheckSiteFrameValidityEvents();
        }

        /// <summary>
        /// Store the current camera world pose for a given frameId.
        /// Called immediately before sending the frame to ensure we capture
        /// the exact pose at capture time.
        /// </summary>
        private void StoreCameraPoseForFrame(int frameId)
        {
            if (cameraPoseBuffer == null || captureCamera == null)
                return;

            Pose cameraPose = new Pose(captureCamera.position, captureCamera.rotation);
            cameraPoseBuffer.Store(frameId, cameraPose);
        }

        /// <summary>
        /// Check if SiteFrame validity has changed and fire appropriate events.
        /// </summary>
        private void CheckSiteFrameValidityEvents()
        {
            if (siteFrameManager == null)
                return;

            bool currentValid = siteFrameManager.IsValid;

            if (currentValid && !lastSiteFrameValid)
            {
                // Just became valid
                OnSiteFrameLocked?.Invoke();
            }
            else if (!currentValid && lastSiteFrameValid)
            {
                // Just became invalid
                OnSiteFrameLost?.Invoke();
            }

            lastSiteFrameValid = currentValid;
        }

        private void LateUpdate()
        {
            if (Time.time - lastMetricsLogTime > 2f)
            {
                lastMetricsLogTime = Time.time;

                if (config.enablePerformanceLogging)
                {
                    Debug.Log("=== DETECTION METRICS ===");
                    Debug.Log($"Avg Latency: {metrics.averageLatency:F3}s ({metrics.averageLatency * 1000:F0}ms)");
                    Debug.Log($"Send Rate: {metrics.sendFrameRate:F1} Hz");
                    Debug.Log($"Success: {metrics.successfulDetections}, Failed: {metrics.failedRequests}, Dropped: {metrics.droppedFrames}");
                    Debug.Log($"Pending Requests: {detectionClient.GetPendingRequestCount()}/{config.maxPendingRequests}");

                    if (tracker != null)
                    {
                        int activeCount = tracker.ActiveTrackCount;
                        int confirmedCount = tracker.ConfirmedTrackCount;
                        Debug.Log($"Tracks: {activeCount} active, {confirmedCount} confirmed");
                    }

                    if (depthAnchorSystem != null && enableDepthAnchors)
                    {
                        Debug.Log($"Anchors: {depthAnchorSystem.ActiveAnchorCount} active, " +
                                 $"Depth Hits: {depthAnchorSystem.SuccessfulDepthHits}, " +
                                 $"Fallbacks: {depthAnchorSystem.FallbacksUsed}");
                    }

                    // === SITEFRAME: Log status ===
                    if (siteFrameManager != null && enableSiteFrame)
                    {
                        string status = siteFrameManager.IsValid ? "VALID" : "INVALID";
                        Debug.Log($"SiteFrame: {status}, Fiducials: {metrics.fiducialObservationsReceived} received, " +
                                 $"{metrics.fiducialObservationsProcessed} processed, " +
                                 $"missingPose={fiducialsMissingPose}, stale={fiducialsStaleResponse}");

                        if (cameraPoseBuffer != null)
                        {
                            Debug.Log($"PoseBuffer: {cameraPoseBuffer.GetDiagnosticInfo()}");
                        }
                    }

                    Debug.Log("========================");
                }
            }
        }

        private IEnumerator SendFrameCoroutine(CapturedFrame capturedFrame)
        {
            float startTime = Time.time;

            // Pass pose hint for ArUco disambiguation if we have one
            PoseHint hint = lastValidPoseHint ?? PoseHint.Invalid;
            yield return detectionClient.SendFrameWithRetry(
                capturedFrame,
                hint,  // Sends X-Hint-Rvec and X-Hint-Tvec headers for ArUco disambiguation
                OnDetectionSuccess,
                OnDetectionFailed
            );

            float latency = Time.time - startTime;
            metrics.RecordLatency(latency);
        }

        private void OnDetectionSuccess(DetectionResponse response)
        {
            float responseAge = Time.realtimeSinceStartup - response.capture_time;

            if (responseAge > maxResponseAgeToAccept)
            {
                Debug.LogWarning($"[ARDetectionManager] Dropping stale response frame={response.frame_id} age={responseAge:F3}s");
                return;
            }

            if (response.frame_id <= lastAcceptedFrameId)
            {
                Debug.LogWarning($"[ARDetectionManager] Dropping out-of-order response frame={response.frame_id}");
                return;
            }
            lastAcceptedFrameId = response.frame_id;

            if (responseAge > 0.5f)
            {
                metrics.lateResponses++;
            }

            latestDetection = response;
            metrics.successfulDetections++;
            metrics.currentDetectionCount = response.count;

            // Class filter
            if (config.enableClassFilter && config.classFilter != null && config.classFilter.Length > 0)
            {
                response.detections.RemoveAll(d =>
                {
                    string detectionClass = d.class_name.ToLower().Trim();
                    foreach (string allowedClass in config.classFilter)
                    {
                        if (detectionClass == allowedClass.ToLower().Trim())
                            return false;
                    }
                    return true;
                });
                response.count = response.detections.Count;
            }

            // Confidence filter
            if (config.confidenceThreshold > 0.25f)
            {
                response.detections.RemoveAll(d => d.confidence < config.confidenceThreshold);
                response.count = response.detections.Count;
            }

            // Process through tracker
            if (tracker != null)
            {
                tracker.ProcessDetections(response);
            }

            // 3D Bounding boxes (optional)
            if (enable3DBoundingBoxes && visualizer3D != null && tracker != null)
            {
                var visibleTracks = tracker.ActiveTracks
                    .Where(t => t.state != TrackState.Lost)
                    .ToList();

                if (visibleTracks.Count > 0)
                {
                    visualizer3D.ShowTrackedObjects(visibleTracks);
                }
            }
            else if (!enable3DBoundingBoxes && visualizer3D != null)
            {
                visualizer3D.ClearAllBoxes();
            }

            // === SITEFRAME: Process fiducial observation if present ===
            ProcessFiducialObservation(response);

            // NOTE: DepthAnchorSystem handles anchor placement automatically in its Update()
            // by reading from ObjectTracker.ActiveTracks

            OnDetectionReceived?.Invoke(response);
        }

        /// <summary>
        /// Process fiducial observation from detection response for SiteFrame.
        /// This retrieves the camera pose at capture time using the frameId,
        /// converts the OpenCV pose to Unity, and submits to SiteFrameManager.
        /// 
        /// Includes comprehensive diagnostic logging when verboseSiteFrameLogs is enabled.
        /// </summary>
        private void ProcessFiducialObservation(DetectionResponse response)
        {
            // Skip if SiteFrame is disabled or not available
            if (!enableSiteFrame || siteFrameManager == null)
                return;

            // Check if fiducial data exists and is valid
            if (response.fiducial == null || !response.fiducial.HasValidPose())
                return;

            metrics.fiducialObservationsReceived++;

            float currentTime = Time.realtimeSinceStartup;
            float responseAge = currentTime - response.capture_time;

            // === DIAGNOSTIC: Prepare logging data ===
            int frameId = response.frame_id;
            bool bufferHasPose = cameraPoseBuffer.TryGetEntry(frameId, out PoseBufferEntry bufferEntry);
            float bufferPoseAge = bufferHasPose ? (currentTime - bufferEntry.CaptureTime) : -1f;

            // Fiducial quality metrics
            int markerId = response.fiducial.id;
            float confidence = response.fiducial.GetEffectiveConfidence();
            float reprojError = response.fiducial.reproj_error;
            float tvecNorm = response.fiducial.GetDistance();
            int cornerCount = response.fiducial.corner_count;

            // === BEHAVIOR RULE: Skip if buffer doesn't have pose ===
            if (!bufferHasPose)
            {
                fiducialsMissingPose++;

                if (verboseSiteFrameLogs)
                {
                    Debug.LogWarning($"[SiteFrame] Missing captured pose for frame_id={frameId}, skipping fiducial. " +
                                   $"Buffer: {cameraPoseBuffer.GetDiagnosticInfo()}");
                }
                return;
            }

            Pose cameraWorldPoseAtCapture = bufferEntry.CameraPose;

            // Get current camera pose for comparison
            Pose currentCameraPose = captureCamera != null
                ? new Pose(captureCamera.position, captureCamera.rotation)
                : Pose.identity;

            // === POSE MISMATCH METRICS ===
            float deltaPosCurrentVsCaptured = Vector3.Distance(currentCameraPose.position, cameraWorldPoseAtCapture.position);
            float deltaRotCurrentVsCaptured = Quaternion.Angle(currentCameraPose.rotation, cameraWorldPoseAtCapture.rotation);

            // Convert OpenCV rvec/tvec to Unity Pose
            Pose tagInCamera = OpenCvPoseConversion.TagInCamera_FromOpenCvRvecTvec(
                response.fiducial.rvec,
                response.fiducial.tvec
            );

            // Pre-compute what the result will be for logging
            Pose newWorldFromSite = OpenCvPoseConversion.ComputeSiteFrameWorldPose(tagInCamera, cameraWorldPoseAtCapture);

            // === DETAILED COORDINATE SYSTEM DEBUG ===
            if (verboseSiteFrameLogs)
            {
                // Log raw server data
                var rvec = response.fiducial.rvec;
                var tvec = response.fiducial.tvec;
                Debug.Log($"[SiteFrame] RAW: rvec=[{rvec[0]:F4}, {rvec[1]:F4}, {rvec[2]:F4}], " +
                         $"tvec=[{tvec[0]:F4}, {tvec[1]:F4}, {tvec[2]:F4}]");

                // Log converted camera-space pose
                Debug.Log($"[SiteFrame] tagInCamera: pos={tagInCamera.position:F3}, euler={tagInCamera.rotation.eulerAngles:F1}");

                // Log camera world pose at capture
                Debug.Log($"[SiteFrame] cameraWorld: pos={cameraWorldPoseAtCapture.position:F3}, euler={cameraWorldPoseAtCapture.rotation.eulerAngles:F1}");

                // Log resulting world pose
                Debug.Log($"[SiteFrame] Computed world pose: pos={newWorldFromSite.position:F3}, euler={newWorldFromSite.rotation.eulerAngles:F1}");
            }

            // === SITEFRAME STABILITY METRICS ===
            bool siteFrameValid = siteFrameManager.IsValid;
            int lockProgress = siteFrameManager.ConsecutiveGoodObservations;
            int requiredForLock = 8; // Should match SiteFrameManager.requiredConsecutiveObservations

            // Check if this would trigger a jump rejection
            float jumpPosM = 0f;
            float jumpRotDeg = 0f;
            bool wouldTriggerJump = false;

            if (siteFrameValid)
            {
                Pose currentWorldFromSite = siteFrameManager.WorldFromSite;
                jumpPosM = Vector3.Distance(newWorldFromSite.position, currentWorldFromSite.position);
                jumpRotDeg = Quaternion.Angle(newWorldFromSite.rotation, currentWorldFromSite.rotation);
                wouldTriggerJump = jumpPosM > 0.5f || jumpRotDeg > 45f; // Match SiteFrameManager thresholds
            }

            // === VERBOSE STRUCTURED LOG ===
            if (verboseSiteFrameLogs)
            {
                string jumpInfo = wouldTriggerJump
                    ? $", JUMP: pos={jumpPosM:F3}m rot={jumpRotDeg:F1}deg (thresh: 0.5m/45deg)"
                    : "";

                Debug.Log($"[SiteFrame:Fiducial] frame_id={frameId}, buffer_has_pose=true, buffer_pose_age={bufferPoseAge:F3}s, " +
                         $"inference_time={response.inference_time:F3}s | " +
                         $"delta_pos_current_vs_captured={deltaPosCurrentVsCaptured:F3}m, delta_rot_current_vs_captured={deltaRotCurrentVsCaptured:F1}deg | " +
                         $"marker_id={markerId}, confidence={confidence:F2}, reproj_error={reprojError:F2}px, tvec_norm={tvecNorm:F3}m, corners={cornerCount} | " +
                         $"siteframe_valid={siteFrameValid}, lock_progress={lockProgress}/{requiredForLock}{jumpInfo}");
            }

            // Submit to SiteFrameManager (now includes reproj_error for quality gating)
            siteFrameManager.SubmitTagPoseInCamera(
                tagInCamera,
                confidence,
                cameraWorldPoseAtCapture,
                markerId,
                reprojError  // Pass reproj_error for outlier quality assessment
            );

            // === UPDATE POSE HINT FOR NEXT FRAME ===
            // Store valid rvec/tvec for server-side ArUco disambiguation continuity.
            // Only update if this was a reasonable quality observation (not obviously bad).
            if (confidence >= 0.2f && reprojError < 10f)
            {
                lastValidPoseHint = new PoseHint
                {
                    rvec = response.fiducial.rvec,
                    tvec = response.fiducial.tvec
                };

                if (verboseSiteFrameLogs && Time.frameCount % 60 == 0)
                {
                    Debug.Log($"[SiteFrame] Updated pose hint: rvec=[{response.fiducial.rvec[0]:F4}, {response.fiducial.rvec[1]:F4}, {response.fiducial.rvec[2]:F4}]");
                }
            }

            metrics.fiducialObservationsProcessed++;
        }

        private void OnDetectionFailed(string error)
        {
            metrics.failedRequests++;
            Debug.LogError($"Detection failed: {error}");
            OnDetectionError?.Invoke(error);
        }

        private void OnDestroy()
        {
            if (frameCaptureService != null)
            {
                frameCaptureService.Dispose();
            }
            StopDetection();
        }

        private void OnDisable()
        {
            StopDetection();
        }

        public void RestartDetection()
        {
            StopDetection();
            cameraPoseBuffer?.Clear();
            StartCoroutine(CheckServerHealthAndStart());
        }

        public TrackedObject GetTrackById(int trackId)
        {
            return tracker?.ActiveTracks.FirstOrDefault(t => t.id == trackId);
        }

        public List<TrackedObject> GetTracksByClass(string className)
        {
            if (tracker == null) return new List<TrackedObject>();
            return tracker.ActiveTracks.Where(t => t.className == className).ToList();
        }

        // ============================================================
        // SITEFRAME CONVENIENCE METHODS
        // ============================================================

        /// <summary>
        /// Convert a world-space pose to SiteFrame coordinates.
        /// Returns the input pose unchanged if SiteFrame is not valid.
        /// </summary>
        public Pose WorldToSitePose(Pose worldPose)
        {
            if (siteFrameManager == null || !siteFrameManager.IsValid)
                return worldPose;

            return siteFrameManager.SitePoseFromWorldPose(worldPose);
        }

        /// <summary>
        /// Convert a SiteFrame-space pose to world coordinates.
        /// Returns the input pose unchanged if SiteFrame is not valid.
        /// </summary>
        public Pose SiteToWorldPose(Pose sitePose)
        {
            if (siteFrameManager == null || !siteFrameManager.IsValid)
                return sitePose;

            return siteFrameManager.WorldPoseFromSitePose(sitePose);
        }

        /// <summary>
        /// Check if SiteFrame is currently valid.
        /// </summary>
        public bool IsSiteFrameValid => siteFrameManager != null && siteFrameManager.IsValid;
    }
}