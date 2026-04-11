// ============================================================================
// FILE: AnnotationState.cs
// State management for governed annotations
// Mirrors AnchorClaimState pattern for annotation lifecycle
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
          /// <summary>
          /// Annotation status enum matching chaincode states
          /// </summary>
          public enum AnnotationStatus
          {
                    None,               // No annotation exists
                    Requesting,         // Local state: request sent, waiting for server
                    Proposed,           // ANN_PROPOSED - waiting for endorsements (GOVERNED tier)
                    EndorsedOrg1,       // ANN_ENDORSED_ORG1 - Org1 endorsed, waiting for Org2
                    EndorsedOrg2,       // ANN_ENDORSED_ORG2 - Org2 endorsed, waiting for Org1
                    Active,             // ANN_ACTIVE - annotation is live
                    Rejected,           // ANN_REJECTED - annotation was rejected
                    Revoked             // ANN_REVOKED - annotation was revoked
          }

          /// <summary>
          /// Represents the state of an annotation for a specific asset
          /// </summary>
          [Serializable]
          public class AnnotationState
          {
                    public string assetId;
                    public string annotationId;
                    public AnnotationStatus status;

                    // Content
                    public string contentText;
                    public string tier;             // "ADVISORY" or "GOVERNED"
                    public string activationMethod; // "AUTO_APPROVE" or "DUAL_ENDORSEMENT"

                    // Endorsement tracking
                    public bool endorsedByOrg1;
                    public bool endorsedByOrg2;

                    // Rejection info
                    public string rejectedBy;
                    public string rejectionReason;

                    // Revocation info
                    public string revokedBy;
                    public string revocationReason;

                    // Timestamps
                    public DateTime? proposedAt;
                    public DateTime? activatedAt;
                    public DateTime? rejectedAt;
                    public DateTime? revokedAt;

                    // Computed properties
                    public bool IsActive => status == AnnotationStatus.Active;
                    public bool IsPending => status == AnnotationStatus.Requesting ||
                                              status == AnnotationStatus.Proposed ||
                                              status == AnnotationStatus.EndorsedOrg1 ||
                                              status == AnnotationStatus.EndorsedOrg2;
                    public bool HasContent => !string.IsNullOrEmpty(contentText);
                    public bool CanRequest => status == AnnotationStatus.None ||
                                               status == AnnotationStatus.Rejected ||
                                               status == AnnotationStatus.Revoked;

                    public string GetStatusDescription()
                    {
                              switch (status)
                              {
                                        case AnnotationStatus.Requesting:
                                                  return "Requesting annotation...";
                                        case AnnotationStatus.Proposed:
                                                  return "Proposed - Awaiting endorsement";
                                        case AnnotationStatus.EndorsedOrg1:
                                                  return "Org1 Endorsed - Awaiting Org2";
                                        case AnnotationStatus.EndorsedOrg2:
                                                  return "Org2 Endorsed - Awaiting Org1";
                                        case AnnotationStatus.Active:
                                                  return "Active ✓";
                                        case AnnotationStatus.Rejected:
                                                  return $"Rejected by {rejectedBy}";
                                        case AnnotationStatus.Revoked:
                                                  return "Revoked";
                                        default:
                                                  return "No annotation";
                              }
                    }
          }

          /// <summary>
          /// Manages annotation state for all assets.
          /// Mirrors the AnchorClaimStateManager pattern.
          /// </summary>
          public class AnnotationStateManager
          {
                    private Dictionary<string, AnnotationState> states = new Dictionary<string, AnnotationState>();

                    // Events
                    public event Action<string, AnnotationState> OnStateChanged;
                    public event Action<string, AnnotationState> OnAnnotationActive;
                    public event Action<string, AnnotationState> OnAnnotationRejected;
                    public event Action<string, AnnotationState> OnAnnotationRevoked;

                    public AnnotationState GetState(string assetId)
                    {
                              return states.TryGetValue(assetId, out var state) ? state : null;
                    }

                    public AnnotationState GetOrCreateState(string assetId)
                    {
                              if (!states.TryGetValue(assetId, out var state))
                              {
                                        state = new AnnotationState
                                        {
                                                  assetId = assetId,
                                                  status = AnnotationStatus.None
                                        };
                                        states[assetId] = state;
                              }
                              return state;
                    }

                    public void SetRequesting(string assetId)
                    {
                              var state = GetOrCreateState(assetId);
                              state.status = AnnotationStatus.Requesting;
                              NotifyStateChanged(assetId, state);
                    }

                    public void SetProposed(string assetId, string annotationId, string contentText, string tier)
                    {
                              var state = GetOrCreateState(assetId);
                              state.status = AnnotationStatus.Proposed;
                              state.annotationId = annotationId;
                              state.contentText = contentText;
                              state.tier = tier;
                              state.proposedAt = DateTime.Now;
                              state.endorsedByOrg1 = false;
                              state.endorsedByOrg2 = false;

                              Debug.Log($"[AnnotationState] {assetId} → ANN_PROPOSED (tier={tier})");

                              NotifyStateChanged(assetId, state);
                    }

                    public void SetEndorsedOrg1(string assetId)
                    {
                              var state = GetOrCreateState(assetId);
                              state.endorsedByOrg1 = true;

                              if (state.endorsedByOrg1 && state.endorsedByOrg2)
                              {
                                        SetActive(assetId, state.contentText, state.tier, "DUAL_ENDORSEMENT");
                              }
                              else
                              {
                                        state.status = AnnotationStatus.EndorsedOrg1;
                                        Debug.Log($"[AnnotationState] {assetId} → ANN_ENDORSED_ORG1");
                                        NotifyStateChanged(assetId, state);
                              }
                    }

                    public void SetEndorsedOrg2(string assetId)
                    {
                              var state = GetOrCreateState(assetId);
                              state.endorsedByOrg2 = true;

                              if (state.endorsedByOrg1 && state.endorsedByOrg2)
                              {
                                        SetActive(assetId, state.contentText, state.tier, "DUAL_ENDORSEMENT");
                              }
                              else
                              {
                                        state.status = AnnotationStatus.EndorsedOrg2;
                                        Debug.Log($"[AnnotationState] {assetId} → ANN_ENDORSED_ORG2");
                                        NotifyStateChanged(assetId, state);
                              }
                    }

                    public void SetActive(string assetId, string contentText, string tier, string activationMethod)
                    {
                              var state = GetOrCreateState(assetId);
                              state.status = AnnotationStatus.Active;
                              state.endorsedByOrg1 = true;
                              state.endorsedByOrg2 = true;
                              state.activatedAt = DateTime.Now;
                              state.activationMethod = activationMethod;

                              if (!string.IsNullOrEmpty(contentText))
                                        state.contentText = contentText;
                              if (!string.IsNullOrEmpty(tier))
                                        state.tier = tier;

                              Debug.Log($"[AnnotationState] {assetId} → ANN_ACTIVE ✓ ({activationMethod})");

                              NotifyStateChanged(assetId, state);
                              OnAnnotationActive?.Invoke(assetId, state);
                    }

                    public void SetRejected(string assetId, string rejectedBy, string reason)
                    {
                              var state = GetOrCreateState(assetId);
                              state.status = AnnotationStatus.Rejected;
                              state.rejectedBy = rejectedBy;
                              state.rejectionReason = reason;
                              state.rejectedAt = DateTime.Now;

                              Debug.Log($"[AnnotationState] {assetId} → ANN_REJECTED by {rejectedBy}");

                              NotifyStateChanged(assetId, state);
                              OnAnnotationRejected?.Invoke(assetId, state);
                    }

                    public void SetRevoked(string assetId, string revokedBy, string reason)
                    {
                              var state = GetOrCreateState(assetId);
                              state.status = AnnotationStatus.Revoked;
                              state.revokedBy = revokedBy;
                              state.revocationReason = reason;
                              state.revokedAt = DateTime.Now;

                              Debug.Log($"[AnnotationState] {assetId} → ANN_REVOKED by {revokedBy}");

                              NotifyStateChanged(assetId, state);
                              OnAnnotationRevoked?.Invoke(assetId, state);
                    }

                    /// <summary>
                    /// Load annotation state from snapshot data
                    /// </summary>
                    public void LoadFromSnapshot(string assetId, string annotationId, string state,
                        string contentText, string tier, bool endorsedOrg1, bool endorsedOrg2)
                    {
                              var annState = GetOrCreateState(assetId);
                              annState.annotationId = annotationId;
                              annState.contentText = contentText;
                              annState.tier = tier;
                              annState.endorsedByOrg1 = endorsedOrg1;
                              annState.endorsedByOrg2 = endorsedOrg2;

                              switch (state)
                              {
                                        case "ANN_PROPOSED": annState.status = AnnotationStatus.Proposed; break;
                                        case "ANN_ENDORSED_ORG1": annState.status = AnnotationStatus.EndorsedOrg1; break;
                                        case "ANN_ENDORSED_ORG2": annState.status = AnnotationStatus.EndorsedOrg2; break;
                                        case "ANN_ACTIVE": annState.status = AnnotationStatus.Active; break;
                                        case "ANN_REJECTED": annState.status = AnnotationStatus.Rejected; break;
                                        case "ANN_REVOKED": annState.status = AnnotationStatus.Revoked; break;
                                        default: annState.status = AnnotationStatus.None; break;
                              }

                              NotifyStateChanged(assetId, annState);
                    }

                    public IEnumerable<AnnotationState> GetActiveAnnotations()
                    {
                              foreach (var kvp in states)
                                        if (kvp.Value.status == AnnotationStatus.Active)
                                                  yield return kvp.Value;
                    }

                    public void Clear()
                    {
                              states.Clear();
                    }

                    private void NotifyStateChanged(string assetId, AnnotationState state)
                    {
                              OnStateChanged?.Invoke(assetId, state);
                    }
          }
}