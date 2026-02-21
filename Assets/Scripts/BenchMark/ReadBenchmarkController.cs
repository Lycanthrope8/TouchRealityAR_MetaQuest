// ============================================================================
// FILE: ReadBenchmarkController.cs
// Experiment 2 (Device-Side) — Read-path benchmark from Quest 3
//
// PURPOSE:
//   Measures snapshot catch-up latency, anchors query latency, and single-
//   anchor history latency FROM the Quest 3 headset over a real network.
//   Complements the laptop-side dataset_scaling.js with device-side data.
//
// USAGE:
//   1. Pre-fill the ledger to the desired tier using dataset_scaling.js
//      from the laptop (e.g., 10, 100, 500, 1000 anchors).
//   2. Attach this controller to a GameObject alongside GatewaySync.
//   3. Set runId, iterations, and sampleAssetId in the Inspector.
//   4. Call StartReadBenchmark() (or enable autoStartOnConnect).
//   5. Results are logged as JSONL and uploaded to the gateway.
//
// LOGGING FORMAT (same BenchmarkRequestLog as BenchmarkModeController):
//   { run_id, lane, op, req_id, asset_id, t_send_ms, t_resp_ms,
//     duration_ms, http_status, error, response_bytes }
//
// The "op" field distinguishes read types:
//   "read_snapshot"  — GET /events/snapshot
//   "read_anchors"   — GET /admin/anchors
//   "read_history"   — GET /claims/:assetId/history
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
          public class ReadBenchmarkController : MonoBehaviour
          {
                    [Header("Gateway Reference")]
                    [SerializeField] private GatewaySync gatewaySync;
                    [SerializeField] private GatewayConfig config;

                    [Header("Run Configuration")]
                    [Tooltip("Unique identifier for this read benchmark run (e.g., exp2-q3-n100-trial1)")]
                    [SerializeField] private string runId = "exp2-q3-read-001";

                    [Tooltip("Lane label (matches BenchmarkModeController convention)")]
                    [SerializeField] private string lane = "A";

                    [Tooltip("Number of iterations per endpoint")]
                    [SerializeField] private int iterations = 20;

                    [Tooltip("Delay between requests in seconds (prevents flooding)")]
                    [SerializeField] private float delayBetweenRequests = 0.1f;

                    [Header("History Query")]
                    [Tooltip("Asset ID to query for history benchmark. Leave empty to auto-detect from snapshot.")]
                    [SerializeField] private string sampleAssetId = "";

                    [Header("Behavior")]
                    [Tooltip("Auto-start when gateway connects")]
                    [SerializeField] private bool autoStartOnConnect = false;

                    [Tooltip("Upload logs to gateway on completion")]
                    [SerializeField] private bool uploadLogsOnComplete = true;

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;

                    // =====================================================================
                    // STATE
                    // =====================================================================
                    private bool isRunning = false;
                    private bool isComplete = false;
                    private List<string> requestLogLines = new List<string>();

                    // Per-endpoint results for summary
                    private List<int> snapshotLatencies = new List<int>();
                    private List<int> snapshotSizes = new List<int>();
                    private List<int> anchorsLatencies = new List<int>();
                    private List<int> anchorsSizes = new List<int>();
                    private List<int> historyLatencies = new List<int>();
                    private List<int> historySizes = new List<int>();

                    private int anchorCount = 0;

                    // Public accessors
                    public bool IsRunning => isRunning;
                    public bool IsComplete => isComplete;
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

                              if (autoStartOnConnect && gatewaySync != null)
                              {
                                        gatewaySync.OnGatewayConnected.AddListener(OnGatewayConnected);
                              }
                    }

                    private void OnDestroy()
                    {
                              if (gatewaySync != null)
                                        gatewaySync.OnGatewayConnected.RemoveListener(OnGatewayConnected);
                    }

                    private void OnGatewayConnected()
                    {
                              if (autoStartOnConnect && !isRunning && !isComplete)
                              {
                                        Debug.Log("[ReadBenchmark] Gateway connected, auto-starting...");
                                        StartCoroutine(DelayedStart(2.0f));
                              }
                    }

                    private IEnumerator DelayedStart(float delay)
                    {
                              yield return new WaitForSeconds(delay);
                              StartReadBenchmark();
                    }

                    // =====================================================================
                    // PUBLIC API
                    // =====================================================================

                    /// <summary>
                    /// Start the read benchmark. Call from UI button or script.
                    /// Ledger must already be pre-filled to the desired anchor count.
                    /// </summary>
                    public void StartReadBenchmark()
                    {
                              if (isRunning)
                              {
                                        Debug.LogWarning("[ReadBenchmark] Already running!");
                                        return;
                              }

                              if (gatewaySync == null || !gatewaySync.IsConnected)
                              {
                                        Debug.LogError("[ReadBenchmark] Gateway not connected!");
                                        return;
                              }

                              Debug.Log($"[ReadBenchmark] ============================================");
                              Debug.Log($"[ReadBenchmark] STARTING READ BENCHMARK: {runId}");
                              Debug.Log($"[ReadBenchmark]   Iterations per endpoint: {iterations}");
                              Debug.Log($"[ReadBenchmark]   Sample asset: {(string.IsNullOrEmpty(sampleAssetId) ? "(auto-detect)" : sampleAssetId)}");
                              Debug.Log($"[ReadBenchmark]   Gateway: {config?.gatewayBaseUrl}");
                              Debug.Log($"[ReadBenchmark] ============================================");

                              isRunning = true;
                              isComplete = false;
                              requestLogLines.Clear();
                              snapshotLatencies.Clear();
                              snapshotSizes.Clear();
                              anchorsLatencies.Clear();
                              anchorsSizes.Clear();
                              historyLatencies.Clear();
                              historySizes.Clear();
                              anchorCount = 0;

                              StartCoroutine(RunReadBenchmark());
                    }

                    /// <summary>
                    /// Get summary for HUD display
                    /// </summary>
                    public string GetSummary()
                    {
                              if (!isRunning && !isComplete) return "Read Benchmark: idle";

                              int total = snapshotLatencies.Count + anchorsLatencies.Count + historyLatencies.Count;
                              int target = iterations * 3;

                              if (isComplete)
                              {
                                        return $"Read Benchmark: COMPLETE\n" +
                                               $"Anchors on ledger: {anchorCount}\n" +
                                               $"Snapshot p50: {Percentile(snapshotLatencies, 50)}ms ({Percentile(snapshotSizes, 50)}B)\n" +
                                               $"Anchors p50: {Percentile(anchorsLatencies, 50)}ms ({Percentile(anchorsSizes, 50)}B)\n" +
                                               $"History p50: {Percentile(historyLatencies, 50)}ms ({Percentile(historySizes, 50)}B)";
                              }

                              return $"Read Benchmark: {total}/{target} requests...";
                    }

                    // =====================================================================
                    // MAIN BENCHMARK COROUTINE
                    // =====================================================================

                    private IEnumerator RunReadBenchmark()
                    {
                              // Step 1: Auto-detect sample asset if not provided
                              if (string.IsNullOrEmpty(sampleAssetId))
                              {
                                        Debug.Log("[ReadBenchmark] Auto-detecting sample asset from snapshot...");
                                        yield return StartCoroutine(DetectSampleAsset());
                              }

                              // Step 2: Benchmark GET /events/snapshot
                              Debug.Log($"[ReadBenchmark] Benchmarking snapshot ({iterations} iterations)...");
                              for (int i = 0; i < iterations; i++)
                              {
                                        yield return StartCoroutine(BenchmarkGet(
                                            config.SnapshotUrl,
                                            "read_snapshot",
                                            $"req-{runId}-snapshot-{i}",
                                            null,
                                            snapshotLatencies,
                                            snapshotSizes
                                        ));
                                        if (delayBetweenRequests > 0)
                                                  yield return new WaitForSeconds(delayBetweenRequests);
                              }
                              Debug.Log($"[ReadBenchmark]   Snapshot done: p50={Percentile(snapshotLatencies, 50)}ms");

                              // Step 3: Benchmark GET /admin/anchors
                              Debug.Log($"[ReadBenchmark] Benchmarking anchors ({iterations} iterations)...");
                              for (int i = 0; i < iterations; i++)
                              {
                                        yield return StartCoroutine(BenchmarkGet(
                                            $"{config.gatewayBaseUrl}/admin/anchors",
                                            "read_anchors",
                                            $"req-{runId}-anchors-{i}",
                                            null,
                                            anchorsLatencies,
                                            anchorsSizes
                                        ));
                                        if (delayBetweenRequests > 0)
                                                  yield return new WaitForSeconds(delayBetweenRequests);
                              }
                              Debug.Log($"[ReadBenchmark]   Anchors done: p50={Percentile(anchorsLatencies, 50)}ms");

                              // Step 4: Benchmark GET /claims/:assetId/history
                              if (!string.IsNullOrEmpty(sampleAssetId))
                              {
                                        Debug.Log($"[ReadBenchmark] Benchmarking history for {sampleAssetId} ({iterations} iterations)...");
                                        for (int i = 0; i < iterations; i++)
                                        {
                                                  yield return StartCoroutine(BenchmarkGet(
                                                      $"{config.gatewayBaseUrl}/claims/{sampleAssetId}/history",
                                                      "read_history",
                                                      $"req-{runId}-history-{i}",
                                                      sampleAssetId,
                                                      historyLatencies,
                                                      historySizes
                                                  ));
                                                  if (delayBetweenRequests > 0)
                                                            yield return new WaitForSeconds(delayBetweenRequests);
                                        }
                                        Debug.Log($"[ReadBenchmark]   History done: p50={Percentile(historyLatencies, 50)}ms");
                              }
                              else
                              {
                                        Debug.LogWarning("[ReadBenchmark] No sample asset found — skipping history benchmark.");
                              }

                              // Step 5: Finalize
                              Finalize();
                    }

                    // =====================================================================
                    // HTTP GET BENCHMARK
                    // =====================================================================

                    private IEnumerator BenchmarkGet(string url, string op, string reqId, string assetId,
                        List<int> latencyList, List<int> sizeList)
                    {
                              long tSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
                                        int durationMs = (int)(tResp - tSend);

                                        string responseText = request.downloadHandler?.text ?? "";
                                        int responseBytes = Encoding.UTF8.GetByteCount(responseText);

                                        bool ok = request.result == UnityWebRequest.Result.Success &&
                                                  request.responseCode >= 200 && request.responseCode < 300;

                                        if (ok)
                                        {
                                                  latencyList.Add(durationMs);
                                                  sizeList.Add(responseBytes);

                                                  // Extract anchor count from first snapshot response
                                                  if (op == "read_snapshot" && anchorCount == 0)
                                                  {
                                                            try
                                                            {
                                                                      var snap = JsonUtility.FromJson<SnapshotResponse>(responseText);
                                                                      if (snap?.assets != null)
                                                                      {
                                                                                anchorCount = snap.assets.Length;
                                                                                Debug.Log($"[ReadBenchmark] Detected {anchorCount} active anchors on ledger");
                                                                      }
                                                            }
                                                            catch { /* non-critical */ }
                                                  }
                                        }

                                        // Log (same format as BenchmarkModeController)
                                        var logEntry = new BenchmarkRequestLog
                                        {
                                                  run_id = runId,
                                                  lane = lane,
                                                  op = op,
                                                  req_id = reqId,
                                                  asset_id = assetId,
                                                  t_send_ms = tSend,
                                                  t_resp_ms = tResp,
                                                  duration_ms = durationMs,
                                                  http_status = (int)request.responseCode,
                                                  error = ok ? null : (request.error ?? $"HTTP {request.responseCode}"),
                                                  response_bytes = responseBytes
                                        };
                                        requestLogLines.Add(JsonUtility.ToJson(logEntry));

                                        if (enableDebugLogs && latencyList.Count % 5 == 0)
                                        {
                                                  Debug.Log($"[ReadBenchmark] {op} #{latencyList.Count}: {durationMs}ms, {responseBytes}B");
                                        }
                              }
                    }

                    // =====================================================================
                    // AUTO-DETECT SAMPLE ASSET
                    // =====================================================================

                    private IEnumerator DetectSampleAsset()
                    {
                              string url = config.SnapshotUrl;

                              using (var request = UnityWebRequest.Get(url))
                              {
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.timeout = config.requestTimeoutSeconds;

                                        yield return request.SendWebRequest();

                                        if (request.result == UnityWebRequest.Result.Success)
                                        {
                                                  try
                                                  {
                                                            var snap = JsonUtility.FromJson<SnapshotResponse>(request.downloadHandler.text);
                                                            if (snap?.assets != null && snap.assets.Length > 0)
                                                            {
                                                                      // Pick middle asset for representative history query
                                                                      int midIdx = snap.assets.Length / 2;
                                                                      sampleAssetId = snap.assets[midIdx].asset_id;
                                                                      anchorCount = snap.assets.Length;
                                                                      Debug.Log($"[ReadBenchmark] Auto-detected: {anchorCount} anchors, " +
                                                                                $"sample={sampleAssetId}");
                                                            }
                                                            else
                                                            {
                                                                      Debug.LogWarning("[ReadBenchmark] Snapshot returned 0 assets — " +
                                                                                       "is the ledger pre-filled?");
                                                            }
                                                  }
                                                  catch (Exception e)
                                                  {
                                                            Debug.LogError($"[ReadBenchmark] Failed to parse snapshot: {e.Message}");
                                                  }
                                        }
                                        else
                                        {
                                                  Debug.LogError($"[ReadBenchmark] Snapshot fetch failed: {request.error}");
                                        }
                              }
                    }

                    // =====================================================================
                    // FINALIZATION
                    // =====================================================================

                    private void Finalize()
                    {
                              isRunning = false;
                              isComplete = true;

                              // Compute and display summary
                              Debug.Log($"[ReadBenchmark] ============================================");
                              Debug.Log($"[ReadBenchmark] READ BENCHMARK COMPLETE: {runId}");
                              Debug.Log($"[ReadBenchmark]   Anchors on ledger: {anchorCount}");
                              Debug.Log($"[ReadBenchmark]   Iterations: {iterations}");
                              Debug.Log($"[ReadBenchmark]");
                              Debug.Log($"[ReadBenchmark]   Snapshot ({snapshotLatencies.Count} ok):");
                              LogStats("    ", snapshotLatencies, snapshotSizes);
                              Debug.Log($"[ReadBenchmark]   Anchors ({anchorsLatencies.Count} ok):");
                              LogStats("    ", anchorsLatencies, anchorsSizes);
                              if (historyLatencies.Count > 0)
                              {
                                        Debug.Log($"[ReadBenchmark]   History ({historyLatencies.Count} ok):");
                                        LogStats("    ", historyLatencies, historySizes);
                              }
                              Debug.Log($"[ReadBenchmark]   Total log lines: {requestLogLines.Count}");
                              Debug.Log($"[ReadBenchmark] ============================================");

                              if (uploadLogsOnComplete)
                              {
                                        StartCoroutine(UploadLogs());
                              }
                    }

                    private void LogStats(string indent, List<int> latencies, List<int> sizes)
                    {
                              if (latencies.Count == 0) return;

                              latencies.Sort();
                              sizes.Sort();

                              float mean = 0;
                              foreach (int v in latencies) mean += v;
                              mean /= latencies.Count;

                              Debug.Log($"[ReadBenchmark] {indent}Latency: p50={Percentile(latencies, 50)}ms  " +
                                        $"p95={Percentile(latencies, 95)}ms  mean={mean:F1}ms");
                              Debug.Log($"[ReadBenchmark] {indent}Size:    p50={Percentile(sizes, 50)}B  " +
                                        $"mean={(float)SumList(sizes) / sizes.Count:F0}B");
                    }

                    // =====================================================================
                    // UPLOAD LOGS
                    // =====================================================================

                    private IEnumerator UploadLogs()
                    {
                              if (requestLogLines.Count == 0) yield break;

                              // Upload summary JSON
                              string summaryUrl = $"{config.gatewayBaseUrl}/admin/benchmark-upload";

                              var summary = new ReadBenchmarkSummary
                              {
                                        run_id = runId,
                                        lane = lane,
                                        device = SystemInfo.deviceModel,
                                        anchor_count = anchorCount,
                                        iterations = iterations,
                                        snapshot_p50_ms = Percentile(snapshotLatencies, 50),
                                        snapshot_p95_ms = Percentile(snapshotLatencies, 95),
                                        snapshot_size_bytes = snapshotSizes.Count > 0 ? Percentile(snapshotSizes, 50) : 0,
                                        anchors_p50_ms = Percentile(anchorsLatencies, 50),
                                        anchors_p95_ms = Percentile(anchorsLatencies, 95),
                                        anchors_size_bytes = anchorsSizes.Count > 0 ? Percentile(anchorsSizes, 50) : 0,
                                        history_p50_ms = Percentile(historyLatencies, 50),
                                        history_p95_ms = Percentile(historyLatencies, 95),
                                        history_size_bytes = historySizes.Count > 0 ? Percentile(historySizes, 50) : 0,
                                        request_log_count = requestLogLines.Count
                              };

                              string summaryJson = JsonUtility.ToJson(summary);

                              using (var request = new UnityWebRequest(summaryUrl, "POST"))
                              {
                                        byte[] bodyRaw = Encoding.UTF8.GetBytes(summaryJson);
                                        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                                        request.downloadHandler = new DownloadHandlerBuffer();
                                        request.SetRequestHeader("Content-Type", "application/json");
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.timeout = 15;

                                        yield return request.SendWebRequest();

                                        if (request.result == UnityWebRequest.Result.Success)
                                                  Debug.Log("[ReadBenchmark] Summary uploaded.");
                                        else
                                                  Debug.LogWarning($"[ReadBenchmark] Summary upload failed: {request.error}");
                              }

                              // Upload JSONL request logs
                              string logUrl = $"{config.gatewayBaseUrl}/admin/benchmark-upload-log";
                              string content = string.Join("\n", requestLogLines);

                              var logBody = new BenchmarkLogUpload
                              {
                                        run_id = runId,
                                        log_type = "read_benchmark_logs",
                                        content = content,
                                        line_count = requestLogLines.Count
                              };

                              string logJson = JsonUtility.ToJson(logBody);

                              using (var request = new UnityWebRequest(logUrl, "POST"))
                              {
                                        byte[] bodyRaw = Encoding.UTF8.GetBytes(logJson);
                                        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                                        request.downloadHandler = new DownloadHandlerBuffer();
                                        request.SetRequestHeader("Content-Type", "application/json");
                                        request.SetRequestHeader("x-api-key", config.apiKey);
                                        request.SetRequestHeader("x-org-id", config.organizationId);
                                        request.timeout = 30;

                                        yield return request.SendWebRequest();

                                        if (request.result == UnityWebRequest.Result.Success)
                                                  Debug.Log("[ReadBenchmark] Logs uploaded.");
                                        else
                                                  Debug.LogWarning($"[ReadBenchmark] Log upload failed: {request.error}");
                              }

                              Debug.Log("[ReadBenchmark] All uploads complete.");
                    }

                    // =====================================================================
                    // HELPERS
                    // =====================================================================

                    private static int Percentile(List<int> sorted, int p)
                    {
                              if (sorted.Count == 0) return 0;
                              var copy = new List<int>(sorted);
                              copy.Sort();
                              int idx = Mathf.Clamp(Mathf.CeilToInt(copy.Count * p / 100f) - 1, 0, copy.Count - 1);
                              return copy[idx];
                    }

                    private static long SumList(List<int> list)
                    {
                              long sum = 0;
                              foreach (int v in list) sum += v;
                              return sum;
                    }
          }

          // =========================================================================
          // SUMMARY TYPE (for upload)
          // =========================================================================

          [Serializable]
          public class ReadBenchmarkSummary
          {
                    public string run_id;
                    public string lane;
                    public string device;
                    public int anchor_count;
                    public int iterations;
                    public int snapshot_p50_ms;
                    public int snapshot_p95_ms;
                    public int snapshot_size_bytes;
                    public int anchors_p50_ms;
                    public int anchors_p95_ms;
                    public int anchors_size_bytes;
                    public int history_p50_ms;
                    public int history_p95_ms;
                    public int history_size_bytes;
                    public int request_log_count;
          }
}