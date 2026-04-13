// ============================================================================
// FILE: AnnotationState.cs
// State management for governed annotations
// v2.1: Multi-card model — dictionary key is "assetId:intentType"
//       Each (assetId, intentType) pair has its own independent lifecycle.
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
    public enum AnnotationStatus
    {
        None,
        Requesting,
        Proposed,
        EndorsedOrg1,
        EndorsedOrg2,
        Active,
        Rejected,
        Revoked
    }

    [Serializable]
    public class AnnotationState
    {
        public string assetId;
        public string intentType;       // v2.1: ASK_ANCHOR or ACTION_SUGGEST
        public string annotationId;
        public AnnotationStatus status;

        // Content
        public string contentText;
        public string tier;
        public string activationMethod;

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

        /// <summary>
        /// Composite key for this annotation: "assetId:intentType"
        /// </summary>
        public string CompositeKey => $"{assetId}:{intentType}";

        public string GetStatusDescription()
        {
            switch (status)
            {
                case AnnotationStatus.Requesting: return "Requesting annotation...";
                case AnnotationStatus.Proposed: return "Proposed - Awaiting endorsement";
                case AnnotationStatus.EndorsedOrg1: return "Org1 Endorsed - Awaiting Org2";
                case AnnotationStatus.EndorsedOrg2: return "Org2 Endorsed - Awaiting Org1";
                case AnnotationStatus.Active: return "Active ✓";
                case AnnotationStatus.Rejected: return $"Rejected by {rejectedBy}";
                case AnnotationStatus.Revoked: return "Revoked";
                default: return "No annotation";
            }
        }
    }

    /// <summary>
    /// Manages annotation state for all (assetId, intentType) pairs.
    /// v2.1: Dictionary key is "assetId:intentType" (composite key).
    /// </summary>
    public class AnnotationStateManager
    {
        private Dictionary<string, AnnotationState> states = new Dictionary<string, AnnotationState>();

        // Events — key parameter is the composite key "assetId:intentType"
        public event Action<string, AnnotationState> OnStateChanged;
        public event Action<string, AnnotationState> OnAnnotationActive;
        public event Action<string, AnnotationState> OnAnnotationRejected;
        public event Action<string, AnnotationState> OnAnnotationRevoked;

        private static string MakeKey(string assetId, string intentType)
        {
            return $"{assetId}:{intentType}";
        }

        public AnnotationState GetState(string assetId, string intentType)
        {
            string key = MakeKey(assetId, intentType);
            return states.TryGetValue(key, out var state) ? state : null;
        }

        public AnnotationState GetOrCreateState(string assetId, string intentType)
        {
            string key = MakeKey(assetId, intentType);
            if (!states.TryGetValue(key, out var state))
            {
                state = new AnnotationState
                {
                    assetId = assetId,
                    intentType = intentType,
                    status = AnnotationStatus.None
                };
                states[key] = state;
            }
            return state;
        }

        public void SetRequesting(string assetId, string intentType)
        {
            var state = GetOrCreateState(assetId, intentType);
            state.status = AnnotationStatus.Requesting;
            NotifyStateChanged(state.CompositeKey, state);
        }

        public void SetProposed(string assetId, string intentType, string annotationId, string contentText, string tier)
        {
            var state = GetOrCreateState(assetId, intentType);
            state.status = AnnotationStatus.Proposed;
            state.annotationId = annotationId;
            state.contentText = contentText;
            state.tier = tier;
            state.proposedAt = DateTime.Now;
            state.endorsedByOrg1 = false;
            state.endorsedByOrg2 = false;

            Debug.Log($"[AnnotationState] {assetId}:{intentType} → ANN_PROPOSED (tier={tier})");
            NotifyStateChanged(state.CompositeKey, state);
        }

        public void SetEndorsedOrg1(string assetId, string intentType)
        {
            var state = GetOrCreateState(assetId, intentType);
            state.endorsedByOrg1 = true;
            if (state.endorsedByOrg1 && state.endorsedByOrg2)
            {
                SetActive(assetId, intentType, state.contentText, state.tier, "DUAL_ENDORSEMENT");
            }
            else
            {
                state.status = AnnotationStatus.EndorsedOrg1;
                Debug.Log($"[AnnotationState] {assetId}:{intentType} → ANN_ENDORSED_ORG1");
                NotifyStateChanged(state.CompositeKey, state);
            }
        }

        public void SetEndorsedOrg2(string assetId, string intentType)
        {
            var state = GetOrCreateState(assetId, intentType);
            state.endorsedByOrg2 = true;
            if (state.endorsedByOrg1 && state.endorsedByOrg2)
            {
                SetActive(assetId, intentType, state.contentText, state.tier, "DUAL_ENDORSEMENT");
            }
            else
            {
                state.status = AnnotationStatus.EndorsedOrg2;
                Debug.Log($"[AnnotationState] {assetId}:{intentType} → ANN_ENDORSED_ORG2");
                NotifyStateChanged(state.CompositeKey, state);
            }
        }

        public void SetActive(string assetId, string intentType, string contentText, string tier, string activationMethod)
        {
            var state = GetOrCreateState(assetId, intentType);
            state.status = AnnotationStatus.Active;
            state.endorsedByOrg1 = true;
            state.endorsedByOrg2 = true;
            state.activatedAt = DateTime.Now;
            state.activationMethod = activationMethod;
            if (!string.IsNullOrEmpty(contentText)) state.contentText = contentText;
            if (!string.IsNullOrEmpty(tier)) state.tier = tier;

            Debug.Log($"[AnnotationState] {assetId}:{intentType} → ANN_ACTIVE ✓ ({activationMethod})");
            NotifyStateChanged(state.CompositeKey, state);
            OnAnnotationActive?.Invoke(state.CompositeKey, state);
        }

        public void SetRejected(string assetId, string intentType, string rejectedBy, string reason)
        {
            var state = GetOrCreateState(assetId, intentType);
            state.status = AnnotationStatus.Rejected;
            state.rejectedBy = rejectedBy;
            state.rejectionReason = reason;
            state.rejectedAt = DateTime.Now;
            Debug.Log($"[AnnotationState] {assetId}:{intentType} → ANN_REJECTED by {rejectedBy}");
            NotifyStateChanged(state.CompositeKey, state);
            OnAnnotationRejected?.Invoke(state.CompositeKey, state);
        }

        public void SetRevoked(string assetId, string intentType, string revokedBy, string reason)
        {
            var state = GetOrCreateState(assetId, intentType);
            state.status = AnnotationStatus.Revoked;
            state.revokedBy = revokedBy;
            state.revocationReason = reason;
            state.revokedAt = DateTime.Now;
            Debug.Log($"[AnnotationState] {assetId}:{intentType} → ANN_REVOKED by {revokedBy}");
            NotifyStateChanged(state.CompositeKey, state);
            OnAnnotationRevoked?.Invoke(state.CompositeKey, state);
        }

        public void LoadFromSnapshot(string assetId, string intentType, string annotationId, string state,
            string contentText, string tier, bool endorsedOrg1, bool endorsedOrg2)
        {
            var annState = GetOrCreateState(assetId, intentType);
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

            NotifyStateChanged(annState.CompositeKey, annState);
        }

        /// <summary>
        /// Get all annotations for a specific asset (across all intent types)
        /// </summary>
        public IEnumerable<AnnotationState> GetAnnotationsForAsset(string assetId)
        {
            foreach (var kvp in states)
                if (kvp.Value.assetId == assetId)
                    yield return kvp.Value;
        }

        /// <summary>
        /// Get all active annotations for a specific asset
        /// </summary>
        public IEnumerable<AnnotationState> GetActiveAnnotationsForAsset(string assetId)
        {
            foreach (var kvp in states)
                if (kvp.Value.assetId == assetId && kvp.Value.status == AnnotationStatus.Active)
                    yield return kvp.Value;
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

        private void NotifyStateChanged(string compositeKey, AnnotationState state)
        {
            OnStateChanged?.Invoke(compositeKey, state);
        }
    }
}
