// ============================================================================
// FILE: SkillCommandButton.cs   (Phase 6A — state-aware suggested commands)
//
// A tiny data holder placed on each spawned "suggested command" button.
// AnchorInfoPanel.PopulateSuggestions() spawns one of these per suggestion for
// the anchor's current claim state, binds it to a natural-language command, and
// sets the button's label. DepthAnchorSystem's poke raycast finds this component
// (by GetComponentInParent, NOT by name) and routes the poke back to
// AnchorInfoPanel.OnSuggestionPicked(this).
//
// IMPORTANT (paper requirement): picking a suggestion does NOT call chaincode.
// It only fills the panel's command line. The user still pokes "Ask LLM" to send
// the text through the SAME LLM skill pipeline as a typed command. Suggested and
// custom commands therefore travel one identical path — the LLM always interprets.
//
// Lives in the ARObjectDetection namespace (same as AnchorInfoPanel and
// DepthAnchorSystem) so no extra using directives are required anywhere.
// ============================================================================

using UnityEngine;
using TMPro;

namespace ARObjectDetection
{
          public class SkillCommandButton : MonoBehaviour
          {
                    [Tooltip("The natural-language command this button represents. Normally set at runtime by AnchorInfoPanel.Bind().")]
                    [SerializeField] private string command;

                    [Tooltip("Optional label showing the command on the button. Auto-found in children if left empty.")]
                    [SerializeField] private TextMeshPro label;

                    private AnchorInfoPanel owner;

                    /// <summary>The natural-language command this button will place on the command line.</summary>
                    public string Command => command;

                    /// <summary>
                    /// Configure this button. Called by AnchorInfoPanel immediately after Instantiate.
                    /// </summary>
                    public void Bind(AnchorInfoPanel panel, string nlCommand)
                    {
                              owner = panel;
                              command = nlCommand;
                              if (label == null) label = GetComponentInChildren<TextMeshPro>(true);
                              if (label != null) label.text = nlCommand;
                              // Rename for readable logs. The name is irrelevant to routing (which is
                              // by component), but must not contain Ask/Confirm/Cancel/Propose tokens.
                              gameObject.name = "Suggestion_" + SafeName(nlCommand);
                    }

                    /// <summary>Non-raycast entry point (e.g. an editor test harness) — same effect as a poke.</summary>
                    public void Pick()
                    {
                              if (owner != null) owner.OnSuggestionPicked(this);
                    }

                    private static string SafeName(string s)
                    {
                              if (string.IsNullOrEmpty(s)) return "cmd";
                              int n = Mathf.Min(24, s.Length);
                              return s.Substring(0, n).Replace(' ', '_');
                    }
          }
}