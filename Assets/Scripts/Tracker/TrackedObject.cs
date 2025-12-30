using UnityEngine;
using System.Collections.Generic;

namespace ARObjectDetection
{
          /// <summary>
          /// Represents a tracked object with stable ID and lifecycle
          /// </summary>
          public class TrackedObject
          {
                    // Identity
                    public int id;
                    public int classId;
                    public string className;

                    // 3D World Space State (PRIMARY)
                    public Vector3 worldPosition;           // Current 3D position
                    public Vector3 worldPositionSmoothed;   // Smoothed for display
                    public Vector3 worldSize;               // Bounding box size in meters
                    public Bounds worldBounds;              // NEW: AABB for 3D IoU
                    public Quaternion worldRotation;        // Box orientation

                    // 2D Image Space (for association)
                    public Rect bbox2D;                     // Bounding box in pixels (1280x1280)
                    public Vector2Int centerPixel;          // Center in pixel coordinates

                    // Motion
                    public Vector3 velocity;                // m/s in world space
                    public Vector3 velocitySmoothed;
                    public Vector2 sizeVelocity;            // Size change in pixels/sec

                    // Ray (for reprojection and motion compensation)
                    public Ray centerRay;                   // Ray from camera through detection center
                    public float depth;                     // Distance from camera (meters)

                    // Lifecycle (TIME-BASED, not frame-based)
                    public TrackState state;
                    public int hits;                        // Number of successful matches
                    public float timeSinceLastUpdate;       // Seconds since last detection
                    public float lastUpdateTime;            // Time.realtimeSinceStartup
                    public float confidence;                // Last detection confidence

                    // Camera pose (for motion compensation)
                    public Pose lastCameraPose;

                    // Display
                    public float trackConfidence;           // Separate from detection confidence
                    public Color displayColor;

                    // History
                    public Queue<Vector3> positionHistory;  // Last 10 positions
                    public Queue<float> updateTimes;        // Last 10 update times

                    // Timebase (what time worldPosition represents)
                    public float stateTime;                 // Time.realtimeSinceStartup for worldPosition

                    // Last measurement (for stable velocity estimation)
                    public Vector3 lastMeasuredPosition;
                    public float lastMeasuredTime;


                    public TrackedObject()
                    {
                              positionHistory = new Queue<Vector3>(10);
                              updateTimes = new Queue<float>(10);
                              trackConfidence = 1.0f;
                              displayColor = Color.green;
                              worldBounds = new Bounds(Vector3.zero, Vector3.one);  // NEW: Initialize bounds
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
          }

          public enum TrackState
          {
                    Tentative,   // New track, not confirmed yet
                    Confirmed,   // Stable track with enough hits
                    Lost         // Track lost, will be deleted
          }
}