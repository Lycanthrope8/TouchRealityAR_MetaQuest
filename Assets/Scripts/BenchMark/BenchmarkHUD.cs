// ============================================================================
// FILE: BenchmarkHUD.cs
// Phase 4 - On-screen HUD for benchmark status display
//
// Attach to a Canvas with TextMeshProUGUI elements for real-time display
// of benchmark progress, latency stats, and FPS on the Quest 3 headset.
// ============================================================================

using UnityEngine;
using TMPro;

namespace ARObjectDetection.Gateway.Benchmark
{
          public class BenchmarkHUD : MonoBehaviour
          {
                    [Header("References")]
                    [SerializeField] private BenchmarkModeController benchmarkController;
                    [SerializeField] private TextMeshProUGUI statusText;
                    [SerializeField] private TextMeshProUGUI fpsText;

                    [Header("Settings")]
                    [SerializeField] private float updateIntervalSec = 0.5f;

                    private float lastUpdateTime;
                    private int frameCount;
                    private float fpsTimer;

                    private void Start()
                    {
                              if (benchmarkController == null)
                                        benchmarkController = FindFirstObjectByType<BenchmarkModeController>();
                    }

                    private void Update()
                    {
                              frameCount++;
                              fpsTimer += Time.unscaledDeltaTime;

                              if (Time.realtimeSinceStartup - lastUpdateTime < updateIntervalSec)
                                        return;

                              lastUpdateTime = Time.realtimeSinceStartup;

                              // Update status
                              if (statusText != null && benchmarkController != null)
                              {
                                        statusText.text = benchmarkController.GetSummary();
                              }

                              // Update FPS
                              if (fpsText != null)
                              {
                                        float fps = frameCount / Mathf.Max(fpsTimer, 0.001f);
                                        float frameTimeMs = 1000f / Mathf.Max(fps, 1f);
                                        fpsText.text = $"FPS: {fps:F0} ({frameTimeMs:F1}ms)";
                                        fpsText.color = fps >= 72f ? Color.green : (fps >= 45f ? Color.yellow : Color.red);
                              }

                              frameCount = 0;
                              fpsTimer = 0f;
                    }

                    /// <summary>
                    /// Called by UI button
                    /// </summary>
                    public void OnStartBenchmarkButton()
                    {
                              if (benchmarkController != null)
                                        benchmarkController.StartBenchmark();
                    }

                    /// <summary>
                    /// Called by UI button
                    /// </summary>
                    public void OnStopBenchmarkButton()
                    {
                              if (benchmarkController != null)
                                        benchmarkController.StopBenchmark();
                    }
          }
}