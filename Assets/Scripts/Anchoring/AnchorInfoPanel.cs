// ============================================================================
// FILE: AnchorInfoPanel.cs
// v2.1: Multi-card — displays annotations for all intent types per asset.
//       Two annotate buttons: ASK_ANCHOR and ACTION_SUGGEST.
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ARObjectDetection
{
    public class AnchorInfoPanel : MonoBehaviour
    {
        [Header("Text References")]
        [SerializeField] private TextMeshPro titleText;
        [SerializeField] private TextMeshPro bodyText;

        [Header("Optional Detailed Text References")]
        [SerializeField] private TextMeshPro idText;
        [SerializeField] private TextMeshPro confidenceText;
        [SerializeField] private TextMeshPro stateText;
        [SerializeField] private TextMeshPro positionText;

        [Header("Asset Identity Fields")]
        [SerializeField] private TextMeshPro tagIdText;
        [SerializeField] private TextMeshPro assetIdText;
        [SerializeField] private TextMeshPro registryStatusText;
        [SerializeField] private TextMeshPro claimIdText;

        [Header("Propose Button (UI Button)")]
        [SerializeField] private Button proposeButton;
        [SerializeField] private TextMeshProUGUI proposeButtonText;

        [Header("Propose Button (3D World Space - Alternative)")]
        [SerializeField] private TextMeshPro proposeButton3DText;
        [SerializeField] private Collider proposeButton3DCollider;

        [Header("Annotation Buttons (v2.1: one per intent type)")]
        [Tooltip("Button to request ASK_ANCHOR annotation")]
        [SerializeField] private Button askAnchorButton;
        [SerializeField] private TextMeshProUGUI askAnchorButtonText;

        [Tooltip("Button to request ACTION_SUGGEST annotation")]
        [SerializeField] private Button actionSuggestButton;
        [SerializeField] private TextMeshProUGUI actionSuggestButtonText;

        [Header("Annotation Buttons (3D World Space - Alternative)")]
        [SerializeField] private TextMeshPro askAnchor3DText;
        [SerializeField] private Collider askAnchor3DCollider;
        [SerializeField] private TextMeshPro actionSuggest3DText;
        [SerializeField] private Collider actionSuggest3DCollider;

        [Header("Annotation Settings")]
        [Tooltip("Default annotation tier: ADVISORY (auto-approve) or GOVERNED (dual endorsement)")]
        [SerializeField] private string defaultAnnotationTier = "ADVISORY";

        [Header("Visual Settings")]
        [SerializeField] private Renderer panelBackground;
        [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);

        [Header("Registry Status Colors")]
        [SerializeField] private Color noneColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        [SerializeField] private Color pendingColor = new Color(1f, 0.8f, 0f, 1f);
        [SerializeField] private Color proposedColor = new Color(0.5f, 0.8f, 1f, 1f);
        [SerializeField] private Color activeColor = new Color(0f, 1f, 0.5f, 1f);
        [SerializeField] private Color rejectedColor = new Color(1f, 0.3f, 0.3f, 1f);
        [SerializeField] private Color revokedColor = new Color(0.6f, 0.3f, 0.6f, 1f);

        [Header("Gateway Integration")]
        [SerializeField] private bool useGateway = true;
        [SerializeField] private MonoBehaviour siteFrameManager;

        private DepthAnchorSystem.DepthAnchorInstance currentAnchor;
        private string currentAssetId = null;
        private Gateway.GatewaySync gatewaySync;

        private void Awake()
        {
            if (titleText == null || bodyText == null)
            {
                var allTexts = GetComponentsInChildren<TextMeshPro>();
                if (allTexts.Length > 0 && titleText == null) titleText = allTexts[0];
                if (allTexts.Length > 1 && bodyText == null) bodyText = allTexts[1];
            }
            if (panelBackground != null && panelBackground.material != null) panelBackground.material.color = backgroundColor;

            if (proposeButton != null) proposeButton.onClick.AddListener(OnProposeButtonClicked);
            if (askAnchorButton != null) askAnchorButton.onClick.AddListener(() => OnAnnotateButtonClicked("ASK_ANCHOR"));
            if (actionSuggestButton != null) actionSuggestButton.onClick.AddListener(() => OnAnnotateButtonClicked("ACTION_SUGGEST"));

            if (siteFrameManager == null) siteFrameManager = FindFirstObjectByType<SiteFrameManager>();
        }

        private void Start()
        {
            if (useGateway)
            {
                gatewaySync = Gateway.GatewaySync.Instance ?? FindFirstObjectByType<Gateway.GatewaySync>();
                if (gatewaySync != null)
                {
                    gatewaySync.OnClaimStatusChanged.AddListener(OnClaimStatusChanged);
                    gatewaySync.OnAnnotationStatusChanged.AddListener(OnAnnotationStatusChanged);
                    Debug.Log("[AnchorInfoPanel] Gateway integration enabled (v2.1 — multi-card annotations)");
                }
            }
        }

        private void OnDestroy()
        {
            if (proposeButton != null) proposeButton.onClick.RemoveAllListeners();
            if (askAnchorButton != null) askAnchorButton.onClick.RemoveAllListeners();
            if (actionSuggestButton != null) actionSuggestButton.onClick.RemoveAllListeners();
            if (gatewaySync != null)
            {
                gatewaySync.OnClaimStatusChanged.RemoveListener(OnClaimStatusChanged);
                gatewaySync.OnAnnotationStatusChanged.RemoveListener(OnAnnotationStatusChanged);
            }
        }

        private void OnClaimStatusChanged(string assetId, Gateway.ClaimStatus status)
        {
            if (assetId == currentAssetId && currentAnchor != null) UpdateInfo(currentAnchor);
        }

        private void OnAnnotationStatusChanged(string compositeKey, Gateway.AnnotationStatus status)
        {
            // compositeKey is "assetId:intentType"
            if (currentAssetId != null && compositeKey.StartsWith(currentAssetId + ":") && currentAnchor != null)
                UpdateInfo(currentAnchor);
        }

        public void UpdateInfo(DepthAnchorSystem.DepthAnchorInstance anchor)
        {
            if (anchor == null) return;
            currentAnchor = anchor;

            currentAssetId = null;
            int tagId = -1;
            if (anchor.hasAprilTagAssociation)
            {
                tagId = anchor.associatedAprilTagId;
                currentAssetId = $"TAG_{tagId}";
            }

            // Get claim state
            Gateway.ClaimStatus claimStatus = Gateway.ClaimStatus.None;
            string claimId = null;
            if (useGateway && gatewaySync != null && !string.IsNullOrEmpty(currentAssetId))
            {
                var claimState = gatewaySync.GetClaimState(currentAssetId);
                if (claimState != null) { claimStatus = claimState.status; claimId = claimState.claimId; }
            }

            // Get annotation states for all intent types (v2.1)
            Gateway.AnnotationState askAnchorState = null;
            Gateway.AnnotationState actionSuggestState = null;
            if (useGateway && gatewaySync != null && !string.IsNullOrEmpty(currentAssetId))
            {
                askAnchorState = gatewaySync.GetAnnotationState(currentAssetId, "ASK_ANCHOR");
                actionSuggestState = gatewaySync.GetAnnotationState(currentAssetId, "ACTION_SUGGEST");
            }

            // Update fields
            if (idText != null) idText.text = $"ID: #{anchor.trackId}";
            if (confidenceText != null) confidenceText.text = $"Confidence: {anchor.confidence:P0}";
            if (stateText != null) stateText.text = $"State: {anchor.trackState}";
            if (positionText != null) positionText.text = $"Position:\nX: {anchor.worldLockedPosition.x:F2}m\nY: {anchor.worldLockedPosition.y:F2}m\nZ: {anchor.worldLockedPosition.z:F2}m";
            if (tagIdText != null) tagIdText.text = anchor.hasAprilTagAssociation ? $"Tag ID: #{tagId}" : "Tag ID: None";
            if (assetIdText != null) assetIdText.text = !string.IsNullOrEmpty(currentAssetId) ? $"Asset ID: {currentAssetId}" : "Asset ID: N/A (no tag)";
            if (claimIdText != null) claimIdText.text = !string.IsNullOrEmpty(claimId) ? $"Claim: {claimId.Substring(0, Mathf.Min(8, claimId.Length))}..." : "Claim: None";
            if (registryStatusText != null) UpdateRegistryStatusText(claimStatus, claimId);
            if (titleText != null) titleText.text = anchor.className.ToUpper();

            // Build body text
            if (bodyText != null)
            {
                string lockStatus = anchor.isLocked ? "<color=#00FF00>LOCKED</color>" : "<color=#FFFF00>UNLOCKED</color>";
                string stateColor = anchor.trackState == TrackState.Confirmed ? "#00FF00" : "#FFFF00";
                string tagInfo = anchor.hasAprilTagAssociation ? $"<color=#00FF00>#{tagId}</color>" : "<color=#888888>None</color>";
                string assetIdInfo = !string.IsNullOrEmpty(currentAssetId) ? $"<color=#00FFFF>{currentAssetId}</color>" : "<color=#888888>N/A</color>";
                string registryInfo = GetRegistryStatusString(claimStatus, claimId);
                string gatewayInfo = "";
                if (useGateway && gatewaySync != null)
                    gatewayInfo = gatewaySync.IsConnected ? "<color=#00FF00>●</color> Gateway" : "<color=#FF0000>●</color> Gateway";

                // v2.1: Annotations first — most important content at the top
                string annotationBlock = "";
                annotationBlock += RenderAnnotationSection("ASK_ANCHOR", askAnchorState);
                annotationBlock += RenderAnnotationSection("ACTION_SUGGEST", actionSuggestState);

                string body = "";

                // Show annotations at top if any exist
                if (!string.IsNullOrEmpty(annotationBlock))
                {
                    body += $"<b>Asset:</b> {assetIdInfo}  <b>Registry:</b> {registryInfo}\n";
                    body += annotationBlock;
                    body += $"\n───────────────\n";
                    body += $"<size=80%><b>Track:</b> #{anchor.trackId}  <b>Conf:</b> {anchor.confidence:P0}  <b>Tag:</b> {tagInfo}\n";
                    body += $"<b>Pos:</b> ({anchor.worldLockedPosition.x:F2}, {anchor.worldLockedPosition.y:F2}, {anchor.worldLockedPosition.z:F2})</size>";
                }
                else
                {
                    // No annotations yet — show full detail view
                    body =
                        $"<b>Track ID:</b> #{anchor.trackId}\n" +
                        $"<b>Confidence:</b> {anchor.confidence:P0}\n" +
                        $"<b>State:</b> <color={stateColor}>{anchor.trackState}</color>\n" +
                        $"<b>Lock:</b> {lockStatus}\n" +
                        $"───────────────\n" +
                        $"<b>Tag ID:</b> {tagInfo}\n" +
                        $"<b>Asset ID:</b> {assetIdInfo}\n" +
                        $"<b>Registry:</b> {registryInfo}\n" +
                        (string.IsNullOrEmpty(gatewayInfo) ? "" : $"{gatewayInfo}\n") +
                        $"───────────────\n" +
                        $"<b>Position:</b>\n" +
                        $"  X: {anchor.worldLockedPosition.x:F3}m\n" +
                        $"  Y: {anchor.worldLockedPosition.y:F3}m\n" +
                        $"  Z: {anchor.worldLockedPosition.z:F3}m";
                }

                bodyText.text = body;
            }

            // Update buttons
            UpdateProposeButton(claimStatus);
            UpdateAnnotateIntentButton(askAnchorButton, askAnchorButtonText, askAnchor3DText, askAnchor3DCollider, claimStatus, askAnchorState, "Describe");
            UpdateAnnotateIntentButton(actionSuggestButton, actionSuggestButtonText, actionSuggest3DText, actionSuggest3DCollider, claimStatus, actionSuggestState, "Actions");
        }

        private string RenderAnnotationSection(string intentType, Gateway.AnnotationState annState)
        {
            if (annState == null || annState.status == Gateway.AnnotationStatus.None) return "";

            string annColor = annState.status switch
            {
                Gateway.AnnotationStatus.Active => "#00FF88",
                Gateway.AnnotationStatus.Proposed or Gateway.AnnotationStatus.EndorsedOrg1 or Gateway.AnnotationStatus.EndorsedOrg2 or Gateway.AnnotationStatus.Requesting => "#FFDD44",
                Gateway.AnnotationStatus.Rejected => "#FF4444",
                Gateway.AnnotationStatus.Revoked => "#9966CC",
                _ => "#888888"
            };

            string intentLabel = intentType == "ASK_ANCHOR" ? "DESCRIPTION" : "ACTIONS";
            string tierLabel = !string.IsNullOrEmpty(annState.tier) ? annState.tier : "N/A";
            string statusLabel = annState.GetStatusDescription();

            string section = $"\n<color={annColor}><b>▸ {intentLabel}</b> [{tierLabel}] {statusLabel}</color>\n";
            if (annState.HasContent) section += $"<color=#CCCCCC>{annState.contentText}</color>";
            return section;
        }

        // ============================================================
        // REGISTRY STATUS
        // ============================================================

        private void UpdateRegistryStatusText(Gateway.ClaimStatus status, string claimId)
        {
            if (registryStatusText == null) return;
            (Color color, string text) = status switch
            {
                Gateway.ClaimStatus.None => (noneColor, "Unproposed"),
                Gateway.ClaimStatus.Pending => (pendingColor, !string.IsNullOrEmpty(claimId) ? "Submitted..." : "Pending..."),
                Gateway.ClaimStatus.Proposed => (proposedColor, "Proposed"),
                Gateway.ClaimStatus.Active => (activeColor, "Approved"),
                Gateway.ClaimStatus.Rejected => (rejectedColor, "Rejected"),
                Gateway.ClaimStatus.Revoked => (revokedColor, "Revoked"),
                _ => (noneColor, "Unknown")
            };
            registryStatusText.text = $"Registry: {text}";
            registryStatusText.color = color;
        }

        private string GetRegistryStatusString(Gateway.ClaimStatus status, string claimId)
        {
            return status switch
            {
                Gateway.ClaimStatus.None => "<color=#AAAAAA>Unproposed</color>",
                Gateway.ClaimStatus.Pending => !string.IsNullOrEmpty(claimId) ? "<color=#FFCC00>Submitted</color>" : "<color=#FFCC00>Pending...</color>",
                Gateway.ClaimStatus.Proposed => "<color=#88CCFF>Proposed</color>",
                Gateway.ClaimStatus.Active => "<color=#00FF88>Approved</color>",
                Gateway.ClaimStatus.Rejected => "<color=#FF5555>Rejected</color>",
                Gateway.ClaimStatus.Revoked => "<color=#9966CC>Revoked</color>",
                _ => "<color=#888888>Unknown</color>"
            };
        }

        // ============================================================
        // PROPOSE BUTTON
        // ============================================================

        private void UpdateProposeButton(Gateway.ClaimStatus status)
        {
            bool canPropose = CanPropose(status);
            string label = GetProposeButtonLabel(status);
            if (proposeButton != null) { proposeButton.interactable = canPropose; if (proposeButtonText != null) { proposeButtonText.text = label; proposeButtonText.color = canPropose ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f); } }
            if (proposeButton3DText != null) { proposeButton3DText.text = label; proposeButton3DText.color = canPropose ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f); if (proposeButton3DCollider != null) proposeButton3DCollider.enabled = canPropose; }
        }

        private bool CanPropose(Gateway.ClaimStatus status)
        {
            return !string.IsNullOrEmpty(currentAssetId) && (status == Gateway.ClaimStatus.None || status == Gateway.ClaimStatus.Rejected || status == Gateway.ClaimStatus.Revoked);
        }

        private string GetProposeButtonLabel(Gateway.ClaimStatus status)
        {
            return status switch
            {
                Gateway.ClaimStatus.None => "Propose",
                Gateway.ClaimStatus.Pending => "Submitting...",
                Gateway.ClaimStatus.Proposed => "Proposed",
                Gateway.ClaimStatus.Active => "Approved",
                Gateway.ClaimStatus.Rejected or Gateway.ClaimStatus.Revoked => "Re-Propose",
                _ => "Propose"
            };
        }

        private void OnProposeButtonClicked()
        {
            if (string.IsNullOrEmpty(currentAssetId) || currentAnchor == null) return;
            var currentStatus = gatewaySync != null ? gatewaySync.GetClaimStatus(currentAssetId) : Gateway.ClaimStatus.None;
            if (!CanPropose(currentStatus)) return;

            if (useGateway && gatewaySync != null)
            {
                Pose worldPose = new Pose(currentAnchor.worldLockedPosition, Quaternion.identity);
                gatewaySync.ProposeAnchor(currentAssetId, worldPose, currentAnchor.confidence, 0.01f, 1);
            }
            if (currentAnchor != null) UpdateInfo(currentAnchor);
        }

        public void OnPropose3DButtonClicked() => OnProposeButtonClicked();

        // ============================================================
        // ANNOTATE BUTTONS (v2.1: per intent type)
        // ============================================================

        private void UpdateAnnotateIntentButton(Button uiButton, TextMeshProUGUI uiText,
            TextMeshPro text3D, Collider collider3D,
            Gateway.ClaimStatus claimStatus, Gateway.AnnotationState annState, string baseLabel)
        {
            bool canAnnotate = CanRequestAnnotationForState(claimStatus, annState);
            string label = GetAnnotateButtonLabelForState(annState, baseLabel);

            if (uiButton != null)
            {
                uiButton.interactable = canAnnotate;
                if (uiText != null) { uiText.text = label; uiText.color = canAnnotate ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f); }
            }
            if (text3D != null)
            {
                text3D.text = label;
                text3D.color = canAnnotate ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
                if (collider3D != null) collider3D.enabled = canAnnotate;
            }
        }

        private bool CanRequestAnnotationForState(Gateway.ClaimStatus claimStatus, Gateway.AnnotationState annState)
        {
            if (string.IsNullOrEmpty(currentAssetId)) return false;
            if (claimStatus != Gateway.ClaimStatus.Active) return false;
            if (annState != null && !annState.CanRequest) return false;
            return true;
        }

        private string GetAnnotateButtonLabelForState(Gateway.AnnotationState annState, string baseLabel)
        {
            if (annState == null) return baseLabel;
            return annState.status switch
            {
                Gateway.AnnotationStatus.Requesting => "Requesting...",
                Gateway.AnnotationStatus.Proposed or Gateway.AnnotationStatus.EndorsedOrg1 or Gateway.AnnotationStatus.EndorsedOrg2 => "Pending...",
                Gateway.AnnotationStatus.Active => $"{baseLabel} ✓",
                Gateway.AnnotationStatus.Rejected or Gateway.AnnotationStatus.Revoked => $"Re-{baseLabel}",
                _ => baseLabel
            };
        }

        private void OnAnnotateButtonClicked(string intentType)
        {
            Debug.Log($"[AnchorInfoPanel] Annotate button clicked: {intentType}");
            if (string.IsNullOrEmpty(currentAssetId) || currentAnchor == null || gatewaySync == null) return;
            if (!gatewaySync.CanRequestAnnotation(currentAssetId, intentType)) return;

            bool initiated = gatewaySync.RequestAnnotation(
                currentAssetId, currentAnchor.className, currentAnchor.confidence,
                intentType, defaultAnnotationTier);

            if (initiated)
                Debug.Log($"[AnchorInfoPanel] 🤖 Annotation requested: {currentAssetId}:{intentType} (tier={defaultAnnotationTier})");

            if (currentAnchor != null) UpdateInfo(currentAnchor);
        }

        public void OnAskAnchor3DButtonClicked() => OnAnnotateButtonClicked("ASK_ANCHOR");
        public void OnActionSuggest3DButtonClicked() => OnAnnotateButtonClicked("ACTION_SUGGEST");

        /// <summary>
        /// Backward-compatible entry point for DepthAnchorSystem's single annotate button.
        /// Defaults to ASK_ANCHOR intent type.
        /// </summary>
        public void OnAnnotate3DButtonClicked() => OnAnnotateButtonClicked("ASK_ANCHOR");

        // ============================================================
        // PUBLIC API
        // ============================================================

        public DepthAnchorSystem.DepthAnchorInstance GetCurrentAnchor() => currentAnchor;
        public string GetCurrentAssetId() => currentAssetId;

        public void Clear()
        {
            currentAnchor = null;
            currentAssetId = null;
            if (titleText != null) titleText.text = "";
            if (bodyText != null) bodyText.text = "";
            if (idText != null) idText.text = "";
            if (confidenceText != null) confidenceText.text = "";
            if (stateText != null) stateText.text = "";
            if (positionText != null) positionText.text = "";
            if (tagIdText != null) tagIdText.text = "";
            if (assetIdText != null) assetIdText.text = "";
            if (registryStatusText != null) registryStatusText.text = "";
            if (claimIdText != null) claimIdText.text = "";
            if (proposeButton != null) proposeButton.interactable = false;
            if (proposeButtonText != null) proposeButtonText.text = "Propose";
            if (askAnchorButton != null) askAnchorButton.interactable = false;
            if (askAnchorButtonText != null) askAnchorButtonText.text = "Describe";
            if (actionSuggestButton != null) actionSuggestButton.interactable = false;
            if (actionSuggestButtonText != null) actionSuggestButtonText.text = "Actions";
        }
    }
}