using UnityEngine;
using TMPro;

namespace ARObjectDetection
{
          /// <summary>
          /// Component for the InfoPanel prefab.
          /// Displays detailed information about a selected anchor.
          /// </summary>
          public class AnchorInfoPanel : MonoBehaviour
          {
                    [Header("Text References")]
                    [SerializeField] private TextMeshPro titleText;
                    [SerializeField] private TextMeshPro bodyText;

                    [Header("Optional References")]
                    [SerializeField] private TextMeshPro idText;
                    [SerializeField] private TextMeshPro confidenceText;
                    [SerializeField] private TextMeshPro stateText;
                    [SerializeField] private TextMeshPro positionText;

                    [Header("Visual Settings")]
                    [SerializeField] private Renderer panelBackground;
                    [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.9f);
                    [SerializeField] private Color borderColor = new Color(0f, 0.8f, 1f, 1f);

                    private DepthAnchorSystem.DepthAnchorInstance currentAnchor;

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
                    }

                    /// <summary>
                    /// Update the panel with anchor information
                    /// </summary>
                    public void UpdateInfo(DepthAnchorSystem.DepthAnchorInstance anchor)
                    {
                              if (anchor == null) return;
                              currentAnchor = anchor;

                              // If using individual text fields
                              if (idText != null) idText.text = $"ID: #{anchor.trackId}";
                              if (confidenceText != null) confidenceText.text = $"Confidence: {anchor.confidence:P0}";
                              if (stateText != null) stateText.text = $"State: {anchor.trackState}";
                              if (positionText != null)
                              {
                                        positionText.text = $"Position:\n" +
                                                           $"X: {anchor.worldLockedPosition.x:F2}m\n" +
                                                           $"Y: {anchor.worldLockedPosition.y:F2}m\n" +
                                                           $"Z: {anchor.worldLockedPosition.z:F2}m";
                              }

                              // Title
                              if (titleText != null)
                              {
                                        titleText.text = anchor.className.ToUpper();
                              }

                              // Body text (combined info)
                              if (bodyText != null)
                              {
                                        string lockStatus = anchor.isLocked ? "<color=#00FF00>LOCKED</color>" : "<color=#FFFF00>UNLOCKED</color>";
                                        string stateColor = anchor.trackState == TrackState.Confirmed ? "#00FF00" : "#FFFF00";

                                        bodyText.text =
                                            $"<b>Track ID:</b> #{anchor.trackId}\n" +
                                            $"<b>Confidence:</b> {anchor.confidence:P0}\n" +
                                            $"<b>State:</b> <color={stateColor}>{anchor.trackState}</color>\n" +
                                            $"<b>Lock:</b> {lockStatus}\n" +
                                            $"<b>Depth Conf:</b> {anchor.lastDepthConfidence:F2}\n" +
                                            $"\n<b>World Position:</b>\n" +
                                            $"  X: {anchor.worldLockedPosition.x:F3}m\n" +
                                            $"  Y: {anchor.worldLockedPosition.y:F3}m\n" +
                                            $"  Z: {anchor.worldLockedPosition.z:F3}m";
                              }

                              // Update border color based on lock state
                              if (panelBackground != null && panelBackground.material != null)
                              {
                                        Color border = anchor.isLocked ? new Color(0f, 1f, 0.5f, 1f) : new Color(1f, 0.8f, 0f, 1f);
                                        // If you have a border material property, set it here
                              }
                    }

                    /// <summary>
                    /// Get the current anchor being displayed
                    /// </summary>
                    public DepthAnchorSystem.DepthAnchorInstance GetCurrentAnchor()
                    {
                              return currentAnchor;
                    }

                    /// <summary>
                    /// Clear the panel
                    /// </summary>
                    public void Clear()
                    {
                              currentAnchor = null;
                              if (titleText != null) titleText.text = "";
                              if (bodyText != null) bodyText.text = "";
                              if (idText != null) idText.text = "";
                              if (confidenceText != null) confidenceText.text = "";
                              if (stateText != null) stateText.text = "";
                              if (positionText != null) positionText.text = "";
                    }
          }
}