// ============================================================================
// FILE: AprilTagPanel.cs
// In-world debug UI panel for AprilTag asset identity system.
// Shows latched tags, conflicts, and allows conflict resolution.
// ============================================================================

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;

namespace ARObjectDetection.AprilTag.UI
{
          public class AprilTagPanel : MonoBehaviour
          {
                    [Header("References")]
                    [SerializeField] private AssetTagManager assetTagManager;
                    [SerializeField] private TagTrackAssociator tagTrackAssociator;
                    [SerializeField] private PoseCorrector poseCorrector;

                    [Header("Panel Settings")]
                    [SerializeField] private Transform panelRoot;
                    [SerializeField] private float panelDistanceFromHead = 1.5f;
                    [SerializeField] private bool followHead = true;
                    [SerializeField] private float followSmoothTime = 0.3f;

                    [Header("Text References")]
                    [SerializeField] private TextMeshPro titleText;
                    [SerializeField] private TextMeshPro statusText;
                    [SerializeField] private TextMeshPro tagListText;

                    private Vector3 panelVelocity;
                    private int selectedConflictTagId = -1;

                    private void Awake()
                    {
                              if (assetTagManager == null)
                                        assetTagManager = FindFirstObjectByType<AssetTagManager>();
                              if (tagTrackAssociator == null)
                                        tagTrackAssociator = FindFirstObjectByType<TagTrackAssociator>();
                              if (poseCorrector == null)
                                        poseCorrector = FindFirstObjectByType<PoseCorrector>();
                              if (panelRoot == null)
                                        panelRoot = transform;

                              if (assetTagManager == null)
                              {
                                        Debug.LogWarning("[AprilTagPanel] AssetTagManager not found");
                              }
                    }

                    private void Update()
                    {
                              if (assetTagManager == null) return;

                              UpdatePanelPosition();
                              UpdateUI();
                    }

                    private void UpdatePanelPosition()
                    {
                              if (!followHead || Camera.main == null) return;

                              Vector3 headPos = Camera.main.transform.position;
                              Vector3 headForward = Camera.main.transform.forward;
                              headForward.y = 0;
                              headForward.Normalize();

                              Vector3 targetPos = headPos + headForward * panelDistanceFromHead;

                              panelRoot.position = Vector3.SmoothDamp(
                                  panelRoot.position,
                                  targetPos,
                                  ref panelVelocity,
                                  followSmoothTime
                              );

                              Vector3 toCamera = Camera.main.transform.position - panelRoot.position;
                              toCamera.y = 0;
                              if (toCamera.sqrMagnitude > 0.01f)
                              {
                                        panelRoot.rotation = Quaternion.LookRotation(toCamera);
                              }
                    }

                    private void UpdateUI()
                    {
                              UpdateStatusText();
                              UpdateTagList();
                    }

                    private void UpdateStatusText()
                    {
                              if (statusText == null) return;

                              bool siteFrameValid = assetTagManager.IsSiteFrameValid;
                              int latched = assetTagManager.LatchedTagCount;
                              int candidates = assetTagManager.CandidateTagCount;
                              int conflicts = assetTagManager.ConflictTagCount;

                              string siteStatus = siteFrameValid ? "<color=#00FF00>VALID</color>" : "<color=#FF8800>INVALID</color>";

                              statusText.text =
                                  $"<b>SiteFrame:</b> {siteStatus}\n" +
                                  $"<b>Latched:</b> {latched}\n" +
                                  $"<b>Candidates:</b> {candidates}\n" +
                                  $"<b>Conflicts:</b> <color={(conflicts > 0 ? "#FF0000" : "#00FF00")}>{conflicts}</color>";
                    }

                    private void UpdateTagList()
                    {
                              if (tagListText == null) return;

                              var allTags = assetTagManager.AllTagStates.Values
                                  .OrderByDescending(s => s.IsLatched)
                                  .ThenByDescending(s => s.TotalDetections)
                                  .Take(8)
                                  .ToList();

                              if (allTags.Count == 0)
                              {
                                        tagListText.text = "<i>No tags detected</i>";
                                        return;
                              }

                              var lines = new List<string>();
                              lines.Add("<b>─── ACTIVE TAGS ───</b>");

                              foreach (var state in allTags)
                              {
                                        string statusIcon;
                                        string statusColor;

                                        switch (state.Status)
                                        {
                                                  case TagStatus.Latched:
                                                            statusIcon = "●";
                                                            statusColor = "#00FF00";
                                                            break;
                                                  case TagStatus.Conflict:
                                                            statusIcon = "⚠";
                                                            statusColor = "#FF0000";
                                                            break;
                                                  default:
                                                            statusIcon = "○";
                                                            statusColor = "#FFAA00";
                                                            break;
                                        }

                                        string trackStr = state.HasLinkedTrack ? $"→T{state.LinkedTrackId}" : "";
                                        string seenStr = state.SecondsSinceLastSeen < 1f ? "now" : $"{state.SecondsSinceLastSeen:F0}s";

                                        lines.Add($"<color={statusColor}>{statusIcon}</color> #{state.TagId} {trackStr} seen:{seenStr}");
                              }

                              tagListText.text = string.Join("\n", lines);
                    }

                    public void SelectConflict(int tagId)
                    {
                              LatchedTagState state = assetTagManager.GetTagState(tagId);
                              if (state != null && state.IsInConflict)
                              {
                                        selectedConflictTagId = tagId;
                              }
                    }

                    public void SetVisible(bool visible)
                    {
                              panelRoot.gameObject.SetActive(visible);
                    }

                    public void ToggleVisibility()
                    {
                              panelRoot.gameObject.SetActive(!panelRoot.gameObject.activeSelf);
                    }

                    // Fallback OnGUI when no 3D panel assigned
                    private void OnGUI()
                    {
                              if (assetTagManager == null) return;
                              if (titleText != null || tagListText != null) return;

                              int x = 830;
                              int y = 10;
                              int width = 350;

                              GUILayout.BeginArea(new Rect(x, y, width, 400));

                              GUILayout.Label("<size=14><b>═══ APRILTAG PANEL ═══</b></size>");

                              string sfStatus = assetTagManager.IsSiteFrameValid ? "VALID" : "INVALID";
                              GUILayout.Label($"SiteFrame: {sfStatus}");
                              GUILayout.Label($"Latched: {assetTagManager.LatchedTagCount} | Candidates: {assetTagManager.CandidateTagCount}");

                              int conflicts = assetTagManager.ConflictTagCount;
                              GUI.color = conflicts > 0 ? Color.red : Color.green;
                              GUILayout.Label($"Conflicts: {conflicts}");
                              GUI.color = Color.white;

                              GUILayout.Label("─── TAGS ───");

                              foreach (var state in assetTagManager.AllTagStates.Values.Take(6))
                              {
                                        string status = state.Status.ToString().Substring(0, 3).ToUpper();
                                        string link = state.HasLinkedTrack ? $"→T{state.LinkedTrackId}" : "";
                                        string seen = state.SecondsSinceLastSeen < 1 ? "now" : $"{state.SecondsSinceLastSeen:F0}s";

                                        GUI.color = state.Status switch
                                        {
                                                  TagStatus.Latched => Color.green,
                                                  TagStatus.Conflict => Color.red,
                                                  _ => Color.yellow
                                        };

                                        GUILayout.Label($"#{state.TagId} [{status}]{link} seen:{seen}");
                              }
                              GUI.color = Color.white;

                              if (conflicts > 0)
                              {
                                        GUILayout.Label("");

                                        var conflict = assetTagManager.GetConflictTags().FirstOrDefault();
                                        if (conflict != null)
                                        {
                                                  GUILayout.Label($"Selected: #{conflict.TagId} (err={conflict.ConflictErrorMeters:F2}m)");

                                                  GUILayout.BeginHorizontal();
                                                  if (GUILayout.Button("Accept (Tag)", GUILayout.Width(100)))
                                                  {
                                                            assetTagManager.AcceptConflict(conflict.TagId);
                                                  }
                                                  if (GUILayout.Button("Reject (Track)", GUILayout.Width(100)))
                                                  {
                                                            assetTagManager.RejectConflict(conflict.TagId);
                                                  }
                                                  GUILayout.EndHorizontal();
                                        }
                              }

                              GUILayout.Label("");
                              if (GUILayout.Button("Clear All Tags", GUILayout.Width(120)))
                              {
                                        assetTagManager.ClearAllTags();
                              }

                              GUILayout.EndArea();
                    }
          }
}