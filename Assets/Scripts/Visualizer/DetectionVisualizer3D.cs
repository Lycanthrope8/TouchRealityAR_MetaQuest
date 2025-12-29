// DetectionVisualizer3D.cs - FIXES for visibility
// Changes:
// 1. Increased boxLifetime to 2.0s (was 0.5s)
// 2. Added continuous box updates instead of recreating
// 3. Better color coding for track states

using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Linq;
using PassthroughCameraSamples;

namespace ARObjectDetection
{
    public class DetectionVisualizer3D : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GameObject boundingBox3DPrefab;
        [SerializeField] private DetectionConfig config;
        [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

        [Header("3D Settings")]
        [SerializeField] private float defaultDepth = 2.0f;
        [SerializeField] private float boxThickness = 0.01f;
        [SerializeField] private LayerMask raycastLayers = ~0;
        [SerializeField] private float maxRaycastDistance = 10f;

        [Header("Label Settings")]
        [Tooltip("Vertical position: 0=top edge, 0.5=center, 1=bottom edge")]
        [Range(0f, 1f)]
        [SerializeField] private float labelOffsetYPercent = 0.05f;

        [Tooltip("Horizontal position: 0=left edge, 0.5=center, 1=right edge")]
        [Range(0f, 1f)]
        [SerializeField] private float labelOffsetXPercent = 0.5f;

        [Tooltip("Scale multiplier for label size")]
        [SerializeField] private float labelScale = 0.01f;

        [Header("Label Corner Settings")]
        [SerializeField] private float labelPadX = 0.02f;
        [SerializeField] private float labelPadY = 0.02f;

        [Header("Performance")]
        [Tooltip("How long boxes stay visible without updates")]
        [SerializeField] private float boxLifetime = 1.0f;  // INCREASED from 0.5s

        [Header("Visibility")]
        [Tooltip("Show tentative (unconfirmed) tracks")]
        [SerializeField] private bool showTentativeTracks = true;  // NEW

        // Track ID -> Box mapping for persistence
        private Dictionary<int, BoundingBox3DInstance> activeBoxesByTrackId = new Dictionary<int, BoundingBox3DInstance>();
        private Queue<BoundingBox3DInstance> boxPool = new Queue<BoundingBox3DInstance>();

        private PassthroughCameraIntrinsics? cameraIntrinsics;

        private void Awake()
        {
            // Pre-instantiate boxes
            for (int i = 0; i < 15; i++)  // Increased pool size
            {
                CreatePooledBox();
            }

            try
            {
                cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(cameraEye);
                Debug.Log($"[3D Visualizer] Camera intrinsics loaded");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[3D Visualizer] Failed to get camera intrinsics: {e.Message}");
            }
        }

        private void Update()
        {
            // Remove boxes that haven't been updated recently
            List<int> tracksToRemove = new List<int>();

            foreach (var kvp in activeBoxesByTrackId)
            {
                if (Time.time - kvp.Value.lastUpdateTime > boxLifetime)
                {
                    tracksToRemove.Add(kvp.Key);
                }
            }

            foreach (int trackId in tracksToRemove)
            {
                if (activeBoxesByTrackId.TryGetValue(trackId, out var box))
                {
                    ReturnBoxToPool(box);
                    activeBoxesByTrackId.Remove(trackId);
                }
            }
        }

        /// <summary>
        /// Show tracked objects with stable IDs (PERSISTENT)
        /// </summary>
        public void ShowTrackedObjects(List<TrackedObject> tracks)
        {
            if (tracks == null || !config.show3DBoundingBoxes)
                return;

            // Update or create boxes for each track
            foreach (var track in tracks)
            {
                // Skip tentative tracks if disabled
                if (!showTentativeTracks && track.state == TrackState.Tentative)
                    continue;

                BoundingBox3DInstance boxInstance;

                // Reuse existing box if available
                if (activeBoxesByTrackId.TryGetValue(track.id, out boxInstance))
                {
                    // Update existing box
                    UpdateTrackedObjectBox(boxInstance, track);
                }
                else
                {
                    // Create new box
                    boxInstance = GetBoxFromPool();
                    activeBoxesByTrackId[track.id] = boxInstance;
                    UpdateTrackedObjectBox(boxInstance, track);
                }

                boxInstance.lastUpdateTime = Time.time;
            }

            Debug.Log($"[3D Visualizer] Showing {activeBoxesByTrackId.Count} boxes for {tracks.Count} tracks");
        }

        /// <summary>
        /// Update box for a tracked object
        /// </summary>
        private void UpdateTrackedObjectBox(BoundingBox3DInstance boxInstance, TrackedObject track)
        {
            // Position and rotation
            boxInstance.transform.position = track.worldPositionSmoothed;

            Vector3 toCamera = Camera.main.transform.position - track.worldPositionSmoothed;
            boxInstance.transform.rotation = Quaternion.LookRotation(toCamera);

            // Update geometry and appearance
            UpdateBox3DForTrack(boxInstance, track);

            // Ensure visible
            boxInstance.gameObject.SetActive(true);
        }

        /// <summary>
        /// Update box appearance for tracked object
        /// </summary>
        private void UpdateBox3DForTrack(BoundingBox3DInstance boxInstance, TrackedObject track)
        {
            if (boxInstance.lineRenderers == null || boxInstance.lineRenderers.Length < 4)
            {
                Debug.LogError($"[3D VIZ] Missing LineRenderers!");
                return;
            }

            Vector3 halfSize = track.worldSize / 2f;

            // Front face edges
            DrawBoxEdge(boxInstance.lineRenderers[0],
                new Vector3(-halfSize.x, -halfSize.y, 0),
                new Vector3(halfSize.x, -halfSize.y, 0));
            DrawBoxEdge(boxInstance.lineRenderers[1],
                new Vector3(halfSize.x, -halfSize.y, 0),
                new Vector3(halfSize.x, halfSize.y, 0));
            DrawBoxEdge(boxInstance.lineRenderers[2],
                new Vector3(halfSize.x, halfSize.y, 0),
                new Vector3(-halfSize.x, halfSize.y, 0));
            DrawBoxEdge(boxInstance.lineRenderers[3],
                new Vector3(-halfSize.x, halfSize.y, 0),
                new Vector3(-halfSize.x, -halfSize.y, 0));

            // Color by track state with better visibility
            Color lineColor;
            float lineWidth;

            switch (track.state)
            {
                case TrackState.Tentative:
                    lineColor = new Color(1f, 0.8f, 0f, 0.8f);  // Bright yellow
                    lineWidth = 0.012f;
                    break;
                case TrackState.Confirmed:
                    lineColor = new Color(0f, 1f, 0f, 1f);  // Bright green
                    lineWidth = 0.015f;  // Thicker for confirmed
                    break;
                case TrackState.Lost:
                    lineColor = new Color(1f, 0f, 0f, 0.5f);  // Fading red
                    lineWidth = 0.010f;
                    break;
                default:
                    lineColor = Color.white;
                    lineWidth = 0.012f;
                    break;
            }

            foreach (var lr in boxInstance.lineRenderers)
            {
                lr.startColor = lineColor;
                lr.endColor = lineColor;
                lr.startWidth = lineWidth;
                lr.endWidth = lineWidth;
                lr.enabled = true;
            }

            // Update label
            if (boxInstance.label != null && config.showLabels)
            {
                string stateSymbol = track.state == TrackState.Confirmed ? "✓" : "◆";
                string labelText = $"{stateSymbol} {track.className} #{track.id}";

                if (config.showConfidence)
                {
                    labelText += $"\n{track.confidence:F2}";
                }

                boxInstance.label.text = labelText;

                Vector3 boxPosition = boxInstance.transform.position;
                Vector3 boxUp = boxInstance.transform.up;
                Vector3 boxRight = boxInstance.transform.right;

                boxInstance.labelRectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                boxInstance.labelRectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                boxInstance.labelRectTransform.pivot = new Vector2(0f, 1f);

                Vector3 labelWorldPos =
                    boxPosition
                    + boxUp * (track.worldSize.y * 0.5f + labelPadY)  // Above box
                    - boxRight * (track.worldSize.x * 0.5f - labelPadX);

                boxInstance.labelRectTransform.position = labelWorldPos;
                boxInstance.labelRectTransform.localScale = Vector3.one * labelScale;
                boxInstance.label.alignment = TMPro.TextAlignmentOptions.TopLeft;
                boxInstance.label.color = lineColor;
                boxInstance.label.gameObject.SetActive(true);
            }
            else if (boxInstance.label != null)
            {
                boxInstance.label.gameObject.SetActive(false);
            }
        }

        private void DrawBoxEdge(LineRenderer lineRenderer, Vector3 start, Vector3 end)
        {
            lineRenderer.positionCount = 2;
            lineRenderer.SetPosition(0, start);
            lineRenderer.SetPosition(1, end);
        }

        public void ClearAllBoxes()
        {
            foreach (var box in activeBoxesByTrackId.Values)
            {
                ReturnBoxToPool(box);
            }
            activeBoxesByTrackId.Clear();
        }

        #region Object Pooling

        private BoundingBox3DInstance GetBoxFromPool()
        {
            if (boxPool.Count > 0)
            {
                return boxPool.Dequeue();
            }
            return CreatePooledBox();
        }

        private void ReturnBoxToPool(BoundingBox3DInstance box)
        {
            box.gameObject.SetActive(false);
            if (box.label != null)
            {
                box.label.gameObject.SetActive(false);
            }
            boxPool.Enqueue(box);
        }

        private BoundingBox3DInstance CreatePooledBox()
        {
            GameObject boxObj;
            GameObject labelObj = null;

            if (boundingBox3DPrefab != null)
            {
                boxObj = Instantiate(boundingBox3DPrefab, transform);
                TextMeshPro existingLabel = boxObj.GetComponentInChildren<TextMeshPro>();

                if (existingLabel != null)
                {
                    labelObj = existingLabel.gameObject;
                    if (labelObj.GetComponent<Billboard>() == null)
                    {
                        labelObj.AddComponent<Billboard>();
                    }
                }
            }
            else
            {
                boxObj = new GameObject("BoundingBox3D");
                boxObj.transform.SetParent(transform);

                for (int i = 0; i < 4; i++)
                {
                    GameObject lineObj = new GameObject($"Edge_{i}");
                    lineObj.transform.SetParent(boxObj.transform);

                    LineRenderer lr = lineObj.AddComponent<LineRenderer>();
                    lr.startWidth = 0.015f;
                    lr.endWidth = 0.015f;
                    lr.material = new Material(Shader.Find("Sprites/Default"));
                    lr.startColor = Color.green;
                    lr.endColor = Color.green;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    lr.useWorldSpace = false;
                }
            }

            boxObj.SetActive(false);
            if (labelObj != null)
            {
                labelObj.SetActive(false);
            }

            BoundingBox3DInstance boxInstance = new BoundingBox3DInstance
            {
                gameObject = boxObj,
                transform = boxObj.transform,
                lineRenderers = boxObj.GetComponentsInChildren<LineRenderer>(),
                label = labelObj != null ? labelObj.GetComponent<TextMeshPro>() : null,
                labelRectTransform = labelObj != null ? labelObj.GetComponent<RectTransform>() : null,
                lastUpdateTime = Time.time
            };

            return boxInstance;
        }

        #endregion

        private class BoundingBox3DInstance
        {
            public GameObject gameObject;
            public Transform transform;
            public LineRenderer[] lineRenderers;
            public TextMeshPro label;
            public RectTransform labelRectTransform;
            public float lastUpdateTime;  // NEW: Track last update
        }
    }
}