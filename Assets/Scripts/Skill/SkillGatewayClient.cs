// ============================================================================
// FILE: SkillGatewayClient.cs
// HTTP client for the LLM-mediated skill-gateway endpoints (Phase 6).
//
// Mirrors the existing GatewayClient coroutine + header pattern:
//   headers: Content-Type, x-api-key, x-org-id
//   transport: UnityWebRequest with explicit timeout
//
// Why this is separate from GatewayClient:
//   The /skills/* responses embed a `decision` object whose `arguments` field has
//   a variable shape that Unity's JsonUtility cannot deserialize. We do a small
//   amount of manual JSON splitting to pull the `decision`, `audit`, and
//   `decision.arguments` sub-objects out, then JsonUtility-parse each piece.
//   Keeping that logic here avoids polluting the (working) GatewayClient.
//
// This client is added as a Component on the same GameObject as GatewaySync.
// ============================================================================

using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ARObjectDetection.Gateway
{
          public class SkillGatewayClient : MonoBehaviour
          {
                    private GatewayConfig config;
                    private bool isInitialized = false;
                    public bool IsInitialized => isInitialized;

                    public void Initialize(GatewayConfig gatewayConfig)
                    {
                              if (gatewayConfig == null)
                              {
                                        Debug.LogError("[SkillGatewayClient] Initialize called with null config!");
                                        return;
                              }
                              config = gatewayConfig;
                              isInitialized = true;
                              Debug.Log($"[SkillGatewayClient] ✓ Initialized: {config.gatewayBaseUrl}{config.skillInterpretEndpoint}");
                    }

                    // =====================================================================
                    // INTERPRET  — POST /skills/interpret
                    // =====================================================================

                    public void Interpret(SkillInterpretRequest request,
                        Action<SkillInterpretResponse> onComplete, Action<string> onError)
                    {
                              if (!isInitialized || config == null) { onError?.Invoke("SkillGatewayClient not initialized"); return; }
                              if (request == null || string.IsNullOrWhiteSpace(request.userText)) { onError?.Invoke("userText required"); return; }
                              StartCoroutine(InterpretCoroutine(request, onComplete, onError));
                    }

                    private IEnumerator InterpretCoroutine(SkillInterpretRequest request,
                        Action<SkillInterpretResponse> onComplete, Action<string> onError)
                    {
                              string url = config.SkillInterpretUrl;
                              string json = JsonUtility.ToJson(request);

                              if (config.enableDebugLogs) Debug.Log($"[SkillGatewayClient] POST {url}\n{json}");

                              using (var web = new UnityWebRequest(url, "POST"))
                              {
                                        web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                                        web.downloadHandler = new DownloadHandlerBuffer();
                                        web.SetRequestHeader("Content-Type", "application/json");
                                        web.SetRequestHeader("x-api-key", config.apiKey);
                                        web.SetRequestHeader("x-org-id", config.organizationId);
                                        web.timeout = config.skillRequestTimeoutSeconds;

                                        yield return web.SendWebRequest();

                                        string body = web.downloadHandler?.text ?? "";

                                        // The gateway returns structured JSON on BOTH 2xx and 4xx (REJECT/CLARIFY
                                        // come back as 200 or 422 with a full decision). So we try to parse the
                                        // body regardless of HTTP code, and only fall back to transport error if
                                        // the body isn't usable.
                                        if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("{"))
                                        {
                                                  SkillInterpretResponse parsed;
                                                  try
                                                  {
                                                            parsed = ParseInterpretResponse(body);
                                                  }
                                                  catch (Exception e)
                                                  {
                                                            onError?.Invoke($"interpret parse error: {e.Message}\nbody: {Truncate(body, 400)}");
                                                            yield break;
                                                  }
                                                  onComplete?.Invoke(parsed);
                                        }
                                        else
                                        {
                                                  onError?.Invoke($"HTTP {web.responseCode}: {web.error} - {Truncate(body, 400)}");
                                        }
                              }
                    }

                    // =====================================================================
                    // EXECUTE  — POST /skills/execute
                    // =====================================================================

                    public void Execute(string decisionId,
                        Action<SkillExecuteResponse> onComplete, Action<string> onError)
                    {
                              if (!isInitialized || config == null) { onError?.Invoke("SkillGatewayClient not initialized"); return; }
                              if (string.IsNullOrEmpty(decisionId)) { onError?.Invoke("decision_id required"); return; }
                              StartCoroutine(ExecuteCoroutine(decisionId, onComplete, onError));
                    }

                    private IEnumerator ExecuteCoroutine(string decisionId,
                        Action<SkillExecuteResponse> onComplete, Action<string> onError)
                    {
                              string url = config.SkillExecuteUrl;
                              var req = new SkillExecuteRequest { decision_id = decisionId, confirm = true };
                              string json = JsonUtility.ToJson(req);

                              if (config.enableDebugLogs) Debug.Log($"[SkillGatewayClient] POST {url}\n{json}");

                              using (var web = new UnityWebRequest(url, "POST"))
                              {
                                        web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                                        web.downloadHandler = new DownloadHandlerBuffer();
                                        web.SetRequestHeader("Content-Type", "application/json");
                                        web.SetRequestHeader("x-api-key", config.apiKey);
                                        web.SetRequestHeader("x-org-id", config.organizationId);
                                        web.timeout = config.skillRequestTimeoutSeconds;

                                        yield return web.SendWebRequest();

                                        string body = web.downloadHandler?.text ?? "";

                                        if (!string.IsNullOrEmpty(body) && body.TrimStart().StartsWith("{"))
                                        {
                                                  SkillExecuteResponse parsed;
                                                  try { parsed = JsonUtility.FromJson<SkillExecuteResponse>(body); }
                                                  catch (Exception e)
                                                  {
                                                            onError?.Invoke($"execute parse error: {e.Message}\nbody: {Truncate(body, 400)}");
                                                            yield break;
                                                  }

                                                  // execute returns success:false with an error string on 409/422/500.
                                                  // We still hand it to onComplete so the UI can show what happened on chain.
                                                  onComplete?.Invoke(parsed);
                                        }
                                        else
                                        {
                                                  onError?.Invoke($"HTTP {web.responseCode}: {web.error} - {Truncate(body, 400)}");
                                        }
                              }
                    }

                    // =====================================================================
                    // HEALTH  — GET /skills/health  (optional "Test Connection" helper)
                    // =====================================================================

                    public void CheckHealth(Action<SkillHealthResponse> onComplete, Action<string> onError)
                    {
                              if (!isInitialized || config == null) { onError?.Invoke("SkillGatewayClient not initialized"); return; }
                              StartCoroutine(HealthCoroutine(onComplete, onError));
                    }

                    private IEnumerator HealthCoroutine(Action<SkillHealthResponse> onComplete, Action<string> onError)
                    {
                              string url = config.SkillHealthUrl;
                              using (var web = UnityWebRequest.Get(url))
                              {
                                        web.SetRequestHeader("x-api-key", config.apiKey);
                                        web.SetRequestHeader("x-org-id", config.organizationId);
                                        web.timeout = config.skillRequestTimeoutSeconds;
                                        yield return web.SendWebRequest();

                                        if (web.result == UnityWebRequest.Result.Success)
                                        {
                                                  try { onComplete?.Invoke(JsonUtility.FromJson<SkillHealthResponse>(web.downloadHandler.text)); }
                                                  catch (Exception e) { onError?.Invoke($"health parse error: {e.Message}"); }
                                        }
                                        else
                                        {
                                                  onError?.Invoke($"HTTP {web.responseCode}: {web.error}");
                                        }
                              }
                    }

                    // =====================================================================
                    // JSON HELPERS
                    //   JsonUtility can't deserialize `decision.arguments` (variable shape) nor
                    //   nested objects it doesn't know. We manually slice the top-level
                    //   `decision` and `audit` sub-objects out of the response, then within the
                    //   decision we slice out `arguments`. Each slice is JsonUtility-parsed.
                    // =====================================================================

                    private SkillInterpretResponse ParseInterpretResponse(string body)
                    {
                              // 1. Parse the flat top-level fields (success, decision_id, etc.).
                              var resp = JsonUtility.FromJson<SkillInterpretResponse>(body);

                              // 2. Extract and parse the nested `decision` object (if present).
                              string decisionJson = ExtractJsonObject(body, "decision");
                              if (!string.IsNullOrEmpty(decisionJson))
                              {
                                        // 2a. Pull `arguments` out first and stash the raw JSON, because
                                        //     JsonUtility on SkillDecision can't parse the arguments object.
                                        string argsJson = ExtractJsonObject(decisionJson, "arguments");

                                        // 2b. Parse the rest of the decision (arguments field is ignored
                                        //     since SkillDecision has no `arguments` member).
                                        var decision = JsonUtility.FromJson<SkillDecision>(decisionJson);
                                        if (decision != null)
                                        {
                                                  decision.argumentsJson = argsJson;
                                                  resp.decision = decision;
                                        }
                              }

                              // 3. Extract and parse the nested `audit` object (if present).
                              string auditJson = ExtractJsonObject(body, "audit");
                              if (!string.IsNullOrEmpty(auditJson))
                              {
                                        resp.audit = JsonUtility.FromJson<SkillAudit>(auditJson);
                              }

                              return resp;
                    }

                    /// <summary>
                    /// Parse the stashed arguments JSON into a typed SkillArguments.
                    /// Returns an empty SkillArguments if the decision had no arguments.
                    /// </summary>
                    public static SkillArguments ParseArguments(SkillDecision decision)
                    {
                              if (decision == null || string.IsNullOrEmpty(decision.argumentsJson))
                                        return new SkillArguments();
                              try { return JsonUtility.FromJson<SkillArguments>(decision.argumentsJson) ?? new SkillArguments(); }
                              catch { return new SkillArguments(); }
                    }

                    /// <summary>
                    /// Extract the JSON text of a named object-valued key from a JSON string.
                    /// Handles nested braces and braces inside strings. Returns null if the key
                    /// is absent or its value is null/not an object.
                    /// Example: ExtractJsonObject("{\"a\":{\"b\":1},\"c\":2}", "a") => "{\"b\":1}"
                    /// </summary>
                    private static string ExtractJsonObject(string json, string key)
                    {
                              if (string.IsNullOrEmpty(json)) return null;
                              string needle = "\"" + key + "\"";
                              int k = json.IndexOf(needle, StringComparison.Ordinal);
                              if (k < 0) return null;

                              // Advance to the colon, then to the first non-whitespace char (the value).
                              int i = k + needle.Length;
                              while (i < json.Length && json[i] != ':') i++;
                              i++; // skip ':'
                              while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                              if (i >= json.Length) return null;

                              // null value → no object
                              if (json[i] == 'n') return null;
                              if (json[i] != '{') return null; // not an object value

                              int depth = 0;
                              bool inStr = false;
                              bool esc = false;
                              int start = i;
                              for (; i < json.Length; i++)
                              {
                                        char c = json[i];
                                        if (inStr)
                                        {
                                                  if (esc) esc = false;
                                                  else if (c == '\\') esc = true;
                                                  else if (c == '"') inStr = false;
                                        }
                                        else
                                        {
                                                  if (c == '"') inStr = true;
                                                  else if (c == '{') depth++;
                                                  else if (c == '}')
                                                  {
                                                            depth--;
                                                            if (depth == 0) return json.Substring(start, i - start + 1);
                                                  }
                                        }
                              }
                              return null; // unbalanced
                    }

                    private static string Truncate(string s, int n)
                        => string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s.Substring(0, n) + "…");
          }
}