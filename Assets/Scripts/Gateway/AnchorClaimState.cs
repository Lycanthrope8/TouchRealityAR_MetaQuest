// ============================================================================
// FILE: AnchorClaimState.cs
// Data models for anchor claim state management in Unity.
// Handles state transitions, event deduplication, and persistence.
// ============================================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
    /// <summary>
    /// Claim status as reported by the gateway/ledger.
    /// </summary>
    public enum ClaimStatus
    {
        /// <summary>No claim exists for this asset</summary>
        None,
        
        /// <summary>Claim submitted to gateway, awaiting commit confirmation</summary>
        Pending,
        
        /// <summary>Claim proposed and commit-confirmed on ledger</summary>
        Proposed,
        
        /// <summary>Claim endorsed/activated on ledger</summary>
        Active,
        
        /// <summary>Claim rejected by supervisor</summary>
        Rejected,
        
        /// <summary>Claim revoked by supervisor</summary>
        Revoked,
        
        /// <summary>Unknown/error state</summary>
        Unknown
    }

    /// <summary>
    /// State for a single asset's claim in the anchor registry.
    /// </summary>
    [Serializable]
    public class AnchorClaimState
    {
        public string assetId;
        public ClaimStatus status = ClaimStatus.None;
        public string claimId;
        public string lastEventId;
        public string publisherId;
        public float lastUpdateTime;
        public float proposedTime;
        public float confirmedTime;
        public string conflictClassification;
        public string rejectionReason;
        public int endorsementCount;

        /// <summary>
        /// True if we have a claim ID (submission acknowledged)
        /// </summary>
        public bool HasClaimId => !string.IsNullOrEmpty(claimId);

        /// <summary>
        /// True if the claim is finalized (not pending)
        /// </summary>
        public bool IsFinalized => status != ClaimStatus.Pending && status != ClaimStatus.None;

        /// <summary>
        /// True if the claim is in a terminal state
        /// </summary>
        public bool IsTerminal => status == ClaimStatus.Rejected || status == ClaimStatus.Revoked;

        /// <summary>
        /// True if another propose is allowed
        /// </summary>
        public bool CanPropose => status == ClaimStatus.None || status == ClaimStatus.Rejected || status == ClaimStatus.Revoked;

        public AnchorClaimState(string assetId)
        {
            this.assetId = assetId;
            this.lastUpdateTime = Time.realtimeSinceStartup;
        }

        public override string ToString()
        {
            return $"[{assetId}] status={status}, claimId={claimId ?? "null"}, eventId={lastEventId ?? "null"}";
        }
    }

    /// <summary>
    /// Central state manager for all anchor claims.
    /// Provides event deduplication and thread-safe updates.
    /// </summary>
    public class AnchorClaimStateManager
    {
        private readonly Dictionary<string, AnchorClaimState> states = new Dictionary<string, AnchorClaimState>();
        private readonly HashSet<string> processedEventKeys = new HashSet<string>();
        private readonly Queue<string> eventKeyQueue = new Queue<string>();
        private readonly object stateLock = new object();

        private const int MAX_EVENT_KEYS = 500;

        // Last received event ID for SSE reconnection
        private string lastEventId = null;
        private const string LAST_EVENT_ID_PREF_KEY = "GatewayLastEventId";

        /// <summary>
        /// Event fired when any claim state changes
        /// </summary>
        public event Action<string, AnchorClaimState> OnStateChanged;

        public AnchorClaimStateManager()
        {
            // Load last event ID from PlayerPrefs
            lastEventId = PlayerPrefs.GetString(LAST_EVENT_ID_PREF_KEY, null);
            if (!string.IsNullOrEmpty(lastEventId))
            {
                Debug.Log($"[AnchorClaimStateManager] Restored lastEventId from prefs: {lastEventId}");
            }
        }

        /// <summary>
        /// Get or create state for an asset
        /// </summary>
        public AnchorClaimState GetOrCreateState(string assetId)
        {
            if (string.IsNullOrEmpty(assetId))
                return null;

            lock (stateLock)
            {
                if (!states.TryGetValue(assetId, out AnchorClaimState state))
                {
                    state = new AnchorClaimState(assetId);
                    states[assetId] = state;
                }
                return state;
            }
        }

        /// <summary>
        /// Get state for an asset (returns null if not found)
        /// </summary>
        public AnchorClaimState GetState(string assetId)
        {
            if (string.IsNullOrEmpty(assetId))
                return null;

            lock (stateLock)
            {
                states.TryGetValue(assetId, out AnchorClaimState state);
                return state;
            }
        }

        /// <summary>
        /// Get all states
        /// </summary>
        public IEnumerable<AnchorClaimState> GetAllStates()
        {
            lock (stateLock)
            {
                return new List<AnchorClaimState>(states.Values);
            }
        }

        /// <summary>
        /// Get the last received event ID for SSE reconnection
        /// </summary>
        public string GetLastEventId()
        {
            return lastEventId;
        }

        /// <summary>
        /// Update last event ID and persist to PlayerPrefs
        /// </summary>
        public void UpdateLastEventId(string eventId)
        {
            if (string.IsNullOrEmpty(eventId))
                return;

            lastEventId = eventId;
            PlayerPrefs.SetString(LAST_EVENT_ID_PREF_KEY, eventId);
            // Note: We don't call PlayerPrefs.Save() every time for performance
            // It will be saved when the app pauses/quits
        }

        /// <summary>
        /// Check if an event has already been processed (deduplication)
        /// </summary>
        public bool HasProcessedEvent(string eventKey)
        {
            if (string.IsNullOrEmpty(eventKey))
                return false;

            lock (stateLock)
            {
                return processedEventKeys.Contains(eventKey);
            }
        }

        /// <summary>
        /// Mark an event as processed
        /// </summary>
        public void MarkEventProcessed(string eventKey)
        {
            if (string.IsNullOrEmpty(eventKey))
                return;

            lock (stateLock)
            {
                if (processedEventKeys.Add(eventKey))
                {
                    eventKeyQueue.Enqueue(eventKey);

                    // Evict old keys if over limit
                    while (eventKeyQueue.Count > MAX_EVENT_KEYS)
                    {
                        string oldKey = eventKeyQueue.Dequeue();
                        processedEventKeys.Remove(oldKey);
                    }
                }
            }
        }

        /// <summary>
        /// Generate a unique event key for deduplication.
        /// Uses claimId+status+eventId as the key.
        /// </summary>
        public static string GenerateEventKey(string claimId, string status, string eventId)
        {
            return $"{claimId ?? ""}:{status ?? ""}:{eventId ?? ""}";
        }

        /// <summary>
        /// Mark an asset as pending (local submission, awaiting commit)
        /// </summary>
        public void SetPending(string assetId, string claimId = null)
        {
            lock (stateLock)
            {
                var state = GetOrCreateState(assetId);
                state.status = ClaimStatus.Pending;
                state.claimId = claimId;
                state.proposedTime = Time.realtimeSinceStartup;
                state.lastUpdateTime = Time.realtimeSinceStartup;
            }

            NotifyStateChanged(assetId);
        }

        /// <summary>
        /// Apply a gateway event to update state.
        /// Returns true if state was updated (not a duplicate).
        /// </summary>
        public bool ApplyEvent(GatewayEvent evt)
        {
            if (evt == null || string.IsNullOrEmpty(evt.assetId))
                return false;

            // Generate dedup key
            string eventKey = GenerateEventKey(evt.claimId, evt.type, evt.eventId);
            
            // Check for duplicate
            if (HasProcessedEvent(eventKey))
            {
                Debug.Log($"[AnchorClaimStateManager] Skipping duplicate event: {eventKey}");
                return false;
            }

            // Mark as processed
            MarkEventProcessed(eventKey);

            // Update last event ID
            if (!string.IsNullOrEmpty(evt.eventId))
            {
                UpdateLastEventId(evt.eventId);
            }

            // Apply state change
            lock (stateLock)
            {
                var state = GetOrCreateState(evt.assetId);
                
                state.lastEventId = evt.eventId;
                state.lastUpdateTime = Time.realtimeSinceStartup;

                if (!string.IsNullOrEmpty(evt.claimId))
                    state.claimId = evt.claimId;

                if (!string.IsNullOrEmpty(evt.publisherId))
                    state.publisherId = evt.publisherId;

                if (!string.IsNullOrEmpty(evt.conflictClassification))
                    state.conflictClassification = evt.conflictClassification;

                if (!string.IsNullOrEmpty(evt.reason))
                    state.rejectionReason = evt.reason;

                if (evt.endorsementCount > 0)
                    state.endorsementCount = evt.endorsementCount;

                // Map event type to status
                switch (evt.type)
                {
                    case "CLAIM_PROPOSED":
                        state.status = ClaimStatus.Proposed;
                        state.confirmedTime = Time.realtimeSinceStartup;
                        break;

                    case "CLAIM_ENDORSED":
                        // Check if now active
                        if (evt.state == "ACTIVE")
                        {
                            state.status = ClaimStatus.Active;
                        }
                        // Otherwise stays Proposed
                        break;

                    case "CLAIM_ACTIVATED":
                    case "ACTIVE_CHANGED":
                        if (!string.IsNullOrEmpty(evt.activeClaimId) || evt.state == "ACTIVE")
                        {
                            state.status = ClaimStatus.Active;
                        }
                        break;

                    case "CLAIM_REJECTED":
                        state.status = ClaimStatus.Rejected;
                        break;

                    case "CLAIM_REVOKED":
                        state.status = ClaimStatus.Revoked;
                        break;

                    case "CLAIM_REOPENED":
                        state.status = ClaimStatus.Proposed;
                        break;

                    default:
                        // Unknown event type - try to infer from state field
                        if (!string.IsNullOrEmpty(evt.state))
                        {
                            state.status = ParseStatus(evt.state);
                        }
                        break;
                }

                Debug.Log($"[AnchorClaimStateManager] Applied event: {evt.type} -> {state}");
            }

            NotifyStateChanged(evt.assetId);
            return true;
        }

        /// <summary>
        /// Apply snapshot data (from catch-up)
        /// </summary>
        public void ApplySnapshot(List<SnapshotAssetState> snapshot)
        {
            if (snapshot == null)
                return;

            foreach (var item in snapshot)
            {
                if (string.IsNullOrEmpty(item.asset_id))
                    continue;

                lock (stateLock)
                {
                    var state = GetOrCreateState(item.asset_id);
                    state.claimId = item.claim_id;
                    state.status = ParseStatus(item.state);
                    state.lastUpdateTime = Time.realtimeSinceStartup;

                    Debug.Log($"[AnchorClaimStateManager] Applied snapshot: {state}");
                }

                NotifyStateChanged(item.asset_id);
            }
        }

        /// <summary>
        /// Clear all states (for testing/reset)
        /// </summary>
        public void ClearAll()
        {
            lock (stateLock)
            {
                states.Clear();
                processedEventKeys.Clear();
                eventKeyQueue.Clear();
            }
            Debug.Log("[AnchorClaimStateManager] All states cleared");
        }

        private void NotifyStateChanged(string assetId)
        {
            var state = GetState(assetId);
            if (state != null)
            {
                OnStateChanged?.Invoke(assetId, state);
            }
        }

        private static ClaimStatus ParseStatus(string state)
        {
            if (string.IsNullOrEmpty(state))
                return ClaimStatus.Unknown;

            switch (state.ToUpperInvariant())
            {
                case "PROPOSED":
                    return ClaimStatus.Proposed;
                case "ACTIVE":
                    return ClaimStatus.Active;
                case "REJECTED":
                    return ClaimStatus.Rejected;
                case "REVOKED":
                    return ClaimStatus.Revoked;
                case "PENDING":
                    return ClaimStatus.Pending;
                default:
                    return ClaimStatus.Unknown;
            }
        }
    }

    /// <summary>
    /// Parsed gateway SSE event
    /// </summary>
    [Serializable]
    public class GatewayEvent
    {
        public string type;
        public string eventId;
        public string timestamp;
        public string assetId;
        public string claimId;
        public string publisherId;
        public string state;
        public string conflictClassification;
        public string reason;
        public string activeClaimId;
        public int endorsementCount;
        public bool isReplay;
    }

    /// <summary>
    /// Asset state from snapshot endpoint
    /// </summary>
    [Serializable]
    public class SnapshotAssetState
    {
        public string asset_id;
        public string claim_id;
        public string state;
        public string publisher_id;
        public int endorsement_count;
    }

    /// <summary>
    /// Response from snapshot endpoint
    /// </summary>
    [Serializable]
    public class SnapshotResponse
    {
        public bool success;
        public List<SnapshotAssetState> assets;
        public string last_event_id;
    }
}
