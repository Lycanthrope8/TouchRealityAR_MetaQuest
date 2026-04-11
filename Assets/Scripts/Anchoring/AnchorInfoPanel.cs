// ============================================================================
// FILE: AnchorInfoPanel.cs
// Component for the InfoPanel prefab.
// Displays detailed information about a selected anchor including:
// - Asset ID (for AprilTag-associated anchors)
// - Tag ID
// - Registry status from Gateway (Pending / Proposed / Active / Rejected / Revoked)
// - Propose button functionality with Gateway integration
// 
// EP5 UPDATE: Now integrates with GatewaySync for real ledger-confirmed state.
// v2.0: Added annotation display + "Request Annotation" button
//       Changed assetId format to TAG_{tagId} (class name is metadata only)
// ============================================================================

using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ARObjectDetection
{
    /// <summary>
    /// Component for the InfoPanel prefab.
    /// Displays detailed information about a selected anchor.
    /// Now integrated with Gateway for ledger-confirmed state and annotations.
    /// </summary>
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
        [Tooltip("Assign a Unity UI Button for the Propose action")]
        [SerializeField] private Button proposeButton;
        [SerializeField] private TextMeshProUGUI proposeButtonText;

        [Header("Propose Button (3D World Space - Alternative)")]
        [Tooltip("Assign a TextMeshPro for a 3D clickable button (alternative to UI Button)")]
        [SerializeField] private TextMeshPro proposeButton3DText;
        [SerializeField] private Collider proposeButton3DCollider;

        [Header("Annotation Button (v2.0)")]
        [Tooltip("Assign a Unity UI Button for the Request Annotation action")]
        [SerializeField] private Button annotateButton;
        [SerializeField] private TextMeshProUGUI annotateButtonText;

        [Header("Annotation Button (3D World Space - Alternative)")]
        [SerializeField] private TextMeshPro annotateButton3DText;
        [SerializeField] private Collider annotateButton3DCollider;

        [Header("Annotation Settings")]
        [Tooltip("Default annotation tier: ADVISORY (auto-approve) or GOVERNED (dual endorsement)")]
        [SerializeField] private string defaultAnnotationTier = "ADVISORY";

        [Header("Visual Settings")]
        [SerializeField] private Renderer panelBackground;
        [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        [SerializeField] private Color borderColor = new Color(0f, 0.8f, 1f, 1f);

        [Header("Registry Status Colors")]
        [SerializeField] private Color noneColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        [SerializeField] private Color pendingColor = new Color(1f, 0.8f, 0f, 1f);
        [SerializeField] private Color proposedColor = new Color(0.5f, 0.8f, 1f, 1f);
        [SerializeField] private Color activeColor = new Color(0f, 1f, 0.5f, 1f);
        [SerializeField] private Color rejectedColor = new Color(1f, 0.3f, 0.3f, 1f);
        [SerializeField] private Color revokedColor = new Color(0.6f, 0.3f, 0.6f, 1f);

        [Header("Gateway Integration")]
        [Tooltip("Use Gateway for real ledger state (disable for offline/local-only mode)")]
        [SerializeField] private bool useGateway = true;

        [Tooltip("Reference to SiteFrameManager for pose conversion")]
        [SerializeField] private MonoBehaviour siteFrameManager;

        private DepthAnchorSystem.DepthAnchorInstance currentAnchor;
        private string currentAssetId = null;

        // Gateway reference (found at runtime)
        private Gateway.GatewaySync gatewaySync;

        private void Awake()
        {
            // Auto-find text components if not assigned
            if (titleText == null || bodyText == null)
            {
                var allTexts = GetComponentsInChildren<TextMeshPro>();
                if (allTexts.Length > 0 && titleText == null) titleText = allTexts[0];
                if (allTexts.Length > 1 && bodyText == null) bodyText = allTexts[1];
            }

            // Setup background
            if (panelBackground != null && panelBackground.material != null)
            {
                panelBackground.material.color = backgroundColor;
            }

            // Setup propose button click handler
            if (proposeButton != null)
            {
                proposeButton.onClick.AddListener(OnProposeButtonClicked);
            }

            // Setup annotate button click handler (v2.0)
            if (annotateButton != null)
            {
                annotateButton.onClick.AddListener(OnAnnotateButtonClicked);
            }

            // Find SiteFrameManager
            if (siteFrameManager == null)
            {
                siteFrameManager = FindFirstObjectByType<SiteFrameManager>();
            }
        }

        private void Start()
        {
            // Find GatewaySync
            if (useGateway)
            {
                gatewaySync = Gateway.GatewaySync.Instance;
                if (gatewaySync == null)
                {
                    gatewaySync = FindFirstObjectByType<Gateway.GatewaySync>();
                }

                if (gatewaySync != null)
                {
                    gatewaySync.OnClaimStatusChanged.AddListener(OnClaimStatusChanged);
                    gatewaySync.OnAnnotationStatusChanged.AddListener(OnAnnotationStatusChanged);
                    Debug.Log("[AnchorInfoPanel] Gateway integration enabled (v2.0 — annotations)");
                }
                else
                {
                    Debug.LogWarning("[AnchorInfoPanel] GatewaySync not found - using local state only");
                }
            }
        }

        private void OnDestroy()
        {
            if (proposeButton != null)
            {
                proposeButton.onClick.RemoveListener(OnProposeButtonClicked);
            }

            if (annotateButton != null)
            {
                annotateButton.onClick.RemoveListener(OnAnnotateButtonClicked);
            }

            if (gatewaySync != null)
            {
                gatewaySync.OnClaimStatusChanged.RemoveListener(OnClaimStatusChanged);
                gatewaySync.OnAnnotationStatusChanged.RemoveListener(OnAnnotationStatusChanged);
            }
        }

        /// <summary>
        /// Called when Gateway reports a claim status change
        /// </summary>
        private void OnClaimStatusChanged(string assetId, Gateway.ClaimStatus status)
        {
            if (assetId == currentAssetId && currentAnchor != null)
            {
                UpdateInfo(currentAnchor);
            }
        }

        /// <summary>
        /// Called when Gateway reports an annotation status change (v2.0)
        /// </summary>
        private void OnAnnotationStatusChanged(string assetId, Gateway.AnnotationStatus status)
        {
            if (assetId == currentAssetId && currentAnchor != null)
            {
                UpdateInfo(currentAnchor);
            }
        }

        /// <summary>
        /// Update the panel with anchor information
        /// </summary>
        public void UpdateInfo(DepthAnchorSystem.DepthAnchorInstance anchor)
        {
            if (anchor == null) return;
            currentAnchor = anchor;

            // Compute Asset ID — TAG-CENTRIC (v2.0: class name is metadata only)
            currentAssetId = null;
            int tagId = -1;

            if (anchor.hasAprilTagAssociation)
            {
                tagId = anchor.associatedAprilTagId;
                // v2.0: Tag-centric identity (class name is NOT part of the governed ID)
                currentAssetId = $"TAG_{tagId}";
            }

            // Get registry state from Gateway or local fallback
            Gateway.ClaimStatus claimStatus = Gateway.ClaimStatus.None;
            string claimId = null;

            if (useGateway && gatewaySync != null && !string.IsNullOrEmpty(currentAssetId))
            {
                var claimState = gatewaySync.GetClaimState(currentAssetId);
                if (claimState != null)
                {
                    claimStatus = claimState.status;
                    claimId = claimState.claimId;
                }
            }

            // Get annotation state (v2.0)
            Gateway.AnnotationState annotationState = null;
            if (useGateway && gatewaySync != null && !string.IsNullOrEmpty(currentAssetId))
            {
                annotationState = gatewaySync.GetAnnotationState(currentAssetId);
            }

            // ============================================================
            // Update Individual Text Fields (if assigned)
            // ============================================================
            if (idText != null)
                idText.text = $"ID: #{anchor.trackId}";

            if (confidenceText != null)
                confidenceText.text = $"Confidence: {anchor.confidence:P0}";

            if (stateText != null)
                stateText.text = $"State: {anchor.trackState}";

            if (positionText != null)
            {
                positionText.text = $"Position:\n" +
                                   $"X: {anchor.worldLockedPosition.x:F2}m\n" +
                                   $"Y: {anchor.worldLockedPosition.y:F2}m\n" +
                                   $"Z: {anchor.worldLockedPosition.z:F2}m";
            }

            // Tag ID field
            if (tagIdText != null)
            {
                if (anchor.hasAprilTagAssociation)
                    tagIdText.text = $"Tag ID: #{tagId}";
                else
                    tagIdText.text = "Tag ID: None";
            }

            // Asset ID field
            if (assetIdText != null)
            {
                if (!string.IsNullOrEmpty(currentAssetId))
                    assetIdText.text = $"Asset ID: {currentAssetId}";
                else
                    assetIdText.text = "Asset ID: N/A (no tag)";
            }

            // Claim ID field
            if (claimIdText != null)
            {
                if (!string.IsNullOrEmpty(claimId))
                    claimIdText.text = $"Claim: {claimId.Substring(0, Mathf.Min(8, claimId.Length))}...";
                else
                    claimIdText.text = "Claim: None";
            }

            // Registry Status field
            if (registryStatusText != null)
            {
                UpdateRegistryStatusText(claimStatus, claimId);
            }

            // ============================================================
            // Title
            // ============================================================
            if (titleText != null)
            {
                titleText.text = anchor.className.ToUpper();
            }

            // ============================================================
            // Body text (combined info)
            // ============================================================
            if (bodyText != null)
            {
                string lockStatus = anchor.isLocked ? "<color=#00FF00>LOCKED</color>" : "<color=#FFFF00>UNLOCKED</color>";
                string stateColor = anchor.trackState == TrackState.Confirmed ? "#00FF00" : "#FFFF00";

                // AprilTag info
                string tagInfo;
                if (anchor.hasAprilTagAssociation)
                    tagInfo = $"<color=#00FF00>#{tagId}</color>";
                else
                    tagInfo = "<color=#888888>None</color>";

                // Asset ID info
                string assetIdInfo;
                if (!string.IsNullOrEmpty(currentAssetId))
                    assetIdInfo = $"<color=#00FFFF>{currentAssetId}</color>";
                else
                    assetIdInfo = "<color=#888888>N/A</color>";

                // Registry status
                string registryInfo = GetRegistryStatusString(claimStatus, claimId);

                // Gateway connection status
                string gatewayInfo = "";
                if (useGateway && gatewaySync != null)
                {
                    gatewayInfo = gatewaySync.IsConnected
                        ? "<color=#00FF00>●</color> Gateway"
                        : "<color=#FF0000>●</color> Gateway";
                }

                // Build body text
                string body =
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

                // Annotation section (v2.0)
                if (annotationState != null && annotationState.status != Gateway.AnnotationStatus.None)
                {
                    string annColor = annotationState.status switch
                    {
                        Gateway.AnnotationStatus.Active => "#00FF88",
                        Gateway.AnnotationStatus.Proposed => "#FFDD44",
                        Gateway.AnnotationStatus.EndorsedOrg1 => "#FFDD44",
                        Gateway.AnnotationStatus.EndorsedOrg2 => "#FFDD44",
                        Gateway.AnnotationStatus.Requesting => "#FFDD44",
                        Gateway.AnnotationStatus.Rejected => "#FF4444",
                        Gateway.AnnotationStatus.Revoked => "#9966CC",
                        _ => "#888888"
                    };

                    string tierLabel = !string.IsNullOrEmpty(annotationState.tier) ? annotationState.tier : "N/A";
                    string annStatusLabel = annotationState.GetStatusDescription();

                    body += $"\n───────────────\n";
                    body += $"<color={annColor}><b>[{tierLabel}] {annStatusLabel}</b></color>\n";

                    if (annotationState.HasContent)
                    {
                        body += $"<color=#CCCCCC>{annotationState.contentText}</color>";
                    }
                }

                bodyText.text = body;
            }

            // ============================================================
            // Update Propose Button State
            // ============================================================
            UpdateProposeButton(claimStatus);

            // ============================================================
            // Update Annotate Button State (v2.0)
            // ============================================================
            UpdateAnnotateButton(claimStatus, annotationState);

            // Update border color based on lock state
            if (panelBackground != null && panelBackground.material != null)
            {
                Color border = anchor.isLocked ? new Color(0f, 1f, 0.5f, 1f) : new Color(1f, 0.8f, 0f, 1f);
            }
        }

        // ============================================================
        // REGISTRY STATUS (existing)
        // ============================================================

        private void UpdateRegistryStatusText(Gateway.ClaimStatus status, string claimId)
        {
            if (registryStatusText == null) return;

            Color color;
            string text;

            switch (status)
            {
                case Gateway.ClaimStatus.None:
                    color = noneColor;
                    text = "Unproposed";
                    break;
                case Gateway.ClaimStatus.Pending:
                    color = pendingColor;
                    text = !string.IsNullOrEmpty(claimId) ? "Submitted..." : "Pending...";
                    break;
                case Gateway.ClaimStatus.Proposed:
                    color = proposedColor;
                    text = "Proposed";
                    break;
                case Gateway.ClaimStatus.Active:
                    color = activeColor;
                    text = "Approved";
                    break;
                case Gateway.ClaimStatus.Rejected:
                    color = rejectedColor;
                    text = "Rejected";
                    break;
                case Gateway.ClaimStatus.Revoked:
                    color = revokedColor;
                    text = "Revoked";
                    break;
                default:
                    color = noneColor;
                    text = "Unknown";
                    break;
            }

            registryStatusText.text = $"Registry: {text}";
            registryStatusText.color = color;
        }

        private string GetRegistryStatusString(Gateway.ClaimStatus status, string claimId)
        {
            switch (status)
            {
                case Gateway.ClaimStatus.None:
                    return "<color=#AAAAAA>Unproposed</color>";
                case Gateway.ClaimStatus.Pending:
                    if (!string.IsNullOrEmpty(claimId))
                        return $"<color=#FFCC00>Submitted</color>";
                    return "<color=#FFCC00>Pending...</color>";
                case Gateway.ClaimStatus.Proposed:
                    return "<color=#88CCFF>Proposed</color>";
                case Gateway.ClaimStatus.Active:
                    return "<color=#00FF88>Approved</color>";
                case Gateway.ClaimStatus.Rejected:
                    return "<color=#FF5555>Rejected</color>";
                case Gateway.ClaimStatus.Revoked:
                    return "<color=#9966CC>Revoked</color>";
                default:
                    return "<color=#888888>Unknown</color>";
            }
        }

        // ============================================================
        // PROPOSE BUTTON (existing)
        // ============================================================

        private void UpdateProposeButton(Gateway.ClaimStatus status)
        {
            bool canPropose = CanPropose(status);
            string label = GetProposeButtonLabel(status);

            // Update UI Button
            if (proposeButton != null)
            {
                proposeButton.interactable = canPropose;

                if (proposeButtonText != null)
                {
                    proposeButtonText.text = label;
                    proposeButtonText.color = canPropose ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
                }
            }

            // Update 3D Text Button (alternative)
            if (proposeButton3DText != null)
            {
                proposeButton3DText.text = label;
                proposeButton3DText.color = canPropose ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);

                if (proposeButton3DCollider != null)
                    proposeButton3DCollider.enabled = canPropose;
            }
        }

        private bool CanPropose(Gateway.ClaimStatus status)
        {
            if (string.IsNullOrEmpty(currentAssetId))
                return false;

            return status == Gateway.ClaimStatus.None ||
                   status == Gateway.ClaimStatus.Rejected ||
                   status == Gateway.ClaimStatus.Revoked;
        }

        private string GetProposeButtonLabel(Gateway.ClaimStatus status)
        {
            switch (status)
            {
                case Gateway.ClaimStatus.None:
                    return "Propose";
                case Gateway.ClaimStatus.Pending:
                    return "Submitting...";
                case Gateway.ClaimStatus.Proposed:
                    return "Proposed";
                case Gateway.ClaimStatus.Active:
                    return "Approved";
                case Gateway.ClaimStatus.Rejected:
                    return "Re-Propose";
                case Gateway.ClaimStatus.Revoked:
                    return "Re-Propose";
                default:
                    return "Propose";
            }
        }

        private void OnProposeButtonClicked()
        {
            if (string.IsNullOrEmpty(currentAssetId))
            {
                Debug.LogWarning("[AnchorInfoPanel] Cannot propose - no Asset ID (anchor not AprilTag-associated)");
                return;
            }

            if (currentAnchor == null)
            {
                Debug.LogWarning("[AnchorInfoPanel] Cannot propose - no current anchor");
                return;
            }

            Gateway.ClaimStatus currentStatus = Gateway.ClaimStatus.None;
            if (gatewaySync != null)
            {
                currentStatus = gatewaySync.GetClaimStatus(currentAssetId);
            }

            if (!CanPropose(currentStatus))
            {
                Debug.LogWarning($"[AnchorInfoPanel] Cannot propose - current status is {currentStatus}");
                return;
            }

            if (useGateway && gatewaySync != null)
            {
                Pose worldPose = new Pose(currentAnchor.worldLockedPosition, Quaternion.identity);
                float confidence = currentAnchor.confidence;
                float stabilityRms = 0.01f;
                int observationCount = 1;

                bool initiated = gatewaySync.ProposeAnchor(
                    currentAssetId,
                    worldPose,
                    confidence,
                    stabilityRms,
                    observationCount
                );

                if (initiated)
                {
                    Debug.Log($"[AnchorInfoPanel] Propose initiated for '{currentAssetId}'");
                }
                else
                {
                    Debug.LogWarning($"[AnchorInfoPanel] Failed to initiate propose for '{currentAssetId}'");
                }
            }
            else
            {
                Debug.LogWarning("[AnchorInfoPanel] Gateway not available - proposal not sent");
            }

            if (currentAnchor != null)
            {
                UpdateInfo(currentAnchor);
            }
        }

        public void OnPropose3DButtonClicked()
        {
            OnProposeButtonClicked();
        }

        // ============================================================
        // ANNOTATE BUTTON (v2.0)
        // ============================================================

        private void UpdateAnnotateButton(Gateway.ClaimStatus claimStatus, Gateway.AnnotationState annotationState)
        {
            bool canAnnotate = CanRequestAnnotation(claimStatus, annotationState);
            string label = GetAnnotateButtonLabel(annotationState);

            // Update UI Button
            if (annotateButton != null)
            {
                annotateButton.interactable = canAnnotate;

                if (annotateButtonText != null)
                {
                    annotateButtonText.text = label;
                    annotateButtonText.color = canAnnotate ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
                }
            }

            // Update 3D Text Button (alternative)
            if (annotateButton3DText != null)
            {
                annotateButton3DText.text = label;
                annotateButton3DText.color = canAnnotate ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);

                if (annotateButton3DCollider != null)
                    annotateButton3DCollider.enabled = canAnnotate;
            }
        }

        private bool CanRequestAnnotation(Gateway.ClaimStatus claimStatus, Gateway.AnnotationState annotationState)
        {
            // Must have an asset ID
            if (string.IsNullOrEmpty(currentAssetId))
                return false;

            // Anchor must be ACTIVE on-chain
            if (claimStatus != Gateway.ClaimStatus.Active)
                return false;

            // No existing active/pending annotation
            if (annotationState != null && !annotationState.CanRequest)
                return false;

            // Check gateway
            if (gatewaySync != null)
                return gatewaySync.CanRequestAnnotation(currentAssetId);

            return false;
        }

        private string GetAnnotateButtonLabel(Gateway.AnnotationState annotationState)
        {
            if (annotationState == null)
                return "Request Annotation";

            switch (annotationState.status)
            {
                case Gateway.AnnotationStatus.Requesting:
                    return "Requesting...";
                case Gateway.AnnotationStatus.Proposed:
                case Gateway.AnnotationStatus.EndorsedOrg1:
                case Gateway.AnnotationStatus.EndorsedOrg2:
                    return "Pending...";
                case Gateway.AnnotationStatus.Active:
                    return "Annotated ✓";
                case Gateway.AnnotationStatus.Rejected:
                    return "Re-Annotate";
                case Gateway.AnnotationStatus.Revoked:
                    return "Re-Annotate";
                default:
                    return "Request Annotation";
            }
        }

        private void OnAnnotateButtonClicked()
        {
            Debug.Log("[AnchorInfoPanel] Annotate button clicked");
            if (string.IsNullOrEmpty(currentAssetId))
            {
                Debug.LogWarning("[AnchorInfoPanel] Cannot annotate - no Asset ID");
                return;
            }

            if (currentAnchor == null)
            {
                Debug.LogWarning("[AnchorInfoPanel] Cannot annotate - no current anchor");
                return;
            }

            if (gatewaySync == null)
            {
                Debug.LogWarning("[AnchorInfoPanel] Cannot annotate - GatewaySync not available");
                return;
            }

            if (!gatewaySync.CanRequestAnnotation(currentAssetId))
            {
                Debug.LogWarning($"[AnchorInfoPanel] Cannot annotate {currentAssetId} - not eligible");
                return;
            }

            bool initiated = gatewaySync.RequestAnnotation(
                currentAssetId,
                currentAnchor.className,
                currentAnchor.confidence,
                defaultAnnotationTier
            );

            if (initiated)
            {
                Debug.Log($"[AnchorInfoPanel] 🤖 Annotation requested for '{currentAssetId}' (tier={defaultAnnotationTier})");
            }
            else
            {
                Debug.LogWarning($"[AnchorInfoPanel] Failed to request annotation for '{currentAssetId}'");
            }

            // Refresh UI
            if (currentAnchor != null)
            {
                UpdateInfo(currentAnchor);
            }
        }

        /// <summary>
        /// Called when 3D annotate button is clicked (via raycast interaction)
        /// </summary>
        public void OnAnnotate3DButtonClicked()
        {
            OnAnnotateButtonClicked();
        }

        // ============================================================
        // PUBLIC API (existing)
        // ============================================================

        public DepthAnchorSystem.DepthAnchorInstance GetCurrentAnchor()
        {
            return currentAnchor;
        }

        public string GetCurrentAssetId()
        {
            return currentAssetId;
        }

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

            // Reset buttons
            if (proposeButton != null)
                proposeButton.interactable = false;
            if (proposeButtonText != null)
                proposeButtonText.text = "Propose";
            if (proposeButton3DText != null)
                proposeButton3DText.text = "Propose";

            if (annotateButton != null)
                annotateButton.interactable = false;
            if (annotateButtonText != null)
                annotateButtonText.text = "Request Annotation";
            if (annotateButton3DText != null)
                annotateButton3DText.text = "Request Annotation";
        }
    }
}