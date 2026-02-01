// ============================================================================
// FILE: AprilTagOverlayManager.cs
// Creates and manages visual overlays for latched AprilTags.
// 
// NOTE: This component creates debug cubes for tag visualization.
// DISABLE THIS COMPONENT if you see unwanted red/colored cubes moving with head.
// The main anchor visualization is handled by DepthAnchorSystem.
// ============================================================================

using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace ARObjectDetection.AprilTag
{
    public class TagOverlay
    {
        public int TagId;
        public GameObject OverlayObject;
        public TextMeshPro LabelText;
        public MeshRenderer OverlayRenderer;
        public float LastUpdateTime;
    }

    public class AprilTagOverlayManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private AssetTagManager assetTagManager;
        [SerializeField] private PoseCorrector poseCorrector;

        [Header("Overlay Settings")]
        [SerializeField] private GameObject overlayPrefab;
        [SerializeField] private float overlayScale = 0.1f;
        [SerializeField] private float positionSmoothTime = 0.1f;

        [Header("Enable/Disable")]
        [Tooltip("DISABLE this if you see unwanted cubes moving with head motion!")]
        [SerializeField] private bool enableOverlays = false;  // DISABLED BY DEFAULT

        [Header("Colors")]
        [SerializeField] private Color latchedColor = Color.green;
        [SerializeField] private Color conflictColor = new Color(1f, 0.5f, 0f);
        [SerializeField] private Color occludedColor = Color.gray;
        [SerializeField] private Color correctingColor = Color.cyan;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = false;  // DISABLED BY DEFAULT

        private Dictionary<int, TagOverlay> overlays = new Dictionary<int, TagOverlay>();
        private Dictionary<int, Vector3> overlayVelocities = new Dictionary<int, Vector3>();

        private void Awake()
        {
            if (!enableOverlays)
            {
                Debug.Log("[AprilTagOverlayManager] Overlays DISABLED - enable in Inspector if you want tag debug cubes");
                return;
            }

            if (assetTagManager == null)
                assetTagManager = FindFirstObjectByType<AssetTagManager>();
            if (poseCorrector == null)
                poseCorrector = FindFirstObjectByType<PoseCorrector>();

            if (assetTagManager == null)
            {
                Debug.LogError("[AprilTagOverlayManager] AssetTagManager not found!");
                enabled = false;
                return;
            }

            assetTagManager.OnTagLatched.AddListener(OnTagLatched);
        }

        private void OnDestroy()
        {
            if (assetTagManager != null)
                assetTagManager.OnTagLatched.RemoveListener(OnTagLatched);

            ClearAllOverlays();
        }

        private void Update()
        {
            if (!enableOverlays) return;
            UpdateAllOverlays();
        }

        private void OnTagLatched(int tagId)
        {
            if (!enableOverlays) return;

            if (!overlays.ContainsKey(tagId))
            {
                CreateOverlay(tagId);
            }
        }

        private void CreateOverlay(int tagId)
        {
            if (!enableOverlays) return;

            LatchedTagState state = assetTagManager.GetTagState(tagId);
            if (state == null)
                return;

            GameObject overlayObj;
            if (overlayPrefab != null)
            {
                overlayObj = Instantiate(overlayPrefab);
            }
            else
            {
                overlayObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                overlayObj.transform.localScale = Vector3.one * overlayScale;
            }

            // CRITICAL: Do NOT parent under camera - keep in world space
            overlayObj.transform.SetParent(null, true);
            overlayObj.name = $"AprilTag_{tagId}_Overlay";

            TextMeshPro label = overlayObj.GetComponentInChildren<TextMeshPro>();
            if (label == null)
            {
                GameObject labelObj = new GameObject("Label");
                labelObj.transform.SetParent(overlayObj.transform);
                labelObj.transform.localPosition = Vector3.up * (overlayScale + 0.02f);
                label = labelObj.AddComponent<TextMeshPro>();
                label.fontSize = 0.5f;
                label.alignment = TextAlignmentOptions.Center;
            }

            label.text = $"Tag #{tagId}";

            TagOverlay overlay = new TagOverlay
            {
                TagId = tagId,
                OverlayObject = overlayObj,
                LabelText = label,
                OverlayRenderer = overlayObj.GetComponent<MeshRenderer>(),
                LastUpdateTime = Time.realtimeSinceStartup
            };

            overlays[tagId] = overlay;
            overlayVelocities[tagId] = Vector3.zero;

            // Initial position - use WORLD pose
            overlayObj.transform.SetPositionAndRotation(
                state.LastTagPoseWorld.position,
                state.LastTagPoseWorld.rotation
            );

            if (enableDebugLogs)
            {
                Debug.Log($"[AprilTagOverlayManager] Created overlay for tag #{tagId} at WORLD pos {state.LastTagPoseWorld.position}");
            }
        }

        private void UpdateAllOverlays()
        {
            foreach (var kvp in overlays)
            {
                UpdateOverlay(kvp.Value);
            }
        }

        private void UpdateOverlay(TagOverlay overlay)
        {
            LatchedTagState state = assetTagManager.GetTagState(overlay.TagId);
            if (state == null || overlay.OverlayObject == null)
                return;

            // Update position using WORLD pose (not camera-relative!)
            Vector3 targetPos = state.LastTagPoseWorld.position;
            Quaternion targetRot = state.LastTagPoseWorld.rotation;

            if (poseCorrector != null && poseCorrector.TryGetCorrectedPose(overlay.TagId, out Pose correctedPose))
            {
                targetPos = correctedPose.position;
                targetRot = correctedPose.rotation;
            }

            Vector3 velocity = overlayVelocities[overlay.TagId];
            Vector3 newPos = Vector3.SmoothDamp(
                overlay.OverlayObject.transform.position,
                targetPos,
                ref velocity,
                positionSmoothTime
            );
            overlayVelocities[overlay.TagId] = velocity;

            overlay.OverlayObject.transform.position = newPos;
            overlay.OverlayObject.transform.rotation = Quaternion.Slerp(
                overlay.OverlayObject.transform.rotation,
                targetRot,
                Time.deltaTime / positionSmoothTime
            );

            // Update appearance
            UpdateOverlayAppearance(overlay, state);

            // Update label
            if (overlay.LabelText != null)
            {
                string trackStr = state.HasLinkedTrack ? $"\n{state.LinkedClassName}" : "";
                overlay.LabelText.text = $"Tag #{overlay.TagId}{trackStr}";
            }

            overlay.LastUpdateTime = Time.realtimeSinceStartup;
        }

        private void UpdateOverlayAppearance(TagOverlay overlay, LatchedTagState state)
        {
            if (overlay.OverlayRenderer == null)
                return;

            Color targetColor;

            if (state.IsInConflict)
            {
                targetColor = conflictColor;
            }
            else if (poseCorrector != null && poseCorrector.IsCorrectionInProgress(state.TagId))
            {
                targetColor = correctingColor;
            }
            else if (state.SecondsSinceLastSeen > 0.5f)
            {
                targetColor = occludedColor;
            }
            else
            {
                targetColor = latchedColor;
            }

            overlay.OverlayRenderer.material.color = Color.Lerp(
                overlay.OverlayRenderer.material.color,
                targetColor,
                Time.deltaTime * 5f
            );
        }

        public TagOverlay GetOverlay(int tagId)
        {
            overlays.TryGetValue(tagId, out TagOverlay overlay);
            return overlay;
        }

        public void RefreshAllOverlays()
        {
            if (!enableOverlays) return;

            foreach (var state in assetTagManager.GetAllLatchedTags())
            {
                if (!overlays.ContainsKey(state.TagId))
                {
                    CreateOverlay(state.TagId);
                }
            }
        }

        public void ClearAllOverlays()
        {
            foreach (var overlay in overlays.Values)
            {
                if (overlay.OverlayObject != null)
                    Destroy(overlay.OverlayObject);
            }
            overlays.Clear();
            overlayVelocities.Clear();
        }

        /// <summary>
        /// Enable or disable overlays at runtime
        /// </summary>
        public void SetOverlaysEnabled(bool enabled)
        {
            enableOverlays = enabled;
            if (!enabled)
            {
                ClearAllOverlays();
            }
        }
    }
}