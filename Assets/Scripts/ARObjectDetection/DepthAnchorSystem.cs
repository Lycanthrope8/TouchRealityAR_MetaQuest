using UnityEngine;
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
                    TopCenter,      // Top edge of bbox (anchor appears above object)
                    Center,         // Center of bbox (anchor appears on object surface)
                    BottomCenter,   // Bottom edge of bbox (anchor appears at object base)
                    Custom          // Use custom Y percentage
          }

          /// <summary>
          /// WORLD-LOCKED Depth Anchor System
          /// 
          /// Key fix: Anchors are placed once with accurate depth and then LOCKED in world space.
          /// They only update position when a NEW detection comes in (not every frame).
          /// This prevents anchors from moving when you turn around.
          /// </summary>
          public class DepthAnchorSystem : MonoBehaviour
          {
                    [Header("Required References")]
                    [SerializeField] private EnvironmentRaycastManager environmentRaycastManager;
                    [SerializeField] private GameObject anchorPrefab;
                    [SerializeField] private ObjectTracker objectTracker;

                    [Header("Camera Settings")]
                    [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

                    [Header("Anchor Positioning")]
                    [Tooltip("Which part of the bounding box to use for anchor placement")]
                    [SerializeField] private AnchorPlacementPoint placementPoint = AnchorPlacementPoint.Center;

                    [Tooltip("Vertical offset from the hit surface (meters). Positive = up, Negative = down")]
                    [SerializeField] private float verticalOffset = 0.02f;

                    [SerializeField] private float fallbackDepth = 2.0f;
                    [SerializeField] private float maxRaycastDistance = 10f;

                    [Tooltip("Custom Y position within bbox (0 = bottom, 0.5 = center, 1 = top). Only used when placementPoint = Custom")]
                    [Range(0f, 1f)]
                    [SerializeField] private float customYPercent = 0.5f;

                    [Header("World Lock Settings")]
                    [Tooltip("Minimum normal confidence to lock anchor position (0-1)")]
                    [Range(0f, 1f)]
                    [SerializeField] private float minConfidenceToLock = 0.5f;

                    [Tooltip("How close a new detection must be to update a locked anchor (meters)")]
                    [SerializeField] private float updateDistanceThreshold = 0.15f;

                    [Tooltip("Time before an unconfirmed anchor can be repositioned (seconds)")]
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
                    [SerializeField] private Color lockedColor = new Color(0f, 0.8f, 1f, 1f);  // Cyan for locked

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;
                    [SerializeField] private bool drawDebugRays = false;

                    // Internal state
                    private Dictionary<int, DepthAnchorInstance> anchors = new Dictionary<int, DepthAnchorInstance>();
                    private Queue<GameObject> anchorPool = new Queue<GameObject>();

                    private PassthroughCameraIntrinsics? cameraIntrinsics;
                    private Transform cameraTransform;

                    // Track which frame IDs we've processed to avoid duplicate updates
                    private int lastProcessedFrameId = -1;

                    // Statistics
                    private int successfulDepthHits = 0;
                    private int fallbacksUsed = 0;
                    private int lockedAnchors = 0;

                    public int ActiveAnchorCount => anchors.Count;
                    public int SuccessfulDepthHits => successfulDepthHits;
                    public int FallbacksUsed => fallbacksUsed;
                    public int LockedAnchors => lockedAnchors;

                    private void Awake()
                    {
                              if (!Initialize())
                              {
                                        enabled = false;
                              }
                    }

                    private bool Initialize()
                    {
                              if (anchorPrefab == null)
                              {
                                        Debug.LogError("[DepthAnchor] ❌ Anchor prefab not assigned!");
                                        return false;
                              }

                              if (objectTracker == null)
                              {
                                        objectTracker = FindFirstObjectByType<ObjectTracker>();
                                        if (objectTracker == null)
                                        {
                                                  Debug.LogError("[DepthAnchor] ❌ ObjectTracker not found!");
                                                  return false;
                                        }
                              }

                              if (environmentRaycastManager == null)
                              {
                                        environmentRaycastManager = FindFirstObjectByType<EnvironmentRaycastManager>();
                                        if (environmentRaycastManager == null)
                                        {
                                                  Debug.LogError("[DepthAnchor] ❌ EnvironmentRaycastManager not found!");
                                                  return false;
                                        }
                              }

                              if (!EnvironmentRaycastManager.IsSupported)
                              {
                                        Debug.LogWarning("[DepthAnchor] ⚠️ Environment Raycast not supported. Using fallback depth.");
                              }

                              try
                              {
                                        cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(cameraEye);
                                        var cam = cameraIntrinsics.Value;
                                        Debug.Log($"[DepthAnchor] ✅ Camera: {cam.Resolution.x}×{cam.Resolution.y}");
                              }
                              catch (System.Exception e)
                              {
                                        Debug.LogError($"[DepthAnchor] ❌ Camera intrinsics failed: {e.Message}");
                                        return false;
                              }

                              if (OVRManager.instance != null)
                              {
                                        cameraTransform = OVRManager.instance.GetComponentInChildren<Camera>()?.transform;
                              }
                              if (cameraTransform == null) cameraTransform = Camera.main?.transform;
                              if (cameraTransform == null)
                              {
                                        Debug.LogError("[DepthAnchor] ❌ No camera transform!");
                                        return false;
                              }

                              // Pre-warm pool
                              for (int i = 0; i < 20; i++)
                              {
                                        var obj = Instantiate(anchorPrefab, transform);
                                        obj.SetActive(false);
                                        anchorPool.Enqueue(obj);
                              }

                              Debug.Log("[DepthAnchor] ✅ World-Locked Depth Anchor System initialized");
                              return true;
                    }

                    private void Update()
                    {
                              if (objectTracker == null || !cameraIntrinsics.HasValue || cameraTransform == null)
                                        return;

                              ProcessTracks();
                              UpdateAnchorVisuals();  // Only update visuals, not positions
                              CleanupStaleAnchors();
                              UpdateLockedCount();
                    }

                    /// <summary>
                    /// Process tracks - only update anchor positions when we have NEW detection data
                    /// </summary>
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

                                        bool anchorExists = anchors.TryGetValue(track.id, out var anchor);

                                        if (!anchorExists)
                                        {
                                                  // Create new anchor
                                                  anchor = CreateAnchor(track);
                                                  anchors[track.id] = anchor;

                                                  // Calculate initial position with depth
                                                  Vector3 worldPos = CalculateWorldPosition(track, intrinsics, cameraPose, out bool gotDepthHit, out float normalConfidence);

                                                  anchor.worldLockedPosition = worldPos;
                                                  anchor.smoothedPosition = worldPos;
                                                  anchor.transform.position = worldPos;
                                                  anchor.isLocked = gotDepthHit && normalConfidence >= minConfidenceToLock;
                                                  anchor.lastDepthConfidence = normalConfidence;
                                                  anchor.lastPlacementTime = now;

                                                  if (enableDebugLogs)
                                                  {
                                                            Debug.Log($"[DepthAnchor] 🆕 Created {(anchor.isLocked ? "LOCKED" : "unlocked")} anchor for {track.className}#{track.id} at {worldPos}");
                                                  }
                                        }
                                        else
                                        {
                                                  // Existing anchor - only update if we have new detection data AND conditions are met
                                                  bool shouldUpdate = ShouldUpdateAnchor(anchor, track, intrinsics, cameraPose, now);

                                                  if (shouldUpdate)
                                                  {
                                                            Vector3 newWorldPos = CalculateWorldPosition(track, intrinsics, cameraPose, out bool gotDepthHit, out float normalConfidence);

                                                            // Only update if this is a better measurement or anchor isn't locked yet
                                                            bool betterMeasurement = normalConfidence > anchor.lastDepthConfidence;
                                                            bool notLockedYet = !anchor.isLocked;
                                                            bool closeEnough = Vector3.Distance(newWorldPos, anchor.worldLockedPosition) < updateDistanceThreshold;

                                                            if (notLockedYet || (betterMeasurement && closeEnough))
                                                            {
                                                                      anchor.worldLockedPosition = newWorldPos;
                                                                      anchor.lastDepthConfidence = normalConfidence;
                                                                      anchor.lastPlacementTime = now;

                                                                      if (gotDepthHit && normalConfidence >= minConfidenceToLock)
                                                                      {
                                                                                anchor.isLocked = true;
                                                                                if (enableDebugLogs)
                                                                                {
                                                                                          Debug.Log($"[DepthAnchor] 🔒 LOCKED anchor {track.className}#{track.id} at {newWorldPos}");
                                                                                }
                                                                      }
                                                            }
                                                  }

                                                  // Always update metadata
                                                  anchor.lastUpdateTime = now;
                                                  anchor.trackState = track.state;
                                                  anchor.confidence = track.confidence;
                                                  anchor.markedForRemoval = false;
                                        }
                              }

                              // Mark anchors for tracks that no longer exist
                              foreach (var kvp in anchors)
                              {
                                        if (!currentTrackIds.Contains(kvp.Key))
                                        {
                                                  kvp.Value.markedForRemoval = true;
                                        }
                              }
                    }

                    /// <summary>
                    /// Determine if we should update an anchor's position
                    /// </summary>
                    private bool ShouldUpdateAnchor(DepthAnchorInstance anchor, TrackedObject track, PassthroughCameraIntrinsics intrinsics, Pose cameraPose, float now)
                    {
                              // If anchor is locked with high confidence, don't update
                              if (anchor.isLocked && anchor.lastDepthConfidence >= 0.9f)
                              {
                                        return false;
                              }

                              // Cooldown check
                              if (now - anchor.lastPlacementTime < lockCooldown)
                              {
                                        return false;
                              }

                              // Check if track has been updated recently (new detection came in)
                              if (track.timeSinceLastUpdate > 0.5f)
                              {
                                        return false;  // Track is stale, don't update
                              }

                              return true;
                    }

                    /// <summary>
                    /// Calculate world position using depth raycast
                    /// </summary>
                    private Vector3 CalculateWorldPosition(TrackedObject track, PassthroughCameraIntrinsics intrinsics, Pose cameraPose, out bool gotDepthHit, out float normalConfidence)
                    {
                              gotDepthHit = false;
                              normalConfidence = 0f;

                              // Get bbox and calculate target pixel based on placement point
                              Rect bbox = track.bbox2D;
                              float centerX = bbox.x + bbox.width * 0.5f;
                              float targetY;

                              switch (placementPoint)
                              {
                                        case AnchorPlacementPoint.TopCenter:
                                                  targetY = bbox.y + bbox.height;  // Top edge
                                                  break;
                                        case AnchorPlacementPoint.BottomCenter:
                                                  targetY = bbox.y;  // Bottom edge
                                                  break;
                                        case AnchorPlacementPoint.Custom:
                                                  targetY = bbox.y + bbox.height * customYPercent;
                                                  break;
                                        case AnchorPlacementPoint.Center:
                                        default:
                                                  targetY = bbox.y + bbox.height * 0.5f;  // Center
                                                  break;
                              }

                              Vector2 targetPixel = new Vector2(centerX, targetY);

                              // Convert to ray
                              float xNorm = (targetPixel.x - intrinsics.PrincipalPoint.x) / intrinsics.FocalLength.x;
                              float yNorm = (targetPixel.y - intrinsics.PrincipalPoint.y) / intrinsics.FocalLength.y;
                              Vector3 rayDirLocal = new Vector3(xNorm, yNorm, 1f).normalized;
                              Vector3 rayDirWorld = cameraPose.rotation * rayDirLocal;
                              Ray depthRay = new Ray(cameraPose.position, rayDirWorld);

                              // Fallback position
                              float depth = (track.depth > 0.1f) ? track.depth : fallbackDepth;
                              Vector3 cameraForward = cameraPose.rotation * Vector3.forward;
                              float cosAngle = Vector3.Dot(rayDirWorld, cameraForward);
                              float rayDistance = (cosAngle > 0.01f) ? depth / cosAngle : depth;
                              Vector3 worldPosition = depthRay.GetPoint(rayDistance);

                              // Try depth raycast
                              if (environmentRaycastManager != null && EnvironmentRaycastManager.IsSupported)
                              {
                                        if (environmentRaycastManager.Raycast(depthRay, out EnvironmentRaycastHit hitInfo, maxRaycastDistance))
                                        {
                                                  worldPosition = hitInfo.point;
                                                  normalConfidence = hitInfo.normalConfidence;
                                                  gotDepthHit = true;
                                                  successfulDepthHits++;

                                                  if (enableDebugLogs && Time.frameCount % 60 == 0)
                                                  {
                                                            Debug.Log($"[DepthAnchor] ✅ DEPTH HIT: {track.className} at {worldPosition}, " +
                                                                      $"distance={Vector3.Distance(cameraPose.position, worldPosition):F3}m, " +
                                                                      $"normalConfidence={normalConfidence:F2}");
                                                  }
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

                              // Apply vertical offset
                              worldPosition += Vector3.up * verticalOffset;

                              // Debug rays
                              if (drawDebugRays)
                              {
                                        Color rayColor = gotDepthHit ? Color.green : Color.yellow;
                                        Debug.DrawRay(cameraPose.position, rayDirWorld * Vector3.Distance(cameraPose.position, worldPosition), rayColor, 0.5f);
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
                              {
                                        anchor.label.gameObject.AddComponent<Billboard>();
                              }

                              obj.SetActive(true);
                              return anchor;
                    }

                    /// <summary>
                    /// Update visual properties only - position is locked in world space
                    /// </summary>
                    private void UpdateAnchorVisuals()
                    {
                              foreach (var anchor in anchors.Values)
                              {
                                        if (anchor.markedForRemoval) continue;

                                        // Smooth towards locked position (not constantly recalculated position!)
                                        anchor.smoothedPosition = Vector3.Lerp(anchor.smoothedPosition, anchor.worldLockedPosition, smoothingAlpha);

                                        if (Vector3.Distance(anchor.transform.position, anchor.smoothedPosition) > movementThreshold)
                                        {
                                                  anchor.transform.position = anchor.smoothedPosition;
                                        }

                                        // Scale based on distance
                                        float distance = Vector3.Distance(cameraTransform.position, anchor.smoothedPosition);
                                        float scale = scaleWithDistance ? Mathf.Clamp(distance * 0.03f, 0.02f, 0.2f) : anchorScale;
                                        anchor.transform.localScale = Vector3.one * scale;

                                        // Color: locked = cyan, confirmed = green, tentative = yellow
                                        Color color;
                                        if (anchor.isLocked)
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
                                        {
                                                  if (r.material != null) r.material.color = color;
                                        }

                                        if (anchor.label != null)
                                        {
                                                  string lockSymbol = anchor.isLocked ? "🔒" : "◇";
                                                  anchor.label.text = $"{lockSymbol} {anchor.className}\n{anchor.confidence:F2}";
                                                  anchor.label.color = color;
                                        }
                              }
                    }

                    private void CleanupStaleAnchors()
                    {
                              List<int> toRemove = new List<int>();

                              foreach (var kvp in anchors)
                              {
                                        if (kvp.Value.markedForRemoval)
                                        {
                                                  toRemove.Add(kvp.Key);
                                        }
                              }

                              foreach (int id in toRemove)
                              {
                                        if (anchors.TryGetValue(id, out var anchor))
                                        {
                                                  ReturnToPool(anchor.gameObject);
                                                  anchors.Remove(id);

                                                  if (enableDebugLogs)
                                                  {
                                                            Debug.Log($"[DepthAnchor] 🗑️ Removed anchor #{id}");
                                                  }
                                        }
                              }
                    }

                    private void UpdateLockedCount()
                    {
                              lockedAnchors = 0;
                              foreach (var anchor in anchors.Values)
                              {
                                        if (anchor.isLocked) lockedAnchors++;
                              }
                    }

                    #region Pool Management

                    private GameObject GetFromPool()
                    {
                              if (anchorPool.Count > 0) return anchorPool.Dequeue();
                              return Instantiate(anchorPrefab, transform);
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
                              foreach (var anchor in anchors.Values)
                              {
                                        ReturnToPool(anchor.gameObject);
                              }
                              anchors.Clear();
                    }

                    public void ResetStatistics()
                    {
                              successfulDepthHits = 0;
                              fallbacksUsed = 0;
                    }

                    public void UnlockAllAnchors()
                    {
                              foreach (var anchor in anchors.Values)
                              {
                                        anchor.isLocked = false;
                              }
                    }

                    #endregion

                    private void OnDestroy() => ClearAllAnchors();

                    private void OnGUI()
                    {
                              if (!enableDebugLogs) return;
                              GUILayout.BeginArea(new Rect(10, 10, 300, 120));
                              GUILayout.Label($"Active Anchors: {anchors.Count}");
                              GUILayout.Label($"Locked: {lockedAnchors}");
                              GUILayout.Label($"Depth Hits: {successfulDepthHits}");
                              GUILayout.Label($"Fallbacks: {fallbacksUsed}");
                              GUILayout.EndArea();
                    }

                    /// <summary>
                    /// Anchor instance with world-lock support
                    /// </summary>
                    private class DepthAnchorInstance
                    {
                              public int trackId;
                              public string className;
                              public GameObject gameObject;
                              public Transform transform;
                              public TextMeshPro label;
                              public Renderer[] renderers;

                              // World-locked position (doesn't change when you turn)
                              public Vector3 worldLockedPosition;
                              public Vector3 smoothedPosition;

                              // Lock state
                              public bool isLocked;
                              public float lastDepthConfidence;
                              public float lastPlacementTime;

                              // Track state
                              public TrackState trackState;
                              public float confidence;
                              public float creationTime;
                              public float lastUpdateTime;
                              public bool markedForRemoval;
                    }
          }
}