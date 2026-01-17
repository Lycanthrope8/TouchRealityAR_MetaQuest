using UnityEngine;

namespace ARObjectDetection
{
          [CreateAssetMenu(fileName = "TrackerConfig", menuName = "AR Detection/Tracker Config")]
          public class TrackerConfig : ScriptableObject
          {
                    [Header("=== 3D-FIRST ASSOCIATION (MR Best Practice) ===")]
                    [Tooltip("Primary 3D distance gate for normal matching (meters)")]
                    public float max3DDistance = 1.5f;

                    [Tooltip("Minimum 3D IoU - used as cost factor, NOT hard gate")]
                    [Range(0.0f, 0.5f)]
                    public float iou3DThreshold = 0.05f;

                    [Tooltip("Minimum 2D IoU - used as cost factor, NOT hard gate")]
                    [Range(0.0f, 0.9f)]
                    public float iou2DThreshold = 0.15f;

                    [Tooltip("Maximum center distance in pixels - used as cost factor, NOT hard gate")]
                    public float maxCenterDistance = 300f;

                    [Header("=== REACQUISITION / REVIVAL (Critical for MR) ===")]
                    [Tooltip("Max 3D distance for reviving Lost tracks (meters) - MUST be >= max3DDistance because 3D estimates are noisier after occlusion")]
                    public float reacquireMax3DDistance = 2.0f;

                    [Tooltip("Time window (seconds) during which Lost tracks can be revived")]
                    [Range(1.0f, 15f)]
                    public float reacquireWindowSec = 5.0f;

                    [Header("=== LIFECYCLE THRESHOLDS ===")]
                    [Tooltip("How long CONFIRMED tracks tolerate no updates before going Lost")]
                    [Range(1.0f, 10f)]
                    public float confirmedMaxMissTimeSec = 4.0f;

                    [Tooltip("How long TENTATIVE tracks tolerate no updates before going Lost")]
                    [Range(0.5f, 5f)]
                    public float tentativeMaxMissTimeSec = 1.5f;

                    [Tooltip("How long Lost tracks are RETAINED for revival (seconds) - critical for MR!")]
                    [Range(2.0f, 20f)]
                    public float lostRetentionTimeSec = 8.0f;

                    [Tooltip("(LEGACY) - kept for backward compat, use confirmedMaxMissTimeSec instead")]
                    [Range(0.5f, 10f)]
                    public float maxMissTimeSec = 2.5f;

                    [Header("3D Bounds Estimation")]
                    [Tooltip("Depth thickness as fraction of center depth")]
                    [Range(0.05f, 0.5f)]
                    public float depthThicknessFraction = 0.15f;

                    [Tooltip("Minimum bounds thickness in meters")]
                    public float minBoundsThickness = 0.10f;

                    [Tooltip("Maximum bounds thickness in meters")]
                    public float maxBoundsThickness = 0.80f;

                    [Header("Track Confirmation")]
                    [Tooltip("Minimum successful matches to become Confirmed")]
                    [Range(1, 10)]
                    public int minHits = 3;

                    [Header("Smoothing")]
                    [Tooltip("Position smoothing (lower = smoother)")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaPosition = 0.6f;

                    [Tooltip("Size smoothing")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaSize = 0.25f;

                    [Tooltip("Velocity smoothing")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaVelocity = 0.8f;

                    [Header("Motion Compensation")]
                    [Tooltip("Enable camera motion compensation")]
                    public bool enableMotionCompensation = true;

                    [Tooltip("CRITICAL: Also motion-compensate Lost tracks during reacquire window")]
                    public bool motionCompensateLostTracks = true;

                    [Tooltip("Maximum expected object velocity (m/s)")]
                    public float maxObjectVelocity = 10.0f;

                    [Header("Debug")]
                    [Tooltip("Show debug logs")]
                    public bool enableDebugLogs = true;

                    [Tooltip("Show track IDs in 3D")]
                    public bool showTrackIDs = true;

                    [Tooltip("Show revival metrics in OnGUI")]
                    public bool showRevivalMetrics = true;

                    [Tooltip("Log summary every N seconds (0 = disabled)")]
                    public float metricsSummaryIntervalSec = 2.0f;

                    private void OnValidate()
                    {
                              // Ensure reacquire distance is >= normal distance (relaxed, not stricter)
                              if (reacquireMax3DDistance < max3DDistance)
                              {
                                        Debug.LogWarning($"[TrackerConfig] reacquireMax3DDistance ({reacquireMax3DDistance}m) should be >= max3DDistance ({max3DDistance}m). " +
                                                       "Revival needs relaxed thresholds because 3D estimates are noisier after occlusion. Auto-correcting.");
                                        reacquireMax3DDistance = max3DDistance;
                              }

                              // Ensure retention > reacquire window
                              if (lostRetentionTimeSec < reacquireWindowSec)
                              {
                                        Debug.LogWarning($"[TrackerConfig] lostRetentionTimeSec ({lostRetentionTimeSec}s) should be >= reacquireWindowSec ({reacquireWindowSec}s)");
                              }
                    }
          }
}