// ============================================================================
// FILE: SkillDtos.cs
// Data Transfer Objects for the LLM-mediated skill-gateway flow (Phase 6).
//
// These map field-for-field to the backend contract:
//   - POST /skills/interpret  request:  { userText, context }
//   - POST /skills/interpret  response: { success, decision_id, requires_confirmation,
//                                         decision, audit, errors, expires_in_ms }
//   - POST /skills/execute    request:  { decision_id, confirm:true }
//   - POST /skills/execute    response: { success, decision_id, anchor, anchor_tx_id,
//                                         final_state, audit_record_tx, audit_link_tx }
//
// IMPORTANT — JsonUtility limitations (Unity's built-in JSON):
//   * JsonUtility cannot deserialize arbitrary/dynamic objects (the `arguments`
//     object varies by function). We therefore keep `arguments` as a raw JSON
//     string (see SkillGatewayClient, which splices it out before parsing) and
//     expose typed accessors for the known v0.1.2 argument keys.
//   * JsonUtility cannot deserialize Dictionaries. Do not add Dictionary fields.
//   * All fields must be public and match JSON keys exactly (snake_case where the
//     server uses snake_case, camelCase where the server uses camelCase).
//
// v0.1.2 function names (from chaincode_interface.json):
//   ProposeAnchor, EndorseClaim, RevokeAnchor, EndorseRevoke,
//   GetClaim, GetClaimHistory, GetSnapshot
// ============================================================================

using System;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
          // -------------------------------------------------------------------------
          // REQUESTS
          // -------------------------------------------------------------------------

          /// <summary>
          /// Body for POST /skills/interpret.
          /// orgMsp is NOT sent — the gateway stamps it from the caller's TLS identity.
          /// We send userText + a context object the LLM uses to ground the request.
          /// </summary>
          [Serializable]
          public class SkillInterpretRequest
          {
                    public string userText;
                    public SkillContext context;
          }

          /// <summary>
          /// The grounding context handed to the LLM. The backend treats this purely as
          /// data (never instructions). Fields are optional; populate what's available.
          /// Matches the field names used in labeling-guide.md / intent_dimensions.md.
          /// </summary>
          [Serializable]
          public class SkillContext
          {
                    public string focusedAssetId;        // the asset the user currently has selected
                    public string currentClaimState;     // PROPOSED / ENDORSED_ORG1 / ACTIVE / REVOKE_PENDING / REVOKED / NONE
                    public string poseHash;              // sha256:... (only needed for ProposeAnchor)
                    public string metadataHash;          // sha256:... (only needed for ProposeAnchor)
                    public string className;             // YOLO class label, for human-readable grounding
                    public float confidence;             // perception confidence 0..1
                    public string[] visibleAssetIds;     // all asset ids currently visible in scene
          }

          /// <summary>
          /// Body for POST /skills/execute. confirm MUST be true (gateway enforces it).
          /// </summary>
          [Serializable]
          public class SkillExecuteRequest
          {
                    public string decision_id;
                    public bool confirm;
          }

          // -------------------------------------------------------------------------
          // DECISION (the LLM output, re-validated by the gateway)
          // -------------------------------------------------------------------------

          /// <summary>
          /// The Decision envelope returned by the runtime and re-validated by the gateway.
          /// REQUIRED_FIELDS in outputParser.js:
          ///   decisionType, intent, selectedFunction, arguments, policyReasoning, shouldInvoke
          /// Optional: selectedChaincode, riskLevel, requiresConfirmation, clarificationQuestion
          ///
          /// NOTE: `arguments` is intentionally NOT declared here as an object, because
          /// JsonUtility can't handle its variable shape. SkillGatewayClient extracts the
          /// raw `arguments` sub-JSON into Decision.argumentsJson before parsing the rest.
          /// </summary>
          [Serializable]
          public class SkillDecision
          {
                    public string decisionType;          // INVOKE / REJECT / CLARIFY
                    public string intent;                // plain-English summary of what the user wants
                    public string selectedChaincode;     // "anchor-registry" or ""
                    public string selectedFunction;      // one of the v0.1.2 names, or ""
                    public string riskLevel;             // READ_ONLY / WRITE_GOVERNED / FORBIDDEN / ...
                    public bool requiresConfirmation;
                    public string clarificationQuestion; // only present when decisionType == CLARIFY
                    public string policyReasoning;       // why the LLM made this decision
                    public bool shouldInvoke;

                    // Filled in by SkillGatewayClient (not a JSON field on its own):
                    [NonSerialized] public string argumentsJson;

                    public bool IsInvoke => decisionType == "INVOKE";
                    public bool IsReject => decisionType == "REJECT";
                    public bool IsClarify => decisionType == "CLARIFY";

                    /// <summary>Pretty one-line summary for logs / debug HUD.</summary>
                    public string Summary()
                    {
                              if (IsClarify) return $"CLARIFY: {clarificationQuestion}";
                              if (IsReject) return $"REJECT: {policyReasoning}";
                              return $"INVOKE {selectedFunction} [{riskLevel}]";
                    }
          }

          /// <summary>
          /// Typed view over the variable `arguments` object. Because JsonUtility can't
          /// parse a dynamic object, we declare every v0.1.2 argument key as an optional
          /// field. Unknown keys are simply ignored; absent keys stay null/0.
          /// Keys come from chaincode_interface.json requiredArgs/optionalArgs.
          /// </summary>
          [Serializable]
          public class SkillArguments
          {
                    public string assetId;       // all write/read functions except GetSnapshot
                    public string poseHash;      // ProposeAnchor
                    public string metadataHash;  // ProposeAnchor
                    public string reason;        // RevokeAnchor
                    public string note;          // EndorseClaim / EndorseRevoke (optional)
                    public string description;   // ProposeAnchor (optional)
                    public float confidence;     // ProposeAnchor (optional)
                    public int sourceFrameId;    // ProposeAnchor (optional)
                    public int limit;            // GetSnapshot (optional)
                    public string cursor;        // GetSnapshot (optional)
          }

          // -------------------------------------------------------------------------
          // AUDIT (provenance metadata; surfaced in Level-3 preview / debug HUD)
          // -------------------------------------------------------------------------

          /// <summary>
          /// The audit sub-object the runtime returns inside both interpret and execute
          /// responses. Field names match interpret.js exactly. Used for the Level-3
          /// transparency panel and the latency-decomposition HUD (experiment S3).
          /// </summary>
          [Serializable]
          public class SkillAudit
          {
                    public string skillId;
                    public string skillVersion;
                    public string skillManifestHash;
                    public string llmProvider;
                    public string llmModel;
                    public string llmCallId;
                    public string llmFinishReason;
                    public string intentHash;
                    public string contextHash;
                    public string argumentHash;
                    public string orgMsp;
                    public int tokenEstimate;
                    public float llmLatencyMs;
                    public float totalLatencyMs;
                    public string timestamp;
          }

          // -------------------------------------------------------------------------
          // RESPONSES
          // -------------------------------------------------------------------------

          /// <summary>
          /// Response from POST /skills/interpret.
          /// `decision` and `audit` are parsed separately by SkillGatewayClient because
          /// `decision.arguments` needs special handling — see that file.
          /// </summary>
          [Serializable]
          public class SkillInterpretResponse
          {
                    public bool success;
                    public string decision_id;
                    public bool requires_confirmation;
                    public int expires_in_ms;
                    public string[] errors;

                    // Populated by SkillGatewayClient after secondary parsing:
                    [NonSerialized] public SkillDecision decision;
                    [NonSerialized] public SkillAudit audit;

                    // Raw error string for non-2xx HTTP (set by the client on failure):
                    [NonSerialized] public string transportError;
          }

          /// <summary>
          /// Response from POST /skills/execute (success path).
          /// </summary>
          [Serializable]
          public class SkillExecuteResponse
          {
                    public bool success;
                    public string decision_id;
                    public string anchor_tx_id;
                    public string final_state;
                    public string audit_record_tx;
                    public string audit_link_tx;
                    public string error;

                    // For failure paths that still recorded audit (HTTP 409):
                    public bool audit_recorded;
                    public string audit_tx;
                    public string audit_terminal;
                    public bool ledger_commit;
          }

          // -------------------------------------------------------------------------
          // HEALTH (optional, used by a "Test Connection" button)
          // -------------------------------------------------------------------------

          [Serializable]
          public class SkillHealthResponse
          {
                    public string gatewayOrg;
                    public string gatewayMsp;
                    public bool runtimeReachable;
                    public string runtimeError;
          }
}