// ============================================================================
// FILE: PoseCorrector.cs
// Drives smooth overlay pose for latched tags using YOLO tracking.
// When tag is visible, applies drift correction smoothly.
// All error computations in SITEFRAME space.
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.AprilTag
{
          public class CorrectionState
          {
                    public int TagId;
                    public int TrackId;
                    public Pose StartPoseSite;
                    public Pose TargetPoseSite;
                    public Pose CurrentPoseSite;
                    public float StartTime;
                    public float Duration;
                    public float InitialErrorSite;
                    public bool IsComplete;

                    public float Progress => Mathf.Clamp01((Time.realtimeSinceStartup - StartTime) / Duration);

                    public bool UpdateCorrection()
                    {
                              if (IsComplete) return false;

                              float t = Progress;
                              float smoothT = t * t * (3f - 2f * t);

                              CurrentPoseSite = new Pose(
                                  Vector3.Lerp(StartPoseSite.position, TargetPoseSite.position, smoothT),
                                  Quaternion.Slerp(StartPoseSite.rotation, TargetPoseSite.rotation, smoothT)
                              );

                              if (t >= 1f)
                              {
                                        IsComplete = true;
                                        CurrentPoseSite = TargetPoseSite;
                              }

                              return !IsComplete;
                    }
          }

          public class PoseCorrector : MonoBehaviour
          {
                    [Header("References")]
                    [SerializeField] private AssetTagManager assetTagManager;
                    [SerializeField] private ObjectTracker objectTracker;
                    [SerializeField] private SiteFrameManager siteFrameManager;

                    [Header("Correction Settings")]
                    [SerializeField] private float gentleCorrectionMaxError = 0.05f;
                    [SerializeField] private float slowCorrectionMaxError = 0.15f;
                    [SerializeField] private DetectionQuality minQualityForCorrection = DetectionQuality.Fair;
                    [SerializeField] private float gentleCorrectionDuration = 0.3f;
                    [SerializeField] private float slowCorrectionDuration = 1.0f;
                    [SerializeField] private float minCorrectionInterval = 0.5f;

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;

                    private Dictionary<int, CorrectionState> activeCorrections = new Dictionary<int, CorrectionState>();
                    private Dictionary<int, float> lastCorrectionTime = new Dictionary<int, float>();
                    private Dictionary<int, Pose> outputPosesSite = new Dictionary<int, Pose>();

                    private int correctionsStarted = 0;
                    private int correctionsCompleted = 0;

                    private void Awake()
                    {
                              if (assetTagManager == null)
                                        assetTagManager = FindFirstObjectByType<AssetTagManager>();
                              if (objectTracker == null)
                                        objectTracker = FindFirstObjectByType<ObjectTracker>();
                              if (siteFrameManager == null)
                                        siteFrameManager = FindFirstObjectByType<SiteFrameManager>();

                              if (assetTagManager == null)
                              {
                                        Debug.LogError("[PoseCorrector] AssetTagManager not found!");
                                        enabled = false;
                                        return;
                              }

                              assetTagManager.OnTagSeen.AddListener(OnTagSeen);
                    }

                    private void OnDestroy()
                    {
                              if (assetTagManager != null)
                                        assetTagManager.OnTagSeen.RemoveListener(OnTagSeen);
                    }

                    private void Update()
                    {
                              UpdateActiveCorrections();
                              UpdateOutputPosesFromTracking();
                    }

                    private void OnTagSeen(int tagId, Pose tagPoseWorld, float confidence)
                    {
                              if (siteFrameManager == null || !siteFrameManager.IsValid)
                                        return;

                              LatchedTagState state = assetTagManager.GetTagState(tagId);
                              if (state == null || !state.IsLatched || !state.HasLinkedTrack || state.IsInConflict)
                                        return;

                              if (state.LastQuality < minQualityForCorrection)
                                        return;

                              if (lastCorrectionTime.TryGetValue(tagId, out float lastTime))
                              {
                                        if (Time.realtimeSinceStartup - lastTime < minCorrectionInterval)
                                                  return;
                              }

                              TrackedObject track = objectTracker?.ActiveTracks.Find(t => t.id == state.LinkedTrackId);
                              if (track == null)
                                        return;

                              // Compute error in SiteFrame space
                              Pose trackedPoseSite = siteFrameManager.SitePoseFromWorldPose(
                                  new Pose(track.worldPositionSmoothed, track.worldRotation));
                              Pose tagPoseSite = state.LastTagPoseSite;

                              Pose objectPoseFromTagSite = new Pose(
                                  tagPoseSite.position + tagPoseSite.rotation * state.TagToObjectOffset.position,
                                  tagPoseSite.rotation * state.TagToObjectOffset.rotation
                              );

                              float posErrorSite = Vector3.Distance(trackedPoseSite.position, objectPoseFromTagSite.position);

                              ApplyCorrectionStrategy(state, trackedPoseSite, objectPoseFromTagSite, posErrorSite);
                    }

                    private void ApplyCorrectionStrategy(LatchedTagState state, Pose trackedPoseSite, Pose tagPoseSite, float posErrorSite)
                    {
                              AprilTagConfig config = assetTagManager.Config;
                              int tagId = state.TagId;

                              if (posErrorSite <= gentleCorrectionMaxError)
                              {
                                        StartCorrection(tagId, state.LinkedTrackId, trackedPoseSite, tagPoseSite, gentleCorrectionDuration, posErrorSite);
                              }
                              else if (posErrorSite <= slowCorrectionMaxError)
                              {
                                        StartCorrection(tagId, state.LinkedTrackId, trackedPoseSite, tagPoseSite, slowCorrectionDuration, posErrorSite);
                              }
                              else if (posErrorSite <= config.ConflictThreshold)
                              {
                                        float scaledDuration = Mathf.Lerp(slowCorrectionDuration, slowCorrectionDuration * 2f,
                                            (posErrorSite - slowCorrectionMaxError) / (config.ConflictThreshold - slowCorrectionMaxError));
                                        StartCorrection(tagId, state.LinkedTrackId, trackedPoseSite, tagPoseSite, scaledDuration, posErrorSite);
                              }
                    }

                    private void StartCorrection(int tagId, int trackId, Pose startPoseSite, Pose targetPoseSite, float duration, float errorSite)
                    {
                              if (activeCorrections.TryGetValue(tagId, out CorrectionState existing) && !existing.IsComplete)
                              {
                                        startPoseSite = existing.CurrentPoseSite;
                              }

                              activeCorrections[tagId] = new CorrectionState
                              {
                                        TagId = tagId,
                                        TrackId = trackId,
                                        StartPoseSite = startPoseSite,
                                        TargetPoseSite = targetPoseSite,
                                        CurrentPoseSite = startPoseSite,
                                        StartTime = Time.realtimeSinceStartup,
                                        Duration = duration,
                                        InitialErrorSite = errorSite,
                                        IsComplete = false
                              };

                              lastCorrectionTime[tagId] = Time.realtimeSinceStartup;
                              outputPosesSite[tagId] = startPoseSite;
                              correctionsStarted++;
                    }

                    private void UpdateActiveCorrections()
                    {
                              var completed = new List<int>();

                              foreach (var kvp in activeCorrections)
                              {
                                        CorrectionState correction = kvp.Value;

                                        if (correction.UpdateCorrection())
                                        {
                                                  outputPosesSite[correction.TagId] = correction.CurrentPoseSite;
                                        }
                                        else
                                        {
                                                  outputPosesSite[correction.TagId] = correction.TargetPoseSite;
                                                  completed.Add(kvp.Key);
                                                  correctionsCompleted++;
                                        }
                              }

                              foreach (int tagId in completed)
                                        activeCorrections.Remove(tagId);
                    }

                    private void UpdateOutputPosesFromTracking()
                    {
                              if (siteFrameManager == null || !siteFrameManager.IsValid)
                                        return;

                              foreach (var state in assetTagManager.GetAllLatchedTags())
                              {
                                        if (activeCorrections.ContainsKey(state.TagId))
                                                  continue;

                                        if (!state.HasLinkedTrack)
                                                  continue;

                                        TrackedObject track = objectTracker?.ActiveTracks.Find(t => t.id == state.LinkedTrackId);
                                        if (track != null)
                                        {
                                                  outputPosesSite[state.TagId] = siteFrameManager.SitePoseFromWorldPose(
                                                      new Pose(track.worldPositionSmoothed, track.worldRotation));
                                        }
                              }
                    }

                    public bool TryGetCorrectedPose(int tagId, out Pose poseWorld)
                    {
                              poseWorld = Pose.identity;

                              if (!outputPosesSite.TryGetValue(tagId, out Pose poseSite))
                                        return false;

                              if (siteFrameManager != null && siteFrameManager.IsValid)
                                        poseWorld = siteFrameManager.WorldPoseFromSitePose(poseSite);
                              else
                                        poseWorld = poseSite;

                              return true;
                    }

                    public bool IsCorrectionInProgress(int tagId)
                    {
                              return activeCorrections.ContainsKey(tagId) && !activeCorrections[tagId].IsComplete;
                    }

                    public float GetCorrectionProgress(int tagId)
                    {
                              if (activeCorrections.TryGetValue(tagId, out CorrectionState correction))
                                        return correction.Progress;
                              return 1f;
                    }

                    public void CancelCorrection(int tagId)
                    {
                              if (activeCorrections.TryGetValue(tagId, out CorrectionState correction))
                              {
                                        outputPosesSite[tagId] = correction.CurrentPoseSite;
                                        activeCorrections.Remove(tagId);
                              }
                    }

                    private void OnGUI()
                    {
                              if (!enableDebugLogs) return;

                              GUILayout.BeginArea(new Rect(420, 380, 400, 80));
                              GUILayout.Label("─── POSE CORRECTOR ───");
                              GUILayout.Label($"Active: {activeCorrections.Count} | Started: {correctionsStarted} | Completed: {correctionsCompleted}");

                              foreach (var correction in activeCorrections.Values)
                              {
                                        GUILayout.Label($"  Tag #{correction.TagId}: {correction.Progress:P0}");
                              }

                              GUILayout.EndArea();
                    }
          }
}