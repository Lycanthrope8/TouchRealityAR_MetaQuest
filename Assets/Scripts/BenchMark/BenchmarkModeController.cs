// ============================================================================
// FILE: BenchmarkModeController.cs
// Phase 4 - Device-first evaluation of blockchain governance layer
//
// PURPOSE:
//   Measures commit-confirmed latency, time-to-consistency, FPS impact,
//   and snapshot catch-up behavior from the Quest 3 headset perspective.
//
// ARCHITECTURE:
//   Device → Gateway → Fabric → SSE → Device (round-trip measurement)
//
// LOGGING:
//   All results are written to JSONL files and optionally uploaded to the
//   gateway backend at experiments/runs/<run_id>/.
//
// USAGE:
//   1. Attach to a GameObject alongside GatewaySync (or reference it).
//   2. Configure run parameters in the Inspector.
//   3. Call StartBenchmark() or enable autoStartOnConnect.
//   4. Results are logged locally and can be uploaded to the gateway.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway.Benchmark
{
          // =========================================================================
          // DATA TYPES (JSON-serializable for logging)
          // =========================================================================

          [Serializable]
          public class BenchmarkRequestLog
          {
                    public string run_id;
                    public string lane;
                    public string op;        // "propose", "endorse", "snapshot", "anchors"
                    public string req_id;
                    public string asset_id;
                    public long t_send_ms;
                    public long t_resp_ms;
                    public int duration_ms;
                    public int http_status;
                    public string error;
                    public int response_bytes;
          }
          
          

          [Serializable]
          public class BenchmarkSseEventLog
          {
                    public string run_id;
                    public string lane;
                    public string event_id;
                    public string event_type;
                    public string asset_id;
                    public string claim_id;
                    public long t_arrival_ms;
                    public string req_id;        // propagated from proposal for correlation
                    public long t_broadcast_ms;  // server broadcast timestamp if available
          }

          [Serializable]
          public class BenchmarkFrameLog
          {
                    public string run_id;
                    public string lane;
                    public long t_ms;
                    public float fps_est;
                    public float frame_time_ms;
                    public long memory_bytes;
          }

          /// <summary>
          /// Per-proposal tracking record for commit-confirmed latency measurement
          /// </summary>
          public class ProposalTracker
          {
                    public string assetId;
                    public string reqId;
                    public string claimId;
                    public long tSendMs;
                    public long tRespMs;           // HTTP response received
                    public long tSseProposedMs;    // SSE CLAIM_PROPOSED arrived
                    public long tSseEndorsed1Ms;   // SSE CLAIM_ENDORSED_ORG1
                    public long tSseEndorsed2Ms;   // SSE CLAIM_ENDORSED_ORG2
                    public long tSseActiveMs;      // SSE CLAIM_ACTIVATED (commit-confirmed)
                    public int httpStatus;
                    public bool completed;         // true when CLAIM_ACTIVATED received
                    public bool failed;
                    public string error;
          }

          // =========================================================================
          // MAIN CONTROLLER
          // =========================================================================

          public class BenchmarkModeController : MonoBehaviour
          {
                    [Header("Gateway Reference")]
                    [SerializeField] private GatewaySync gatewaySync;
                    [SerializeField] private GatewayConfig config;

                    [Header("Run Configuration")]
                    [Tooltip("Unique identifier for this benchmark run")]
                    [SerializeField] private string runId = "unity-bench-001";

                    [Tooltip("Lane label (e.g., A for proposer device, B for observer device)")]
                    [SerializeField] private string lane = "A";

                    [Tooltip("Number of warmup proposal operations to send before the main benchmark (not counted in results)")]
                    [SerializeField] private int warmupCount = 3;

                    [Tooltip("Number of proposals to send")]
                    [SerializeField] private int proposalCount = 50;

                    [Tooltip("Proposals per second (0.2 = one every 5s, 2 = two per second)")]
                    [SerializeField] private float proposalRate = 1.0f;

                    [Tooltip("Asset ID prefix for benchmark anchors")]
                    [SerializeField] private string assetIdPrefix = "";

                    [Header("Snapshot / Anchors Polling")]
                    [Tooltip("Interval in seconds to poll /events/snapshot and /admin/anchors")]
                    [SerializeField] private float snapshotPollIntervalSec = 15f;

                    [Header("FPS Logging")]
                    [Tooltip("Interval in seconds for FPS/frame-time sampling")]
                    [SerializeField] private float fpsLogIntervalSec = 1.0f;
                    [SerializeField] private float baselineSeconds = 30f;

                    [Header("Behavior")]
                    [Tooltip("Automatically start benchmark when gateway connects")]
                    [SerializeField] private bool autoStartOnConnect = false;

                    [Tooltip("Upload logs to gateway backend on completion")]
                    [SerializeField] private bool uploadLogsOnComplete = true;

                    [Tooltip("Maximum time to wait for all proposals to reach ACTIVE (seconds)")]
                    [SerializeField] private float completionTimeoutSec = 120f;

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;

                    // =====================================================================
                    // STATE
                    // =====================================================================
                    private bool isBenchmarkRunning = false;
                    private bool isBenchmarkComplete = false;
                    private int proposalsSent = 0;
                    private int proposalsCompleted = 0;
                    private int proposalsFailed = 0;
                    private float benchmarkStartTime;
                    private float benchmarkEndTime;

                    // Tracking
                    private Dictionary<string, ProposalTracker> trackersByAssetId = new Dictionary<string, ProposalTracker>();
                    private Dictionary<string, ProposalTracker> trackersByReqId = new Dictionary<string, ProposalTracker>();

                    // Log buffers
                    private List<string> requestLogLines = new List<string>();
                    private List<string> sseEventLogLines = new List<string>();
                    private List<string> frameLogLines = new List<string>();

                    // FPS tracking
                    private int frameCount = 0;
                    private float fpsTimer = 0f;
                    private float lastFpsLogTime = 0f;

                    // Snapshot polling
                    private float lastSnapshotPollTime = 0f;
                    private int snapshotRequests = 0;
                    private int anchorsRequests = 0;

                    // Coroutines
                    private Coroutine proposalCoroutine;
                    private Coroutine completionWatchCoroutine;

                    // Public accessors
                    public bool IsBenchmarkRunning => isBenchmarkRunning;
                    public bool IsBenchmarkComplete => isBenchmarkComplete;
                    public int ProposalsSent => proposalsSent;
                    public int ProposalsCompleted => proposalsCompleted;
                    public int ProposalsFailed => proposalsFailed;
                    public string RunId => runId;

                    // =====================================================================
                    // LIFECYCLE
                    // =====================================================================

                    private void Start()
                    {
                              if (gatewaySync == null)
                                        gatewaySync = FindFirstObjectByType<GatewaySync>();

                              if (config == null && gatewaySync != null)
                                        config = gatewaySync.Config;

                              // Generate default prefix with timestamp for uniqueness
                              if (string.IsNullOrEmpty(assetIdPrefix))
                                        assetIdPrefix = $"bench-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-";

                              if (autoStartOnConnect && gatewaySync != null)
                              {
                                        gatewaySync.OnGatewayConnected.AddListener(OnGatewayConnected);
                              }
                    }

                    private void Update()
                    {
                              if (!isBenchmarkRunning) return;

                              // FPS logging
                              frameCount++;
                              fpsTimer += Time.unscaledDeltaTime;

                              float now = Time.realtimeSinceStartup;
                              if (now - lastFpsLogTime >= fpsLogIntervalSec)
                              {
                                        LogFrameMetrics();
                                        lastFpsLogTime = now;
                              }

                              // Snapshot/anchors polling
                              if (now - lastSnapshotPollTime >= snapshotPollIntervalSec)
                              {
                                        PollSnapshotAndAnchors();
                                        lastSnapshotPollTime = now;
                              }
                    }

                    private void OnDestroy()
                    {
                              if (gatewaySync != null)
                              {
                                        gatewaySync.OnGatewayConnected.RemoveListener(OnGatewayConnected);
                              }

                              UnsubscribeSseEvents();
                    }

                    // =====================================================================
                    // PUBLIC API
                    // =====================================================================

                    /// <summary>
                    /// Start the benchmark run. Can be called from UI button or script.
                    /// </summary>
                    public void StartBenchmark()
                    {
                              if (isBenchmarkRunning)
                              {
                                        Debug.LogWarning("[Benchmark] Already running!");
                                        return;
                              }

                              if (gatewaySync == null || !gatewaySync.IsConnected)
                              {
                                        Debug.LogError("[Benchmark] Gateway not connected! Cannot start.");
                                        return;
                              }

                              Debug.Log($"[Benchmark] ============================================");
                              Debug.Log($"[Benchmark] STARTING BENCHMARK RUN: {runId}");
                              Debug.Log($"[Benchmark]   Lane: {lane}");
                              Debug.Log($"[Benchmark]   Proposals: {proposalCount}");
                              Debug.Log($"[Benchmark]   Rate: {proposalRate} ops/sec");
                              Debug.Log($"[Benchmark]   Prefix: {assetIdPrefix}");
                              Debug.Log($"[Benchmark]   Gateway: {config?.gatewayBaseUrl}");
                              Debug.Log($"[Benchmark] ============================================");

                              isBenchmarkRunning = true;
                              isBenchmarkComplete = false;
                              proposalsSent = 0;
                              proposalsCompleted = 0;
                              proposalsFailed = 0;
                              benchmarkStartTime = Time.realtimeSinceStartup;

                              trackersByAssetId.Clear();
                              trackersByReqId.Clear();
                              requestLogLines.Clear();
                              sseEventLogLines.Clear();
                              frameLogLines.Clear();

                              lastFpsLogTime = Time.realtimeSinceStartup;
                              lastSnapshotPollTime = Time.realtimeSinceStartup;

                              // Subscribe to SSE events for timing
                              SubscribeSseEvents();

                              // Start sending proposals
                              proposalCoroutine = StartCoroutine(WarmupThenEmit());

                              // Start completion watcher
                              completionWatchCoroutine = StartCoroutine(CompletionWatcherCoroutine());
                    }
                    private IEnumerator WarmupThenEmit()
                    {
                              // Phase 0: Baseline FPS (no network activity)
                              if (baselineSeconds > 0)
                              {
                                        Debug.Log($"[Benchmark] Baseline phase: logging FPS for {baselineSeconds}s...");
                                        float baselineEnd = Time.realtimeSinceStartup + baselineSeconds;

                                        while (Time.realtimeSinceStartup < baselineEnd)
                                        {
                                                  yield return null; // Update() will log FPS normally
                                        }
                                        Debug.Log($"[Benchmark] Baseline phase complete.");
                              }

                              // Phase 1: Warmup
                              if (warmupCount > 0)
                              {
                                        Debug.Log($"[Benchmark] Warmup: sending {warmupCount} throwaway proposals...");
                                        for (int i = 0; i < warmupCount; i++)
                                        {
                                                  string warmupAssetId = $"warmup-{runId}-{i:D4}";
                                                  yield return StartCoroutine(SendWarmupProposal(warmupAssetId));
                                                  yield return new WaitForSeconds(0.5f);
                                        }
                                        Debug.Log("[Benchmark] Warmup done. Waiting 3s for steady state...");
                                        yield return new WaitForSeconds(3.0f);
                              }

                              // Phase 2: Real benchmark
                              yield return StartCoroutine(ProposalEmitterCoroutine());
                    }

                    private IEnumerator SendWarmupProposal(string assetId)
                    {
                              string url = config.ProposeEndpoint;

                              var poseSite = GatewayClient.CreatePoseSite(Pose.identity);
                              var quality = GatewayClient.CreateQualityMetrics(0.9f, 0.01f, 5);

                              var body = new BenchmarkProposeRequest
                              {
                                        asset_id = assetId,
                                        pose_site = poseSite,
                                        quality_metrics = quality,
                                        run_id = runId,
                                        req_id = $"warmup-{assetId}",
                                        lane = lane
                              };

                              string json = JsonUtility.ToJson(body);

                              using (var request = new UnityWebRequest(url, "POST"))
                              {
                                        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
                                        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                                        request.downloadHandler = new DownloadHandlerBuffer();
                                        request.SetRequestHeader("Content-Type", "application/json");
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.timeout = config.requestTimeoutSeconds;

                                        yield return request.SendWebRequest();

                                        long duration = (long)(request.downloadedBytes > 0 ? 0 : 0); // just wait for completion
                                        Debug.Log($"[Benchmark] Warmup proposal {assetId}: HTTP {request.responseCode}");
                              }
                    }

                    /// <summary>
                    /// Stop the benchmark (cancels pending proposals, logs results so far)
                    /// </summary>
                    public void StopBenchmark()
                    {
                              if (!isBenchmarkRunning) return;

                              Debug.Log("[Benchmark] Stopping benchmark...");

                              if (proposalCoroutine != null)
                              {
                                        StopCoroutine(proposalCoroutine);
                                        proposalCoroutine = null;
                              }
                              if (completionWatchCoroutine != null)
                              {
                                        StopCoroutine(completionWatchCoroutine);
                                        completionWatchCoroutine = null;
                              }

                              FinalizeBenchmark("stopped_by_user");
                    }

                    /// <summary>
                    /// Get a summary string for display on an InfoPanel or debug overlay
                    /// </summary>
                    public string GetSummary()
                    {
                              if (!isBenchmarkRunning && !isBenchmarkComplete)
                                        return "Benchmark: idle";

                              float elapsed = (isBenchmarkComplete ? benchmarkEndTime : Time.realtimeSinceStartup) - benchmarkStartTime;

                              return $"Run: {runId} | Lane: {lane}\n" +
                                     $"Sent: {proposalsSent}/{proposalCount} | Done: {proposalsCompleted} | Fail: {proposalsFailed}\n" +
                                     $"Elapsed: {elapsed:F1}s | SSE events: {sseEventLogLines.Count}\n" +
                                     $"Snapshots: {snapshotRequests} | Anchors: {anchorsRequests}\n" +
                                     $"{(isBenchmarkComplete ? "COMPLETE" : "RUNNING...")}";
                    }

                    // =====================================================================
                    // PROPOSAL EMITTER
                    // =====================================================================

                    private IEnumerator ProposalEmitterCoroutine()
                    {
                              float intervalSec = 1.0f / Mathf.Max(proposalRate, 0.01f);

                              for (int i = 0; i < proposalCount; i++)
                              {
                                        string assetId = $"{assetIdPrefix}{i:D4}";
                                        string reqId = $"req-{runId}-{i}";

                                        // FIRE-AND-FORGET: launch HTTP request without blocking the loop
                                        StartCoroutine(SendProposal(assetId, reqId, i));

                                        // Fixed-rate wait: independent of HTTP round-trip time
                                        if (i < proposalCount - 1)
                                        {
                                                  yield return new WaitForSeconds(intervalSec);
                                        }
                              }

                              if (enableDebugLogs)
                                        Debug.Log($"[Benchmark] All {proposalCount} proposals dispatched.");
                    }

                    private IEnumerator SendProposal(string assetId, string reqId, int index)
                    {
                              // Create tracker
                              var tracker = new ProposalTracker
                              {
                                        assetId = assetId,
                                        reqId = reqId,
                                        tSendMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                              };
                              trackersByAssetId[assetId] = tracker;
                              trackersByReqId[reqId] = tracker;

                              // Build request body with benchmark metadata
                              var poseSite = GatewayClient.CreatePoseSite(new Pose(
                                  new Vector3(index * 0.5f, 0f, 0f),  // spread anchors for visualization
                                  Quaternion.identity
                              ));
                              var quality = GatewayClient.CreateQualityMetrics(0.95f, 0.01f, 10);

                              // Use raw HTTP to include run_id, req_id, lane in the body
                              string url = config.ProposeEndpoint;
                              var body = new BenchmarkProposeRequest
                              {
                                        asset_id = assetId,
                                        pose_site = poseSite,
                                        quality_metrics = quality,
                                        run_id = runId,
                                        req_id = reqId,
                                        lane = lane
                              };

                              string json = JsonUtility.ToJson(body);

                              using (var request = new UnityWebRequest(url, "POST"))
                              {
                                        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                                        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                                        request.downloadHandler = new DownloadHandlerBuffer();
                                        request.SetRequestHeader("Content-Type", "application/json");
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.SetRequestHeader("x-run-id", runId);
                                        request.SetRequestHeader("x-req-id", reqId);
                                        request.SetRequestHeader("x-lane", lane);
                                        request.timeout = config.requestTimeoutSeconds;

                                        yield return request.SendWebRequest();

                                        long tRespMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        tracker.tRespMs = tRespMs;
                                        tracker.httpStatus = (int)request.responseCode;

                                        string responseText = request.downloadHandler?.text ?? "";
                                        int responseBytes = responseText != null ? Encoding.UTF8.GetByteCount(responseText) : 0;

                                        if (request.result == UnityWebRequest.Result.Success &&
                                            request.responseCode >= 200 && request.responseCode < 300)
                                        {
                                                  try
                                                  {
                                                            var response = JsonUtility.FromJson<ProposeResponse>(responseText);
                                                            tracker.claimId = response.claim_id;
                                                            proposalsSent++;

                                                            if (enableDebugLogs && proposalsSent % 10 == 0)
                                                                      Debug.Log($"[Benchmark] Sent {proposalsSent}/{proposalCount}: {assetId} " +
                                                                               $"(HTTP {(int)tracker.tRespMs - (int)tracker.tSendMs}ms)");
                                                  }
                                                  catch (Exception e)
                                                  {
                                                            tracker.failed = true;
                                                            tracker.error = $"Parse: {e.Message}";
                                                            proposalsFailed++;
                                                  }
                                        }
                                        else
                                        {
                                                  tracker.failed = true;
                                                  tracker.error = $"HTTP {request.responseCode}: {request.error}";
                                                  proposalsFailed++;
                                                  Debug.LogWarning($"[Benchmark] Proposal FAILED for {assetId}: {tracker.error}");
                                        }

                                        // Log request
                                        var logEntry = new BenchmarkRequestLog
                                        {
                                                  run_id = runId,
                                                  lane = lane,
                                                  op = "propose",
                                                  req_id = reqId,
                                                  asset_id = assetId,
                                                  t_send_ms = tracker.tSendMs,
                                                  t_resp_ms = tRespMs,
                                                  duration_ms = (int)(tRespMs - tracker.tSendMs),
                                                  http_status = tracker.httpStatus,
                                                  error = tracker.error,
                                                  response_bytes = responseBytes
                                        };
                                        requestLogLines.Add(JsonUtility.ToJson(logEntry));
                              }
                    }

                    // =====================================================================
                    // SSE EVENT TRACKING
                    // =====================================================================

                    private void SubscribeSseEvents()
                    {
                              if (gatewaySync != null)
                              {
                                        // We need direct SSE access; use the GatewaySseClient's OnEventReceived
                                        var sseClient = gatewaySync.GetComponent<GatewaySseClient>();
                                        if (sseClient == null)
                                                  sseClient = gatewaySync.GetComponentInChildren<GatewaySseClient>();

                                        if (sseClient != null)
                                        {
                                                  sseClient.OnEventReceived += OnSseEventReceived;
                                                  if (enableDebugLogs)
                                                            Debug.Log("[Benchmark] Subscribed to SSE events");
                                        }
                                        else
                                        {
                                                  Debug.LogWarning("[Benchmark] Could not find GatewaySseClient for SSE subscription!");
                                        }
                              }
                    }

                    private void UnsubscribeSseEvents()
                    {
                              if (gatewaySync != null)
                              {
                                        var sseClient = gatewaySync.GetComponent<GatewaySseClient>();
                                        if (sseClient == null)
                                                  sseClient = gatewaySync.GetComponentInChildren<GatewaySseClient>();

                                        if (sseClient != null)
                                        {
                                                  sseClient.OnEventReceived -= OnSseEventReceived;
                                        }
                              }
                    }

                    private void OnSseEventReceived(GatewayEvent evt)
                    {
                              if (!isBenchmarkRunning) return;

                              string eventType = evt.type;
                              string assetId = evt.GetAssetId();

                              // Skip non-claim events
                              if (string.IsNullOrEmpty(assetId) ||
                                  eventType == "HEARTBEAT" ||
                                  eventType == "CONNECTED")
                                        return;

                              // Only track our benchmark assets
                              if (!string.IsNullOrEmpty(assetIdPrefix) && !assetId.StartsWith(assetIdPrefix))
                                        return;

                              long arrivalMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                              // Log SSE event
                              var sseLog = new BenchmarkSseEventLog
                              {
                                        run_id = runId,
                                        lane = lane,
                                        event_id = evt.event_id,
                                        event_type = eventType,
                                        asset_id = assetId,
                                        claim_id = evt.GetClaimId(),
                                        t_arrival_ms = arrivalMs,
                                        req_id = "",         // Will be populated from tracker if available
                                        t_broadcast_ms = 0   // Server broadcast time if available in event
                              };

                              // Try to extract broadcast_time_ms from the raw event (it's in the JSON)
                              // The GatewayEvent doesn't have it as a field, but we log what we can

                              // Update tracker timestamps
                              if (trackersByAssetId.TryGetValue(assetId, out var tracker))
                              {
                                        sseLog.req_id = tracker.reqId;

                                        switch (eventType)
                                        {
                                                  case "CLAIM_PROPOSED":
                                                            tracker.tSseProposedMs = arrivalMs;
                                                            break;

                                                  case "CLAIM_ENDORSED_ORG1":
                                                            tracker.tSseEndorsed1Ms = arrivalMs;
                                                            break;

                                                  case "CLAIM_ENDORSED_ORG2":
                                                            tracker.tSseEndorsed2Ms = arrivalMs;
                                                            break;

                                                  case "CLAIM_ACTIVATED":
                                                            tracker.tSseActiveMs = arrivalMs;
                                                            tracker.completed = true;
                                                            proposalsCompleted++;

                                                            if (enableDebugLogs && proposalsCompleted % 10 == 0)
                                                            {
                                                                      long commitLatency = tracker.tSseActiveMs - tracker.tSendMs;
                                                                      Debug.Log($"[Benchmark] ACTIVATED {proposalsCompleted}/{proposalsSent}: " +
                                                                               $"{assetId} commit-confirm={commitLatency}ms");
                                                            }
                                                            break;

                                                  case "CLAIM_REJECTED":
                                                            tracker.failed = true;
                                                            tracker.error = $"REJECTED: {evt.reason}";
                                                            proposalsFailed++;
                                                            break;
                                        }
                              }

                              sseEventLogLines.Add(JsonUtility.ToJson(sseLog));
                    }

                    // =====================================================================
                    // FPS / FRAME METRICS
                    // =====================================================================

                    private void LogFrameMetrics()
                    {
                              float fps = frameCount / Mathf.Max(fpsTimer, 0.001f);
                              float frameTimeMs = (fpsTimer / Mathf.Max(frameCount, 1)) * 1000f;

                              var log = new BenchmarkFrameLog
                              {
                                        run_id = runId,
                                        lane = lane,
                                        t_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                        fps_est = Mathf.Round(fps * 10f) / 10f,
                                        frame_time_ms = Mathf.Round(frameTimeMs * 100f) / 100f,
                                        memory_bytes = GC.GetTotalMemory(false)
                              };

                              frameLogLines.Add(JsonUtility.ToJson(log));

                              // Reset counters
                              frameCount = 0;
                              fpsTimer = 0f;
                    }
                    

                    // =====================================================================
                    // SNAPSHOT / ANCHORS POLLING
                    // =====================================================================

                    private void PollSnapshotAndAnchors()
                    {
                              StartCoroutine(PollSnapshotCoroutine());
                              StartCoroutine(PollAnchorsCoroutine());
                    }

                    private IEnumerator PollSnapshotCoroutine()
                    {
                              string url = config.SnapshotUrl;
                              long tSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                              string reqId = $"req-{runId}-snapshot-{snapshotRequests}";

                              using (var request = UnityWebRequest.Get(url))
                              {
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.SetRequestHeader("x-run-id", runId);
                                        request.SetRequestHeader("x-req-id", reqId);
                                        request.SetRequestHeader("x-lane", lane);
                                        request.timeout = config.requestTimeoutSeconds;

                                        yield return request.SendWebRequest();

                                        long tResp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        string responseText = request.downloadHandler?.text ?? "";
                                        int responseBytes = Encoding.UTF8.GetByteCount(responseText);

                                        var logEntry = new BenchmarkRequestLog
                                        {
                                                  run_id = runId,
                                                  lane = lane,
                                                  op = "snapshot",
                                                  req_id = reqId,
                                                  asset_id = null,
                                                  t_send_ms = tSend,
                                                  t_resp_ms = tResp,
                                                  duration_ms = (int)(tResp - tSend),
                                                  http_status = (int)request.responseCode,
                                                  error = request.result != UnityWebRequest.Result.Success ? request.error : null,
                                                  response_bytes = responseBytes
                                        };
                                        requestLogLines.Add(JsonUtility.ToJson(logEntry));
                                        snapshotRequests++;

                                        if (enableDebugLogs)
                                                  Debug.Log($"[Benchmark] Snapshot poll: {logEntry.duration_ms}ms, {responseBytes}B");
                              }
                    }

                    private IEnumerator PollAnchorsCoroutine()
                    {
                              string url = $"{config.gatewayBaseUrl}/admin/anchors";
                              long tSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                              string reqId = $"req-{runId}-anchors-{anchorsRequests}";

                              using (var request = UnityWebRequest.Get(url))
                              {
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.SetRequestHeader("x-run-id", runId);
                                        request.SetRequestHeader("x-req-id", reqId);
                                        request.SetRequestHeader("x-lane", lane);
                                        request.timeout = config.requestTimeoutSeconds;

                                        yield return request.SendWebRequest();

                                        long tResp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                        string responseText = request.downloadHandler?.text ?? "";
                                        int responseBytes = Encoding.UTF8.GetByteCount(responseText);

                                        var logEntry = new BenchmarkRequestLog
                                        {
                                                  run_id = runId,
                                                  lane = lane,
                                                  op = "anchors",
                                                  req_id = reqId,
                                                  asset_id = null,
                                                  t_send_ms = tSend,
                                                  t_resp_ms = tResp,
                                                  duration_ms = (int)(tResp - tSend),
                                                  http_status = (int)request.responseCode,
                                                  error = request.result != UnityWebRequest.Result.Success ? request.error : null,
                                                  response_bytes = responseBytes
                                        };
                                        requestLogLines.Add(JsonUtility.ToJson(logEntry));
                                        anchorsRequests++;
                              }
                    }

                    // =====================================================================
                    // COMPLETION WATCHER
                    // =====================================================================

                    private IEnumerator CompletionWatcherCoroutine()
                    {
                              float deadline = Time.realtimeSinceStartup + completionTimeoutSec;

                              // Wait until all proposals are sent
                              while (proposalsSent < proposalCount && Time.realtimeSinceStartup < deadline)
                              {
                                        yield return new WaitForSeconds(0.5f);
                              }

                              // Wait for all sent proposals to reach ACTIVE or fail
                              while ((proposalsCompleted + proposalsFailed) < proposalsSent &&
                                     Time.realtimeSinceStartup < deadline)
                              {
                                        yield return new WaitForSeconds(0.5f);
                              }

                              if ((proposalsCompleted + proposalsFailed) >= proposalsSent)
                              {
                                        FinalizeBenchmark("completed");
                              }
                              else
                              {
                                        int remaining = proposalsSent - proposalsCompleted - proposalsFailed;
                                        Debug.LogWarning($"[Benchmark] Timeout! {remaining} proposals did not reach terminal state.");
                                        FinalizeBenchmark("timeout");
                              }
                    }

                    // =====================================================================
                    // FINALIZATION & REPORTING
                    // =====================================================================

                    private void FinalizeBenchmark(string reason)
                    {
                              if (!isBenchmarkRunning) return;

                              isBenchmarkRunning = false;
                              isBenchmarkComplete = true;
                              benchmarkEndTime = Time.realtimeSinceStartup;

                              UnsubscribeSseEvents();

                              // Log final FPS sample
                              LogFrameMetrics();

                              // Compute stats
                              var stats = ComputeStats();

                              float elapsedSec = benchmarkEndTime - benchmarkStartTime;

                              Debug.Log($"[Benchmark] ============================================");
                              Debug.Log($"[Benchmark] BENCHMARK COMPLETE: {runId}");
                              Debug.Log($"[Benchmark]   Reason: {reason}");
                              Debug.Log($"[Benchmark]   Duration: {elapsedSec:F1}s");
                              Debug.Log($"[Benchmark]   Sent: {proposalsSent}, Completed: {proposalsCompleted}, Failed: {proposalsFailed}");
                              Debug.Log($"[Benchmark]   Commit-Confirm Latency:");
                              Debug.Log($"[Benchmark]     Min: {stats.commitConfirmMinMs}ms");
                              Debug.Log($"[Benchmark]     P50: {stats.commitConfirmP50Ms}ms");
                              Debug.Log($"[Benchmark]     P95: {stats.commitConfirmP95Ms}ms");
                              Debug.Log($"[Benchmark]     P99: {stats.commitConfirmP99Ms}ms");
                              Debug.Log($"[Benchmark]     Max: {stats.commitConfirmMaxMs}ms");
                              Debug.Log($"[Benchmark]   HTTP Propose Latency:");
                              Debug.Log($"[Benchmark]     Mean: {stats.httpProposeMeanMs}ms");
                              Debug.Log($"[Benchmark]   Request logs: {requestLogLines.Count}");
                              Debug.Log($"[Benchmark]   SSE event logs: {sseEventLogLines.Count}");
                              Debug.Log($"[Benchmark]   Frame logs: {frameLogLines.Count}");
                              Debug.Log($"[Benchmark] ============================================");

                              // Save locally and optionally upload
                              SaveLogsLocal();

                              if (uploadLogsOnComplete)
                              {
                                        StartCoroutine(UploadLogsToGateway(stats, reason));
                              }
                    }

                    private BenchmarkStats ComputeStats()
                    {
                              var stats = new BenchmarkStats();

                              List<long> commitLatencies = new List<long>();
                              List<long> httpLatencies = new List<long>();
                              List<long> timeToConsistencies = new List<long>();

                              foreach (var tracker in trackersByAssetId.Values)
                              {
                                        if (tracker.completed && tracker.tSseActiveMs > 0 && tracker.tSendMs > 0)
                                        {
                                                  long commitConfirm = tracker.tSseActiveMs - tracker.tSendMs;
                                                  commitLatencies.Add(commitConfirm);
                                        }

                                        if (tracker.tRespMs > 0 && tracker.tSendMs > 0)
                                        {
                                                  httpLatencies.Add(tracker.tRespMs - tracker.tSendMs);
                                        }

                                        // Time-to-consistency: from HTTP response to SSE ACTIVE
                                        if (tracker.completed && tracker.tSseActiveMs > 0 && tracker.tRespMs > 0)
                                        {
                                                  long ttc = tracker.tSseActiveMs - tracker.tRespMs;
                                                  timeToConsistencies.Add(ttc);
                                        }
                              }

                              commitLatencies.Sort();
                              httpLatencies.Sort();
                              timeToConsistencies.Sort();

                              if (commitLatencies.Count > 0)
                              {
                                        stats.commitConfirmMinMs = commitLatencies[0];
                                        stats.commitConfirmP50Ms = Percentile(commitLatencies, 50);
                                        stats.commitConfirmP95Ms = Percentile(commitLatencies, 95);
                                        stats.commitConfirmP99Ms = Percentile(commitLatencies, 99);
                                        stats.commitConfirmMaxMs = commitLatencies[commitLatencies.Count - 1];
                                        stats.commitConfirmMeanMs = Mean(commitLatencies);
                              }

                              if (httpLatencies.Count > 0)
                              {
                                        stats.httpProposeMeanMs = Mean(httpLatencies);
                                        stats.httpProposeP50Ms = Percentile(httpLatencies, 50);
                                        stats.httpProposeP95Ms = Percentile(httpLatencies, 95);
                              }

                              if (timeToConsistencies.Count > 0)
                              {
                                        stats.timeToConsistencyMeanMs = Mean(timeToConsistencies);
                                        stats.timeToConsistencyP50Ms = Percentile(timeToConsistencies, 50);
                                        stats.timeToConsistencyP95Ms = Percentile(timeToConsistencies, 95);
                              }

                              return stats;
                    }

                    private long Percentile(List<long> sorted, int p)
                    {
                              if (sorted.Count == 0) return 0;
                              int idx = Mathf.Max(0, Mathf.CeilToInt(sorted.Count * p / 100f) - 1);
                              return sorted[idx];
                    }

                    private long Mean(List<long> values)
                    {
                              if (values.Count == 0) return 0;
                              long sum = 0;
                              foreach (var v in values) sum += v;
                              return sum / values.Count;
                    }

                    // =====================================================================
                    // LOCAL SAVE
                    // =====================================================================

                    private void SaveLogsLocal()
                    {
                              try
                              {
                                        // Save to Application.persistentDataPath on Quest
                                        string baseDir = System.IO.Path.Combine(Application.persistentDataPath, "benchmark_runs", runId);
                                        System.IO.Directory.CreateDirectory(baseDir);

                                        WriteLines(System.IO.Path.Combine(baseDir, "request_logs.jsonl"), requestLogLines);
                                        WriteLines(System.IO.Path.Combine(baseDir, "sse_event_logs.jsonl"), sseEventLogLines);
                                        WriteLines(System.IO.Path.Combine(baseDir, "frame_logs.jsonl"), frameLogLines);

                                        // Write per-proposal detailed tracker data
                                        var trackerLines = new List<string>();
                                        foreach (var t in trackersByAssetId.Values)
                                        {
                                                  var detail = new BenchmarkProposalDetail
                                                  {
                                                            run_id = runId,
                                                            lane = lane,
                                                            asset_id = t.assetId,
                                                            req_id = t.reqId,
                                                            claim_id = t.claimId ?? "",
                                                            t_send_ms = t.tSendMs,
                                                            t_http_resp_ms = t.tRespMs,
                                                            t_sse_proposed_ms = t.tSseProposedMs,
                                                            t_sse_endorsed1_ms = t.tSseEndorsed1Ms,
                                                            t_sse_endorsed2_ms = t.tSseEndorsed2Ms,
                                                            t_sse_active_ms = t.tSseActiveMs,
                                                            http_status = t.httpStatus,
                                                            completed = t.completed,
                                                            failed = t.failed,
                                                            error = t.error ?? "",
                                                            commit_confirm_ms = t.completed ? (int)(t.tSseActiveMs - t.tSendMs) : -1
                                                  };
                                                  trackerLines.Add(JsonUtility.ToJson(detail));
                                        }
                                        WriteLines(System.IO.Path.Combine(baseDir, "proposal_details.jsonl"), trackerLines);

                                        Debug.Log($"[Benchmark] Logs saved to: {baseDir}");
                              }
                              catch (Exception e)
                              {
                                        Debug.LogError($"[Benchmark] Failed to save logs locally: {e.Message}");
                              }
                    }

                    private void WriteLines(string path, List<string> lines)
                    {
                              System.IO.File.WriteAllText(path, string.Join("\n", lines) + "\n");
                    }

                    // =====================================================================
                    // UPLOAD TO GATEWAY
                    // =====================================================================

                    private IEnumerator UploadLogsToGateway(BenchmarkStats stats, string reason)
                    {
                              // Upload the summary + raw logs to gateway via POST /admin/benchmark-upload
                              // The gateway experimentLogger already creates files under experiments/runs/<run_id>/
                              // We send a consolidated summary that the gateway can store alongside its own logs.

                              string url = $"{config.gatewayBaseUrl}/admin/benchmark-upload";

                              var summary = new BenchmarkSummaryUpload
                              {
                                        run_id = runId,
                                        lane = lane,
                                        device = "quest3",
                                        org_id = config.organizationId,
                                        proposal_count = proposalCount,
                                        proposal_rate = proposalRate,
                                        proposals_sent = proposalsSent,
                                        proposals_completed = proposalsCompleted,
                                        proposals_failed = proposalsFailed,
                                        duration_sec = benchmarkEndTime - benchmarkStartTime,
                                        reason = reason,
                                        commit_confirm_min_ms = stats.commitConfirmMinMs,
                                        commit_confirm_p50_ms = stats.commitConfirmP50Ms,
                                        commit_confirm_p95_ms = stats.commitConfirmP95Ms,
                                        commit_confirm_p99_ms = stats.commitConfirmP99Ms,
                                        commit_confirm_max_ms = stats.commitConfirmMaxMs,
                                        commit_confirm_mean_ms = stats.commitConfirmMeanMs,
                                        http_propose_mean_ms = stats.httpProposeMeanMs,
                                        time_to_consistency_mean_ms = stats.timeToConsistencyMeanMs,
                                        time_to_consistency_p50_ms = stats.timeToConsistencyP50Ms,
                                        time_to_consistency_p95_ms = stats.timeToConsistencyP95Ms,
                                        request_log_count = requestLogLines.Count,
                                        sse_event_log_count = sseEventLogLines.Count,
                                        frame_log_count = frameLogLines.Count
                              };

                              string json = JsonUtility.ToJson(summary, true);

                              Debug.Log($"[Benchmark] Uploading summary to {url}...");

                              using (var request = new UnityWebRequest(url, "POST"))
                              {
                                        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                                        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                                        request.downloadHandler = new DownloadHandlerBuffer();
                                        request.SetRequestHeader("Content-Type", "application/json");
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.timeout = 15;

                                        yield return request.SendWebRequest();

                                        if (request.result == UnityWebRequest.Result.Success)
                                        {
                                                  Debug.Log($"[Benchmark] Summary uploaded successfully.");
                                        }
                                        else
                                        {
                                                  Debug.LogWarning($"[Benchmark] Summary upload failed: {request.error} " +
                                                                 $"(this is non-critical, logs are saved locally)");
                                        }
                              }

                              // Upload raw JSONL logs in batches
                              yield return StartCoroutine(UploadJsonlFile("request_logs", requestLogLines));
                              yield return StartCoroutine(UploadJsonlFile("sse_event_logs", sseEventLogLines));
                              yield return StartCoroutine(UploadJsonlFile("frame_logs", frameLogLines));

                              Debug.Log("[Benchmark] All log uploads complete.");
                    }

                    private IEnumerator UploadJsonlFile(string logType, List<string> lines)
                    {
                              if (lines.Count == 0) yield break;

                              string url = $"{config.gatewayBaseUrl}/admin/benchmark-upload-log";
                              string content = string.Join("\n", lines);

                              var body = new BenchmarkLogUpload
                              {
                                        run_id = runId,
                                        log_type = logType,
                                        content = content,
                                        line_count = lines.Count
                              };

                              string json = JsonUtility.ToJson(body);

                              using (var request = new UnityWebRequest(url, "POST"))
                              {
                                        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                                        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                                        request.downloadHandler = new DownloadHandlerBuffer();
                                        request.SetRequestHeader("Content-Type", "application/json");
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.timeout = 30;

                                        yield return request.SendWebRequest();

                                        if (request.result != UnityWebRequest.Result.Success)
                                        {
                                                  Debug.LogWarning($"[Benchmark] Upload {logType} failed: {request.error}");
                                        }
                              }
                    }

                    // =====================================================================
                    // EVENT HANDLERS
                    // =====================================================================

                    private void OnGatewayConnected()
                    {
                              if (autoStartOnConnect && !isBenchmarkRunning && !isBenchmarkComplete)
                              {
                                        Debug.Log("[Benchmark] Gateway connected, auto-starting benchmark...");
                                        StartCoroutine(DelayedStart(2.0f));
                              }
                    }

                    private IEnumerator DelayedStart(float delaySec)
                    {
                              yield return new WaitForSeconds(delaySec);
                              StartBenchmark();
                    }
          }

          // =========================================================================
          // HELPER SERIALIZABLE TYPES
          // =========================================================================

          [Serializable]
          public class BenchmarkProposeRequest
          {
                    public string asset_id;
                    public PoseSite pose_site;
                    public QualityMetrics quality_metrics;
                    public string run_id;
                    public string req_id;
                    public string lane;
          }

          [Serializable]
          public class BenchmarkProposalDetail
          {
                    public string run_id;
                    public string lane;
                    public string asset_id;
                    public string req_id;
                    public string claim_id;
                    public long t_send_ms;
                    public long t_http_resp_ms;
                    public long t_sse_proposed_ms;
                    public long t_sse_endorsed1_ms;
                    public long t_sse_endorsed2_ms;
                    public long t_sse_active_ms;
                    public int http_status;
                    public bool completed;
                    public bool failed;
                    public string error;
                    public int commit_confirm_ms;
          }

          public class BenchmarkStats
          {
                    public long commitConfirmMinMs;
                    public long commitConfirmP50Ms;
                    public long commitConfirmP95Ms;
                    public long commitConfirmP99Ms;
                    public long commitConfirmMaxMs;
                    public long commitConfirmMeanMs;
                    public long httpProposeMeanMs;
                    public long httpProposeP50Ms;
                    public long httpProposeP95Ms;
                    public long timeToConsistencyMeanMs;
                    public long timeToConsistencyP50Ms;
                    public long timeToConsistencyP95Ms;
          }

          [Serializable]
          public class BenchmarkSummaryUpload
          {
                    public string run_id;
                    public string lane;
                    public string device;
                    public string org_id;
                    public int proposal_count;
                    public float proposal_rate;
                    public int proposals_sent;
                    public int proposals_completed;
                    public int proposals_failed;
                    public float duration_sec;
                    public string reason;
                    public long commit_confirm_min_ms;
                    public long commit_confirm_p50_ms;
                    public long commit_confirm_p95_ms;
                    public long commit_confirm_p99_ms;
                    public long commit_confirm_max_ms;
                    public long commit_confirm_mean_ms;
                    public long http_propose_mean_ms;
                    public long time_to_consistency_mean_ms;
                    public long time_to_consistency_p50_ms;
                    public long time_to_consistency_p95_ms;
                    public int request_log_count;
                    public int sse_event_log_count;
                    public int frame_log_count;
          }

          [Serializable]
          public class BenchmarkLogUpload
          {
                    public string run_id;
                    public string log_type;
                    public string content;
                    public int line_count;
          }
}