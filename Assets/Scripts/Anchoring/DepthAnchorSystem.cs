// ============================================================================
// FILE: DepthAnchorSystem.cs
// DEPTH ANCHOR SYSTEM - SESSION PERSISTENT ANCHORS WITH FOV-AWARE REMOVAL
// 
// BEHAVIOR:
// - Anchors are ONLY created after N consecutive frames of detection ("confirmed")
// - Once confirmed, anchors stay for the ENTIRE SESSION unless removed
// - Anchors are ONLY removed if:
//   1. The anchor is currently IN VIEW (within camera FOV)
//   2. AND NOT detected for N consecutive frames while in view
//   3. AND the anchor does NOT have AprilTag association (GREEN anchors NEVER removed)
// - If anchor is NOT in view, it persists indefinitely (no accumulation of misses)
// - Position is STATIC (set once at confirmation, never moved)
// 
// COLORS:
// - Default: CYAN
// - After CONFIRMED AprilTag association: GREEN
// - Selected: ORANGE
// 
// CRITICAL FIX: AprilTag-associated (GREEN) anchors are NEVER removed during session
// ============================================================================

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
    public class DepthAnchorSystem : MonoBehaviour
    {
        [Header("Required References")]
        [SerializeField] private EnvironmentRaycastManager environmentRaycastManager;
        [SerializeField] private GameObject anchorPrefab;
        [SerializeField] private ObjectTracker objectTracker;

        [Header("AprilTag Association")]
        [SerializeField] private MonoBehaviour tagTrackAssociator;

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

        [Header("Camera Settings")]
        [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

        [Header("Anchor Colors")]
        [SerializeField] private Color defaultAnchorColor = new Color(0f, 1f, 1f, 1f);
        [SerializeField] private Color aprilTagAssociatedColor = new Color(0f, 1f, 0f, 1f);
        [SerializeField] private Color selectedColor = new Color(1f, 0.5f, 0f, 1f);
        [SerializeField] private Color pendingColor = new Color(1f, 1f, 0f, 0.5f);

        [Header("Placement Settings")]
        [SerializeField] private float verticalOffset = 0.02f;
        [SerializeField] private float maxRaycastDistance = 10f;

        [Header("Visual Settings")]
        [SerializeField] private float anchorScale = 0.1f;
        [SerializeField] private bool scaleWithDistance = false;

        [Header("Filtering")]
        [SerializeField] private float minimumConfidence = 0.3f;

        [Header("=== Confirmation & Removal Settings ===")]
        [Tooltip("Frames of consecutive detection required to confirm anchor")]
        [Range(5, 30)]
        [SerializeField] private int framesRequiredToConfirm = 15;

        [Tooltip("Consecutive frames missed WHILE IN VIEW to remove anchor (DOES NOT APPLY TO GREEN/APRILTAG ANCHORS)")]
        [Range(10, 50)]
        [SerializeField] private int framesToRemoveWhenMissedInView = 20;

        [Tooltip("FOV margin for in-view check (degrees)")]
        [SerializeField] private float fovMargin = 5f;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = false;

        [Header("Events")]
        public UnityEvent<DepthAnchorInstance> OnAnchorSelected;
        public UnityEvent OnAnchorDeselected;
        public UnityEvent<DepthAnchorInstance> OnAnchorConfirmed;
        public UnityEvent<DepthAnchorInstance> OnAnchorRemoved;

        private Dictionary<int, DepthAnchorInstance> confirmedAnchors = new Dictionary<int, DepthAnchorInstance>();
        private Dictionary<int, PendingAnchorState> pendingAnchors = new Dictionary<int, PendingAnchorState>();
        private Queue<GameObject> anchorPool = new Queue<GameObject>();

        private PassthroughCameraIntrinsics? cameraIntrinsics;
        private Transform cameraTransform;

        private DepthAnchorInstance selectedAnchor = null;
        private GameObject activeInfoPanel = null;
        private Transform controllerTransform;

        private int cachedRayFrame = -1;
        private bool cachedRayHasHit = false;
        private RaycastHit cachedRayHit;

        private int successfulDepthHits = 0;
        private int fallbacksUsed = 0;
        private int anchorsCreated = 0;
        private int anchorsRemoved = 0;
        private int pendingPromoted = 0;

        private MaterialPropertyBlock materialPropertyBlock;
        private System.Reflection.MethodInfo getTagForTrackMethod;
        private bool hasTagTrackAssociator = false;
        private HashSet<int> tracksSeenThisFrame = new HashSet<int>();

        public int TotalAnchorCount => confirmedAnchors.Count;
        public int ActiveAnchorCount => confirmedAnchors.Count;
        public int PendingAnchorCount => pendingAnchors.Count;
        public int LostAnchorCount => 0;
        public DepthAnchorInstance SelectedAnchor => selectedAnchor;
        public int SuccessfulDepthHits => successfulDepthHits;
        public int FallbacksUsed => fallbacksUsed;

        private class PendingAnchorState
        {
            public int trackId;
            public string className;
            public int consecutiveFramesSeen;
            public Vector3 accumulatedPosition;
            public int positionSamples;
            public float lastConfidence;
            public float lastDepthConfidence;
            public float firstSeenTime;
            public float lastSeenTime;
            public Ray lastCenterRay;
        }

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
                environmentRaycastManager = FindFirstObjectByType<EnvironmentRaycastManager>();

            if (tagTrackAssociator == null)
            {
                var associatorType = System.Type.GetType("ARObjectDetection.AprilTag.TagTrackAssociator, Assembly-CSharp");
                if (associatorType != null)
                    tagTrackAssociator = FindFirstObjectByType(associatorType) as MonoBehaviour;
            }

            if (tagTrackAssociator != null)
            {
                getTagForTrackMethod = tagTrackAssociator.GetType().GetMethod("GetTagForTrack");
                hasTagTrackAssociator = (getTagForTrackMethod != null);
                if (hasTagTrackAssociator)
                    Debug.Log("[DepthAnchor] TagTrackAssociator found");
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

            materialPropertyBlock = new MaterialPropertyBlock();

            for (int i = 0; i < 30; i++)
            {
                var obj = Instantiate(anchorPrefab, transform);
                SetupAnchorCollider(obj);
                obj.SetActive(false);
                anchorPool.Enqueue(obj);
            }

            Debug.Log($"[DepthAnchor] ✅ SESSION PERSISTENT ANCHORS initialized");
            Debug.Log($"[DepthAnchor] Confirm after {framesRequiredToConfirm} frames, " +
                     $"Remove after {framesToRemoveWhenMissedInView} missed-while-visible frames " +
                     $"(GREEN/AprilTag anchors NEVER removed)");
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
            }
        }

        private void ProcessTracks()
        {
            float now = Time.realtimeSinceStartup;
            tracksSeenThisFrame.Clear();

            foreach (var track in objectTracker.ActiveTracks)
            {
                if (track.state != TrackState.Lost && track.confidence >= minimumConfidence)
                {
                    tracksSeenThisFrame.Add(track.id);
                }
            }

            foreach (var track in objectTracker.ActiveTracks)
            {
                if (track.state == TrackState.Lost) continue;
                if (track.confidence < minimumConfidence) continue;

                ProcessTrackDetection(track, now);
            }

            HandleMissedAnchors(now);
            CleanupStalePending(now);
        }

        private void ProcessTrackDetection(TrackedObject track, float now)
        {
            int trackId = track.id;

            if (confirmedAnchors.TryGetValue(trackId, out var anchor))
            {
                anchor.missedWhileVisibleCount = 0;
                anchor.lastSeenFrameId = Time.frameCount;
                anchor.lastUpdateTime = now;
                anchor.trackState = track.state;
                anchor.confidence = track.confidence;
                UpdateAprilTagAssociation(anchor);
                return;
            }

            if (pendingAnchors.TryGetValue(trackId, out var pending))
            {
                pending.consecutiveFramesSeen++;
                pending.lastSeenTime = now;
                pending.lastConfidence = track.confidence;
                pending.lastCenterRay = track.centerRay;
                pending.accumulatedPosition += track.worldPositionSmoothed;
                pending.positionSamples++;

                if (enableDebugLogs && pending.consecutiveFramesSeen % 5 == 0)
                {
                    Debug.Log($"[DepthAnchor] Pending #{trackId} ({track.className}): " +
                             $"{pending.consecutiveFramesSeen}/{framesRequiredToConfirm} frames");
                }

                if (pending.consecutiveFramesSeen >= framesRequiredToConfirm)
                {
                    PromotePendingToConfirmed(pending, track, now);
                }
                return;
            }

            pendingAnchors[trackId] = new PendingAnchorState
            {
                trackId = trackId,
                className = track.className,
                consecutiveFramesSeen = 1,
                accumulatedPosition = track.worldPositionSmoothed,
                positionSamples = 1,
                lastConfidence = track.confidence,
                firstSeenTime = now,
                lastSeenTime = now,
                lastCenterRay = track.centerRay
            };

            if (enableDebugLogs)
            {
                Debug.Log($"[DepthAnchor] Started tracking pending #{trackId} ({track.className})");
            }
        }

        private void PromotePendingToConfirmed(PendingAnchorState pending, TrackedObject track, float now)
        {
            Vector3 avgPosition = pending.accumulatedPosition / pending.positionSamples;
            Vector3 finalPosition;
            float depthConfidence = 0f;

            if (TryGetDepthPosition(pending.lastCenterRay, avgPosition, out Vector3 depthPos, out depthConfidence))
            {
                finalPosition = depthPos;
                successfulDepthHits++;
            }
            else
            {
                finalPosition = avgPosition + Vector3.up * verticalOffset;
                fallbacksUsed++;
            }

            GameObject obj = GetFromPool();

            var anchor = new DepthAnchorInstance
            {
                trackId = pending.trackId,
                className = pending.className,
                gameObject = obj,
                transform = obj.transform,
                label = obj.GetComponentInChildren<TextMeshPro>(),
                renderers = obj.GetComponentsInChildren<Renderer>(),
                creationTime = now,
                isConfirmed = true,
                worldAnchorPosition = finalPosition,
                lastDepthConfidence = depthConfidence,
                hasAprilTagAssociation = false,
                associatedAprilTagId = -1,
                missedWhileVisibleCount = 0,
                lastSeenFrameId = Time.frameCount,
                lastUpdateTime = now,
                trackState = track.state,
                confidence = track.confidence,
                isLocked = true
            };

            anchor.transform.position = finalPosition;

            if (anchor.label != null && anchor.label.GetComponent<Billboard>() == null)
                anchor.label.gameObject.AddComponent<Billboard>();

            obj.SetActive(true);

            confirmedAnchors[pending.trackId] = anchor;
            pendingAnchors.Remove(pending.trackId);

            anchorsCreated++;
            pendingPromoted++;

            Debug.Log($"[DepthAnchor] ★ Anchor #{pending.trackId} ({pending.className}) CONFIRMED after {framesRequiredToConfirm} frames at {finalPosition:F2}");

            OnAnchorConfirmed?.Invoke(anchor);
            UpdateAprilTagAssociation(anchor);
        }

        private void HandleMissedAnchors(float now)
        {
            Pose cameraPose = new Pose(cameraTransform.position, cameraTransform.rotation);
            var toRemove = new List<int>();

            foreach (var kvp in confirmedAnchors)
            {
                int trackId = kvp.Key;
                var anchor = kvp.Value;

                if (tracksSeenThisFrame.Contains(trackId))
                    continue;

                // ============================================================
                // CRITICAL FIX: AprilTag-associated (GREEN) anchors are NEVER removed
                // ============================================================
                if (anchor.hasAprilTagAssociation)
                {
                    // GREEN anchor - do NOT accumulate misses, do NOT remove
                    // These anchors persist for the entire session
                    continue;
                }

                bool isInView = IsAnchorInView(cameraPose, anchor.worldAnchorPosition);

                if (isInView)
                {
                    anchor.missedWhileVisibleCount++;

                    if (enableDebugLogs && anchor.missedWhileVisibleCount % 5 == 0)
                    {
                        Debug.Log($"[DepthAnchor] Anchor #{trackId} missed while visible: " +
                                 $"{anchor.missedWhileVisibleCount}/{framesToRemoveWhenMissedInView}");
                    }

                    if (anchor.missedWhileVisibleCount >= framesToRemoveWhenMissedInView)
                    {
                        toRemove.Add(trackId);
                    }
                }
            }

            foreach (int trackId in toRemove)
            {
                RemoveAnchor(trackId);
            }
        }

        private void RemoveAnchor(int trackId)
        {
            if (!confirmedAnchors.TryGetValue(trackId, out var anchor))
                return;

            // Double-check: NEVER remove AprilTag-associated anchors
            if (anchor.hasAprilTagAssociation)
            {
                Debug.LogWarning($"[DepthAnchor] Attempted to remove AprilTag-associated anchor #{trackId} - BLOCKED");
                return;
            }

            Debug.Log($"[DepthAnchor] ✖ Anchor #{trackId} ({anchor.className}) REMOVED after " +
                     $"{framesToRemoveWhenMissedInView} missed-while-visible frames");

            if (selectedAnchor == anchor)
            {
                DeselectAnchor();
            }

            anchor.gameObject.SetActive(false);
            anchorPool.Enqueue(anchor.gameObject);

            confirmedAnchors.Remove(trackId);
            anchorsRemoved++;

            OnAnchorRemoved?.Invoke(anchor);
        }

        private void CleanupStalePending(float now)
        {
            var stale = new List<int>();
            float staleThreshold = 1.0f;

            foreach (var kvp in pendingAnchors)
            {
                if (!tracksSeenThisFrame.Contains(kvp.Key))
                {
                    kvp.Value.consecutiveFramesSeen = 0;

                    if (now - kvp.Value.lastSeenTime > staleThreshold)
                    {
                        stale.Add(kvp.Key);
                    }
                }
            }

            foreach (int trackId in stale)
            {
                pendingAnchors.Remove(trackId);
                if (enableDebugLogs)
                {
                    Debug.Log($"[DepthAnchor] Pending #{trackId} expired (not seen for {staleThreshold}s)");
                }
            }
        }

        private bool IsAnchorInView(Pose cameraPose, Vector3 worldPos)
        {
            Vector3 toAnchor = worldPos - cameraPose.position;

            float dotForward = Vector3.Dot(cameraPose.rotation * Vector3.forward, toAnchor.normalized);
            if (dotForward <= 0)
                return false;

            Vector3 localDir = Quaternion.Inverse(cameraPose.rotation) * toAnchor.normalized;

            float halfFovRad = (45f - fovMargin) * Mathf.Deg2Rad;
            float maxTan = Mathf.Tan(halfFovRad);

            if (Mathf.Abs(localDir.x / localDir.z) > maxTan)
                return false;
            if (Mathf.Abs(localDir.y / localDir.z) > maxTan)
                return false;

            return true;
        }

        private void UpdateAprilTagAssociation(DepthAnchorInstance anchor)
        {
            if (!hasTagTrackAssociator || anchor.hasAprilTagAssociation)
                return;

            int tagId = GetTagIdForTrack(anchor.trackId);
            if (tagId >= 0)
            {
                anchor.hasAprilTagAssociation = true;
                anchor.associatedAprilTagId = tagId;
                Debug.Log($"[DepthAnchor] ★ Anchor #{anchor.trackId} ({anchor.className}) -> GREEN (AprilTag #{tagId}) - PROTECTED FROM REMOVAL");
            }
        }

        private int GetTagIdForTrack(int trackId)
        {
            if (!hasTagTrackAssociator || tagTrackAssociator == null || getTagForTrackMethod == null)
                return -1;

            try
            {
                object result = getTagForTrackMethod.Invoke(tagTrackAssociator, new object[] { trackId });
                if (result is int tagId)
                    return tagId;
            }
            catch { }

            return -1;
        }

        private bool TryGetDepthPosition(Ray centerRay, Vector3 fallbackPos, out Vector3 position, out float confidence)
        {
            position = fallbackPos + Vector3.up * verticalOffset;
            confidence = 0f;

            if (environmentRaycastManager == null || !EnvironmentRaycastManager.IsSupported)
                return false;

            if (environmentRaycastManager.Raycast(centerRay, out EnvironmentRaycastHit hitInfo, maxRaycastDistance))
            {
                position = hitInfo.point + Vector3.up * verticalOffset;
                confidence = hitInfo.normalConfidence;
                return true;
            }

            return false;
        }

        private void UpdateAnchorDisplay()
        {
            foreach (var anchor in confirmedAnchors.Values)
            {
                float distance = Vector3.Distance(cameraTransform.position, anchor.worldAnchorPosition);
                float scale = scaleWithDistance ? Mathf.Clamp(distance * 0.03f, 0.02f, 0.2f) : anchorScale;
                anchor.transform.localScale = Vector3.one * scale;

                Color color;
                if (anchor.isSelected)
                    color = selectedColor;
                else if (anchor.hasAprilTagAssociation)
                    color = aprilTagAssociatedColor;
                else
                    color = defaultAnchorColor;

                foreach (var r in anchor.renderers)
                {
                    if (r != null)
                    {
                        r.GetPropertyBlock(materialPropertyBlock);
                        materialPropertyBlock.SetColor("_Color", color);
                        materialPropertyBlock.SetColor("_BaseColor", color);
                        r.SetPropertyBlock(materialPropertyBlock);
                    }
                }

                if (anchor.label != null)
                {
                    string symbol;
                    if (anchor.isSelected) symbol = "★";
                    else if (anchor.hasAprilTagAssociation) symbol = $"🏷#{anchor.associatedAprilTagId}";
                    else symbol = "◇";

                    anchor.label.text = $"{symbol} {anchor.className}\n#{anchor.trackId}";
                    anchor.label.color = color;
                }

                if (anchor.isSelected && activeInfoPanel != null && activeInfoPanel.activeSelf)
                    UpdateInfoPanelContent(anchor);
            }
        }

        private void HandleInteraction()
        {
            if (controllerTransform == null) return;
            if (OVRInput.GetDown(selectButton, interactionController))
                TrySelectAnchor();
        }

        private void TrySelectAnchor()
        {
            if (!cachedRayHasHit || cachedRayFrame != Time.frameCount)
            {
                DeselectAnchor();
                return;
            }

            // ============================================================
            // PRIORITY 1: Check if we hit the Propose button on InfoPanel
            // ============================================================
            if (IsProposeButttonHit(cachedRayHit))
            {
                Debug.Log("[DepthAnchor] Propose button collider hit");
                OnProposeButtonHit();
                return; // Don't deselect or do anything else
            }

            // ============================================================
            // PRIORITY 1b (Phase 6A): a "Suggested command" button.
            // Detected by COMPONENT (not name) and FIRST, so leftover child names
            // inherited from the duplicated source button cannot misroute the poke.
            // Picking only fills the command line — it never calls chaincode.
            // ============================================================
            var pokedSuggestion = cachedRayHit.collider != null
                ? cachedRayHit.collider.GetComponentInParent<SkillCommandButton>()
                : null;
            if (pokedSuggestion != null)
            {
                Debug.Log("[DepthAnchor] Suggestion button collider hit: " + pokedSuggestion.Command);
                OnSuggestionButtonHit(pokedSuggestion);
                return;
            }

            // ============================================================
            // PRIORITY 1c (Phase 6A): the "Suggested" toggle that shows/hides the list.
            // ============================================================
            if (IsSuggestedToggleHit(cachedRayHit))
            {
                Debug.Log("[DepthAnchor] Suggested toggle collider hit");
                OnSuggestedToggleHit();
                return;
            }

            // ============================================================
            // PRIORITY 2a: Ask button (Phase 6 — opens keyboard, sends NL)
            // (was "Describe"; old name still matched for back-compat)
            // ============================================================
            if (IsAskButtonHit(cachedRayHit))
            {
                Debug.Log("[DepthAnchor] Ask button collider hit");
                OnAskButtonHit();
                return;
            }

            // ============================================================
            // PRIORITY 2b: Confirm button (Phase 6 — executes the decision)
            // (was "Actions"; old name still matched for back-compat)
            // ============================================================
            if (IsConfirmButtonHit(cachedRayHit))
            {
                Debug.Log("[DepthAnchor] Confirm button collider hit");
                OnConfirmButtonHit();
                return;
            }

            // ============================================================
            // PRIORITY 2c: Cancel button (Phase 6 — cancels the decision)
            // ============================================================
            if (IsCancelButtonHit(cachedRayHit))
            {
                Debug.Log("[DepthAnchor] Cancel button collider hit");
                OnCancelButtonHit();
                return;
            }

            // ============================================================
            // PRIORITY 3: Check if we hit the InfoPanel itself (not button)
            // If so, do nothing - don't deselect the anchor
            // ============================================================
            if (IsInfoPanelHit(cachedRayHit))
            {
                // Clicked on info panel but not on button - just ignore
                return;
            }

            // ============================================================
            // PRIORITY 4: Check if we hit an anchor
            // ============================================================
            DepthAnchorInstance hitAnchor = null;
            foreach (var anchor in confirmedAnchors.Values)
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
                // Clicked on empty space - deselect
                DeselectAnchor();
            }
        }

        /// <summary>
        /// Check if the raycast hit the Propose button
        /// </summary>
        private bool IsProposeButttonHit(RaycastHit hit)
        {
            if (hit.collider == null) return false;

            // Check if hit object or any parent is named "ProposeButton"
            Transform t = hit.collider.transform;
            while (t != null)
            {
                if (t.name.Contains("ProposeButton") || t.name.Contains("proposeButton"))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Check if the raycast hit the Ask button (Phase 6).
        /// Matches "AskButton" and the legacy "DescribeButton" name.
        /// </summary>
        private bool IsAskButtonHit(RaycastHit hit)
        {
            if (hit.collider == null) return false;

            Transform t = hit.collider.transform;
            while (t != null)
            {
                if (t.name.Contains("AskButton") || t.name.Contains("askButton") ||
                    t.name.Contains("DescribeButton") || t.name.Contains("describeButton"))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Check if the raycast hit the Confirm button (Phase 6).
        /// Matches "ConfirmButton" and the legacy "ActionsButton" name.
        /// </summary>
        private bool IsConfirmButtonHit(RaycastHit hit)
        {
            if (hit.collider == null) return false;

            Transform t = hit.collider.transform;
            while (t != null)
            {
                if (t.name.Contains("ConfirmButton") || t.name.Contains("confirmButton") ||
                    t.name.Contains("ActionsButton") || t.name.Contains("actionsButton"))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Check if the raycast hit the Cancel button (Phase 6).
        /// </summary>
        private bool IsCancelButtonHit(RaycastHit hit)
        {
            if (hit.collider == null) return false;

            Transform t = hit.collider.transform;
            while (t != null)
            {
                if (t.name.Contains("CancelButton") || t.name.Contains("cancelButton"))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Check if the raycast hit the "Suggested" toggle button (Phase 6A).
        /// </summary>
        private bool IsSuggestedToggleHit(RaycastHit hit)
        {
            if (hit.collider == null) return false;

            Transform t = hit.collider.transform;
            while (t != null)
            {
                if (t.name.Contains("SuggestedToggle") || t.name.Contains("suggestedToggle"))
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Check if the raycast hit the InfoPanel (but not the button)
        /// </summary>
        private bool IsInfoPanelHit(RaycastHit hit)
        {
            if (hit.collider == null) return false;
            if (activeInfoPanel == null) return false;

            // Check if hit object is part of the InfoPanel hierarchy
            Transform t = hit.collider.transform;
            while (t != null)
            {
                if (t.gameObject == activeInfoPanel)
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Handle Propose button click
        /// </summary>
        private void OnProposeButtonHit()
        {
            if (activeInfoPanel == null)
            {
                Debug.LogWarning("[DepthAnchor] Propose button hit but no active InfoPanel");
                return;
            }

            var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
            if (infoPanel != null)
            {
                infoPanel.OnPropose3DButtonClicked();
                Debug.Log("[DepthAnchor] ✓ Propose button clicked!");
            }
            else
            {
                Debug.LogWarning("[DepthAnchor] Propose button hit but AnchorInfoPanel component not found");
            }
        }

        /// <summary>
        /// Handle Ask button click (Phase 6) — opens the keyboard to type an NL command.
        /// </summary>
        private void OnAskButtonHit()
        {
            if (activeInfoPanel == null)
            {
                Debug.LogWarning("[DepthAnchor] Ask button hit but no active InfoPanel");
                return;
            }

            var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
            if (infoPanel != null)
            {
                Debug.Log("[DepthAnchor] ✓ Ask button clicked!");
                infoPanel.OnAsk3DButtonClicked();
            }
            else
            {
                Debug.LogWarning("[DepthAnchor] Ask button hit but AnchorInfoPanel component not found");
            }
        }

        /// <summary>
        /// Handle Confirm button click (Phase 6) — executes the previewed decision.
        /// </summary>
        private void OnConfirmButtonHit()
        {
            if (activeInfoPanel == null)
            {
                Debug.LogWarning("[DepthAnchor] Confirm button hit but no active InfoPanel");
                return;
            }

            var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
            if (infoPanel != null)
            {
                Debug.Log("[DepthAnchor] ✓ Confirm button clicked!");
                infoPanel.OnConfirm3DClicked();
            }
            else
            {
                Debug.LogWarning("[DepthAnchor] Confirm button hit but AnchorInfoPanel component not found");
            }
        }

        /// <summary>
        /// Handle Cancel button click (Phase 6) — cancels/dismisses the decision.
        /// </summary>
        private void OnCancelButtonHit()
        {
            if (activeInfoPanel == null)
            {
                Debug.LogWarning("[DepthAnchor] Cancel button hit but no active InfoPanel");
                return;
            }

            var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
            if (infoPanel != null)
            {
                Debug.Log("[DepthAnchor] ✓ Cancel button clicked!");
                infoPanel.OnCancel3DClicked();
            }
            else
            {
                Debug.LogWarning("[DepthAnchor] Cancel button hit but AnchorInfoPanel component not found");
            }
        }

        /// <summary>
        /// Handle the "Suggested" toggle click (Phase 6A) — shows/hides the list.
        /// </summary>
        private void OnSuggestedToggleHit()
        {
            if (activeInfoPanel == null)
            {
                Debug.LogWarning("[DepthAnchor] Suggested toggle hit but no active InfoPanel");
                return;
            }

            var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
            if (infoPanel != null)
            {
                Debug.Log("[DepthAnchor] ✓ Suggested toggle clicked!");
                infoPanel.OnSuggestedToggle3D();
            }
            else
            {
                Debug.LogWarning("[DepthAnchor] Suggested toggle hit but AnchorInfoPanel component not found");
            }
        }

        /// <summary>
        /// Handle a suggestion-button click (Phase 6A) — fills the command line.
        /// </summary>
        private void OnSuggestionButtonHit(SkillCommandButton btn)
        {
            if (activeInfoPanel == null)
            {
                Debug.LogWarning("[DepthAnchor] Suggestion button hit but no active InfoPanel");
                return;
            }

            var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
            if (infoPanel != null)
            {
                Debug.Log("[DepthAnchor] ✓ Suggestion picked: " + btn.Command);
                infoPanel.OnSuggestionPicked(btn);
            }
            else
            {
                Debug.LogWarning("[DepthAnchor] Suggestion button hit but AnchorInfoPanel component not found");
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
                string aprilTagInfo = anchor.hasAprilTagAssociation
                    ? $"AprilTag: #{anchor.associatedAprilTagId} (GREEN)"
                    : "AprilTag: None (CYAN)";

                tmp.text = $"<b>{anchor.className}</b>\n" +
                           $"───────────\n" +
                           $"Track ID: #{anchor.trackId}\n" +
                           $"State: CONFIRMED\n" +
                           $"{aprilTagInfo}\n" +
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

            Vector3 toCamera = (cameraTransform.position - selectedAnchor.worldAnchorPosition).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, toCamera).normalized;

            activeInfoPanel.transform.position = selectedAnchor.worldAnchorPosition +
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

        private GameObject GetFromPool()
        {
            if (anchorPool.Count > 0) return anchorPool.Dequeue();
            var obj = Instantiate(anchorPrefab, transform);
            SetupAnchorCollider(obj);
            return obj;
        }

        public DepthAnchorInstance GetAnchorByTrackId(int trackId)
        {
            confirmedAnchors.TryGetValue(trackId, out var anchor);
            return anchor;
        }

        public IEnumerable<DepthAnchorInstance> GetAllAnchors() => confirmedAnchors.Values;
        public IEnumerable<DepthAnchorInstance> GetActiveAnchors() => confirmedAnchors.Values;
        public IEnumerable<DepthAnchorInstance> GetLostAnchors() => Enumerable.Empty<DepthAnchorInstance>();

        /// <summary>
        /// Get the number of AprilTag-associated (GREEN) anchors
        /// </summary>
        public int GetAprilTagAssociatedAnchorCount()
        {
            return confirmedAnchors.Values.Count(a => a.hasAprilTagAssociation);
        }

        public void ClearAllAnchors()
        {
            DeselectAnchor();
            foreach (var anchor in confirmedAnchors.Values)
            {
                anchor.gameObject.SetActive(false);
                anchorPool.Enqueue(anchor.gameObject);
            }
            confirmedAnchors.Clear();
            pendingAnchors.Clear();
            Debug.Log("[DepthAnchor] All anchors cleared");
        }

        private void OnDestroy()
        {
            if (activeInfoPanel != null) Destroy(activeInfoPanel);
        }

        // NOTE: OnGUI() method has been REMOVED to eliminate debug spam.
        // Debug information can be viewed via the InfoPanel or via code-based logging.

        [System.Serializable]
        public class DepthAnchorInstance
        {
            public int trackId;
            public string className;
            public GameObject gameObject;
            public Transform transform;
            public TextMeshPro label;
            public Renderer[] renderers;
            public Vector3 worldAnchorPosition;
            public bool hasAprilTagAssociation;
            public int associatedAprilTagId;
            public float lastDepthConfidence;
            public bool isSelected;
            public bool isConfirmed;
            public bool isLocked;
            public TrackState trackState;
            public float confidence;
            public int missedWhileVisibleCount;
            public int lastSeenFrameId;
            public bool isLost => false;
            public float lostTime => 0f;
            public float creationTime;
            public float lastUpdateTime;
            public Vector3 worldLockedPosition => worldAnchorPosition;
            public Vector3 smoothedPosition => worldAnchorPosition;
        }
    }
}