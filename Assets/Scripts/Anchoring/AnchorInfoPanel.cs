// ============================================================================
// FILE: AnchorInfoPanel.cs
// Component for the InfoPanel prefab.
// Displays detailed information about a selected anchor including:
// - Asset ID (for AprilTag-associated anchors)
// - Tag ID
// - Registry status (Unproposed / Proposed / Approved / Rejected)
// - Propose button functionality
// 
// NO OnGUI - all UI is rendered via TextMeshPro and Unity UI
// ============================================================================

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

namespace ARObjectDetection
{
    /// <summary>
    /// Registry state for an asset (local-only, no backend)
    /// </summary>
    public enum RegistryState
    {
        Unproposed,
        Proposed,
        Approved,
        Rejected
    }

    /// <summary>
    /// Component for the InfoPanel prefab.
    /// Displays detailed information about a selected anchor.
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

        [Header("Propose Button (UI Button)")]
        [Tooltip("Assign a Unity UI Button for the Propose action")]
        [SerializeField] private Button proposeButton;
        [SerializeField] private TextMeshProUGUI proposeButtonText;

        [Header("Propose Button (3D World Space - Alternative)")]
        [Tooltip("Assign a TextMeshPro for a 3D clickable button (alternative to UI Button)")]
        [SerializeField] private TextMeshPro proposeButton3DText;
        [SerializeField] private Collider proposeButton3DCollider;

        [Header("Visual Settings")]
        [SerializeField] private Renderer panelBackground;
        [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        [SerializeField] private Color borderColor = new Color(0f, 0.8f, 1f, 1f);

        [Header("Registry Status Colors")]
        [SerializeField] private Color unproposedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        [SerializeField] private Color proposedColor = new Color(1f, 0.8f, 0f, 1f);
        [SerializeField] private Color approvedColor = new Color(0f, 1f, 0.5f, 1f);
        [SerializeField] private Color rejectedColor = new Color(1f, 0.3f, 0.3f, 1f);

        // ============================================================
        // REGISTRY STATE STORAGE (In-memory, keyed by asset_id)
        // ============================================================
        private static Dictionary<string, RegistryState> registryStates = new Dictionary<string, RegistryState>();

        private DepthAnchorSystem.DepthAnchorInstance currentAnchor;
        private string currentAssetId = null;

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
        }

        private void OnDestroy()
        {
            if (proposeButton != null)
            {
                proposeButton.onClick.RemoveListener(OnProposeButtonClicked);
            }
        }

        /// <summary>
        /// Update the panel with anchor information
        /// </summary>
        public void UpdateInfo(DepthAnchorSystem.DepthAnchorInstance anchor)
        {
            if (anchor == null) return;
            currentAnchor = anchor;

            // Compute Asset ID if AprilTag-associated
            currentAssetId = null;
            int tagId = -1;

            if (anchor.hasAprilTagAssociation)
            {
                tagId = anchor.associatedAprilTagId;
                // Asset ID format: "<className>_TAG_<tagId>"
                currentAssetId = $"{anchor.className}_TAG_{tagId}";
            }

            // Get or create registry state
            RegistryState registryState = GetRegistryState(currentAssetId);

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

            // Registry Status field
            if (registryStatusText != null)
            {
                UpdateRegistryStatusText(registryState);
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
                string registryInfo = GetRegistryStatusString(registryState);

                bodyText.text =
                    $"<b>Track ID:</b> #{anchor.trackId}\n" +
                    $"<b>Confidence:</b> {anchor.confidence:P0}\n" +
                    $"<b>State:</b> <color={stateColor}>{anchor.trackState}</color>\n" +
                    $"<b>Lock:</b> {lockStatus}\n" +
                    $"───────────────\n" +
                    $"<b>Tag ID:</b> {tagInfo}\n" +
                    $"<b>Asset ID:</b> {assetIdInfo}\n" +
                    $"<b>Registry:</b> {registryInfo}\n" +
                    $"───────────────\n" +
                    $"<b>Position:</b>\n" +
                    $"  X: {anchor.worldLockedPosition.x:F3}m\n" +
                    $"  Y: {anchor.worldLockedPosition.y:F3}m\n" +
                    $"  Z: {anchor.worldLockedPosition.z:F3}m";
            }

            // ============================================================
            // Update Propose Button State
            // ============================================================
            UpdateProposeButton(registryState);

            // Update border color based on lock state
            if (panelBackground != null && panelBackground.material != null)
            {
                Color border = anchor.isLocked ? new Color(0f, 1f, 0.5f, 1f) : new Color(1f, 0.8f, 0f, 1f);
            }
        }

        private void UpdateRegistryStatusText(RegistryState state)
        {
            if (registryStatusText == null) return;

            Color color;
            string text;

            switch (state)
            {
                case RegistryState.Unproposed:
                    color = unproposedColor;
                    text = "Unproposed";
                    break;
                case RegistryState.Proposed:
                    color = proposedColor;
                    text = "Proposed";
                    break;
                case RegistryState.Approved:
                    color = approvedColor;
                    text = "Approved";
                    break;
                case RegistryState.Rejected:
                    color = rejectedColor;
                    text = "Rejected";
                    break;
                default:
                    color = unproposedColor;
                    text = "Unknown";
                    break;
            }

            registryStatusText.text = $"Registry: {text}";
            registryStatusText.color = color;
        }

        private string GetRegistryStatusString(RegistryState state)
        {
            switch (state)
            {
                case RegistryState.Unproposed:
                    return "<color=#AAAAAA>Unproposed</color>";
                case RegistryState.Proposed:
                    return "<color=#FFCC00>Proposed</color>";
                case RegistryState.Approved:
                    return "<color=#00FF88>Approved</color>";
                case RegistryState.Rejected:
                    return "<color=#FF5555>Rejected</color>";
                default:
                    return "<color=#888888>Unknown</color>";
            }
        }

        private void UpdateProposeButton(RegistryState state)
        {
            // Update UI Button
            if (proposeButton != null)
            {
                bool canPropose = CanPropose(state);
                proposeButton.interactable = canPropose;

                if (proposeButtonText != null)
                {
                    proposeButtonText.text = GetProposeButtonLabel(state);
                    proposeButtonText.color = canPropose ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
                }
            }

            // Update 3D Text Button (alternative)
            if (proposeButton3DText != null)
            {
                bool canPropose = CanPropose(state);
                proposeButton3DText.text = GetProposeButtonLabel(state);
                proposeButton3DText.color = canPropose ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);

                if (proposeButton3DCollider != null)
                    proposeButton3DCollider.enabled = canPropose;
            }
        }

        private bool CanPropose(RegistryState state)
        {
            // Can only propose if:
            // 1. Asset ID is available (anchor is AprilTag-associated / GREEN)
            // 2. State is Unproposed OR Rejected
            if (string.IsNullOrEmpty(currentAssetId))
                return false;

            return state == RegistryState.Unproposed || state == RegistryState.Rejected;
        }

        private string GetProposeButtonLabel(RegistryState state)
        {
            switch (state)
            {
                case RegistryState.Unproposed:
                    return "Propose";
                case RegistryState.Proposed:
                    return "Proposed";
                case RegistryState.Approved:
                    return "Approved";
                case RegistryState.Rejected:
                    return "Propose"; // Can re-propose after rejection
                default:
                    return "Propose";
            }
        }

        /// <summary>
        /// Called when the Propose button is clicked
        /// </summary>
        private void OnProposeButtonClicked()
        {
            if (string.IsNullOrEmpty(currentAssetId))
            {
                Debug.LogWarning("[AnchorInfoPanel] Cannot propose - no Asset ID (anchor not AprilTag-associated)");
                return;
            }

            RegistryState currentState = GetRegistryState(currentAssetId);

            if (!CanPropose(currentState))
            {
                Debug.LogWarning($"[AnchorInfoPanel] Cannot propose - current state is {currentState}");
                return;
            }

            // Set state to Proposed
            SetProposed(currentAssetId);

            Debug.Log($"[AnchorInfoPanel] Asset '{currentAssetId}' marked as PROPOSED");

            // Refresh the UI
            if (currentAnchor != null)
            {
                UpdateInfo(currentAnchor);
            }
        }

        /// <summary>
        /// Called when 3D button is clicked (via raycast interaction)
        /// Call this from your interaction system when the 3D button collider is hit
        /// </summary>
        public void OnPropose3DButtonClicked()
        {
            OnProposeButtonClicked();
        }

        // ============================================================
        // STATIC REGISTRY API
        // These methods can be called from anywhere to manage registry state
        // ============================================================

        /// <summary>
        /// Get the registry state for an asset_id.
        /// Returns Unproposed if not found.
        /// </summary>
        public static RegistryState GetRegistryState(string assetId)
        {
            if (string.IsNullOrEmpty(assetId))
                return RegistryState.Unproposed;

            if (registryStates.TryGetValue(assetId, out RegistryState state))
                return state;

            return RegistryState.Unproposed;
        }

        /// <summary>
        /// Set an asset as Proposed.
        /// </summary>
        public static void SetProposed(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return;
            registryStates[assetId] = RegistryState.Proposed;
            Debug.Log($"[AnchorInfoPanel.Registry] {assetId} -> PROPOSED");
        }

        /// <summary>
        /// Set an asset as Approved (called by supervisor/backend).
        /// </summary>
        public static void SetApproved(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return;
            registryStates[assetId] = RegistryState.Approved;
            Debug.Log($"[AnchorInfoPanel.Registry] {assetId} -> APPROVED");
        }

        /// <summary>
        /// Set an asset as Rejected (called by supervisor/backend).
        /// After rejection, the Propose button becomes clickable again.
        /// </summary>
        public static void SetRejected(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return;
            registryStates[assetId] = RegistryState.Rejected;
            Debug.Log($"[AnchorInfoPanel.Registry] {assetId} -> REJECTED");
        }

        /// <summary>
        /// Reset an asset to Unproposed state.
        /// </summary>
        public static void ResetToUnproposed(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return;
            registryStates[assetId] = RegistryState.Unproposed;
            Debug.Log($"[AnchorInfoPanel.Registry] {assetId} -> UNPROPOSED (reset)");
        }

        /// <summary>
        /// Clear all registry states (for testing/reset).
        /// </summary>
        public static void ClearAllRegistryStates()
        {
            registryStates.Clear();
            Debug.Log("[AnchorInfoPanel.Registry] All registry states cleared");
        }

        /// <summary>
        /// Get all registered asset IDs and their states (for debugging/display).
        /// </summary>
        public static Dictionary<string, RegistryState> GetAllRegistryStates()
        {
            return new Dictionary<string, RegistryState>(registryStates);
        }

        /// <summary>
        /// Get the current anchor being displayed
        /// </summary>
        public DepthAnchorSystem.DepthAnchorInstance GetCurrentAnchor()
        {
            return currentAnchor;
        }

        /// <summary>
        /// Get the current asset ID being displayed
        /// </summary>
        public string GetCurrentAssetId()
        {
            return currentAssetId;
        }

        /// <summary>
        /// Clear the panel
        /// </summary>
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

            // Reset button
            if (proposeButton != null)
                proposeButton.interactable = false;
            if (proposeButtonText != null)
                proposeButtonText.text = "Propose";
            if (proposeButton3DText != null)
                proposeButton3DText.text = "Propose";
        }
    }
}