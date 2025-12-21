using UnityEngine;
using TMPro;
using System.Collections.Generic;
using PassthroughCameraSamples;

namespace ARObjectDetection
{
    /// <summary>
    /// Visualizes object detections as 3D bounding boxes in world space
    /// Following Meta's PassthroughCameraApiSamples approach
    /// Expects bboxes in same resolution as camera intrinsics (1280x1280)
    /// </summary>
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

        [Header("Performance")]
        [SerializeField] private float boxLifetime = 0.5f;

        // Pool of bounding box objects
        private List<BoundingBox3DInstance> activeBoxes = new List<BoundingBox3DInstance>();
        private Queue<BoundingBox3DInstance> boxPool = new Queue<BoundingBox3DInstance>();

        // Camera intrinsics
        private PassthroughCameraIntrinsics? cameraIntrinsics;

        private void Awake()
        {
            // Pre-instantiate boxes
            for (int i = 0; i < 10; i++)
            {
                CreatePooledBox();
            }

            // Get camera intrinsics
            try
            {
                cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(cameraEye);
                Debug.Log($"[3D Visualizer] Camera intrinsics: Resolution={cameraIntrinsics.Value.Resolution}, " +
                         $"Focal=({cameraIntrinsics.Value.FocalLength.x:F1}, {cameraIntrinsics.Value.FocalLength.y:F1}), " +
                         $"Principal=({cameraIntrinsics.Value.PrincipalPoint.x:F1}, {cameraIntrinsics.Value.PrincipalPoint.y:F1})");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[3D Visualizer] Failed to get camera intrinsics: {e.Message}");
            }
        }

        private void Update()
        {
            // Fade out old boxes
            for (int i = activeBoxes.Count - 1; i >= 0; i--)
            {
                var box = activeBoxes[i];
                if (Time.time - box.spawnTime > boxLifetime)
                {
                    ReturnBoxToPool(box);
                    activeBoxes.RemoveAt(i);
                }
            }
        }

        public void ShowDetections(DetectionResponse response)
        {
            if (response == null || response.detections == null || !config.show3DBoundingBoxes)
                return;

            if (!cameraIntrinsics.HasValue)
            {
                Debug.LogWarning("[3D Visualizer] Camera intrinsics not available");
                return;
            }

            ClearAllBoxes();

            if (response.image_size == null || response.image_size.Length != 2)
            {
                Debug.LogError("[3D Visualizer] Invalid image_size in response");
                return;
            }

            int imageWidth = response.image_size[0];
            int imageHeight = response.image_size[1];

            PassthroughCameraIntrinsics intrinsics = cameraIntrinsics.Value;

            // CRITICAL: Image dimensions MUST match camera intrinsics
            if (imageWidth != intrinsics.Resolution.x || imageHeight != intrinsics.Resolution.y)
            {
                Debug.LogError($"[3D Visualizer] RESOLUTION MISMATCH! " +
                              $"Image: {imageWidth}×{imageHeight} vs " +
                              $"Camera: {intrinsics.Resolution.x}×{intrinsics.Resolution.y}\n" +
                              $"Unity is NOT sending full camera resolution to server!\n" +
                              $"Check FrameCaptureService - it should NOT resize images.");
                return;
            }

            Debug.Log($"[3D Visualizer] Processing {response.count} detections from {imageWidth}×{imageHeight}");

            foreach (var detection in response.detections)
            {
                if (detection.bbox == null || detection.bbox.Length != 4)
                    continue;

                ShowBoundingBox3D(detection, imageWidth, imageHeight);
            }
        }

        private void ShowBoundingBox3D(Detection detection, int imageWidth, int imageHeight)
        {
            PassthroughCameraIntrinsics intrinsics = cameraIntrinsics.Value;

            // Bbox coordinates (already in camera resolution space)
            float x1 = detection.bbox[0];
            float y1 = detection.bbox[1];
            float x2 = detection.bbox[2];
            float y2 = detection.bbox[3];

            // FLIP Y-AXIS: Camera Y is inverted (0 at top, height at bottom)
            // We need to flip it for ScreenPointToRayInWorld
            y1 = imageHeight - y1;
            y2 = imageHeight - y2;

            // Calculate center point (with flipped Y)
            Vector2Int centerPoint = new Vector2Int(
                Mathf.RoundToInt((x1 + x2) / 2f),
                Mathf.RoundToInt((y1 + y2) / 2f)
            );

            Debug.Log($"[3D] {detection.class_name} | " +
                     $"Bbox:[{detection.bbox[0]:F0},{detection.bbox[1]:F0} → {detection.bbox[2]:F0},{detection.bbox[3]:F0}] (original) | " +
                     $"Flipped Y:[{x1:F0},{y1:F0} → {x2:F0},{y2:F0}] | " +
                     $"Center:[{centerPoint.x},{centerPoint.y}] | " +
                     $"Size:[{x2 - x1:F0}×{Mathf.Abs(y2 - y1):F0}]px");

            // Convert pixel coordinates to world ray
            // This is the key Meta API call
            Ray centerRay = PassthroughCameraUtils.ScreenPointToRayInWorld(cameraEye, centerPoint);

            Debug.Log($"[3D] Ray origin:[{centerRay.origin.x:F3},{centerRay.origin.y:F3},{centerRay.origin.z:F3}] " +
                     $"dir:[{centerRay.direction.x:F3},{centerRay.direction.y:F3},{centerRay.direction.z:F3}]");

            // Try raycast for actual depth
            float depth = defaultDepth;
            Vector3 worldPosition;

            if (Physics.Raycast(centerRay, out RaycastHit hit, maxRaycastDistance, raycastLayers))
            {
                worldPosition = hit.point;
                depth = hit.distance;
                Debug.Log($"[3D] Raycast HIT → {hit.collider.name} at {depth:F2}m");
            }
            else
            {
                worldPosition = centerRay.origin + centerRay.direction * defaultDepth;
                Debug.Log($"[3D] Raycast MISS → using default depth {defaultDepth}m");
            }

            // Calculate 3D box size from pixel size and depth
            // Using pinhole camera model: world_size = (pixel_size * depth) / focal_length
            float pixelWidth = x2 - x1;
            float pixelHeight = Mathf.Abs(y2 - y1);  // Abs because y2 might be < y1 after flip

            float worldWidth = (pixelWidth * depth) / intrinsics.FocalLength.x;
            float worldHeight = (pixelHeight * depth) / intrinsics.FocalLength.y;

            Debug.Log($"[3D] World size: {worldWidth:F3}×{worldHeight:F3}m at {depth:F2}m depth");

            // Create and position box
            BoundingBox3DInstance boxInstance = GetBoxFromPool();

            boxInstance.transform.position = worldPosition;
            boxInstance.transform.rotation = Quaternion.LookRotation(centerRay.direction);

            // Draw the box
            UpdateBox3D(boxInstance, worldWidth, worldHeight, detection);

            boxInstance.gameObject.SetActive(true);
            boxInstance.spawnTime = Time.time;
            activeBoxes.Add(boxInstance);

            Debug.Log($"[3D] Box created for {detection.class_name}\n");
        }

        private void UpdateBox3D(BoundingBox3DInstance boxInstance, float width, float height, Detection detection)
        {
            Vector3 halfSize = new Vector3(width / 2f, height / 2f, boxThickness / 2f);

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

            // Update label
            if (boxInstance.label != null && config.showLabels)
            {
                string labelText = detection.class_name;
                if (config.showConfidence)
                {
                    labelText += $" {detection.confidence:F2}";
                }
                boxInstance.label.text = labelText;
                boxInstance.label.gameObject.SetActive(true);
                boxInstance.label.transform.localPosition = new Vector3(0, halfSize.y + 0.05f, 0);
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
            for (int i = activeBoxes.Count - 1; i >= 0; i--)
            {
                ReturnBoxToPool(activeBoxes[i]);
            }
            activeBoxes.Clear();
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
            boxPool.Enqueue(box);
        }

        private BoundingBox3DInstance CreatePooledBox()
        {
            GameObject boxObj;

            if (boundingBox3DPrefab != null)
            {
                boxObj = Instantiate(boundingBox3DPrefab, transform);
            }
            else
            {
                Debug.LogWarning("[3D Visualizer] No prefab, creating procedural box");
                boxObj = new GameObject("BoundingBox3D");
                boxObj.transform.SetParent(transform);

                // Create 4 line renderers for box edges
                for (int i = 0; i < 4; i++)
                {
                    GameObject lineObj = new GameObject($"Edge_{i}");
                    lineObj.transform.SetParent(boxObj.transform);

                    LineRenderer lr = lineObj.AddComponent<LineRenderer>();
                    lr.startWidth = 0.01f;
                    lr.endWidth = 0.01f;
                    lr.material = new Material(Shader.Find("Sprites/Default"));
                    lr.startColor = Color.green;
                    lr.endColor = Color.green;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    lr.useWorldSpace = false;
                }

                // Create label
                GameObject labelObj = new GameObject("Label");
                labelObj.transform.SetParent(boxObj.transform);

                TextMeshPro tmp = labelObj.AddComponent<TextMeshPro>();
                tmp.fontSize = 2.0f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;

                labelObj.AddComponent<Billboard>();
            }

            boxObj.SetActive(false);

            BoundingBox3DInstance boxInstance = new BoundingBox3DInstance
            {
                gameObject = boxObj,
                transform = boxObj.transform,
                lineRenderers = boxObj.GetComponentsInChildren<LineRenderer>(),
                label = boxObj.GetComponentInChildren<TextMeshPro>()
            };

            if (boxInstance.lineRenderers == null || boxInstance.lineRenderers.Length < 4)
            {
                Debug.LogError("[3D Visualizer] Prefab needs 4+ LineRenderers!");
            }

            return boxInstance;
        }

        #endregion

        private class BoundingBox3DInstance
        {
            public GameObject gameObject;
            public Transform transform;
            public LineRenderer[] lineRenderers;
            public TextMeshPro label;
            public float spawnTime;
        }
    }
}