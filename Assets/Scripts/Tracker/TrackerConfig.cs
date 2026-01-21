using UnityEngine;

namespace ARObjectDetection
{
          /// <summary>
          /// Configuration for ObjectTracker - OPTIMIZED FOR LONG-TERM MR UX
          /// 
          /// Design Philosophy:
          /// - Objects don't "disappear" in the real world, they get occluded
          /// - Track IDs should be nearly permanent during a session
          /// - Lost tracks are retained for long periods and can always be revived
          /// - Minimal ID switching = stable anchors/overlays
          /// </summary>
          [CreateAssetMenu(fileName = "TrackerConfig", menuName = "AR Detection/Tracker Config")]
          public class TrackerConfig : ScriptableObject
          {
                    [Header("=== 3D-FIRST ASSOCIATION ===")]
                    [Tooltip("Primary 3D distance gate for normal matching (meters)")]
                    public float max3DDistance = 1.5f;

                    [Tooltip("3D IoU threshold - used as cost factor, NOT hard gate")]
                    [Range(0.0f, 0.5f)]
                    public float iou3DThreshold = 0.05f;

                    [Tooltip("2D IoU threshold - used as cost factor only")]
                    [Range(0.0f, 0.9f)]
                    public float iou2DThreshold = 0.15f;

                    [Tooltip("2D center distance - used as cost factor only, NOT hard gate")]
                    public float maxCenterDistance = 300f;

                    [Header("=== REACQUISITION / REVIVAL ===")]
                    [Tooltip("Max 3D distance for reviving Lost tracks - relaxed because estimates are noisier after occlusion")]
                    public float reacquireMax3DDistance = 2.5f;

                    [Tooltip("Time window during which Lost tracks can be revived via matching (seconds)")]
                    [Range(5f, 120f)]
                    public float reacquireWindowSec = 60f;

                    [Header("=== TRACK LIFECYCLE (Long-Term MR) ===")]
                    [Tooltip("How long CONFIRMED tracks tolerate no detections before going Lost")]
                    [Range(2f, 30f)]
                    public float confirmedMaxMissTimeSec = 10f;

                    [Tooltip("How long TENTATIVE tracks tolerate no detections before going Lost")]
                    [Range(1f, 10f)]
                    public float tentativeMaxMissTimeSec = 3f;

                    [Tooltip("How long Lost tracks are RETAINED before permanent deletion (seconds) - SET HIGH for long-term MR")]
                    [Range(10f, 300f)]
                    public float lostRetentionTimeSec = 120f;

                    [Tooltip("(LEGACY) Kept for backward compat")]
                    public float maxMissTimeSec = 2.5f;

                    [Header("=== TRACK CONFIRMATION ===")]
                    [Tooltip("Minimum successful matches to become Confirmed")]
                    [Range(1, 10)]
                    public int minHits = 3;

                    [Header("=== 3D BOUNDS ESTIMATION ===")]
                    [Tooltip("Depth thickness as fraction of center depth")]
                    [Range(0.05f, 0.5f)]
                    public float depthThicknessFraction = 0.15f;

                    [Tooltip("Minimum bounds thickness in meters")]
                    public float minBoundsThickness = 0.10f;

                    [Tooltip("Maximum bounds thickness in meters")]
                    public float maxBoundsThickness = 0.80f;

                    [Header("=== SMOOTHING ===")]
                    [Tooltip("Position smoothing (lower = smoother, higher = more responsive)")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaPosition = 0.4f;

                    [Tooltip("Size smoothing")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaSize = 0.2f;

                    [Tooltip("Velocity smoothing")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaVelocity = 0.6f;

                    [Header("=== MOTION COMPENSATION ===")]
                    [Tooltip("Enable camera motion compensation for active tracks")]
                    public bool enableMotionCompensation = true;

                    [Tooltip("Motion-compensate Lost tracks during reacquire window (keeps 2D state fresh)")]
                    public bool motionCompensateLostTracks = true;

                    [Tooltip("Continue velocity prediction for Lost tracks (keeps worldPosition moving)")]
                    public bool predictLostTrackMotion = false;

                    [Tooltip("Maximum expected object velocity (m/s)")]
                    public float maxObjectVelocity = 5.0f;

                    [Header("=== DUPLICATE PREVENTION ===")]
                    [Tooltip("Minimum distance between tracks of same class (meters)")]
                    public float minTrackSeparation = 0.25f;

                    [Tooltip("When merging duplicates, prefer the older track")]
                    public bool preferOlderTrackOnMerge = true;

                    [Header("=== DEBUG ===")]
                    [Tooltip("Enable debug logs")]
                    public bool enableDebugLogs = true;

                    [Tooltip("Show track IDs in 3D visualizations")]
                    public bool showTrackIDs = true;

                    [Tooltip("Show revival/lifecycle metrics in OnGUI")]
                    public bool showRevivalMetrics = true;

                    [Tooltip("Log metrics summary every N seconds (0 = disabled)")]
                    public float metricsSummaryIntervalSec = 5.0f;

                    private void OnValidate()
                    {
                              // Ensure reacquire distance >= normal distance
                              if (reacquireMax3DDistance < max3DDistance)
                              {
                                        Debug.LogWarning($"[TrackerConfig] reacquireMax3DDistance should be >= max3DDistance. Auto-correcting.");
                                        reacquireMax3DDistance = max3DDistance * 1.5f;
                              }

                              // Ensure retention > reacquire window
                              if (lostRetentionTimeSec < reacquireWindowSec)
                              {
                                        Debug.LogWarning($"[TrackerConfig] lostRetentionTimeSec should be >= reacquireWindowSec. Auto-correcting.");
                                        lostRetentionTimeSec = reacquireWindowSec * 1.5f;
                              }

                              // Warn if retention is short for long-term MR
                              if (lostRetentionTimeSec < 30f)
                              {
                                        Debug.LogWarning($"[TrackerConfig] lostRetentionTimeSec={lostRetentionTimeSec}s is short for long-term MR. Consider 60-120s.");
                              }
                    }
          }
}