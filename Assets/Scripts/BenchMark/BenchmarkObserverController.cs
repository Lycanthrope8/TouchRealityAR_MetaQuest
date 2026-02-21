// ============================================================================
// FILE: BenchmarkObserverController.cs
// Phase 4 - Observer device for cross-device time-to-consistency measurement
//
// PURPOSE:
//   Runs on a SECOND Quest 3 headset (or Unity Editor instance) that only
//   listens to SSE events and polls snapshot/anchors. Does NOT send proposals.
//   Measures how quickly state propagates to a different client.
//
// USAGE:
//   1. Attach to a GameObject alongside GatewaySync.
//   2. Set the same runId and assetIdPrefix as the proposer device.
//   3. Set lane = "B" (proposer uses "A").
//   4. Call StartObserving() after gateway connects.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway.Benchmark
{
          public class BenchmarkObserverController : MonoBehaviour
          {
                    [Header("Gateway Reference")]
                    [SerializeField] private GatewaySync gatewaySync;
                    [SerializeField] private GatewayConfig config;

                    [Header("Run Configuration")]
                    [SerializeField] private string runId = "unity-bench-001";
                    [SerializeField] private string lane = "B";
                    [SerializeField] private string assetIdPrefix = "";

                    [Header("Polling")]
                    [SerializeField] private float snapshotPollIntervalSec = 15f;
                    [SerializeField] private float fpsLogIntervalSec = 1.0f;

                    [Header("Behavior")]
                    [SerializeField] private bool autoStartOnConnect = true;
                    [SerializeField] private float observeDurationSec = 300f;

                    [Header("Debug")]
                    [SerializeField] private bool enableDebugLogs = true;

                    private bool isObserving = false;
                    private float observeStartTime;
                    private int sseEventsReceived = 0;

                    // Logs
                    private List<string> sseEventLogLines = new List<string>();
                    private List<string> requestLogLines = new List<string>();
                    private List<string> frameLogLines = new List<string>();

                    // FPS tracking
                    private int frameCount;
                    private float fpsTimer;
                    private float lastFpsLogTime;
                    private float lastSnapshotPollTime;
                    private int snapshotRequests;
                    private int anchorsRequests;

                    public bool IsObserving => isObserving;
                    public int SseEventsReceived => sseEventsReceived;

                    private void Start()
                    {
                              if (gatewaySync == null)
                                        gatewaySync = FindFirstObjectByType<GatewaySync>();
                              if (config == null && gatewaySync != null)
                                        config = gatewaySync.Config;

                              if (autoStartOnConnect && gatewaySync != null)
                                        gatewaySync.OnGatewayConnected.AddListener(OnGatewayConnected);
                    }

                    private void Update()
                    {
                              if (!isObserving) return;

                              frameCount++;
                              fpsTimer += Time.unscaledDeltaTime;

                              float now = Time.realtimeSinceStartup;

                              if (now - lastFpsLogTime >= fpsLogIntervalSec)
                              {
                                        LogFrameMetrics();
                                        lastFpsLogTime = now;
                              }

                              if (now - lastSnapshotPollTime >= snapshotPollIntervalSec)
                              {
                                        StartCoroutine(PollSnapshotCoroutine());
                                        StartCoroutine(PollAnchorsCoroutine());
                                        lastSnapshotPollTime = now;
                              }

                              // Auto-stop after observeDurationSec
                              if (now - observeStartTime > observeDurationSec)
                              {
                                        StopObserving();
                              }
                    }

                    private void OnDestroy()
                    {
                              if (gatewaySync != null)
                                        gatewaySync.OnGatewayConnected.RemoveListener(OnGatewayConnected);
                              UnsubscribeSse();
                    }

                    public void StartObserving()
                    {
                              if (isObserving) return;

                              Debug.Log($"[BenchmarkObserver] Starting observation for run={runId}, lane={lane}");

                              isObserving = true;
                              observeStartTime = Time.realtimeSinceStartup;
                              lastFpsLogTime = observeStartTime;
                              lastSnapshotPollTime = observeStartTime;

                              sseEventLogLines.Clear();
                              requestLogLines.Clear();
                              frameLogLines.Clear();

                              SubscribeSse();
                    }

                    public void StopObserving()
                    {
                              if (!isObserving) return;

                              isObserving = false;
                              UnsubscribeSse();

                              Debug.Log($"[BenchmarkObserver] Stopped. SSE events received: {sseEventsReceived}");

                              SaveLogsLocal();
                    }

                    private void SubscribeSse()
                    {
                              var sseClient = gatewaySync?.GetComponent<GatewaySseClient>();
                              if (sseClient != null)
                                        sseClient.OnEventReceived += OnSseEvent;
                    }

                    private void UnsubscribeSse()
                    {
                              var sseClient = gatewaySync?.GetComponent<GatewaySseClient>();
                              if (sseClient != null)
                                        sseClient.OnEventReceived -= OnSseEvent;
                    }

                    private void OnSseEvent(GatewayEvent evt)
                    {
                              if (!isObserving) return;

                              string assetId = evt.GetAssetId();
                              if (string.IsNullOrEmpty(assetId) ||
                                  evt.type == "HEARTBEAT" ||
                                  evt.type == "CONNECTED")
                                        return;

                              if (!string.IsNullOrEmpty(assetIdPrefix) && !assetId.StartsWith(assetIdPrefix))
                                        return;

                              sseEventsReceived++;

                              var log = new BenchmarkSseEventLog
                              {
                                        run_id = runId,
                                        lane = lane,
                                        event_id = evt.event_id,
                                        event_type = evt.type,
                                        asset_id = assetId,
                                        claim_id = evt.GetClaimId(),
                                        t_arrival_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                              };
                              sseEventLogLines.Add(JsonUtility.ToJson(log));

                              if (enableDebugLogs && sseEventsReceived % 10 == 0)
                                        Debug.Log($"[BenchmarkObserver] SSE events: {sseEventsReceived} (latest: {evt.type} for {assetId})");
                    }

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

                              frameCount = 0;
                              fpsTimer = 0f;
                    }

                    private IEnumerator PollSnapshotCoroutine()
                    {
                              string url = config.SnapshotUrl;
                              long tSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                              string reqId = $"req-{runId}-observer-snapshot-{snapshotRequests}";

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

                                        var logEntry = new BenchmarkRequestLog
                                        {
                                                  run_id = runId,
                                                  lane = lane,
                                                  op = "snapshot",
                                                  req_id = reqId,
                                                  t_send_ms = tSend,
                                                  t_resp_ms = tResp,
                                                  duration_ms = (int)(tResp - tSend),
                                                  http_status = (int)request.responseCode,
                                                  error = request.result != UnityWebRequest.Result.Success ? request.error : null,
                                                  response_bytes = Encoding.UTF8.GetByteCount(responseText)
                                        };
                                        requestLogLines.Add(JsonUtility.ToJson(logEntry));
                                        snapshotRequests++;
                              }
                    }

                    private IEnumerator PollAnchorsCoroutine()
                    {
                              string url = $"{config.gatewayBaseUrl}/admin/anchors";
                              long tSend = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                              string reqId = $"req-{runId}-observer-anchors-{anchorsRequests}";

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

                                        var logEntry = new BenchmarkRequestLog
                                        {
                                                  run_id = runId,
                                                  lane = lane,
                                                  op = "anchors",
                                                  req_id = reqId,
                                                  t_send_ms = tSend,
                                                  t_resp_ms = tResp,
                                                  duration_ms = (int)(tResp - tSend),
                                                  http_status = (int)request.responseCode,
                                                  error = request.result != UnityWebRequest.Result.Success ? request.error : null,
                                                  response_bytes = Encoding.UTF8.GetByteCount(responseText)
                                        };
                                        requestLogLines.Add(JsonUtility.ToJson(logEntry));
                                        anchorsRequests++;
                              }
                    }

                    private void SaveLogsLocal()
                    {
                              try
                              {
                                        string baseDir = System.IO.Path.Combine(
                                            Application.persistentDataPath, "benchmark_runs", runId, $"observer_{lane}");
                                        System.IO.Directory.CreateDirectory(baseDir);

                                        WriteLines(System.IO.Path.Combine(baseDir, "sse_event_logs.jsonl"), sseEventLogLines);
                                        WriteLines(System.IO.Path.Combine(baseDir, "request_logs.jsonl"), requestLogLines);
                                        WriteLines(System.IO.Path.Combine(baseDir, "frame_logs.jsonl"), frameLogLines);

                                        Debug.Log($"[BenchmarkObserver] Logs saved to: {baseDir}");
                              }
                              catch (Exception e)
                              {
                                        Debug.LogError($"[BenchmarkObserver] Save failed: {e.Message}");
                              }
                    }

                    private void WriteLines(string path, List<string> lines)
                    {
                              System.IO.File.WriteAllText(path, string.Join("\n", lines) + "\n");
                    }

                    private void OnGatewayConnected()
                    {
                              if (autoStartOnConnect && !isObserving)
                              {
                                        StartCoroutine(DelayedStart());
                              }
                    }

                    private IEnumerator DelayedStart()
                    {
                              yield return new WaitForSeconds(2f);
                              StartObserving();
                    }

                    public string GetSummary()
                    {
                              float elapsed = isObserving ? Time.realtimeSinceStartup - observeStartTime : 0f;
                              return $"Observer [{lane}] | SSE: {sseEventsReceived} | " +
                                     $"Snapshots: {snapshotRequests} | Elapsed: {elapsed:F0}s";
                    }
          }
}