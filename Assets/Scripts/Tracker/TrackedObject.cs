using UnityEngine;
using System.Collections.Generic;

namespace ARObjectDetection
{
          /// <summary>
          /// Represents a tracked object with stable ID and lifecycle.
          /// DESIGNED FOR LONG-TERM MR: IDs are nearly permanent, tracks survive occlusions.
          /// </summary>
          public class TrackedObject
          {
                    // ============================================================
                    // IDENTITY (stable throughout session)
                    // ============================================================
                    public int id;
                    public int classId;
                    public string className;

                    // ============================================================
                    // 3D WORLD SPACE STATE (PRIMARY for MR)
                    // ============================================================

                    /// <summary>Raw 3D position from latest measurement</summary>
                    public Vector3 worldPosition;

                    /// <summary>Smoothed 3D position for display/anchors - USE THIS for overlays</summary>
                    public Vector3 worldPositionSmoothed;

                    /// <summary>Bounding box size in meters</summary>
                    public Vector3 worldSize;

                    /// <summary>Axis-aligned bounding box for 3D IoU</summary>
                    public Bounds worldBounds;

                    /// <summary>Box orientation</summary>
                    public Quaternion worldRotation;

                    // ============================================================
                    // 2D IMAGE SPACE (secondary, for association cost)
                    // ============================================================

                    /// <summary>Bounding box in camera pixels</summary>
                    public Rect bbox2D;

                    /// <summary>Center in pixel coordinates</summary>
                    public Vector2Int centerPixel;

                    // ============================================================
                    // MOTION STATE
                    // ============================================================

                    /// <summary>Velocity in m/s (world space)</summary>
                    public Vector3 velocity;

                    /// <summary>Smoothed velocity</summary>
                    public Vector3 velocitySmoothed;

                    /// <summary>Size change rate in pixels/sec</summary>
                    public Vector2 sizeVelocity;

                    // ============================================================
                    // RAY / DEPTH
                    // ============================================================

                    /// <summary>Ray from camera through detection center</summary>
                    public Ray centerRay;

                    /// <summary>Distance from camera (meters)</summary>
                    public float depth;

                    // ============================================================
                    // LIFECYCLE STATE
                    // ============================================================

                    /// <summary>Current track state</summary>
                    public TrackState state;

                    /// <summary>Number of successful detection matches</summary>
                    public int hits;

                    /// <summary>Seconds since last successful detection match</summary>
                    public float timeSinceLastUpdate;

                    /// <summary>Time.realtimeSinceStartup when last matched to a detection</summary>
                    public float lastUpdateTime;

                    /// <summary>Last detection confidence</summary>
                    public float confidence;

                    // ============================================================
                    // LOST STATE TRACKING (critical for long-term MR)
                    // ============================================================

                    /// <summary>
                    /// Time.realtimeSinceStartup when track entered Lost state.
                    /// Reset to 0 when revived. Used for retention timing.
                    /// </summary>
                    public float lostTime;

                    /// <summary>State before going Lost (for proper revival)</summary>
                    public TrackState stateBeforeLost;

                    /// <summary>True if this track has been revived at least once</summary>
                    public bool wasRevived;

                    /// <summary>Total revival count for this track</summary>
                    public int revivalCount;

                    /// <summary>
                    /// Time.realtimeSinceStartup when track was created.
                    /// Used to prefer older tracks during merge/dedup.
                    /// </summary>
                    public float creationTime;

                    // ============================================================
                    // CAMERA POSE (for motion compensation)
                    // ============================================================

                    /// <summary>Camera pose at last update (for motion compensation)</summary>
                    public Pose lastCameraPose;

                    // ============================================================
                    // DISPLAY / CONFIDENCE
                    // ============================================================

                    /// <summary>Track confidence (decays when not seen)</summary>
                    public float trackConfidence;

                    /// <summary>Display color</summary>
                    public Color displayColor;

                    // ============================================================
                    // HISTORY
                    // ============================================================

                    /// <summary>Recent positions for velocity estimation</summary>
                    public Queue<Vector3> positionHistory;

                    /// <summary>Timestamps for position history</summary>
                    public Queue<float> updateTimes;

                    // ============================================================
                    // TIME BASE
                    // ============================================================

                    /// <summary>Time.realtimeSinceStartup that worldPosition represents</summary>
                    public float stateTime;

                    /// <summary>Last measured position (for velocity calc)</summary>
                    public Vector3 lastMeasuredPosition;

                    /// <summary>Time of last measurement</summary>
                    public float lastMeasuredTime;

                    // ============================================================
                    // CONSTRUCTOR
                    // ============================================================

                    public TrackedObject()
                    {
                              positionHistory = new Queue<Vector3>(10);
                              updateTimes = new Queue<float>(10);
                              trackConfidence = 1.0f;
                              displayColor = Color.green;
                              worldBounds = new Bounds(Vector3.zero, Vector3.one);
                              lostTime = 0f;
                              stateBeforeLost = TrackState.Tentative;
                              wasRevived = false;
                              revivalCount = 0;
                              creationTime = Time.realtimeSinceStartup;
                    }

                    // ============================================================
                    // HISTORY MANAGEMENT
                    // ============================================================

                    public void AddToHistory(Vector3 position, float time)
                    {
                              positionHistory.Enqueue(position);
                              updateTimes.Enqueue(time);

                              while (positionHistory.Count > 10)
                              {
                                        positionHistory.Dequeue();
                                        updateTimes.Dequeue();
                              }
                    }

                    // ============================================================
                    // LIFECYCLE METHODS
                    // ============================================================

                    /// <summary>
                    /// Mark this track as Lost. Records timestamp for retention tracking.
                    /// </summary>
                    public void MarkLost(float currentTime)
                    {
                              if (state == TrackState.Lost) return; // Already lost

                              stateBeforeLost = state;
                              state = TrackState.Lost;
                              lostTime = currentTime;
                    }

                    /// <summary>
                    /// Revive this track from Lost state.
                    /// Restores previous state or Confirmed if enough hits.
                    /// </summary>
                    public void Revive(int minHitsForConfirmed = 3)
                    {
                              if (state != TrackState.Lost) return; // Not lost

                              // Restore to Confirmed if enough hits, otherwise previous state
                              if (hits >= minHitsForConfirmed)
                              {
                                        state = TrackState.Confirmed;
                              }
                              else
                              {
                                        state = (stateBeforeLost != TrackState.Lost) ? stateBeforeLost : TrackState.Tentative;
                              }

                              lostTime = 0f;
                              wasRevived = true;
                              revivalCount++;
                              // Note: timeSinceLastUpdate will be reset by UpdateTrack
                    }

                    /// <summary>
                    /// Check if this track is eligible for revival (Lost but within time window).
                    /// </summary>
                    public bool IsEligibleForRevival(float reacquireWindowSec)
                    {
                              if (state != TrackState.Lost) return false;
                              return timeSinceLastUpdate <= reacquireWindowSec;
                    }

                    /// <summary>
                    /// Check if this Lost track should be permanently deleted (retention expired).
                    /// </summary>
                    public bool ShouldPrune(float currentTime, float lostRetentionTimeSec)
                    {
                              if (state != TrackState.Lost) return false;
                              if (lostTime <= 0f) return false;

                              float timeSinceLost = currentTime - lostTime;
                              return timeSinceLost > lostRetentionTimeSec;
                    }

                    /// <summary>
                    /// Get how long this track has been in Lost state.
                    /// </summary>
                    public float GetTimeSinceLost(float currentTime)
                    {
                              if (state != TrackState.Lost || lostTime <= 0f) return 0f;
                              return currentTime - lostTime;
                    }

                    /// <summary>
                    /// Get track age (time since creation).
                    /// </summary>
                    public float GetAge(float currentTime)
                    {
                              return currentTime - creationTime;
                    }
          }

          public enum TrackState
          {
                    Tentative,   // New track, not yet confirmed
                    Confirmed,   // Stable track with enough hits
                    Lost         // Temporarily lost, retained for revival
          }
}