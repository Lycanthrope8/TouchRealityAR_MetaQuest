// ============================================================================
// FILE: SkillDecisionState.cs
// Client-side state for the two-phase skill flow (Phase 6).
//
// The skill-gateway is a human-in-the-loop, two-phase commit:
//   Phase A: POST /skills/interpret  → gateway parks a Decision, returns decision_id
//   Phase B: POST /skills/execute    → user confirmed; gateway invokes Fabric
//
// Between A and B the Decision lives here, awaiting the user's Confirm/Cancel.
// This is the skill-flow analogue of AnchorClaimState, but it models the
// *interpretation* lifecycle rather than the on-chain anchor lifecycle.
// (The on-chain anchor state continues to be tracked by AnchorClaimStateManager
//  via SSE, exactly as before — this class does not replace that.)
// ============================================================================

using System;

namespace ARObjectDetection.Gateway
{
          public enum SkillFlowStage
          {
                    Idle,            // nothing in progress
                    Interpreting,    // waiting for /skills/interpret to return
                    AwaitingConfirm, // INVOKE decision parked, user must Confirm/Cancel
                    Executing,       // waiting for /skills/execute to return
                    Done,            // execute succeeded
                    Rejected,        // gateway REJECTed the request
                    Clarify,         // gateway asked for CLARIFY
                    Error            // transport/parse/chaincode error
          }

          /// <summary>
          /// Holds the live state of one in-flight skill interaction. There is normally
          /// only one of these active at a time (the user is interacting with one panel),
          /// but it is keyed by assetId so concurrent flows on different anchors don't
          /// clobber each other.
          /// </summary>
          [Serializable]
          public class SkillDecisionState
          {
                    public string assetId;             // the anchor this interaction targets ("" for snapshot/global)
                    public string userText;            // what the user typed
                    public SkillFlowStage stage = SkillFlowStage.Idle;

                    // Filled after interpret returns:
                    public string decisionId;
                    public SkillDecision decision;     // the parsed Decision (null until interpret returns)
                    public SkillAudit audit;           // provenance (for Level-3 panel / HUD)
                    public int expiresInMs;            // confirmation window from the gateway

                    // Filled after execute returns:
                    public string anchorTxId;
                    public string finalState;
                    public string auditRecordTx;
                    public string auditLinkTx;

                    // On any failure:
                    public string errorMessage;

                    public bool IsBusy => stage == SkillFlowStage.Interpreting || stage == SkillFlowStage.Executing;
                    public bool NeedsConfirm => stage == SkillFlowStage.AwaitingConfirm;

                    public void Reset(string assetId, string userText)
                    {
                              this.assetId = assetId;
                              this.userText = userText;
                              stage = SkillFlowStage.Idle;
                              decisionId = null;
                              decision = null;
                              audit = null;
                              expiresInMs = 0;
                              anchorTxId = null;
                              finalState = null;
                              auditRecordTx = null;
                              auditLinkTx = null;
                              errorMessage = null;
                    }

                    public string StageDescription()
                    {
                              return stage switch
                              {
                                        SkillFlowStage.Idle => "Ready",
                                        SkillFlowStage.Interpreting => "Interpreting…",
                                        SkillFlowStage.AwaitingConfirm => "Awaiting your confirmation",
                                        SkillFlowStage.Executing => "Submitting to ledger…",
                                        SkillFlowStage.Done => "Committed ✓",
                                        SkillFlowStage.Rejected => "Rejected by policy",
                                        SkillFlowStage.Clarify => "Needs clarification",
                                        SkillFlowStage.Error => "Error",
                                        _ => ""
                              };
                    }
          }
}