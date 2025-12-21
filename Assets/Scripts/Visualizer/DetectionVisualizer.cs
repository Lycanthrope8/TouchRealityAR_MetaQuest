using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

namespace ARObjectDetection
{
    /// <summary>
    /// Visualizes object detections as 2D bounding boxes on canvas
    /// </summary>
    public class DetectionVisualizer : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Canvas detectionCanvas;
        [SerializeField] private GameObject boundingBoxPrefab;
        [SerializeField] private DetectionConfig config;

        [Header("Settings")]
        [SerializeField] private float boxLifetime = 0.5f;

        // Pool of bounding box objects
        private List<BoundingBoxUI> activeBoxes = new List<BoundingBoxUI>();
        private Queue<BoundingBoxUI> boxPool = new Queue<BoundingBoxUI>();

        private RectTransform canvasRect;
        private Vector2 referenceResolution = new Vector2(1920, 1080);

        private void Awake()
        {
            if (detectionCanvas != null)
            {
                canvasRect = detectionCanvas.GetComponent<RectTransform>();

                // Get reference resolution from Canvas Scaler
                var canvasScaler = detectionCanvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                if (canvasScaler != null)
                {
                    referenceResolution = canvasScaler.referenceResolution;
                    Debug.Log($"[DetectionVisualizer] Using Canvas Scaler reference resolution: {referenceResolution.x}x{referenceResolution.y}");
                }
                else
                {
                    Debug.LogWarning("[DetectionVisualizer] No Canvas Scaler found, using default 1920x1080");
                }
            }

            // Pre-instantiate boxes for object pooling
            for (int i = 0; i < 10; i++)
            {
                CreatePooledBox();
            }
        }

        private void Update()
        {
            // Fade out and deactivate old boxes
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
            if (response == null || response.detections == null || !config.show2DBoundingBoxes)
                return;

            ClearAllBoxes();

            if (response.image_size == null || response.image_size.Length != 2)
                return;

            int imageWidth = response.image_size[0];
            int imageHeight = response.image_size[1];

            foreach (var detection in response.detections)
            {
                if (detection.bbox == null || detection.bbox.Length != 4)
                    continue;

                ShowBoundingBox(detection, imageWidth, imageHeight);
            }
        }

        private void ShowBoundingBox(Detection detection, int imageWidth, int imageHeight)
        {
            BoundingBoxUI boxUI = GetBoxFromPool();

            Rect bbox = ConvertToCanvasRect(detection.bbox, imageWidth, imageHeight);

            boxUI.rectTransform.anchoredPosition = new Vector2(bbox.center.x, bbox.center.y);
            boxUI.rectTransform.sizeDelta = new Vector2(bbox.width, bbox.height);

            if (boxUI.label != null && config.showLabels)
            {
                string labelText = detection.class_name;
                if (config.showConfidence)
                {
                    labelText += $" {detection.confidence:F2}";
                }
                boxUI.label.text = labelText;
                boxUI.label.gameObject.SetActive(true);
            }
            else if (boxUI.label != null)
            {
                boxUI.label.gameObject.SetActive(false);
            }

            boxUI.gameObject.SetActive(true);
            boxUI.spawnTime = Time.time;
            activeBoxes.Add(boxUI);
        }

        private Rect ConvertToCanvasRect(float[] bbox, int imageWidth, int imageHeight)
        {
            float imageAspect = (float)imageWidth / imageHeight;
            float canvasAspect = referenceResolution.x / referenceResolution.y;

            float displayWidth, displayHeight;

            if (imageAspect > canvasAspect)
            {
                displayWidth = referenceResolution.x;
                displayHeight = referenceResolution.x / imageAspect;
            }
            else
            {
                displayHeight = referenceResolution.y;
                displayWidth = referenceResolution.y * imageAspect;
            }

            float x1_norm = bbox[0] / imageWidth;
            float y1_norm = bbox[1] / imageHeight;
            float x2_norm = bbox[2] / imageWidth;
            float y2_norm = bbox[3] / imageHeight;

            float centerX_norm = (x1_norm + x2_norm) / 2f;
            float centerY_norm = (y1_norm + y2_norm) / 2f;
            float width_norm = x2_norm - x1_norm;
            float height_norm = y2_norm - y1_norm;

            float canvasCenterX = (centerX_norm - 0.5f) * displayWidth;
            float canvasCenterY = (0.5f - centerY_norm) * displayHeight;

            float canvasWidth = width_norm * displayWidth;
            float canvasHeight = height_norm * displayHeight;

            return new Rect(
                canvasCenterX - canvasWidth / 2f,
                canvasCenterY - canvasHeight / 2f,
                canvasWidth,
                canvasHeight
            );
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

        private BoundingBoxUI GetBoxFromPool()
        {
            if (boxPool.Count > 0)
            {
                return boxPool.Dequeue();
            }
            else
            {
                return CreatePooledBox();
            }
        }

        private void ReturnBoxToPool(BoundingBoxUI box)
        {
            box.gameObject.SetActive(false);
            boxPool.Enqueue(box);
        }

        private BoundingBoxUI CreatePooledBox()
        {
            if (boundingBoxPrefab == null || detectionCanvas == null)
            {
                Debug.LogError("BoundingBoxPrefab or DetectionCanvas not assigned!");
                return null;
            }

            GameObject boxObj = Instantiate(boundingBoxPrefab, detectionCanvas.transform);
            boxObj.SetActive(false);

            BoundingBoxUI boxUI = new BoundingBoxUI
            {
                gameObject = boxObj,
                rectTransform = boxObj.GetComponent<RectTransform>(),
                image = boxObj.GetComponent<Image>(),
                outline = boxObj.GetComponent<Outline>(),
                label = boxObj.GetComponentInChildren<TextMeshProUGUI>()
            };

            boxUI.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            boxUI.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            boxUI.rectTransform.pivot = new Vector2(0.5f, 0.5f);

            return boxUI;
        }

        #endregion

        private class BoundingBoxUI
        {
            public GameObject gameObject;
            public RectTransform rectTransform;
            public Image image;
            public Outline outline;
            public TextMeshProUGUI label;
            public float spawnTime;
        }
    }
}