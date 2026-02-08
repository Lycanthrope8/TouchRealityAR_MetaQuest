// ============================================================================
// FILE: AnchorClaimState.cs
// State management for anchor claims with DUAL ENDORSEMENT workflow
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// Claim status enum matching chaincode states
    /// </summary>
    public enum ClaimStatus
    {
        None,           // No claim exists
        Pending,        // Local pending (before server confirms)
        Proposed,       // PROPOSED - waiting for both endorsements
        EndorsedOrg1,   // ENDORSED_ORG1 - Org1 endorsed, waiting for Org2
        EndorsedOrg2,   // ENDORSED_ORG2 - Org2 endorsed, waiting for Org1
        Active,         // ACTIVE - Both orgs endorsed
        Rejected,       // REJECTED - One org rejected
        RevokePending,  // REVOKE_PENDING - Revocation initiated
        Revoked         // REVOKED - Anchor deleted
    }

    /// <summary>
    /// Represents the state of an anchor claim
    /// </summary>
    [Serializable]
    public class AnchorClaimState
    {
        public string assetId;
        public string claimId;
        public ClaimStatus status;

        // Proposal info
        public string proposedViaOrg;
        public DateTime proposedAt;

        // Dual endorsement tracking
        public bool endorsedByOrg1;
        public bool endorsedByOrg2;
        public DateTime? endorsedOrg1At;
        public DateTime? endorsedOrg2At;
        public DateTime? activatedAt;

        // Rejection info
        public string rejectedBy;
        public string rejectionReason;
        public DateTime? rejectedAt;

        // Revocation info
        public string revokeInitiatedBy;
        public string revokeRequiredEndorser;
        public string revokeReason;
        public DateTime? revokeInitiatedAt;

        // Computed properties
        public bool CanPropose => status == ClaimStatus.None || status == ClaimStatus.Rejected || status == ClaimStatus.Revoked;
        public bool CanRevoke => status == ClaimStatus.Active;
        public bool IsRevokePending => status == ClaimStatus.RevokePending;
        public bool IsActive => status == ClaimStatus.Active;
        public bool IsPending => status == ClaimStatus.Pending || status == ClaimStatus.Proposed ||
                                 status == ClaimStatus.EndorsedOrg1 || status == ClaimStatus.EndorsedOrg2;

        public bool RequiresMyAction(string myMspId)
        {
            if (status == ClaimStatus.RevokePending)
            {
                return revokeRequiredEndorser == myMspId;
            }
            return false;
        }

        public string GetStatusDescription()
        {
            switch (status)
            {
                case ClaimStatus.Proposed:
                    return "Proposed - Awaiting Org1 & Org2 endorsement";
                case ClaimStatus.EndorsedOrg1:
                    return "Org1 Endorsed ✓ - Awaiting Org2";
                case ClaimStatus.EndorsedOrg2:
                    return "Org2 Endorsed ✓ - Awaiting Org1";
                case ClaimStatus.Active:
                    return "Active ✓✓ - Both orgs endorsed";
                case ClaimStatus.Rejected:
                    return $"Rejected by {rejectedBy}";
                case ClaimStatus.RevokePending:
                    return $"Revoke pending - Awaiting {revokeRequiredEndorser}";
                case ClaimStatus.Revoked:
                    return "Revoked - Anchor deleted";
                default:
                    return status.ToString();
            }
        }
    }

    /// <summary>
    /// Manages the state of all anchor claims
    /// </summary>
    public class AnchorClaimStateManager
    {
        private Dictionary<string, AnchorClaimState> states = new Dictionary<string, AnchorClaimState>();

        // Events
        public event Action<string, AnchorClaimState> OnStateChanged;
        public event Action<string, AnchorClaimState> OnProposed;
        public event Action<string, AnchorClaimState> OnEndorsedOrg1;
        public event Action<string, AnchorClaimState> OnEndorsedOrg2;
        public event Action<string, AnchorClaimState> OnActivated;
        public event Action<string, AnchorClaimState> OnRejected;
        public event Action<string, AnchorClaimState> OnRevokePending;
        public event Action<string, AnchorClaimState> OnRevoked;

        public AnchorClaimState GetState(string assetId)
        {
            return states.TryGetValue(assetId, out var state) ? state : null;
        }

        public AnchorClaimState GetOrCreateState(string assetId)
        {
            if (!states.TryGetValue(assetId, out var state))
            {
                state = new AnchorClaimState
                {
                    assetId = assetId,
                    status = ClaimStatus.None
                };
                states[assetId] = state;
            }
            return state;
        }

        /// <summary>
        /// Set state to Pending (local state before server confirms)
        /// </summary>
        public void SetPending(string assetId)
        {
            var state = GetOrCreateState(assetId);
            state.status = ClaimStatus.Pending;
            state.proposedAt = DateTime.Now;
            NotifyStateChanged(assetId, state);
        }

        /// <summary>
        /// Set state to Proposed (server confirmed proposal)
        /// </summary>
        public void SetProposed(string assetId, string claimId, string proposedViaOrg)
        {
            var state = GetOrCreateState(assetId);
            state.status = ClaimStatus.Proposed;
            state.claimId = claimId;
            state.proposedViaOrg = proposedViaOrg;
            state.proposedAt = DateTime.Now;
            state.endorsedByOrg1 = false;
            state.endorsedByOrg2 = false;

            Debug.Log($"[StateManager] {assetId} → PROPOSED (claimId: {claimId})");

            NotifyStateChanged(assetId, state);
            OnProposed?.Invoke(assetId, state);
        }

        /// <summary>
        /// Set state to EndorsedOrg1
        /// </summary>
        public void SetEndorsedOrg1(string assetId)
        {
            var state = GetOrCreateState(assetId);
            state.endorsedByOrg1 = true;
            state.endorsedOrg1At = DateTime.Now;

            // Check if both orgs have endorsed
            if (state.endorsedByOrg1 && state.endorsedByOrg2)
            {
                SetActive(assetId);
            }
            else
            {
                state.status = ClaimStatus.EndorsedOrg1;
                Debug.Log($"[StateManager] {assetId} → ENDORSED_ORG1 (waiting for Org2)");
                NotifyStateChanged(assetId, state);
                OnEndorsedOrg1?.Invoke(assetId, state);
            }
        }

        /// <summary>
        /// Set state to EndorsedOrg2
        /// </summary>
        public void SetEndorsedOrg2(string assetId)
        {
            var state = GetOrCreateState(assetId);
            state.endorsedByOrg2 = true;
            state.endorsedOrg2At = DateTime.Now;

            // Check if both orgs have endorsed
            if (state.endorsedByOrg1 && state.endorsedByOrg2)
            {
                SetActive(assetId);
            }
            else
            {
                state.status = ClaimStatus.EndorsedOrg2;
                Debug.Log($"[StateManager] {assetId} → ENDORSED_ORG2 (waiting for Org1)");
                NotifyStateChanged(assetId, state);
                OnEndorsedOrg2?.Invoke(assetId, state);
            }
        }

        /// <summary>
        /// Set state to Active (both orgs endorsed)
        /// </summary>
        public void SetActive(string assetId)
        {
            var state = GetOrCreateState(assetId);
            state.status = ClaimStatus.Active;
            state.endorsedByOrg1 = true;
            state.endorsedByOrg2 = true;
            state.activatedAt = DateTime.Now;

            Debug.Log($"[StateManager] {assetId} → ACTIVE ✓✓ (Both orgs endorsed!)");

            NotifyStateChanged(assetId, state);
            OnActivated?.Invoke(assetId, state);
        }

        /// <summary>
        /// Set state to Rejected
        /// </summary>
        public void SetRejected(string assetId, string rejectedBy, string reason)
        {
            var state = GetOrCreateState(assetId);
            state.status = ClaimStatus.Rejected;
            state.rejectedBy = rejectedBy;
            state.rejectionReason = reason;
            state.rejectedAt = DateTime.Now;

            Debug.Log($"[StateManager] {assetId} → REJECTED by {rejectedBy}");

            NotifyStateChanged(assetId, state);
            OnRejected?.Invoke(assetId, state);
        }

        /// <summary>
        /// Set state to RevokePending
        /// </summary>
        public void SetRevokePending(string assetId, string initiatedBy, string requiredEndorser, string reason)
        {
            var state = GetOrCreateState(assetId);
            state.status = ClaimStatus.RevokePending;
            state.revokeInitiatedBy = initiatedBy;
            state.revokeRequiredEndorser = requiredEndorser;
            state.revokeReason = reason;
            state.revokeInitiatedAt = DateTime.Now;

            Debug.Log($"[StateManager] {assetId} → REVOKE_PENDING (initiated by {initiatedBy}, needs {requiredEndorser})");

            NotifyStateChanged(assetId, state);
            OnRevokePending?.Invoke(assetId, state);
        }

        /// <summary>
        /// Set state to Revoked
        /// </summary>
        public void SetRevoked(string assetId)
        {
            var state = GetOrCreateState(assetId);
            state.status = ClaimStatus.Revoked;

            Debug.Log($"[StateManager] {assetId} → REVOKED (anchor deleted)");

            NotifyStateChanged(assetId, state);
            OnRevoked?.Invoke(assetId, state);
        }

        private void NotifyStateChanged(string assetId, AnchorClaimState state)
        {
            OnStateChanged?.Invoke(assetId, state);
        }

        /// <summary>
        /// Get all states with a specific status
        /// </summary>
        public IEnumerable<AnchorClaimState> GetStatesByStatus(ClaimStatus status)
        {
            foreach (var kvp in states)
            {
                if (kvp.Value.status == status)
                    yield return kvp.Value;
            }
        }

        /// <summary>
        /// Get all active anchors
        /// </summary>
        public IEnumerable<AnchorClaimState> GetActiveAnchors()
        {
            return GetStatesByStatus(ClaimStatus.Active);
        }

        /// <summary>
        /// Get all pending claims (Proposed, EndorsedOrg1, EndorsedOrg2)
        /// </summary>
        public IEnumerable<AnchorClaimState> GetPendingClaims()
        {
            foreach (var kvp in states)
            {
                if (kvp.Value.IsPending)
                    yield return kvp.Value;
            }
        }

        /// <summary>
        /// Get pending revocations
        /// </summary>
        public IEnumerable<AnchorClaimState> GetPendingRevocations()
        {
            return GetStatesByStatus(ClaimStatus.RevokePending);
        }

        /// <summary>
        /// Get pending revocations requiring action from specified org
        /// </summary>
        public IEnumerable<AnchorClaimState> GetPendingRevocationsRequiringAction(string mspId)
        {
            foreach (var kvp in states)
            {
                if (kvp.Value.status == ClaimStatus.RevokePending &&
                    kvp.Value.revokeRequiredEndorser == mspId)
                {
                    yield return kvp.Value;
                }
            }
        }

        /// <summary>
        /// Clear all states
        /// </summary>
        public void Clear()
        {
            states.Clear();
        }
    }
}