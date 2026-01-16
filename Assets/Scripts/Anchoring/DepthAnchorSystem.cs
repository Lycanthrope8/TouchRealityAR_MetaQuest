using UnityEngine;
using UnityEngine.Events;
using System.Collections.Generic;
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
          /// WORLD-LOCKED Depth Anchor System with Interaction Support
          /// + Visible controller selection ray (LineRenderer is provided by you in the scene)
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
                    [SerializeField] private bool onlyConfirmedTracks = false;
                    [SerializeField] private float minimumConfidence = 0.3f;

                    [Header("Visual Settings")]
                    [SerializeField] private float anchorScale = 0.1f;
                    [SerializeField] private bool scaleWithDistance = false;
                    [SerializeField] private Color confirmedColor = new Color(0.2f, 1f, 0.4f, 1f);
                    [SerializeField] private Color tentativeColor = new Color(1f, 0.85f, 0.2f, 0.9f);
                    [SerializeField] private Color lockedColor = new Color(0f, 0.8f, 1f, 1f);
                    [SerializeField] private Color selectedColor = new Color(1f, 0.5f, 0f, 1f);

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;
                    [SerializeField] private bool drawDebugRays = false;

                    [Header("Events")]
                    public UnityEvent<DepthAnchorInstance> OnAnchorSelected;
                    public UnityEvent OnAnchorDeselected;

                    private Dictionary<int, DepthAnchorInstance> anchors = new Dictionary<int, DepthAnchorInstance>();
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

                    public int ActiveAnchorCount => anchors.Count;
                    public int SuccessfulDepthHits => successfulDepthHits;
                    public int FallbacksUsed => fallbacksUsed;
                    public int LockedAnchors => lockedAnchors;
                    public DepthAnchorInstance SelectedAnchor => selectedAnchor;

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

                              Debug.Log("[DepthAnchor] Initialized with interaction + selection ray support");
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
                              CleanupStaleAnchors();
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

                                        foreach (var anchor in anchors.Values)
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
                                        Debug.Log($"[DepthAnchor] Selected: {anchor.className}#{anchor.trackId}");

                              ShowInfoPanel(anchor);
                              OnAnchorSelected?.Invoke(anchor);
                    }

                    private void DeselectAnchor()
                    {
                              if (selectedAnchor != null)
                              {
                                        selectedAnchor.isSelected = false;

                                        if (enableDebugLogs)
                                                  Debug.Log($"[DepthAnchor] Deselected: {selectedAnchor.className}#{selectedAnchor.trackId}");

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
                                        tmp.text = $"<b>{anchor.className}</b>\n" +
                                                   $"───────────\n" +
                                                   $"ID: #{anchor.trackId}\n" +
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

                    #region Track Processing

                    private void ProcessTracks()
                    {
                              var intrinsics = cameraIntrinsics.Value;
                              Pose cameraPose = new Pose(cameraTransform.position, cameraTransform.rotation);
                              HashSet<int> currentTrackIds = new HashSet<int>();
                              float now = Time.realtimeSinceStartup;

                              foreach (var track in objectTracker.ActiveTracks)
                              {
                                        if (track.state == TrackState.Lost) continue;
                                        if (onlyConfirmedTracks && track.state != TrackState.Confirmed) continue;
                                        if (track.confidence < minimumConfidence) continue;

                                        currentTrackIds.Add(track.id);

                                        if (!anchors.TryGetValue(track.id, out var anchor))
                                        {
                                                  anchor = CreateAnchor(track);
                                                  anchors[track.id] = anchor;

                                                  Vector3 worldPos = CalculateWorldPosition(track, intrinsics, cameraPose, out bool gotDepthHit, out float normalConfidence);

                                                  anchor.worldLockedPosition = worldPos;
                                                  anchor.smoothedPosition = worldPos;
                                                  anchor.transform.position = worldPos;
                                                  anchor.isLocked = gotDepthHit && normalConfidence >= minConfidenceToLock;
                                                  anchor.lastDepthConfidence = normalConfidence;
                                                  anchor.lastPlacementTime = now;
                                        }
                                        else
                                        {
                                                  if (ShouldUpdateAnchor(anchor, track, now))
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
                                                  anchor.trackState = track.state;
                                                  anchor.confidence = track.confidence;
                                                  anchor.markedForRemoval = false;
                                        }
                              }

                              foreach (var kvp in anchors)
                              {
                                        if (!currentTrackIds.Contains(kvp.Key))
                                        {
                                                  kvp.Value.markedForRemoval = true;
                                                  if (selectedAnchor == kvp.Value) DeselectAnchor();
                                        }
                              }
                    }

                    private bool ShouldUpdateAnchor(DepthAnchorInstance anchor, TrackedObject track, float now)
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

                    private DepthAnchorInstance CreateAnchor(TrackedObject track)
                    {
                              GameObject obj = GetFromPool();

                              var anchor = new DepthAnchorInstance
                              {
                                        trackId = track.id,
                                        className = track.className,
                                        gameObject = obj,
                                        transform = obj.transform,
                                        label = obj.GetComponentInChildren<TextMeshPro>(),
                                        renderers = obj.GetComponentsInChildren<Renderer>(),
                                        creationTime = Time.realtimeSinceStartup,
                                        trackState = track.state,
                                        confidence = track.confidence
                              };

                              if (anchor.label != null && anchor.label.GetComponent<Billboard>() == null)
                                        anchor.label.gameObject.AddComponent<Billboard>();

                              obj.SetActive(true);
                              return anchor;
                    }

                    private void UpdateAnchorVisuals()
                    {
                              foreach (var anchor in anchors.Values)
                              {
                                        if (anchor.markedForRemoval) continue;

                                        anchor.smoothedPosition = Vector3.Lerp(anchor.smoothedPosition, anchor.worldLockedPosition, smoothingAlpha);

                                        if (Vector3.Distance(anchor.transform.position, anchor.smoothedPosition) > movementThreshold)
                                                  anchor.transform.position = anchor.smoothedPosition;

                                        float distance = Vector3.Distance(cameraTransform.position, anchor.smoothedPosition);
                                        float scale = scaleWithDistance ? Mathf.Clamp(distance * 0.03f, 0.02f, 0.2f) : anchorScale;
                                        anchor.transform.localScale = Vector3.one * scale;

                                        Color color = anchor.isSelected ? selectedColor :
                                                      anchor.isLocked ? lockedColor :
                                                      anchor.trackState == TrackState.Confirmed ? confirmedColor : tentativeColor;

                                        foreach (var r in anchor.renderers)
                                                  if (r.material != null) r.material.color = color;

                                        if (anchor.label != null)
                                        {
                                                  string symbol = anchor.isSelected ? "★" : (anchor.isLocked ? "🔒" : "◇");
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

                    private void CleanupStaleAnchors()
                    {
                              List<int> toRemove = new List<int>();
                              foreach (var kvp in anchors)
                                        if (kvp.Value.markedForRemoval) toRemove.Add(kvp.Key);

                              foreach (int id in toRemove)
                              {
                                        if (anchors.TryGetValue(id, out var anchor))
                                        {
                                                  ReturnToPool(anchor.gameObject);
                                                  anchors.Remove(id);
                                        }
                              }
                    }

                    private void UpdateLockedCount()
                    {
                              lockedAnchors = 0;
                              foreach (var anchor in anchors.Values)
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
                              foreach (var anchor in anchors.Values) ReturnToPool(anchor.gameObject);
                              anchors.Clear();
                    }

                    public void ResetStatistics()
                    {
                              successfulDepthHits = 0;
                              fallbacksUsed = 0;
                    }

                    public void UnlockAllAnchors()
                    {
                              foreach (var anchor in anchors.Values) anchor.isLocked = false;
                    }

                    public DepthAnchorInstance GetAnchorByTrackId(int trackId)
                    {
                              anchors.TryGetValue(trackId, out var anchor);
                              return anchor;
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
                              GUILayout.BeginArea(new Rect(10, 10, 320, 120));
                              GUILayout.Label($"Anchors: {anchors.Count} | Locked: {lockedAnchors}");
                              GUILayout.Label($"Selected: {(selectedAnchor != null ? $"{selectedAnchor.className}#{selectedAnchor.trackId}" : "None")}");
                              GUILayout.EndArea();
                    }

                    [System.Serializable]
                    public class DepthAnchorInstance
                    {
                              public int trackId;
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

                              public bool markedForRemoval;
                    }
          }
}
