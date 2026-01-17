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
          /// WORLD-LOCKED Depth Anchor System with Persistent Anchor Identity
          /// 
          /// KEY FEATURE: Anchors survive track ID changes during the session.
          /// When ObjectTracker assigns a new track ID to a re-acquired object,
          /// this system re-associates the new track ID to the existing anchor
          /// based on 3D spatial proximity and class matching.
          /// 
          /// This prevents duplicate anchors when:
          /// - User shakes head quickly
          /// - User looks away and back
          /// - Temporary occlusion causes track loss
          /// </summary>
          public class DepthAnchorSystem : MonoBehaviour
          {
                    [Header("Required References")]
                    [SerializeField] private EnvironmentRaycastManager environmentRaycastManager;
                    [SerializeField] private GameObject anchorPrefab;
                    [SerializeField] private ObjectTracker objectTracker;

                    [Header("Interaction")]
                    [Tooltip("InfoPanel prefab to show when anchor is selected")]
                    [SerializeField] private GameObject infoPanelPrefab;

                    [Tooltip("Which controller to use")]
                    [SerializeField] private OVRInput.Controller interactionController = OVRInput.Controller.RTouch;

                    [Tooltip("Button to trigger selection")]
                    [SerializeField] private OVRInput.Button selectButton = OVRInput.Button.PrimaryIndexTrigger;

                    [SerializeField] private float maxSelectionDistance = 10f;
                    [SerializeField] private LayerMask anchorLayerMask = ~0;
                    [SerializeField] private Vector3 infoPanelOffset = new Vector3(0.15f, 0.1f, 0f);

                    [Header("Selection Ray (LineRenderer you add in the scene)")]
                    [Tooltip("Assign a LineRenderer component (do not leave null). This script will only set its two points.")]
                    [SerializeField] private LineRenderer selectionRayLine;

                    [Tooltip("If true, ray only shows while the select button is held.")]
                    [SerializeField] private bool showRayOnlyWhileHoldingSelect = false;

                    [Tooltip("If true, hide the ray when it hits nothing. If false, draw full length.")]
                    [SerializeField] private bool hideRayWhenNoHit = false;

                    [Tooltip("Max length for the visual ray. Usually set same as maxSelectionDistance.")]
                    [SerializeField] private float selectionRayMaxDistance = 10f;

                    [Tooltip("Your anchor colliders are triggers by default. Collide ensures ray hits triggers regardless of project settings.")]
                    [SerializeField] private QueryTriggerInteraction selectionRayTriggerInteraction = QueryTriggerInteraction.Collide;

                    [Tooltip("Optional: Tint the ray color based on hit/no-hit by overriding LineRenderer colors each frame.")]
                    [SerializeField] private bool tintRayByHit = false;

                    [SerializeField] private Color rayNoHitColor = new Color(1f, 0f, 1f, 1f); // magenta
                    [SerializeField] private Color rayHitColor = new Color(0f, 1f, 1f, 1f);   // cyan

                    [Header("Camera Settings")]
                    [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

                    [Header("Anchor Positioning")]
                    [SerializeField] private AnchorPlacementPoint placementPoint = AnchorPlacementPoint.Center;
                    [SerializeField] private float verticalOffset = 0.02f;
                    [SerializeField] private float fallbackDepth = 2.0f;
                    [SerializeField] private float maxRaycastDistance = 10f;
                    [Range(0f, 1f)]
                    [SerializeField] private float customYPercent = 0.5f;

                    [Header("World Lock Settings")]
                    [Range(0f, 1f)]
                    [SerializeField] private float minConfidenceToLock = 0.5f;
                    [SerializeField] private float updateDistanceThreshold = 0.15f;
                    [SerializeField] private float lockCooldown = 0.5f;

                    [Header("Smoothing")]
                    [Range(0.1f, 1.0f)]
                    [SerializeField] private float smoothingAlpha = 0.3f;
                    [SerializeField] private float movementThreshold = 0.005f;

                    [Header("Filtering")]
                    [SerializeField] private bool onlyConfirmedTracks = true;
                    [SerializeField] private float minimumConfidence = 0.3f;

                    [Header("Visual Settings")]
                    [SerializeField] private float anchorScale = 0.1f;
                    [SerializeField] private bool scaleWithDistance = false;
                    [SerializeField] private Color confirmedColor = new Color(0.2f, 1f, 0.4f, 1f);
                    [SerializeField] private Color tentativeColor = new Color(1f, 0.85f, 0.2f, 0.9f);
                    [SerializeField] private Color lockedColor = new Color(0f, 0.8f, 1f, 1f);
                    [SerializeField] private Color selectedColor = new Color(1f, 0.5f, 0f, 1f);
                    [SerializeField] private Color orphanedColor = new Color(0.5f, 0.5f, 0.5f, 0.7f); // Gray for orphaned anchors

                    [Header("=== PERSISTENT ANCHOR IDENTITY ===")]
                    [Tooltip("Maximum distance (meters) for re-associating a new track ID to an existing anchor of the same class")]
                    [SerializeField] private float reassociationRadiusMeters = 0.5f;

                    [Tooltip("Maximum distance (meters) within which only one anchor per class is allowed (deduplication)")]
                    [SerializeField] private float dedupRadiusMeters = 0.25f;

                    [Tooltip("Enable automatic cleanup of orphaned anchors after TTL expires")]
                    [SerializeField] private bool orphanCleanupEnabled = false;

                    [Tooltip("Time in seconds before orphaned anchors are removed (only if orphanCleanupEnabled is true)")]
                    [SerializeField] private float orphanTTLSeconds = 120f;

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;
                    [SerializeField] private bool drawDebugRays = false;

                    [Header("Events")]
                    public UnityEvent<DepthAnchorInstance> OnAnchorSelected;
                    public UnityEvent OnAnchorDeselected;

                    // ============================================================
                    // PERSISTENT ANCHOR REGISTRY
                    // ============================================================

                    /// <summary>
                    /// Primary anchor registry keyed by persistent GUID.
                    /// Anchors remain here even when tracks are lost.
                    /// </summary>
                    private Dictionary<string, DepthAnchorInstance> anchorRegistry = new Dictionary<string, DepthAnchorInstance>();

                    /// <summary>
                    /// Maps currently active track IDs to their associated anchor GUIDs.
                    /// When a track is lost, its entry is removed, but the anchor persists in anchorRegistry.
                    /// </summary>
                    private Dictionary<int, string> trackIdToAnchorGuid = new Dictionary<int, string>();

                    // Legacy dictionary for backward compatibility (now points to registry entries)
                    private Dictionary<int, DepthAnchorInstance> anchors => GetAnchorsByTrackId();

                    private Queue<GameObject> anchorPool = new Queue<GameObject>();

                    private PassthroughCameraIntrinsics? cameraIntrinsics;
                    private Transform cameraTransform;

                    private DepthAnchorInstance selectedAnchor = null;
                    private GameObject activeInfoPanel = null;
                    private Transform controllerTransform;

                    // Cache the raycast each frame so the line + selection use the exact same hit
                    private int cachedRayFrame = -1;
                    private bool cachedRayHasHit = false;
                    private RaycastHit cachedRayHit;

                    private int successfulDepthHits = 0;
                    private int fallbacksUsed = 0;
                    private int lockedAnchors = 0;
                    private int reassociationCount = 0;
                    private int dedupCount = 0;

                    public int ActiveAnchorCount => anchorRegistry.Count;
                    public int AssociatedAnchorCount => trackIdToAnchorGuid.Count;
                    public int OrphanedAnchorCount => anchorRegistry.Count(a => !a.Value.currentlyAssociatedTrackId.HasValue);
                    public int SuccessfulDepthHits => successfulDepthHits;
                    public int FallbacksUsed => fallbacksUsed;
                    public int LockedAnchors => lockedAnchors;
                    public int ReassociationCount => reassociationCount;
                    public int DedupCount => dedupCount;
                    public DepthAnchorInstance SelectedAnchor => selectedAnchor;

                    /// <summary>
                    /// Get anchors indexed by their currently associated track ID (for backward compatibility)
                    /// </summary>
                    private Dictionary<int, DepthAnchorInstance> GetAnchorsByTrackId()
                    {
                              var result = new Dictionary<int, DepthAnchorInstance>();
                              foreach (var kvp in trackIdToAnchorGuid)
                              {
                                        if (anchorRegistry.TryGetValue(kvp.Value, out var anchor))
                                        {
                                                  result[kvp.Key] = anchor;
                                        }
                              }
                              return result;
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
                              {
                                        environmentRaycastManager = FindFirstObjectByType<EnvironmentRaycastManager>();
                                        if (environmentRaycastManager == null)
                                        {
                                                  Debug.LogError("[DepthAnchor] EnvironmentRaycastManager not found!");
                                                  return false;
                                        }
                              }

                              try
                              {
                                        cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(cameraEye);
                              }
                              catch (System.Exception e)
                              {
                                        Debug.LogError($"[DepthAnchor] Camera intrinsics failed: {e.Message}");
                                        return false;
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

                              // Validate selection ray line (you provide it in scene)
                              if (selectionRayLine != null)
                              {
                                        selectionRayLine.positionCount = 2;
                                        selectionRayLine.useWorldSpace = true;
                              }
                              else if (enableDebugLogs)
                              {
                                        Debug.LogWarning("[DepthAnchor] selectionRayLine is NULL. Assign a LineRenderer in Inspector if you want the visible ray.");
                              }

                              // Pre-warm pool
                              for (int i = 0; i < 20; i++)
                              {
                                        var obj = Instantiate(anchorPrefab, transform);
                                        SetupAnchorCollider(obj);
                                        obj.SetActive(false);
                                        anchorPool.Enqueue(obj);
                              }

                              Debug.Log($"[DepthAnchor] Initialized with PERSISTENT ANCHOR IDENTITY | " +
                                       $"ReassocRadius={reassociationRadiusMeters}m, DedupRadius={dedupRadiusMeters}m, " +
                                       $"OrphanCleanup={orphanCleanupEnabled}");
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

                              // Fallback (still allows selection ray from head if needed)
                              if (controllerTransform == null) controllerTransform = cameraTransform;
                    }

                    private void SetupAnchorCollider(GameObject obj)
                    {
                              if (obj.GetComponent<Collider>() == null)
                              {
                                        var col = obj.AddComponent<SphereCollider>();
                                        col.radius = 0.5f;
                                        col.isTrigger = true; // ray needs QueryTriggerInteraction.Collide to be robust
                              }
                    }

                    private void Update()
                    {
                              if (objectTracker == null || !cameraIntrinsics.HasValue || cameraTransform == null) return;

                              if (controllerTransform == null) FindControllerTransform();

                              // Update ray + cache hit each frame (line & selection share same result)
                              UpdateSelectionRayAndCache();

                              ProcessTracks();
                              UpdateAnchorVisuals();
                              CleanupOrphanedAnchors();
                              UpdateLockedCount();
                              HandleInteraction();
                              UpdateInfoPanelPosition();
                    }

                    #region Selection Ray (LineRenderer you provide)

                    private void UpdateSelectionRayAndCache()
                    {
                              cachedRayFrame = Time.frameCount;
                              cachedRayHasHit = false;

                              if (controllerTransform == null) return;

                              Ray ray = new Ray(controllerTransform.position, controllerTransform.forward);

                              cachedRayHasHit = Physics.Raycast(
                                  ray,
                                  out cachedRayHit,
                                  Mathf.Max(maxSelectionDistance, selectionRayMaxDistance),
                                  anchorLayerMask,
                                  selectionRayTriggerInteraction);

                              // Drive the line renderer (if assigned)
                              if (selectionRayLine == null) return;

                              bool shouldShow = !showRayOnlyWhileHoldingSelect ||
                                                OVRInput.Get(selectButton, interactionController);

                              if (!shouldShow)
                              {
                                        selectionRayLine.enabled = false;
                                        return;
                              }

                              Vector3 origin = ray.origin;
                              Vector3 end;

                              if (cachedRayHasHit)
                              {
                                        end = cachedRayHit.point;
                                        selectionRayLine.enabled = true;
                              }
                              else
                              {
                                        end = origin + ray.direction * selectionRayMaxDistance;
                                        selectionRayLine.enabled = !hideRayWhenNoHit;
                              }

                              if (!selectionRayLine.enabled) return;

                              selectionRayLine.positionCount = 2;
                              selectionRayLine.useWorldSpace = true;
                              selectionRayLine.SetPosition(0, origin);
                              selectionRayLine.SetPosition(1, end);

                              if (tintRayByHit)
                              {
                                        Color c = cachedRayHasHit ? rayHitColor : rayNoHitColor;
                                        selectionRayLine.startColor = c;
                                        selectionRayLine.endColor = c;
                              }
                    }

                    #endregion

                    #region Interaction

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
                              // Reuse cached result if available for this frame
                              bool hasHit = (cachedRayFrame == Time.frameCount) ? cachedRayHasHit : false;
                              RaycastHit hit = cachedRayHit;

                              // If cache isn't from this frame (rare), do a fresh raycast
                              if (cachedRayFrame != Time.frameCount)
                              {
                                        Ray ray = new Ray(controllerTransform.position, controllerTransform.forward);
                                        hasHit = Physics.Raycast(ray, out hit, maxSelectionDistance, anchorLayerMask, selectionRayTriggerInteraction);
                              }

                              if (hasHit)
                              {
                                        DepthAnchorInstance hitAnchor = null;

                                        foreach (var anchor in anchorRegistry.Values)
                                        {
                                                  if (anchor == null || anchor.transform == null) continue;

                                                  // Collider might be on root or child
                                                  if (hit.collider != null &&
                                                      (hit.collider.gameObject == anchor.gameObject ||
                                                       hit.collider.transform.IsChildOf(anchor.transform)))
                                                  {
                                                            hitAnchor = anchor;
                                                            break;
                                                  }
                                        }

                                        if (hitAnchor != null)
                                        {
                                                  if (selectedAnchor == hitAnchor) DeselectAnchor();
                                                  else SelectAnchor(hitAnchor);
                                        }
                                        else
                                        {
                                                  DeselectAnchor();
                                        }
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

                              if (enableDebugLogs)
                                        Debug.Log($"[DepthAnchor] Selected: {anchor.className} (GUID: {anchor.anchorGuid.Substring(0, 8)}...)");

                              ShowInfoPanel(anchor);
                              OnAnchorSelected?.Invoke(anchor);
                    }

                    private void DeselectAnchor()
                    {
                              if (selectedAnchor != null)
                              {
                                        selectedAnchor.isSelected = false;

                                        if (enableDebugLogs)
                                                  Debug.Log($"[DepthAnchor] Deselected: {selectedAnchor.className}");

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

                              var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
                              if (infoPanel != null)
                                        infoPanel.UpdateInfo(anchor);
                              else
                                        UpdateInfoPanelText(anchor);

                              UpdateInfoPanelPosition();
                              activeInfoPanel.SetActive(true);
                    }

                    private void UpdateInfoPanelText(DepthAnchorInstance anchor)
                    {
                              if (activeInfoPanel == null) return;

                              var tmp = activeInfoPanel.GetComponentInChildren<TextMeshPro>();
                              if (tmp != null)
                              {
                                        string trackInfo = anchor.currentlyAssociatedTrackId.HasValue
                                            ? $"Track #{anchor.currentlyAssociatedTrackId.Value}"
                                            : "ORPHANED";

                                        tmp.text = $"<b>{anchor.className}</b>\n" +
                                                   $"───────────\n" +
                                                   $"GUID: {anchor.anchorGuid.Substring(0, 8)}...\n" +
                                                   $"{trackInfo}\n" +
                                                   $"Confidence: {anchor.confidence:P0}\n" +
                                                   $"State: {anchor.trackState}\n" +
                                                   $"Locked: {(anchor.isLocked ? "Yes" : "No")}\n" +
                                                   $"Position:\n" +
                                                   $"  X: {anchor.worldLockedPosition.x:F2}m\n" +
                                                   $"  Y: {anchor.worldLockedPosition.y:F2}m\n" +
                                                   $"  Z: {anchor.worldLockedPosition.z:F2}m";
                              }
                    }

                    private void UpdateInfoPanelPosition()
                    {
                              if (activeInfoPanel == null || selectedAnchor == null) return;

                              Vector3 toCamera = (cameraTransform.position - selectedAnchor.smoothedPosition).normalized;
                              Vector3 right = Vector3.Cross(Vector3.up, toCamera).normalized;

                              activeInfoPanel.transform.position = selectedAnchor.smoothedPosition +
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

                    #endregion

                    #region Track Processing with Persistent Identity

                    private void ProcessTracks()
                    {
                              var intrinsics = cameraIntrinsics.Value;
                              Pose cameraPose = new Pose(cameraTransform.position, cameraTransform.rotation);
                              HashSet<int> currentTrackIds = new HashSet<int>();
                              float now = Time.realtimeSinceStartup;

                              // Step 1: Build set of track IDs we're processing this frame
                              var tracksToProcess = new List<TrackedObject>();
                              foreach (var track in objectTracker.ActiveTracks)
                              {
                                        if (track.state == TrackState.Lost) continue;
                                        if (onlyConfirmedTracks && track.state != TrackState.Confirmed) continue;
                                        if (track.confidence < minimumConfidence) continue;

                                        tracksToProcess.Add(track);
                                        currentTrackIds.Add(track.id);
                              }

                              // Step 2: Process each track
                              foreach (var track in tracksToProcess)
                              {
                                        ProcessSingleTrack(track, intrinsics, cameraPose, now);
                              }

                              // Step 3: Handle disassociation for tracks that are no longer present
                              var trackIdsToRemove = trackIdToAnchorGuid.Keys
                                  .Where(trackId => !currentTrackIds.Contains(trackId))
                                  .ToList();

                              foreach (int trackId in trackIdsToRemove)
                              {
                                        if (trackIdToAnchorGuid.TryGetValue(trackId, out string anchorGuid))
                                        {
                                                  if (anchorRegistry.TryGetValue(anchorGuid, out var anchor))
                                                  {
                                                            // Mark as orphaned but keep the anchor
                                                            anchor.currentlyAssociatedTrackId = null;
                                                            anchor.lastSeenTime = now;

                                                            if (enableDebugLogs)
                                                            {
                                                                      Debug.Log($"[DepthAnchor] 👻 Track {trackId} lost → Anchor {anchor.className} " +
                                                                               $"(GUID: {anchorGuid.Substring(0, 8)}...) now ORPHANED");
                                                            }
                                                  }
                                                  trackIdToAnchorGuid.Remove(trackId);
                                        }
                              }

                              // Step 4: Deduplication pass - ensure no two anchors of same class are too close
                              PerformDeduplication(now);
                    }

                    private void ProcessSingleTrack(TrackedObject track, PassthroughCameraIntrinsics intrinsics, Pose cameraPose, float now)
                    {
                              // Check if this track ID is already associated with an anchor
                              if (trackIdToAnchorGuid.TryGetValue(track.id, out string existingGuid))
                              {
                                        // Track is already associated - just update the anchor
                                        if (anchorRegistry.TryGetValue(existingGuid, out var existingAnchor))
                                        {
                                                  UpdateExistingAnchor(existingAnchor, track, intrinsics, cameraPose, now);
                                        }
                                        return;
                              }

                              // This is a new/unknown track ID - try to re-associate with existing anchor
                              Vector3 trackWorldPos = CalculateWorldPosition(track, intrinsics, cameraPose, out bool gotDepthHit, out float normalConfidence);

                              DepthAnchorInstance matchedAnchor = TryFindMatchingAnchor(track.className, trackWorldPos, track.id);

                              if (matchedAnchor != null)
                              {
                                        // Re-association successful!
                                        AssociateTrackToAnchor(track.id, matchedAnchor, now);
                                        UpdateExistingAnchor(matchedAnchor, track, intrinsics, cameraPose, now);

                                        reassociationCount++;

                                        if (enableDebugLogs)
                                        {
                                                  float dist = Vector3.Distance(trackWorldPos, matchedAnchor.worldLockedPosition);
                                                  Debug.Log($"[DepthAnchor] 🔄 RE-ASSOCIATED: Track {track.id} ({track.className}) → " +
                                                           $"Existing anchor (GUID: {matchedAnchor.anchorGuid.Substring(0, 8)}...) | dist={dist:F2}m");
                                        }
                              }
                              else
                              {
                                        // No matching anchor found - create new one
                                        DepthAnchorInstance newAnchor = CreateNewAnchor(track, trackWorldPos, gotDepthHit, normalConfidence, now);
                                        AssociateTrackToAnchor(track.id, newAnchor, now);

                                        if (enableDebugLogs)
                                        {
                                                  Debug.Log($"[DepthAnchor] 🆕 NEW ANCHOR: Track {track.id} ({track.className}) → " +
                                                           $"GUID: {newAnchor.anchorGuid.Substring(0, 8)}...");
                                        }
                              }
                    }

                    /// <summary>
                    /// Try to find an existing anchor that matches the given class and is within reassociation radius.
                    /// Returns the best match or null if no suitable anchor found.
                    /// </summary>
                    private DepthAnchorInstance TryFindMatchingAnchor(string className, Vector3 worldPosition, int excludeTrackId)
                    {
                              DepthAnchorInstance bestMatch = null;
                              float bestDistance = reassociationRadiusMeters;

                              foreach (var anchor in anchorRegistry.Values)
                              {
                                        // Must match class
                                        if (anchor.className != className) continue;

                                        // Skip if anchor is already associated with a different active track
                                        // (we don't want to steal anchors from active tracks)
                                        if (anchor.currentlyAssociatedTrackId.HasValue &&
                                            anchor.currentlyAssociatedTrackId.Value != excludeTrackId &&
                                            trackIdToAnchorGuid.ContainsKey(anchor.currentlyAssociatedTrackId.Value))
                                        {
                                                  continue;
                                        }

                                        // Check distance
                                        float dist = Vector3.Distance(worldPosition, anchor.worldLockedPosition);
                                        if (dist < bestDistance)
                                        {
                                                  bestDistance = dist;
                                                  bestMatch = anchor;
                                        }
                              }

                              return bestMatch;
                    }

                    private void AssociateTrackToAnchor(int trackId, DepthAnchorInstance anchor, float now)
                    {
                              // Remove any existing association for this track
                              if (trackIdToAnchorGuid.ContainsKey(trackId))
                              {
                                        trackIdToAnchorGuid.Remove(trackId);
                              }

                              // Create new association
                              trackIdToAnchorGuid[trackId] = anchor.anchorGuid;
                              anchor.currentlyAssociatedTrackId = trackId;
                              anchor.lastSeenTime = now;
                    }

                    private void UpdateExistingAnchor(DepthAnchorInstance anchor, TrackedObject track,
                        PassthroughCameraIntrinsics intrinsics, Pose cameraPose, float now)
                    {
                              if (ShouldUpdateAnchorPosition(anchor, track, now))
                              {
                                        Vector3 newWorldPos = CalculateWorldPosition(track, intrinsics, cameraPose, out bool gotDepthHit, out float normalConfidence);

                                        if (!anchor.isLocked || (normalConfidence > anchor.lastDepthConfidence &&
                                            Vector3.Distance(newWorldPos, anchor.worldLockedPosition) < updateDistanceThreshold))
                                        {
                                                  anchor.worldLockedPosition = newWorldPos;
                                                  anchor.lastDepthConfidence = normalConfidence;
                                                  anchor.lastPlacementTime = now;

                                                  if (gotDepthHit && normalConfidence >= minConfidenceToLock)
                                                            anchor.isLocked = true;
                                        }
                              }

                              anchor.lastUpdateTime = now;
                              anchor.lastSeenTime = now;
                              anchor.trackState = track.state;
                              anchor.confidence = track.confidence;
                              anchor.trackId = track.id; // Update legacy trackId for info panel
                    }

                    private DepthAnchorInstance CreateNewAnchor(TrackedObject track, Vector3 worldPos, bool gotDepthHit, float normalConfidence, float now)
                    {
                              GameObject obj = GetFromPool();
                              string guid = System.Guid.NewGuid().ToString();

                              var anchor = new DepthAnchorInstance
                              {
                                        anchorGuid = guid,
                                        trackId = track.id,
                                        className = track.className,
                                        gameObject = obj,
                                        transform = obj.transform,
                                        label = obj.GetComponentInChildren<TextMeshPro>(),
                                        renderers = obj.GetComponentsInChildren<Renderer>(),
                                        creationTime = now,
                                        trackState = track.state,
                                        confidence = track.confidence,
                                        worldLockedPosition = worldPos,
                                        smoothedPosition = worldPos,
                                        isLocked = gotDepthHit && normalConfidence >= minConfidenceToLock,
                                        lastDepthConfidence = normalConfidence,
                                        lastPlacementTime = now,
                                        lastUpdateTime = now,
                                        lastSeenTime = now,
                                        currentlyAssociatedTrackId = null // Will be set by AssociateTrackToAnchor
                              };

                              anchor.transform.position = worldPos;

                              if (anchor.label != null && anchor.label.GetComponent<Billboard>() == null)
                                        anchor.label.gameObject.AddComponent<Billboard>();

                              obj.SetActive(true);

                              // Add to registry
                              anchorRegistry[guid] = anchor;

                              return anchor;
                    }

                    /// <summary>
                    /// Deduplication: If two anchors of the same class are within dedupRadius,
                    /// keep only the "better" one (higher confidence, locked, etc.)
                    /// </summary>
                    private void PerformDeduplication(float now)
                    {
                              var anchorsToRemove = new HashSet<string>();

                              var anchorList = anchorRegistry.Values.ToList();

                              for (int i = 0; i < anchorList.Count; i++)
                              {
                                        var anchor1 = anchorList[i];
                                        if (anchorsToRemove.Contains(anchor1.anchorGuid)) continue;

                                        for (int j = i + 1; j < anchorList.Count; j++)
                                        {
                                                  var anchor2 = anchorList[j];
                                                  if (anchorsToRemove.Contains(anchor2.anchorGuid)) continue;

                                                  // Same class?
                                                  if (anchor1.className != anchor2.className) continue;

                                                  // Within dedup radius?
                                                  float dist = Vector3.Distance(anchor1.worldLockedPosition, anchor2.worldLockedPosition);
                                                  if (dist > dedupRadiusMeters) continue;

                                                  // Determine which to keep
                                                  DepthAnchorInstance keepAnchor, removeAnchor;

                                                  if (ShouldPreferAnchor(anchor1, anchor2))
                                                  {
                                                            keepAnchor = anchor1;
                                                            removeAnchor = anchor2;
                                                  }
                                                  else
                                                  {
                                                            keepAnchor = anchor2;
                                                            removeAnchor = anchor1;
                                                  }

                                                  // If the anchor being removed has an associated track, reassign it
                                                  if (removeAnchor.currentlyAssociatedTrackId.HasValue)
                                                  {
                                                            int trackId = removeAnchor.currentlyAssociatedTrackId.Value;
                                                            trackIdToAnchorGuid[trackId] = keepAnchor.anchorGuid;
                                                            keepAnchor.currentlyAssociatedTrackId = trackId;
                                                            keepAnchor.lastSeenTime = now;
                                                  }

                                                  anchorsToRemove.Add(removeAnchor.anchorGuid);
                                                  dedupCount++;

                                                  if (enableDebugLogs)
                                                  {
                                                            Debug.Log($"[DepthAnchor] 🔀 DEDUP: Merged anchor {removeAnchor.anchorGuid.Substring(0, 8)}... " +
                                                                     $"into {keepAnchor.anchorGuid.Substring(0, 8)}... ({keepAnchor.className}, dist={dist:F2}m)");
                                                  }
                                        }
                              }

                              // Actually remove the duplicates
                              foreach (string guid in anchorsToRemove)
                              {
                                        if (anchorRegistry.TryGetValue(guid, out var anchor))
                                        {
                                                  if (selectedAnchor == anchor) DeselectAnchor();
                                                  ReturnToPool(anchor.gameObject);
                                                  anchorRegistry.Remove(guid);
                                        }
                              }
                    }

                    /// <summary>
                    /// Returns true if anchor1 should be preferred over anchor2
                    /// </summary>
                    private bool ShouldPreferAnchor(DepthAnchorInstance a1, DepthAnchorInstance a2)
                    {
                              // Prefer locked over unlocked
                              if (a1.isLocked && !a2.isLocked) return true;
                              if (a2.isLocked && !a1.isLocked) return false;

                              // Prefer associated over orphaned
                              bool a1Associated = a1.currentlyAssociatedTrackId.HasValue;
                              bool a2Associated = a2.currentlyAssociatedTrackId.HasValue;
                              if (a1Associated && !a2Associated) return true;
                              if (a2Associated && !a1Associated) return false;

                              // Prefer confirmed over tentative
                              if (a1.trackState == TrackState.Confirmed && a2.trackState != TrackState.Confirmed) return true;
                              if (a2.trackState == TrackState.Confirmed && a1.trackState != TrackState.Confirmed) return false;

                              // Prefer higher depth confidence
                              if (a1.lastDepthConfidence > a2.lastDepthConfidence + 0.1f) return true;
                              if (a2.lastDepthConfidence > a1.lastDepthConfidence + 0.1f) return false;

                              // Prefer higher detection confidence
                              if (a1.confidence > a2.confidence) return true;
                              if (a2.confidence > a1.confidence) return false;

                              // Prefer older (more established)
                              return a1.creationTime < a2.creationTime;
                    }

                    private bool ShouldUpdateAnchorPosition(DepthAnchorInstance anchor, TrackedObject track, float now)
                    {
                              if (anchor.isLocked && anchor.lastDepthConfidence >= 0.9f) return false;
                              if (now - anchor.lastPlacementTime < lockCooldown) return false;
                              if (track.timeSinceLastUpdate > 0.5f) return false;
                              return true;
                    }

                    private Vector3 CalculateWorldPosition(TrackedObject track, PassthroughCameraIntrinsics intrinsics, Pose cameraPose, out bool gotDepthHit, out float normalConfidence)
                    {
                              gotDepthHit = false;
                              normalConfidence = 0f;

                              Rect bbox = track.bbox2D;
                              float centerX = bbox.x + bbox.width * 0.5f;

                              float targetY = placementPoint switch
                              {
                                        AnchorPlacementPoint.TopCenter => bbox.y + bbox.height,
                                        AnchorPlacementPoint.BottomCenter => bbox.y,
                                        AnchorPlacementPoint.Custom => bbox.y + bbox.height * customYPercent,
                                        _ => bbox.y + bbox.height * 0.5f
                              };

                              Vector2 targetPixel = new Vector2(centerX, targetY);
                              float xNorm = (targetPixel.x - intrinsics.PrincipalPoint.x) / intrinsics.FocalLength.x;
                              float yNorm = (targetPixel.y - intrinsics.PrincipalPoint.y) / intrinsics.FocalLength.y;
                              Vector3 rayDirLocal = new Vector3(xNorm, yNorm, 1f).normalized;
                              Vector3 rayDirWorld = cameraPose.rotation * rayDirLocal;
                              Ray depthRay = new Ray(cameraPose.position, rayDirWorld);

                              float depth = (track.depth > 0.1f) ? track.depth : fallbackDepth;
                              Vector3 cameraForward = cameraPose.rotation * Vector3.forward;
                              float cosAngle = Vector3.Dot(rayDirWorld, cameraForward);
                              float rayDistance = (cosAngle > 0.01f) ? depth / cosAngle : depth;

                              Vector3 worldPosition = depthRay.GetPoint(rayDistance);

                              if (environmentRaycastManager != null && EnvironmentRaycastManager.IsSupported)
                              {
                                        if (environmentRaycastManager.Raycast(depthRay, out EnvironmentRaycastHit hitInfo, maxRaycastDistance))
                                        {
                                                  worldPosition = hitInfo.point;
                                                  normalConfidence = hitInfo.normalConfidence;
                                                  gotDepthHit = true;
                                                  successfulDepthHits++;
                                        }
                                        else
                                        {
                                                  fallbacksUsed++;
                                        }
                              }
                              else
                              {
                                        fallbacksUsed++;
                              }

                              worldPosition += Vector3.up * verticalOffset;

                              if (drawDebugRays)
                              {
                                        Debug.DrawRay(cameraPose.position, rayDirWorld * Vector3.Distance(cameraPose.position, worldPosition),
                                            gotDepthHit ? Color.green : Color.yellow, 0.1f);
                              }

                              return worldPosition;
                    }

                    #endregion

                    #region Anchor Visuals

                    private void UpdateAnchorVisuals()
                    {
                              foreach (var anchor in anchorRegistry.Values)
                              {
                                        anchor.smoothedPosition = Vector3.Lerp(anchor.smoothedPosition, anchor.worldLockedPosition, smoothingAlpha);

                                        if (Vector3.Distance(anchor.transform.position, anchor.smoothedPosition) > movementThreshold)
                                                  anchor.transform.position = anchor.smoothedPosition;

                                        float distance = Vector3.Distance(cameraTransform.position, anchor.smoothedPosition);
                                        float scale = scaleWithDistance ? Mathf.Clamp(distance * 0.03f, 0.02f, 0.2f) : anchorScale;
                                        anchor.transform.localScale = Vector3.one * scale;

                                        // Determine color based on state
                                        Color color;
                                        if (anchor.isSelected)
                                        {
                                                  color = selectedColor;
                                        }
                                        else if (!anchor.currentlyAssociatedTrackId.HasValue)
                                        {
                                                  color = orphanedColor; // Orphaned anchors shown in gray
                                        }
                                        else if (anchor.isLocked)
                                        {
                                                  color = lockedColor;
                                        }
                                        else if (anchor.trackState == TrackState.Confirmed)
                                        {
                                                  color = confirmedColor;
                                        }
                                        else
                                        {
                                                  color = tentativeColor;
                                        }

                                        foreach (var r in anchor.renderers)
                                                  if (r.material != null) r.material.color = color;

                                        if (anchor.label != null)
                                        {
                                                  string symbol;
                                                  if (anchor.isSelected) symbol = "★";
                                                  else if (!anchor.currentlyAssociatedTrackId.HasValue) symbol = "👻";
                                                  else if (anchor.isLocked) symbol = "🔒";
                                                  else symbol = "◇";

                                                  anchor.label.text = $"{symbol} {anchor.className}\n{anchor.confidence:F2}";
                                                  anchor.label.color = color;
                                        }

                                        if (anchor.isSelected && activeInfoPanel != null && activeInfoPanel.activeSelf)
                                        {
                                                  var infoPanel = activeInfoPanel.GetComponent<AnchorInfoPanel>();
                                                  if (infoPanel != null) infoPanel.UpdateInfo(anchor);
                                                  else UpdateInfoPanelText(anchor);
                                        }
                              }
                    }

                    private void CleanupOrphanedAnchors()
                    {
                              if (!orphanCleanupEnabled) return;

                              float now = Time.realtimeSinceStartup;
                              var anchorsToRemove = new List<string>();

                              foreach (var kvp in anchorRegistry)
                              {
                                        var anchor = kvp.Value;

                                        // Only cleanup orphaned (unassociated) anchors
                                        if (anchor.currentlyAssociatedTrackId.HasValue) continue;

                                        float orphanAge = now - anchor.lastSeenTime;
                                        if (orphanAge > orphanTTLSeconds)
                                        {
                                                  anchorsToRemove.Add(kvp.Key);

                                                  if (enableDebugLogs)
                                                  {
                                                            Debug.Log($"[DepthAnchor] 🗑️ Removing orphaned anchor {anchor.className} " +
                                                                     $"(GUID: {anchor.anchorGuid.Substring(0, 8)}...) after {orphanAge:F0}s");
                                                  }
                                        }
                              }

                              foreach (string guid in anchorsToRemove)
                              {
                                        if (anchorRegistry.TryGetValue(guid, out var anchor))
                                        {
                                                  if (selectedAnchor == anchor) DeselectAnchor();
                                                  ReturnToPool(anchor.gameObject);
                                                  anchorRegistry.Remove(guid);
                                        }
                              }
                    }

                    private void UpdateLockedCount()
                    {
                              lockedAnchors = 0;
                              foreach (var anchor in anchorRegistry.Values)
                                        if (anchor.isLocked) lockedAnchors++;
                    }

                    #endregion

                    #region Pool

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

                    #endregion

                    #region Public API

                    public void ClearAllAnchors()
                    {
                              DeselectAnchor();
                              foreach (var anchor in anchorRegistry.Values)
                                        ReturnToPool(anchor.gameObject);
                              anchorRegistry.Clear();
                              trackIdToAnchorGuid.Clear();
                    }

                    public void ResetStatistics()
                    {
                              successfulDepthHits = 0;
                              fallbacksUsed = 0;
                              reassociationCount = 0;
                              dedupCount = 0;
                    }

                    public void UnlockAllAnchors()
                    {
                              foreach (var anchor in anchorRegistry.Values) anchor.isLocked = false;
                    }

                    /// <summary>
                    /// Get anchor by its currently associated track ID (may return null if track is orphaned)
                    /// </summary>
                    public DepthAnchorInstance GetAnchorByTrackId(int trackId)
                    {
                              if (trackIdToAnchorGuid.TryGetValue(trackId, out string guid))
                              {
                                        if (anchorRegistry.TryGetValue(guid, out var anchor))
                                        {
                                                  return anchor;
                                        }
                              }
                              return null;
                    }

                    /// <summary>
                    /// Get anchor by its persistent GUID
                    /// </summary>
                    public DepthAnchorInstance GetAnchorByGuid(string guid)
                    {
                              anchorRegistry.TryGetValue(guid, out var anchor);
                              return anchor;
                    }

                    /// <summary>
                    /// Get all anchors (including orphaned ones)
                    /// </summary>
                    public IEnumerable<DepthAnchorInstance> GetAllAnchors()
                    {
                              return anchorRegistry.Values;
                    }

                    /// <summary>
                    /// Get only anchors that are currently associated with active tracks
                    /// </summary>
                    public IEnumerable<DepthAnchorInstance> GetAssociatedAnchors()
                    {
                              return anchorRegistry.Values.Where(a => a.currentlyAssociatedTrackId.HasValue);
                    }

                    /// <summary>
                    /// Get only orphaned anchors
                    /// </summary>
                    public IEnumerable<DepthAnchorInstance> GetOrphanedAnchors()
                    {
                              return anchorRegistry.Values.Where(a => !a.currentlyAssociatedTrackId.HasValue);
                    }

                    /// <summary>
                    /// Manually force cleanup of all orphaned anchors
                    /// </summary>
                    public void CleanupAllOrphanedAnchors()
                    {
                              var orphanedGuids = anchorRegistry
                                  .Where(kvp => !kvp.Value.currentlyAssociatedTrackId.HasValue)
                                  .Select(kvp => kvp.Key)
                                  .ToList();

                              foreach (string guid in orphanedGuids)
                              {
                                        if (anchorRegistry.TryGetValue(guid, out var anchor))
                                        {
                                                  if (selectedAnchor == anchor) DeselectAnchor();
                                                  ReturnToPool(anchor.gameObject);
                                                  anchorRegistry.Remove(guid);
                                        }
                              }

                              if (enableDebugLogs)
                              {
                                        Debug.Log($"[DepthAnchor] 🗑️ Manually cleaned up {orphanedGuids.Count} orphaned anchors");
                              }
                    }

                    #endregion

                    private void OnDestroy()
                    {
                              ClearAllAnchors();
                              if (activeInfoPanel != null) Destroy(activeInfoPanel);
                    }

                    private void OnGUI()
                    {
                              if (!enableDebugLogs) return;
                              GUILayout.BeginArea(new Rect(10, 10, 350, 150));
                              GUILayout.Label($"Anchors: {anchorRegistry.Count} total | {AssociatedAnchorCount} active | {OrphanedAnchorCount} orphaned");
                              GUILayout.Label($"Locked: {lockedAnchors} | Reassocs: {reassociationCount} | Dedups: {dedupCount}");
                              GUILayout.Label($"Selected: {(selectedAnchor != null ? $"{selectedAnchor.className}" : "None")}");
                              GUILayout.Label($"Settings: ReassocR={reassociationRadiusMeters}m, DedupR={dedupRadiusMeters}m");
                              GUILayout.EndArea();
                    }

                    [System.Serializable]
                    public class DepthAnchorInstance
                    {
                              // === PERSISTENT IDENTITY ===
                              public string anchorGuid;                    // Stable GUID that survives track ID changes
                              public int? currentlyAssociatedTrackId;      // Current track ID (null if orphaned)

                              // === Legacy (kept for backward compat with AnchorInfoPanel) ===
                              public int trackId;                          // Last known track ID
                              public string className;

                              public GameObject gameObject;
                              public Transform transform;

                              public TextMeshPro label;
                              public Renderer[] renderers;

                              public Vector3 worldLockedPosition;
                              public Vector3 smoothedPosition;

                              public bool isLocked;
                              public float lastDepthConfidence;
                              public float lastPlacementTime;

                              public bool isSelected;

                              public TrackState trackState;
                              public float confidence;

                              public float creationTime;
                              public float lastUpdateTime;
                              public float lastSeenTime;                   // When this anchor was last associated with a track
                    }
          }
}