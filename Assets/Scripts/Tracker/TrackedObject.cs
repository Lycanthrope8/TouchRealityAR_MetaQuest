using UnityEngine;
using System.Collections.Generic;

namespace ARObjectDetection
{
          /// <summary>
          /// Represents a tracked object with stable ID and lifecycle.
          /// FIXED: Proper lostTime tracking for retention and revival.
          /// </summary>
          public class TrackedObject
          {
                    // Identity
                    public int id;
                    public int classId;
                    public string className;

                    // 3D World Space State (PRIMARY for MR)
                    public Vector3 worldPosition;           // Current 3D position
                    public Vector3 worldPositionSmoothed;   // Smoothed for display
                    public Vector3 worldSize;               // Bounding box size in meters
                    public Bounds worldBounds;              // AABB for 3D IoU
                    public Quaternion worldRotation;        // Box orientation

                    // 2D Image Space (for association - SECONDARY, motion-compensated)
                    public Rect bbox2D;                     // Bounding box in pixels
                    public Vector2Int centerPixel;          // Center in pixel coordinates

                    // Motion
                    public Vector3 velocity;                // m/s in world space
                    public Vector3 velocitySmoothed;
                    public Vector2 sizeVelocity;            // Size change in pixels/sec

                    // Ray (for reprojection and motion compensation)
                    public Ray centerRay;                   // Ray from camera through detection center
                    public float depth;                     // Distance from camera (meters)

                    // Lifecycle
                    public TrackState state;
                    public int hits;                        // Number of successful matches
                    public float timeSinceLastUpdate;       // Seconds since last detection match
                    public float lastUpdateTime;            // Time.realtimeSinceStartup when last matched
                    public float confidence;                // Last detection confidence

                    // === LOST STATE TRACKING ===
                    /// <summary>
                    /// Time.realtimeSinceStartup when track entered Lost state.
                    /// Reset to 0 when track is revived.
                    /// </summary>
                    public float lostTime;

                    /// <summary>
                    /// True if this track has been revived at least once.
                    /// </summary>
                    public bool wasRevived;

                    /// <summary>
                    /// Total number of times this track has been revived.
                    /// </summary>
                    public int revivalCount;

                    /// <summary>
                    /// State before going Lost (for revival - restore to this state)
                    /// </summary>
                    public TrackState stateBeforeLost;

                    // Camera pose (for motion compensation)
                    public Pose lastCameraPose;

                    // Display
                    public float trackConfidence;
                    public Color displayColor;

                    // History
                    public Queue<Vector3> positionHistory;
                    public Queue<float> updateTimes;

                    // Timebase
                    public float stateTime;

                    // Last measurement
                    public Vector3 lastMeasuredPosition;
                    public float lastMeasuredTime;


                    public TrackedObject()
                    {
                              positionHistory = new Queue<Vector3>(10);
                              updateTimes = new Queue<float>(10);
                              trackConfidence = 1.0f;
                              displayColor = Color.green;
                              worldBounds = new Bounds(Vector3.zero, Vector3.one);
                              lostTime = 0f;
                              wasRevived = false;
                              revivalCount = 0;
                              stateBeforeLost = TrackState.Tentative;
                    }

                    public void AddToHistory(Vector3 position, float time)
                    {
                              positionHistory.Enqueue(position);
                              updateTimes.Enqueue(time);

                              if (positionHistory.Count > 10)
                              {
                                        positionHistory.Dequeue();
                                        updateTimes.Dequeue();
                              }
                    }

                    /// <summary>
                    /// Mark this track as Lost and record the timestamp.
                    /// </summary>
                    public void MarkLost(float currentTime)
                    {
                              if (state != TrackState.Lost)
                              {
                                        stateBeforeLost = state;
                                        state = TrackState.Lost;
                                        lostTime = currentTime;
                              }
                    }

                    /// <summary>
                    /// Revive this track from Lost state.
                    /// </summary>
                    public void Revive()
                    {
                              // Restore to previous state, or Confirmed if it had enough hits
                              if (hits >= 3) // Could use config.minHits but keeping simple
                              {
                                        state = TrackState.Confirmed;
                              }
                              else
                              {
                                        state = stateBeforeLost != TrackState.Lost ? stateBeforeLost : TrackState.Tentative;
                              }

                              lostTime = 0f;
                              wasRevived = true;
                              revivalCount++;
                              // timeSinceLastUpdate will be reset by UpdateTrack
                    }

                    /// <summary>
                    /// Check if this track is eligible for revival matching.
                    /// Must be Lost AND within reacquire time window.
                    /// </summary>
                    public bool IsEligibleForRevival(float reacquireWindowSec)
                    {
                              if (state != TrackState.Lost) return false;
                              // Use timeSinceLastUpdate (time since last successful match)
                              return timeSinceLastUpdate <= reacquireWindowSec;
                    }

                    /// <summary>
                    /// Check if this Lost track should be permanently deleted.
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
          }

          public enum TrackState
          {
                    Tentative,   // New track, not confirmed yet
                    Confirmed,   // Stable track with enough hits
                    Lost         // Track lost, kept for potential revival
          }
}