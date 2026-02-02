// ============================================================================
// FILE: AnchorClaimState.cs
// State models and manager for anchor claims.
// EP5: Added GetLastEventId, GetOrCreateState, GetAllStates
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
    public enum ClaimStatus
    {
        None,
        Pending,
        Proposed,
        Active,
        Rejected,
        Revoked,
        Unknown
    }

    [Serializable]
    public class AnchorClaimState
    {
        public string assetId;
        public ClaimStatus status = ClaimStatus.None;
        public string claimId;
        public string lastEventId;
        public string publisherId;
        public string conflictClassification;
        public string rejectionReason;
        public int endorsementCount;
        public string createdAt;
        public string updatedAt;

        public bool CanPropose => status == ClaimStatus.None ||
                                   status == ClaimStatus.Rejected ||
                                   status == ClaimStatus.Revoked;
    }

    [Serializable]
    public class GatewayEvent
    {
        public string type;
        public string eventId;
        public string assetId;
        public string claimId;
        public string publisherId;
        public string state;
        public string reason;
        public int endorsementCount;
        public string timestamp;
        public bool isReplay;
    }

    public class AnchorClaimStateManager
    {
        private Dictionary<string, AnchorClaimState> states = new Dictionary<string, AnchorClaimState>();
        private HashSet<string> processedEventKeys = new HashSet<string>();
        private string lastEventId;
        private const int MAX_PROCESSED_KEYS = 500;
        private const string PREFS_LAST_EVENT_ID = "GatewayLastEventId";

        public event Action<string, AnchorClaimState> OnStateChanged;

        public AnchorClaimStateManager()
        {
            // Restore last event ID from PlayerPrefs
            lastEventId = PlayerPrefs.GetString(PREFS_LAST_EVENT_ID, null);
            if (!string.IsNullOrEmpty(lastEventId))
            {
                Debug.Log($"[AnchorClaimStateManager] Restored lastEventId: {lastEventId}");
            }
        }

        public string GetLastEventId()
        {
            return lastEventId;
        }

        public AnchorClaimState GetState(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;
            states.TryGetValue(assetId, out var state);
            return state;
        }

        public AnchorClaimState GetOrCreateState(string assetId)
        {
            if (string.IsNullOrEmpty(assetId)) return null;

            if (!states.TryGetValue(assetId, out var state))
            {
                state = new AnchorClaimState { assetId = assetId };
                states[assetId] = state;
            }
            return state;
        }

        public IEnumerable<AnchorClaimState> GetAllStates()
        {
            return states.Values;
        }

        public void SetPending(string assetId, string claimId = null)
        {
            var state = GetOrCreateState(assetId);
            state.status = ClaimStatus.Pending;
            if (!string.IsNullOrEmpty(claimId))
                state.claimId = claimId;
            state.updatedAt = DateTime.UtcNow.ToString("o");

            OnStateChanged?.Invoke(assetId, state);
        }

        public bool ApplyEvent(GatewayEvent evt)
        {
            if (evt == null || string.IsNullOrEmpty(evt.assetId))
                return false;

            // Deduplication key
            string eventKey = $"{evt.claimId}:{evt.type}:{evt.eventId}";
            if (processedEventKeys.Contains(eventKey))
            {
                return false; // Duplicate
            }

            // Add to processed set
            processedEventKeys.Add(eventKey);

            // Evict old keys if needed
            if (processedEventKeys.Count > MAX_PROCESSED_KEYS)
            {
                // Simple eviction: clear half
                processedEventKeys.Clear();
            }

            // Update last event ID
            if (!string.IsNullOrEmpty(evt.eventId))
            {
                lastEventId = evt.eventId;
                PlayerPrefs.SetString(PREFS_LAST_EVENT_ID, lastEventId);
            }

            // Get or create state
            var state = GetOrCreateState(evt.assetId);

            // Update state from event
            if (!string.IsNullOrEmpty(evt.claimId))
                state.claimId = evt.claimId;
            if (!string.IsNullOrEmpty(evt.publisherId))
                state.publisherId = evt.publisherId;
            if (evt.endorsementCount > 0)
                state.endorsementCount = evt.endorsementCount;

            state.lastEventId = evt.eventId;
            state.updatedAt = evt.timestamp ?? DateTime.UtcNow.ToString("o");

            // Map event type to status
            ClaimStatus newStatus = state.status;
            switch (evt.type)
            {
                case "CLAIM_PROPOSED":
                    newStatus = ClaimStatus.Proposed;
                    break;
                case "CLAIM_ENDORSED":
                case "CLAIM_ACTIVATED":
                    if (evt.state == "ACTIVE")
                        newStatus = ClaimStatus.Active;
                    break;
                case "CLAIM_REJECTED":
                    newStatus = ClaimStatus.Rejected;
                    state.rejectionReason = evt.reason;
                    break;
                case "CLAIM_REVOKED":
                    newStatus = ClaimStatus.Revoked;
                    break;
                case "CLAIM_REOPENED":
                    newStatus = ClaimStatus.Proposed;
                    break;
                case "ACTIVE_CHANGED":
                    // Could go either way
                    break;
            }

            bool statusChanged = state.status != newStatus;
            state.status = newStatus;

            // Notify listeners
            OnStateChanged?.Invoke(evt.assetId, state);

            return true;
        }

        public void ApplySnapshot(SnapshotAssetState[] assets)
        {
            if (assets == null) return;

            foreach (var asset in assets)
            {
                if (string.IsNullOrEmpty(asset.asset_id)) continue;

                var state = GetOrCreateState(asset.asset_id);
                state.claimId = asset.claim_id;
                state.publisherId = asset.publisher_id;
                state.endorsementCount = asset.endorsement_count;

                switch (asset.state)
                {
                    case "PROPOSED":
                        state.status = ClaimStatus.Proposed;
                        break;
                    case "ACTIVE":
                        state.status = ClaimStatus.Active;
                        break;
                    case "REJECTED":
                        state.status = ClaimStatus.Rejected;
                        break;
                    case "REVOKED":
                        state.status = ClaimStatus.Revoked;
                        break;
                    default:
                        state.status = ClaimStatus.Unknown;
                        break;
                }

                OnStateChanged?.Invoke(asset.asset_id, state);
            }
        }

        public void ClearAll()
        {
            states.Clear();
            processedEventKeys.Clear();
            lastEventId = null;
            PlayerPrefs.DeleteKey(PREFS_LAST_EVENT_ID);
        }
    }
}