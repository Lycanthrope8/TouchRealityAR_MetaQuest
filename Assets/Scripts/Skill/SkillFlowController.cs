// ============================================================================
// FILE: SkillFlowController.cs
// Orchestrates the LLM-mediated skill flow (Phase 6).
//
// Lives on the SAME GameObject as GatewaySync. It does NOT modify GatewaySync;
// it reuses GatewaySync.Config (the shared GatewayConfig) and adds a parallel
// SkillGatewayClient for the /skills/* endpoints.
//
// Flow:
//   BeginInterpret(assetId, userText, context)
//        → POST /skills/interpret
//        → on INVOKE+requires_confirmation: stage = AwaitingConfirm, fire OnAwaitingConfirm
//        → on REJECT:   stage = Rejected,  fire OnRejected
//        → on CLARIFY:  stage = Clarify,   fire OnClarify
//   ConfirmExecute()      (called when user taps Confirm)
//        → POST /skills/execute
//        → on success: stage = Done, fire OnExecuted   (anchor color updates arrive
//                                                        separately via SSE, as before)
//        → on chaincode failure: stage = Error, fire OnError
//   Cancel()              (called when user taps Cancel)
//        → discards the parked decision locally (gateway expires it after 5 min)
//
// A single SkillDecisionState ("current") models the in-flight interaction. The UI
// (SkillInfoPanel) subscribes to the events below and reads `Current` for display.
// ============================================================================

using System;
using UnityEngine;
using UnityEngine.Events;

namespace ARObjectDetection.Gateway
{
          public class SkillFlowController : MonoBehaviour
          {
                    [Header("Dependencies (auto-found if null)")]
                    [SerializeField] private GatewaySync gatewaySync;
                    [SerializeField] private SkillGatewayClient skillClient;

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;

                    [Header("Events")]
                    public UnityEvent<SkillDecisionState> OnStageChanged;     // fired on every stage transition
                    public UnityEvent<SkillDecisionState> OnAwaitingConfirm;  // INVOKE parked, show preview + Confirm/Cancel
                    public UnityEvent<SkillDecisionState> OnRejected;         // REJECT — show policyReasoning
                    public UnityEvent<SkillDecisionState> OnClarify;          // CLARIFY — show clarificationQuestion
                    public UnityEvent<SkillDecisionState> OnExecuted;         // execute succeeded
                    public UnityEvent<SkillDecisionState> OnError;            // transport/parse/chaincode error

                    private readonly SkillDecisionState current = new SkillDecisionState();
                    public SkillDecisionState Current => current;

                    private static SkillFlowController instance;
                    public static SkillFlowController Instance => instance;

                    private void Awake()
                    {
                              if (instance != null && instance != this) { /* allow multiple, but prefer first */ }
                              else instance = this;
                    }

                    private void Start()
                    {
                              if (gatewaySync == null)
                                        gatewaySync = GatewaySync.Instance ?? FindFirstObjectByType<GatewaySync>();

                              if (gatewaySync == null)
                              {
                                        Debug.LogError("[SkillFlowController] No GatewaySync found — skill flow disabled.");
                                        enabled = false;
                                        return;
                              }

                              // Reuse the same GatewayConfig that GatewaySync uses.
                              var config = gatewaySync.Config;
                              if (config == null)
                              {
                                        Debug.LogError("[SkillFlowController] GatewaySync has no GatewayConfig — skill flow disabled.");
                                        enabled = false;
                                        return;
                              }

                              if (skillClient == null)
                                        skillClient = GetComponent<SkillGatewayClient>() ?? gameObject.AddComponent<SkillGatewayClient>();
                              skillClient.Initialize(config);

                              if (enableDebugLogs) Debug.Log("[SkillFlowController] ✓ Ready (skill flow enabled).");
                    }

                    private void OnDestroy()
                    {
                              if (instance == this) instance = null;
                    }

                    // =====================================================================
                    // PUBLIC API
                    // =====================================================================

                    /// <summary>
                    /// Step 1: send the user's natural-language text to the gateway for interpretation.
                    /// `context` should be built by the caller (SkillInfoPanel) from the selected anchor.
                    /// </summary>
                    public bool BeginInterpret(string assetId, string userText, SkillContext context)
                    {
                              if (skillClient == null || !skillClient.IsInitialized)
                              {
                                        SetError("Skill client not initialized");
                                        return false;
                              }
                              if (string.IsNullOrWhiteSpace(userText))
                              {
                                        SetError("Please enter a request first");
                                        return false;
                              }
                              if (current.IsBusy)
                              {
                                        if (enableDebugLogs) Debug.LogWarning("[SkillFlowController] Busy; ignoring new interpret request.");
                                        return false;
                              }

                              current.Reset(assetId, userText);
                              current.stage = SkillFlowStage.Interpreting;
                              FireStage();

                              var request = new SkillInterpretRequest { userText = userText, context = context };

                              if (enableDebugLogs) Debug.Log($"[SkillFlowController] interpret: \"{userText}\" (asset={assetId})");

                              skillClient.Interpret(request, OnInterpretComplete, err => SetError($"interpret: {err}"));
                              return true;
                    }

                    /// <summary>
                    /// Step 2a: the user tapped Confirm. Execute the parked decision.
                    /// </summary>
                    public bool ConfirmExecute()
                    {
                              if (current.stage != SkillFlowStage.AwaitingConfirm || string.IsNullOrEmpty(current.decisionId))
                              {
                                        if (enableDebugLogs) Debug.LogWarning("[SkillFlowController] ConfirmExecute called with nothing to confirm.");
                                        return false;
                              }

                              current.stage = SkillFlowStage.Executing;
                              FireStage();

                              if (enableDebugLogs) Debug.Log($"[SkillFlowController] execute: {current.decisionId}");

                              skillClient.Execute(current.decisionId, OnExecuteComplete, err => SetError($"execute: {err}"));
                              return true;
                    }

                    /// <summary>
                    /// Step 2b: the user tapped Cancel. Drop the parked decision locally.
                    /// The gateway will expire it on its own after expires_in_ms.
                    /// </summary>
                    public void Cancel()
                    {
                              if (enableDebugLogs) Debug.Log("[SkillFlowController] cancelled.");
                              current.Reset(current.assetId, current.userText);
                              current.stage = SkillFlowStage.Idle;
                              FireStage();
                    }

                    /// <summary>Clear everything back to Idle (e.g. when the panel closes).</summary>
                    public void ResetFlow()
                    {
                              current.Reset(null, null);
                              current.stage = SkillFlowStage.Idle;
                              FireStage();
                    }

                    // =====================================================================
                    // CALLBACKS
                    // =====================================================================

                    private void OnInterpretComplete(SkillInterpretResponse resp)
                    {
                              current.decisionId = resp.decision_id;
                              current.decision = resp.decision;
                              current.audit = resp.audit;
                              current.expiresInMs = resp.expires_in_ms;

                              if (resp.decision == null)
                              {
                                        // Validation failed so hard the gateway didn't return a decision body.
                                        string errs = (resp.errors != null && resp.errors.Length > 0)
                                            ? string.Join("; ", resp.errors) : "no decision returned";
                                        SetError($"gateway: {errs}");
                                        return;
                              }

                              switch (resp.decision.decisionType)
                              {
                                        case "INVOKE":
                                                  if (resp.requires_confirmation)
                                                  {
                                                            current.stage = SkillFlowStage.AwaitingConfirm;
                                                            FireStage();
                                                            OnAwaitingConfirm?.Invoke(current);
                                                  }
                                                  else
                                                  {
                                                            // INVOKE that the gateway already rejected during validation
                                                            // (returned as success:false). Treat as rejection.
                                                            current.stage = SkillFlowStage.Rejected;
                                                            if (string.IsNullOrEmpty(current.errorMessage))
                                                                      current.errorMessage = resp.decision.policyReasoning;
                                                            FireStage();
                                                            OnRejected?.Invoke(current);
                                                  }
                                                  break;

                                        case "REJECT":
                                                  current.stage = SkillFlowStage.Rejected;
                                                  FireStage();
                                                  OnRejected?.Invoke(current);
                                                  break;

                                        case "CLARIFY":
                                                  current.stage = SkillFlowStage.Clarify;
                                                  FireStage();
                                                  OnClarify?.Invoke(current);
                                                  break;

                                        default:
                                                  SetError($"unknown decisionType: {resp.decision.decisionType}");
                                                  break;
                              }
                    }

                    private void OnExecuteComplete(SkillExecuteResponse resp)
                    {
                              if (resp.success)
                              {
                                        current.anchorTxId = resp.anchor_tx_id;
                                        current.finalState = resp.final_state;
                                        current.auditRecordTx = resp.audit_record_tx;
                                        current.auditLinkTx = resp.audit_link_tx;
                                        current.stage = SkillFlowStage.Done;
                                        FireStage();
                                        OnExecuted?.Invoke(current);

                                        if (enableDebugLogs)
                                                  Debug.Log($"[SkillFlowController] ✓ executed {current.decisionId} " +
                                                            $"anchor_tx={resp.anchor_tx_id} final_state={resp.final_state}");
                              }
                              else
                              {
                                        // Chaincode rejected (e.g. lifecycle violation). The attempt is still
                                        // recorded on-chain (audit_recorded). Surface the reason to the user.
                                        string msg = !string.IsNullOrEmpty(resp.error) ? resp.error : "chaincode rejected the transaction";
                                        if (resp.audit_recorded) msg += " (attempt recorded on-chain)";
                                        SetError(msg);
                              }
                    }

                    // =====================================================================
                    // INTERNAL
                    // =====================================================================

                    private void SetError(string message)
                    {
                              current.errorMessage = message;
                              current.stage = SkillFlowStage.Error;
                              Debug.LogError($"[SkillFlowController] {message}");
                              FireStage();
                              OnError?.Invoke(current);
                    }

                    private void FireStage() => OnStageChanged?.Invoke(current);
          }
}