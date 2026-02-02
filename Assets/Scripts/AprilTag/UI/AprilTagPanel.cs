// ============================================================================
// FILE: AprilTagPanel.cs
// In-world debug UI panel for AprilTag asset identity system.
// Shows latched tags, conflicts, and allows conflict resolution.
// 
// OnGUI REMOVED - all UI is via TextMeshPro only
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

        [Header("Conflict Resolution Buttons (Optional)")]
        [SerializeField] private GameObject acceptButtonObject;
        [SerializeField] private GameObject rejectButtonObject;
        [SerializeField] private TextMeshPro conflictInfoText;

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
            UpdateConflictUI();
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

        private void UpdateConflictUI()
        {
            int conflicts = assetTagManager.ConflictTagCount;

            // Show/hide conflict buttons
            if (acceptButtonObject != null)
                acceptButtonObject.SetActive(conflicts > 0);
            if (rejectButtonObject != null)
                rejectButtonObject.SetActive(conflicts > 0);

            // Update conflict info text
            if (conflictInfoText != null)
            {
                if (conflicts > 0)
                {
                    var conflict = assetTagManager.GetConflictTags().FirstOrDefault();
                    if (conflict != null)
                    {
                        selectedConflictTagId = conflict.TagId;
                        conflictInfoText.text = $"Conflict: Tag #{conflict.TagId}\nError: {conflict.ConflictErrorMeters:F2}m";
                    }
                    else
                    {
                        conflictInfoText.text = "";
                        selectedConflictTagId = -1;
                    }
                }
                else
                {
                    conflictInfoText.text = "";
                    selectedConflictTagId = -1;
                }
            }
        }

        // ============================================================
        // PUBLIC METHODS (for button callbacks)
        // ============================================================

        public void SelectConflict(int tagId)
        {
            LatchedTagState state = assetTagManager.GetTagState(tagId);
            if (state != null && state.IsInConflict)
            {
                selectedConflictTagId = tagId;
            }
        }

        /// <summary>
        /// Accept the tag pose for the current conflict (button callback)
        /// </summary>
        public void OnAcceptConflictClicked()
        {
            if (selectedConflictTagId >= 0)
            {
                assetTagManager.AcceptConflict(selectedConflictTagId);
                selectedConflictTagId = -1;
            }
        }

        /// <summary>
        /// Reject the tag pose for the current conflict (button callback)
        /// </summary>
        public void OnRejectConflictClicked()
        {
            if (selectedConflictTagId >= 0)
            {
                assetTagManager.RejectConflict(selectedConflictTagId);
                selectedConflictTagId = -1;
            }
        }

        /// <summary>
        /// Clear all tags (button callback)
        /// </summary>
        public void OnClearAllTagsClicked()
        {
            assetTagManager.ClearAllTags();
        }

        public void SetVisible(bool visible)
        {
            panelRoot.gameObject.SetActive(visible);
        }

        public void ToggleVisibility()
        {
            panelRoot.gameObject.SetActive(!panelRoot.gameObject.activeSelf);
        }

        // NOTE: OnGUI() method has been REMOVED to eliminate debug spam.
        // All UI is now rendered via TextMeshPro components in the scene.
    }
}