using UnityEngine;

namespace ARObjectDetection
{
          [CreateAssetMenu(fileName = "TrackerConfig", menuName = "AR Detection/Tracker Config")]
          public class TrackerConfig : ScriptableObject
          {
                    [Header("Association - 3D IoU Based")]
                    [Tooltip("Minimum 3D IoU for bbox matching (0-1) - Start low for noisy depth")]
                    [Range(0.01f, 0.5f)]
                    public float iou3DThreshold = 0.05f;  // NEW: 3D IoU threshold

                    [Tooltip("Minimum 2D IoU for bbox matching (0-1) - Fallback/secondary metric")]
                    [Range(0.05f, 0.9f)]
                    public float iou2DThreshold = 0.15f;  // Renamed from iouThreshold

                    [Tooltip("Maximum center distance in pixels for matching")]
                    public float maxCenterDistance = 300f;

                    [Tooltip("Maximum 3D distance in meters for matching")]
                    public float max3DDistance = 2.5f;

                    [Header("3D Bounds Estimation")]
                    [Tooltip("Depth thickness as fraction of center depth (e.g., 0.15 = ±7.5% of depth)")]
                    [Range(0.05f, 0.5f)]
                    public float depthThicknessFraction = 0.15f;  // NEW

                    [Tooltip("Minimum bounds thickness in meters")]
                    public float minBoundsThickness = 0.10f;  // NEW

                    [Tooltip("Maximum bounds thickness in meters")]
                    public float maxBoundsThickness = 0.80f;  // NEW

                    [Header("Lifecycle (Time-Based)")]
                    [Tooltip("Minimum successful matches to become Confirmed")]
                    [Range(1, 10)]
                    public int minHits = 3;

                    [Tooltip("Maximum time without detection before deletion (seconds)")]
                    [Range(0.5f, 10f)]
                    public float maxMissTimeSec = 2.5f;

                    [Header("Smoothing (Display State)")]
                    [Tooltip("Position smoothing (lower = smoother, higher = more responsive)")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaPosition = 0.35f;  // CHANGED: Was 0.3

                    [Tooltip("Size smoothing")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaSize = 0.25f;  // CHANGED: Was 0.20

                    [Tooltip("Velocity smoothing")]
                    [Range(0.05f, 1f)]
                    public float smoothingAlphaVelocity = 0.4f;

                    [Header("Motion Compensation")]
                    [Tooltip("Enable camera motion compensation")]
                    public bool enableMotionCompensation = true;

                    [Tooltip("Maximum expected object velocity (m/s)")]
                    public float maxObjectVelocity = 2.0f;

                    [Header("Debug")]
                    [Tooltip("Show debug logs - TURN THIS ON to diagnose issues")]
                    public bool enableDebugLogs = true;

                    [Tooltip("Show track IDs in 3D")]
                    public bool showTrackIDs = true;
          }
}