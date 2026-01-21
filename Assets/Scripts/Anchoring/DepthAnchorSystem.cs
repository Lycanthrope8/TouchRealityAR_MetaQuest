using UnityEngine;
using UnityEngine.Events;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using PassthroughCameraSamples;
using Meta.XR;
using Meta.XR.MRUtilityKit;

namespace ARObjectDetection
{
    /// <summary>
    /// Anchor placement mode - determines how anchors follow objects
    /// </summary>
    public enum AnchorFollowMode
    {
        /// <summary>World-locked: Place once, don't move (best for static furniture)</summary>
        WorldLocked,

        /// <summary>Continuous follow: Smoothly follow track position (best for portable objects)</summary>
        ContinuousFollow,

        /// <summary>Hybrid: Lock when depth confidence is high, otherwise follow</summary>
        Hybrid
    }

    /// <summary>
    /// Where to place the anchor relative to the bounding box
    /// </summary>
    public enum AnchorPlacementPoint
    {
        TopCenter,
        Center,
        BottomCenter,
        Custom
    }

    /// <summary>
    /// DEPTH ANCHOR SYSTEM - LONG-TERM MR EDITION (v2 - World-Space Anchoring)
    /// 
    /// CORE FIX: Anchors now maintain a FIXED WORLD POSITION that only updates
    /// when we receive FRESH detection measurements. This prevents drift during
    /// head motion because the anchor's world position is independent of camera pose.
    /// 
    /// Design Philosophy:
    /// - Anchors are placed in WORLD SPACE and stay there
    /// - Only FRESH detections (within freshness threshold) can update anchor position
    /// - Between detections, anchors remain perfectly stable in world space
    /// - Quest's SLAM tracking keeps world space stable relative to real world
    /// 
    /// Key Change from v1:
    /// - OLD: anchor.targetPosition = track.worldPositionSmoothed (could drift)
    /// - NEW: anchor.worldAnchorPosition = track.worldPosition ONLY when data is fresh
    ///        anchor stays at worldAnchorPosition until next fresh update
    /// </summary>
    public class DepthAnchorSystem : MonoBehaviour
    {
        [Header("Required References")]
        [SerializeField] private EnvironmentRaycastManager environmentRaycastManager;
        [SerializeField] private GameObject anchorPrefab;
        [SerializeField] private ObjectTracker objectTracker;

        [Header("Interaction")]
        [SerializeField] private GameObject infoPanelPrefab;
        [SerializeField] private OVRInput.Controller interactionController = OVRInput.Controller.RTouch;
        [SerializeField] private OVRInput.Button selectButton = OVRInput.Button.PrimaryIndexTrigger;
        [SerializeField] private float maxSelectionDistance = 10f;
        [SerializeField] private LayerMask anchorLayerMask = ~0;
        [SerializeField] private Vector3 infoPanelOffset = new Vector3(0.15f, 0.1f, 0f);

        [Header("Selection Ray")]
        [SerializeField] private LineRenderer selectionRayLine;
        [SerializeField] private bool showRayOnlyWhileHoldingSelect = false;
        [SerializeField] private bool hideRayWhenNoHit = false;
        [SerializeField] private float selectionRayMaxDistance = 10f;
        [SerializeField] private QueryTriggerInteraction selectionRayTriggerInteraction = QueryTriggerInteraction.Collide;
        [SerializeField] private bool tintRayByHit = false;
        [SerializeField] private Color rayNoHitColor = new Color(1f, 0f, 1f, 1f);
        [SerializeField] private Color rayHitColor = new Color(0f, 1f, 1f, 1f);

        [Header("Camera Settings")]
        [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

        [Header("=== ANCHOR BEHAVIOR ===")]
        [Tooltip("Default follow mode for anchors")]
        [SerializeField] private AnchorFollowMode defaultFollowMode = AnchorFollowMode.ContinuousFollow;

        [Tooltip("Classes that should use ContinuousFollow (portable objects)")]
        [SerializeField]
        private string[] portableClasses = new string[]
        {
            "mouse", "cell phone", "cup", "bottle", "remote", "book",
            "keyboard", "laptop", "handbag", "backpack"
        };

        [Tooltip("Classes that should use WorldLocked (static furniture)")]
        [SerializeField]
        private string[] staticClasses = new string[]
        {
            "couch", "chair", "bed", "dining table", "tv", "refrigerator",
            "oven", "sink", "toilet", "potted plant"
        };

        [Header("=== WORLD-SPACE ANCHORING (Anti-Drift) ===")]
        [Tooltip("Maximum age of track data to consider 'fresh' for position updates (seconds). " +
                 "Anchors only update position when track data is fresher than this.")]
        [Range(0.1f, 1.0f)]
        [SerializeField] private float maxFreshnessForUpdate = 0.35f;

        [Tooltip("Smooth the anchor position when receiving fresh updates (reduces jitter)")]
        [SerializeField] private bool smoothFreshUpdates = true;

        [Tooltip("Smoothing factor for fresh position updates (lower = smoother, higher = more responsive)")]
        [Range(0.1f, 1.0f)]
        [SerializeField] private float freshUpdateSmoothingAlpha = 0.5f;

        [Tooltip("Minimum position change to accept an update (meters). Prevents micro-jitter.")]
        [SerializeField] private float minPositionChangeForUpdate = 0.005f;

        [Header("=== DISPLAY SMOOTHING ===")]
        [Tooltip("Smooth time for display position (cosmetic smoothing only)")]
        [SerializeField] private float displaySmoothTime = 0.08f;

        [Tooltip("Maximum display follow speed (m/s)")]
        [SerializeField] private float maxDisplaySpeed = 8f;

        [Header("=== WORLD LOCK SETTINGS ===")]
        [SerializeField] private AnchorPlacementPoint placementPoint = AnchorPlacementPoint.Center;
        [SerializeField] private float verticalOffset = 0.02f;
        [SerializeField] private float fallbackDepth = 2.0f;
        [SerializeField] private float maxRaycastDistance = 10f;
        [Range(0f, 1f)]
        [SerializeField] private float customYPercent = 0.5f;
        [Range(0f, 1f)]
        [SerializeField] private float minConfidenceToLock = 0.5f;
        [SerializeField] private float lockCooldown = 0.5f;

        [Header("=== ANCHOR LIFECYCLE ===")]
        [Tooltip("Keep anchors visible when track is Lost (show as 'last seen')")]
        [SerializeField] private bool keepAnchorsWhenLost = true;

        [Tooltip("How long to keep Lost anchors visible (0 = forever until track pruned)")]
        [SerializeField] private float lostAnchorVisibilityTime = 0f;

        [Tooltip("Fade out Lost anchors over time")]
        [SerializeField] private bool fadeLostAnchors = true;

        [Header("Filtering")]
        [SerializeField] private bool onlyConfirmedTracks = false;
        [SerializeField] private float minimumConfidence = 0.3f;

        [Header("Visual Settings")]
        [SerializeField] private float anchorScale = 0.1f;
        [SerializeField] private bool scaleWithDistance = false;
        [SerializeField] private Color confirmedColor = new Color(0.2f, 1f, 0.4f, 1f);
        [SerializeField] private Color tentativeColor = new Color(1f, 0.85f, 0.2f, 0.9f);
        [SerializeField] private Color lockedColor = new Color(0f, 0.8f, 1f, 1f);
        [SerializeField] private Color selectedColor = new Color(1f, 0.5f, 0f, 1f);
        [SerializeField] private Color lostColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        [SerializeField] private Color staleColor = new Color(0.7f, 0.7f, 0.5f, 0.9f);

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = false;
        [SerializeField] private bool drawDebugRays = false;
        [SerializeField] private bool showDataFreshnessInLabel = false;

        [Header("Events")]
        public UnityEvent<DepthAnchorInstance> OnAnchorSelected;
        public UnityEvent OnAnchorDeselected;

        // ============================================================
        // INTERNAL STATE
        // ============================================================

        private Dictionary<int, DepthAnchorInstance> anchors = new Dictionary<int, DepthAnchorInstance>();
        private Queue<GameObject> anchorPool = new Queue<GameObject>();

        private PassthroughCameraIntrinsics? cameraIntrinsics;
        private Transform cameraTransform;

        private DepthAnchorInstance selectedAnchor = null;
        private GameObject activeInfoPanel = null;
        private Transform controllerTransform;

        // Ray cache
        private int cachedRayFrame = -1;
        private bool cachedRayHasHit = false;
        private RaycastHit cachedRayHit;

        // Metrics
        private int successfulDepthHits = 0;
        private int fallbacksUsed = 0;
        private int freshUpdatesApplied = 0;
        private int staleUpdatesSkipped = 0;

        // Class lookup sets (for O(1) lookup)
        private HashSet<string> portableClassSet;
        private HashSet<string> staticClassSet;

        // ============================================================
        // PUBLIC PROPERTIES
        // ============================================================

        public int ActiveAnchorCount => anchors.Count;
        public int VisibleAnchorCount => anchors.Count(a => a.Value.gameObject.activeSelf);
        public int SuccessfulDepthHits => successfulDepthHits;
        public int FallbacksUsed => fallbacksUsed;
        public DepthAnchorInstance SelectedAnchor => selectedAnchor;
        public int FreshUpdatesApplied => freshUpdatesApplied;
        public int StaleUpdatesSkipped => staleUpdatesSkipped;

        // ============================================================
        // UNITY LIFECYCLE
        // ============================================================

        private void Awake()
        {
            if (!Initialize()) enabled = false;
        }

        private bool Initialize()
        {
            if (anchorPrefab == null)
            {
                Debug.LogError("[DepthAnchor] Anchor prefab not assigned!");
                return false;
            }

            if (objectTracker == null)
            {
                objectTracker = FindFirstObjectByType<ObjectTracker>();
                if (objectTracker == null)
                {
                    Debug.LogError("[DepthAnchor] ObjectTracker not found!");
                    return false;
                }
            }

            if (environmentRaycastManager == null)
            {
                environmentRaycastManager = FindFirstObjectByType<EnvironmentRaycastManager>();
            }

            try
            {
                cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(cameraEye);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DepthAnchor] Camera intrinsics failed: {e.Message}");
            }

            if (OVRManager.instance != null)
                cameraTransform = OVRManager.instance.GetComponentInChildren<Camera>()?.transform;
            if (cameraTransform == null) cameraTransform = Camera.main?.transform;
            if (cameraTransform == null)
            {
                Debug.LogError("[DepthAnchor] No camera transform!");
                return false;
            }

            FindControllerTransform();

            if (selectionRayLine != null)
            {
                selectionRayLine.positionCount = 2;
                selectionRayLine.useWorldSpace = true;
            }

            // Build class lookup sets
            portableClassSet = new HashSet<string>(portableClasses.Select(c => c.ToLower()));
            staticClassSet = new HashSet<string>(staticClasses.Select(c => c.ToLower()));

            // Pre-warm pool
            for (int i = 0; i < 30; i++)
            {
                var obj = Instantiate(anchorPrefab, transform);
                SetupAnchorCollider(obj);
                obj.SetActive(false);
                anchorPool.Enqueue(obj);
            }

            Debug.Log($"[DepthAnchor] ✅ WORLD-SPACE ANCHORING v2 initialized");
            Debug.Log($"[DepthAnchor] Freshness threshold: {maxFreshnessForUpdate}s, " +
                     $"Smoothing: {(smoothFreshUpdates ? freshUpdateSmoothingAlpha.ToString("F2") : "OFF")}");
            return true;
        }

        private void FindControllerTransform()
        {
            if (OVRManager.instance != null)
            {
                var rig = OVRManager.instance.GetComponent<OVRCameraRig>();
                if (rig != null)
                {
                    controllerTransform = interactionController == OVRInput.Controller.RTouch
                        ? rig.rightHandAnchor : rig.leftHandAnchor;
                }
            }
            if (controllerTransform == null) controllerTransform = cameraTransform;
        }

        private void SetupAnchorCollider(GameObject obj)
        {
            if (obj.GetComponent<Collider>() == null)
            {
                var col = obj.AddComponent<SphereCollider>();
                col.radius = 0.5f;
                col.isTrigger = true;
            }
        }

        private void Update()
        {
            if (objectTracker == null || cameraTransform == null) return;
            if (controllerTransform == null) FindControllerTransform();

            UpdateSelectionRay();
            ProcessTracks();
            UpdateAnchorDisplay();
            HandleInteraction();
            UpdateInfoPanelPosition();
        }

        // ============================================================
        // SELECTION RAY
        // ============================================================

        private void UpdateSelectionRay()
        {
            cachedRayFrame = Time.frameCount;
            cachedRayHasHit = false;

            if (controllerTransform == null) return;

            Ray ray = new Ray(controllerTransform.position, controllerTransform.forward);

            cachedRayHasHit = Physics.Raycast(
                ray, out cachedRayHit,
                Mathf.Max(maxSelectionDistance, selectionRayMaxDistance),
                anchorLayerMask, selectionRayTriggerInteraction);

            if (selectionRayLine == null) return;

            bool shouldShow = !showRayOnlyWhileHoldingSelect ||
                              OVRInput.Get(selectButton, interactionController);

            if (!shouldShow)
            {
                selectionRayLine.enabled = false;
                return;
            }

            Vector3 end = cachedRayHasHit ? cachedRayHit.point : ray.origin + ray.direction * selectionRayMaxDistance;
            selectionRayLine.enabled = cachedRayHasHit || !hideRayWhenNoHit;

            if (selectionRayLine.enabled)
            {
                selectionRayLine.SetPosition(0, ray.origin);
                selectionRayLine.SetPosition(1, end);

                if (tintRayByHit)
                {
                    Color c = cachedRayHasHit ? rayHitColor : rayNoHitColor;
                    selectionRayLine.startColor = c;
                    selectionRayLine.endColor = c;
                }
            }
        }

        // ============================================================
        // TRACK PROCESSING
        // ============================================================

        private void ProcessTracks()
        {
            float now = Time.realtimeSinceStartup;
            HashSet<int> seenTrackIds = new HashSet<int>();

            // Process all tracks (including Lost if keepAnchorsWhenLost)
            foreach (var track in objectTracker.ActiveTracks)
            {
                // Filter based on settings
                if (!keepAnchorsWhenLost && track.state == TrackState.Lost) continue;
                if (onlyConfirmedTracks && track.state == TrackState.Tentative) continue;
                if (track.confidence < minimumConfidence && track.state != TrackState.Lost) continue;

                seenTrackIds.Add(track.id);

                if (!anchors.TryGetValue(track.id, out var anchor))
                {
                    // Create new anchor
                    anchor = CreateAnchor(track, now);
                    anchors[track.id] = anchor;
                }

                // Update anchor from track (WORLD-SPACE ANCHORING LOGIC)
                UpdateAnchorWorldPosition(anchor, track, now);
            }

            // Remove anchors for tracks that no longer exist
            var toRemove = anchors.Keys.Where(id => !seenTrackIds.Contains(id)).ToList();
            foreach (int id in toRemove)
            {
                if (anchors.TryGetValue(id, out var anchor))
                {
                    if (selectedAnchor == anchor) DeselectAnchor();
                    ReturnToPool(anchor.gameObject);
                    anchors.Remove(id);
                }
            }
        }

        private DepthAnchorInstance CreateAnchor(TrackedObject track, float now)
        {
            GameObject obj = GetFromPool();
            AnchorFollowMode mode = GetFollowModeForClass(track.className);

            var anchor = new DepthAnchorInstance
            {
                trackId = track.id,
                className = track.className,
                gameObject = obj,
                transform = obj.transform,
                label = obj.GetComponentInChildren<TextMeshPro>(),
                renderers = obj.GetComponentsInChildren<Renderer>(),
                creationTime = now,
                followMode = mode,
                displayVelocity = Vector3.zero,
                isLocked = false,
                lastDepthConfidence = 0f,
                lastPlacementTime = now,
                // World-space anchoring state
                worldAnchorPosition = Vector3.zero,
                displayPosition = Vector3.zero,
                lastFreshUpdateTime = 0f,
                dataFreshness = 0f
            };

            // Initialize position based on mode
            Vector3 initialPos;

            if (mode == AnchorFollowMode.WorldLocked || mode == AnchorFollowMode.Hybrid)
            {
                // Try to get depth position for static objects
                if (TryGetDepthPosition(track, out Vector3 depthPos, out float confidence))
                {
                    initialPos = depthPos;
                    anchor.lastDepthConfidence = confidence;

                    if (confidence >= minConfidenceToLock)
                    {
                        anchor.isLocked = true;
                        if (enableDebugLogs)
                            Debug.Log($"[DepthAnchor] 🔒 Created LOCKED anchor for #{track.id} ({track.className})");
                    }
                }
                else
                {
                    initialPos = track.worldPosition + Vector3.up * verticalOffset;
                }
            }
            else
            {
                // Portable objects: use raw worldPosition (not smoothed - we do our own smoothing)
                initialPos = track.worldPosition;
            }

            // Set BOTH world anchor and display to initial position
            anchor.worldAnchorPosition = initialPos;
            anchor.displayPosition = initialPos;
            anchor.transform.position = initialPos;
            anchor.lastFreshUpdateTime = now;

            if (anchor.label != null && anchor.label.GetComponent<Billboard>() == null)
            {
                anchor.label.gameObject.AddComponent<Billboard>();
            }

            obj.SetActive(true);

            if (enableDebugLogs)
            {
                Debug.Log($"[DepthAnchor] 🆕 Created anchor for #{track.id} ({track.className}) mode={mode}");
            }

            return anchor;
        }

        /// <summary>
        /// CORE WORLD-SPACE ANCHORING LOGIC
        /// 
        /// Key principle: The anchor's worldAnchorPosition is FIXED in world space.
        /// It only updates when we receive FRESH track data (timeSinceLastUpdate < threshold).
        /// Between fresh updates, the anchor stays perfectly stable regardless of head motion.
        /// </summary>
        private void UpdateAnchorWorldPosition(DepthAnchorInstance anchor, TrackedObject track, float now)
        {
            anchor.trackState = track.state;
            anchor.confidence = track.confidence;
            anchor.lastUpdateTime = now;

            // Track data freshness
            anchor.dataFreshness = track.timeSinceLastUpdate;
            bool isDataFresh = track.timeSinceLastUpdate <= maxFreshnessForUpdate;

            // Handle Lost state
            if (track.state == TrackState.Lost)
            {
                anchor.isLost = true;
                if (anchor.lostTime <= 0f)
                    anchor.lostTime = now;
                // Don't update worldAnchorPosition - keep it fixed
                return;
            }

            anchor.isLost = false;
            anchor.lostTime = 0f;

            // ============================================================
            // WORLD-SPACE POSITION UPDATE (only when data is fresh)
            // ============================================================

            switch (anchor.followMode)
            {
                case AnchorFollowMode.ContinuousFollow:
                    // Portable objects: update position ONLY when data is fresh
                    if (isDataFresh)
                    {
                        Vector3 newWorldPos = track.worldPosition;

                        // Check if position changed enough to warrant an update
                        float positionDelta = Vector3.Distance(anchor.worldAnchorPosition, newWorldPos);

                        if (positionDelta >= minPositionChangeForUpdate)
                        {
                            if (smoothFreshUpdates)
                            {
                                // Smooth the world anchor position update
                                anchor.worldAnchorPosition = Vector3.Lerp(
                                    anchor.worldAnchorPosition,
                                    newWorldPos,
                                    freshUpdateSmoothingAlpha
                                );
                            }
                            else
                            {
                                anchor.worldAnchorPosition = newWorldPos;
                            }

                            anchor.lastFreshUpdateTime = now;
                            freshUpdatesApplied++;

                            if (enableDebugLogs && Time.frameCount % 60 == 0)
                            {
                                Debug.Log($"[DepthAnchor] ✓ Fresh update #{track.id} delta={positionDelta:F3}m age={track.timeSinceLastUpdate:F3}s");
                            }
                        }
                    }
                    else
                    {
                        staleUpdatesSkipped++;
                        // Data is stale - do NOT update worldAnchorPosition
                        // The anchor stays fixed at its last known good position
                    }
                    break;

                case AnchorFollowMode.WorldLocked:
                    // Static objects: lock position using depth raycast
                    if (!anchor.isLocked && isDataFresh)
                    {
                        bool gotDepth = TryGetDepthPosition(track, out Vector3 depthPos, out float depthConfidence);

                        if (gotDepth && depthConfidence >= minConfidenceToLock)
                        {
                            anchor.worldAnchorPosition = depthPos;
                            anchor.lastDepthConfidence = depthConfidence;
                            anchor.isLocked = true;
                            anchor.lastPlacementTime = now;
                            anchor.lastFreshUpdateTime = now;

                            if (enableDebugLogs)
                                Debug.Log($"[DepthAnchor] 🔒 LOCKED #{track.id} ({track.className}) conf={depthConfidence:F2}");
                        }
                        else if (!anchor.isLocked)
                        {
                            // Not locked yet, use track position but only if fresh
                            anchor.worldAnchorPosition = track.worldPosition;
                            anchor.lastDepthConfidence = depthConfidence;
                            anchor.lastFreshUpdateTime = now;
                        }
                    }
                    // If locked, worldAnchorPosition never changes (world-locked)
                    break;

                case AnchorFollowMode.Hybrid:
                    // Hybrid: try to lock with depth, otherwise follow (only when fresh)
                    if (!anchor.isLocked && isDataFresh)
                    {
                        bool gotDepth = TryGetDepthPosition(track, out Vector3 depthPos, out float depthConfidence);

                        if (gotDepth && depthConfidence >= minConfidenceToLock &&
                            now - anchor.lastPlacementTime > lockCooldown)
                        {
                            anchor.worldAnchorPosition = depthPos;
                            anchor.lastDepthConfidence = depthConfidence;
                            anchor.isLocked = true;
                            anchor.lastPlacementTime = now;
                            anchor.lastFreshUpdateTime = now;
                        }
                        else
                        {
                            // Follow until we get good lock (only update when fresh)
                            Vector3 newWorldPos = track.worldPosition;
                            float positionDelta = Vector3.Distance(anchor.worldAnchorPosition, newWorldPos);

                            if (positionDelta >= minPositionChangeForUpdate)
                            {
                                if (smoothFreshUpdates)
                                {
                                    anchor.worldAnchorPosition = Vector3.Lerp(
                                        anchor.worldAnchorPosition,
                                        newWorldPos,
                                        freshUpdateSmoothingAlpha
                                    );
                                }
                                else
                                {
                                    anchor.worldAnchorPosition = newWorldPos;
                                }
                                anchor.lastFreshUpdateTime = now;
                            }
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Try to get depth-adjusted position using environment raycast
        /// </summary>
        private bool TryGetDepthPosition(TrackedObject track, out Vector3 position, out float confidence)
        {
            position = track.worldPosition + Vector3.up * verticalOffset;
            confidence = 0f;

            if (environmentRaycastManager == null || !EnvironmentRaycastManager.IsSupported)
            {
                fallbacksUsed++;
                return false;
            }

            // Cast ray from camera through track center
            Ray depthRay = track.centerRay;

            if (environmentRaycastManager.Raycast(depthRay, out EnvironmentRaycastHit hitInfo, maxRaycastDistance))
            {
                position = hitInfo.point + Vector3.up * verticalOffset;
                confidence = hitInfo.normalConfidence;
                successfulDepthHits++;
                return true;
            }
            else
            {
                fallbacksUsed++;
                return false;
            }
        }

        private AnchorFollowMode GetFollowModeForClass(string className)
        {
            string lower = className.ToLower();

            if (portableClassSet.Contains(lower))
                return AnchorFollowMode.ContinuousFollow;

            if (staticClassSet.Contains(lower))
                return AnchorFollowMode.WorldLocked;

            return defaultFollowMode;
        }

        // ============================================================
        // DISPLAY UPDATE (cosmetic smoothing only)
        // ============================================================

        /// <summary>
        /// Update the visual display position of anchors.
        /// This is purely cosmetic smoothing - the worldAnchorPosition is the true position.
        /// </summary>
        private void UpdateAnchorDisplay()
        {
            float now = Time.realtimeSinceStartup;

            foreach (var anchor in anchors.Values)
            {
                // Smooth display position towards world anchor position
                // This is cosmetic only - prevents visual pop when position updates
                anchor.displayPosition = Vector3.SmoothDamp(
                    anchor.displayPosition,
                    anchor.worldAnchorPosition,
                    ref anchor.displayVelocity,
                    displaySmoothTime,
                    maxDisplaySpeed
                );

                // Update transform
                anchor.transform.position = anchor.displayPosition;

                // Scale
                float distance = Vector3.Distance(cameraTransform.position, anchor.displayPosition);
                float scale = scaleWithDistance ? Mathf.Clamp(distance * 0.03f, 0.02f, 0.2f) : anchorScale;
                anchor.transform.localScale = Vector3.one * scale;

                // Determine visual state
                bool isStale = anchor.dataFreshness > maxFreshnessForUpdate;

                // Color based on state
                Color color;
                float alpha = 1f;

                if (anchor.isSelected)
                {
                    color = selectedColor;
                }
                else if (anchor.isLost)
                {
                    color = lostColor;

                    if (fadeLostAnchors && anchor.lostTime > 0)
                    {
                        float lostDuration = now - anchor.lostTime;
                        alpha = Mathf.Clamp01(1f - (lostDuration / 10f));
                    }
                }
                else if (anchor.isLocked)
                {
                    color = lockedColor;
                }
                else if (isStale && !anchor.isLocked)
                {
                    // Show stale data visually (anchor is holding position)
                    color = staleColor;
                }
                else if (anchor.trackState == TrackState.Confirmed)
                {
                    color = confirmedColor;
                }
                else
                {
                    color = tentativeColor;
                }

                color.a *= alpha;

                foreach (var r in anchor.renderers)
                {
                    if (r.material != null)
                    {
                        r.material.color = color;
                    }
                }

                // Label
                if (anchor.label != null)
                {
                    string symbol;
                    if (anchor.isSelected) symbol = "★";
                    else if (anchor.isLost) symbol = "👻";
                    else if (anchor.isLocked) symbol = "🔒";
                    else if (isStale) symbol = "⏸"; // Paused/holding
                    else if (anchor.followMode == AnchorFollowMode.ContinuousFollow) symbol = "↔";
                    else symbol = "◇";

                    string freshnessInfo = "";
                    if (showDataFreshnessInLabel)
                    {
                        freshnessInfo = $"\n{anchor.dataFreshness * 1000:F0}ms";
                    }

                    anchor.label.text = $"{symbol} {anchor.className}\n#{anchor.trackId} {anchor.confidence:F2}{freshnessInfo}";
                    anchor.label.color = color;
                }

                // Update info panel if selected
                if (anchor.isSelected && activeInfoPanel != null && activeInfoPanel.activeSelf)
                {
                    UpdateInfoPanelContent(anchor);
                }
            }
        }

        // ============================================================
        // INTERACTION
        // ============================================================

        private void HandleInteraction()
        {
            if (controllerTransform == null) return;

            if (OVRInput.GetDown(selectButton, interactionController))
            {
                TrySelectAnchor();
            }
        }

        private void TrySelectAnchor()
        {
            if (!cachedRayHasHit || cachedRayFrame != Time.frameCount)
            {
                DeselectAnchor();
                return;
            }

            DepthAnchorInstance hitAnchor = null;
            foreach (var anchor in anchors.Values)
            {
                if (cachedRayHit.collider != null &&
                    (cachedRayHit.collider.gameObject == anchor.gameObject ||
                     cachedRayHit.collider.transform.IsChildOf(anchor.transform)))
                {
                    hitAnchor = anchor;
                    break;
                }
            }

            if (hitAnchor != null)
            {
                if (selectedAnchor == hitAnchor)
                    DeselectAnchor();
                else
                    SelectAnchor(hitAnchor);
            }
            else
            {
                DeselectAnchor();
            }
        }

        private void SelectAnchor(DepthAnchorInstance anchor)
        {
            if (selectedAnchor != null) selectedAnchor.isSelected = false;

            selectedAnchor = anchor;
            selectedAnchor.isSelected = true;

            ShowInfoPanel(anchor);
            OnAnchorSelected?.Invoke(anchor);
        }

        private void DeselectAnchor()
        {
            if (selectedAnchor != null)
            {
                selectedAnchor.isSelected = false;
                selectedAnchor = null;
            }
            HideInfoPanel();
            OnAnchorDeselected?.Invoke();
        }

        // ============================================================
        // INFO PANEL
        // ============================================================

        private void ShowInfoPanel(DepthAnchorInstance anchor)
        {
            if (infoPanelPrefab == null) return;

            if (activeInfoPanel == null)
                activeInfoPanel = Instantiate(infoPanelPrefab, transform);

            UpdateInfoPanelContent(anchor);
            UpdateInfoPanelPosition();
            activeInfoPanel.SetActive(true);
        }

        private void UpdateInfoPanelContent(DepthAnchorInstance anchor)
        {
            if (activeInfoPanel == null) return;

            var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
            if (infoPanel != null)
            {
                infoPanel.UpdateInfo(anchor);
                return;
            }

            var tmp = activeInfoPanel.GetComponentInChildren<TextMeshPro>();
            if (tmp != null)
            {
                string stateStr = anchor.isLost ? "LOST" : anchor.trackState.ToString();
                string modeStr = anchor.followMode.ToString();
                bool isStale = anchor.dataFreshness > maxFreshnessForUpdate;
                string dataStatus = isStale ? $"STALE ({anchor.dataFreshness * 1000:F0}ms)" : $"FRESH ({anchor.dataFreshness * 1000:F0}ms)";

                tmp.text = $"<b>{anchor.className}</b>\n" +
                           $"───────────\n" +
                           $"Track ID: #{anchor.trackId}\n" +
                           $"State: {stateStr}\n" +
                           $"Mode: {modeStr}\n" +
                           $"Locked: {(anchor.isLocked ? "Yes" : "No")}\n" +
                           $"Data: {dataStatus}\n" +
                           $"Confidence: {anchor.confidence:P0}\n" +
                           $"Position:\n" +
                           $"  X: {anchor.worldAnchorPosition.x:F2}m\n" +
                           $"  Y: {anchor.worldAnchorPosition.y:F2}m\n" +
                           $"  Z: {anchor.worldAnchorPosition.z:F2}m";
            }
        }

        private void UpdateInfoPanelPosition()
        {
            if (activeInfoPanel == null || selectedAnchor == null) return;

            Vector3 toCamera = (cameraTransform.position - selectedAnchor.displayPosition).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, toCamera).normalized;

            activeInfoPanel.transform.position = selectedAnchor.displayPosition +
                right * infoPanelOffset.x +
                Vector3.up * infoPanelOffset.y +
                toCamera * infoPanelOffset.z;

            activeInfoPanel.transform.rotation = Quaternion.LookRotation(
                activeInfoPanel.transform.position - cameraTransform.position);
        }

        private void HideInfoPanel()
        {
            if (activeInfoPanel != null) activeInfoPanel.SetActive(false);
        }

        // ============================================================
        // OBJECT POOL
        // ============================================================

        private GameObject GetFromPool()
        {
            if (anchorPool.Count > 0) return anchorPool.Dequeue();
            var obj = Instantiate(anchorPrefab, transform);
            SetupAnchorCollider(obj);
            return obj;
        }

        private void ReturnToPool(GameObject obj)
        {
            if (obj == null) return;
            obj.SetActive(false);
            anchorPool.Enqueue(obj);
        }

        // ============================================================
        // PUBLIC API
        // ============================================================

        public void ClearAllAnchors()
        {
            DeselectAnchor();
            foreach (var anchor in anchors.Values)
                ReturnToPool(anchor.gameObject);
            anchors.Clear();
        }

        public void ResetStatistics()
        {
            successfulDepthHits = 0;
            fallbacksUsed = 0;
            freshUpdatesApplied = 0;
            staleUpdatesSkipped = 0;
        }

        public void UnlockAllAnchors()
        {
            foreach (var anchor in anchors.Values)
                anchor.isLocked = false;
        }

        public DepthAnchorInstance GetAnchorByTrackId(int trackId)
        {
            anchors.TryGetValue(trackId, out var anchor);
            return anchor;
        }

        public IEnumerable<DepthAnchorInstance> GetAllAnchors() => anchors.Values;

        public IEnumerable<DepthAnchorInstance> GetVisibleAnchors() =>
            anchors.Values.Where(a => !a.isLost);

        // ============================================================
        // CLEANUP
        // ============================================================

        private void OnDestroy()
        {
            ClearAllAnchors();
            if (activeInfoPanel != null) Destroy(activeInfoPanel);
        }

        // ============================================================
        // DEBUG GUI
        // ============================================================

        private void OnGUI()
        {
            if (!enableDebugLogs) return;

            GUILayout.BeginArea(new Rect(10, 300, 400, 130));
            GUILayout.Label($"═══ WORLD-SPACE ANCHORS v2 ═══");
            GUILayout.Label($"Anchors: {anchors.Count} | Visible: {VisibleAnchorCount}");
            GUILayout.Label($"Selected: {(selectedAnchor != null ? $"#{selectedAnchor.trackId} {selectedAnchor.className}" : "None")}");
            GUILayout.Label($"Depth hits: {successfulDepthHits} | Fallbacks: {fallbacksUsed}");
            GUILayout.Label($"Fresh updates: {freshUpdatesApplied} | Stale skipped: {staleUpdatesSkipped}");
            GUILayout.Label($"Freshness threshold: {maxFreshnessForUpdate * 1000:F0}ms");
            GUILayout.EndArea();
        }

        // ============================================================
        // ANCHOR INSTANCE CLASS
        // ============================================================

        [System.Serializable]
        public class DepthAnchorInstance
        {
            public int trackId;
            public string className;

            public GameObject gameObject;
            public Transform transform;
            public TextMeshPro label;
            public Renderer[] renderers;

            // ============================================================
            // WORLD-SPACE ANCHORING (v2)
            // ============================================================

            /// <summary>
            /// The anchor's TRUE position in world space.
            /// Only updated when fresh track data arrives.
            /// Stays FIXED between updates regardless of head motion.
            /// </summary>
            public Vector3 worldAnchorPosition;

            /// <summary>
            /// Current display position (smoothed towards worldAnchorPosition).
            /// This is purely cosmetic - prevents visual pop on updates.
            /// </summary>
            public Vector3 displayPosition;

            /// <summary>Velocity for display smoothing (SmoothDamp)</summary>
            public Vector3 displayVelocity;

            /// <summary>When we last received fresh data and updated position</summary>
            public float lastFreshUpdateTime;

            /// <summary>Current data freshness (timeSinceLastUpdate from track)</summary>
            public float dataFreshness;

            // Behavior
            public AnchorFollowMode followMode;
            public bool isLocked;
            public float lastDepthConfidence;
            public float lastPlacementTime;

            // Selection
            public bool isSelected;

            // Track state mirror
            public TrackState trackState;
            public float confidence;

            // Lost state
            public bool isLost;
            public float lostTime;

            // Metadata
            public float creationTime;
            public float lastUpdateTime;

            // For AnchorInfoPanel compatibility
            public Vector3 worldLockedPosition => worldAnchorPosition;
            public Vector3 smoothedPosition => displayPosition;
        }
    }
}