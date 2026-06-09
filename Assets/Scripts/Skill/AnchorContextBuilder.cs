// ============================================================================
// FILE: AnchorContextBuilder.cs
// Builds the SkillContext payload handed to the LLM at /skills/interpret (Phase 6).
//
// The LLM uses this context to ground the user's natural-language request:
//   - focusedAssetId: which anchor the user currently has selected
//   - currentClaimState: that anchor's on-chain lifecycle state (drives whether
//                        "approve" means EndorseClaim vs EndorseRevoke, etc.)
//   - poseHash/metadataHash: needed only for ProposeAnchor
//   - visibleAssetIds: all anchors in view (helps the LLM resolve "this"/"that")
//
// assetId convention matches the existing project: "TAG_{aprilTagId}"
// (see AnchorInfoPanel.cs UpdateInfo and DepthAnchorSystem.DepthAnchorInstance).
// ============================================================================

using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ARObjectDetection.Gateway
{
          public static class AnchorContextBuilder
          {
                    /// <summary>
                    /// Build a SkillContext for the given selected anchor. `gatewaySync` provides
                    /// the on-chain claim state; `allAnchors` (optional) populates visibleAssetIds.
                    /// </summary>
                    public static SkillContext Build(
                        DepthAnchorSystem.DepthAnchorInstance anchor,
                        GatewaySync gatewaySync,
                        IEnumerable<DepthAnchorSystem.DepthAnchorInstance> allAnchors = null)
                    {
                              var ctx = new SkillContext();
                              if (anchor == null) return ctx;

                              string assetId = AssetIdFor(anchor);
                              ctx.focusedAssetId = assetId;
                              ctx.className = anchor.className;
                              ctx.confidence = anchor.confidence;

                              // On-chain claim state → context string the labeling guide expects.
                              if (gatewaySync != null && !string.IsNullOrEmpty(assetId))
                              {
                                        var claim = gatewaySync.GetClaimState(assetId);
                                        ctx.currentClaimState = MapClaimState(claim?.status ?? ClaimStatus.None);
                              }
                              else
                              {
                                        ctx.currentClaimState = "NONE";
                              }

                              // Pose + metadata hashes (needed for ProposeAnchor; harmless otherwise).
                              // We hash a canonical string form of the world pose and a small metadata blob.
                              // NOTE: this is a client-side convenience hash for the LLM's argument
                              // construction. The gateway/chaincode does not trust these for security;
                              // they're provenance fingerprints, consistent with poseHash usage elsewhere.
                              Vector3 p = anchor.worldLockedPosition;
                              string poseCanonical = $"{p.x:F4},{p.y:F4},{p.z:F4}";
                              ctx.poseHash = "sha256:" + Sha256Hex(poseCanonical);

                              string metaCanonical = $"{anchor.className}|{anchor.confidence:F3}|{assetId}";
                              ctx.metadataHash = "sha256:" + Sha256Hex(metaCanonical);

                              // Visible asset ids — helps the LLM resolve ambiguous referents.
                              if (allAnchors != null)
                              {
                                        var ids = new List<string>();
                                        foreach (var a in allAnchors)
                                        {
                                                  string aid = AssetIdFor(a);
                                                  if (!string.IsNullOrEmpty(aid)) ids.Add(aid);
                                        }
                                        ctx.visibleAssetIds = ids.ToArray();
                              }
                              else
                              {
                                        ctx.visibleAssetIds = string.IsNullOrEmpty(assetId)
                                            ? new string[0] : new[] { assetId };
                              }

                              return ctx;
                    }

                    /// <summary>
                    /// The asset id for an anchor: "TAG_{aprilTagId}" when associated, else null.
                    /// Matches AnchorInfoPanel.UpdateInfo's existing convention.
                    /// </summary>
                    public static string AssetIdFor(DepthAnchorSystem.DepthAnchorInstance anchor)
                    {
                              if (anchor == null) return null;
                              if (anchor.hasAprilTagAssociation && anchor.associatedAprilTagId >= 0)
                                        return $"TAG_{anchor.associatedAprilTagId}";
                              return null;
                    }

                    private static string MapClaimState(ClaimStatus status)
                    {
                              return status switch
                              {
                                        ClaimStatus.None => "NONE",
                                        ClaimStatus.Pending => "PROPOSED",      // optimistic local pending
                                        ClaimStatus.Proposed => "PROPOSED",
                                        ClaimStatus.EndorsedOrg1 => "ENDORSED_ORG1",
                                        ClaimStatus.EndorsedOrg2 => "ENDORSED_ORG2",
                                        ClaimStatus.Active => "ACTIVE",
                                        ClaimStatus.Rejected => "REJECTED",
                                        ClaimStatus.RevokePending => "REVOKE_PENDING",
                                        ClaimStatus.Revoked => "REVOKED",
                                        _ => "NONE"
                              };
                    }

                    private static string Sha256Hex(string input)
                    {
                              using (var sha = SHA256.Create())
                              {
                                        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                                        var sb = new StringBuilder(bytes.Length * 2);
                                        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                                        return sb.ToString();
                              }
                    }
          }
}