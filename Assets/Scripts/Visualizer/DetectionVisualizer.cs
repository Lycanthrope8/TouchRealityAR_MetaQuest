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
                    [SerializeField] private float boxLifetime = 0.5f; // How long boxes stay visible

                    // Pool of bounding box objects
                    private List<BoundingBoxUI> activeBoxes = new List<BoundingBoxUI>();
                    private Queue<BoundingBoxUI> boxPool = new Queue<BoundingBoxUI>();

                    private RectTransform canvasRect;
                    private Vector2 canvasSize;

                    private void Awake()
                    {
                              if (detectionCanvas != null)
                              {
                                        canvasRect = detectionCanvas.GetComponent<RectTransform>();
                              }

                              // Pre-instantiate some boxes for object pooling
                              for (int i = 0; i < 10; i++)
                              {
                                        CreatePooledBox();
                              }
                    }

                    private void Update()
                    {
                              // Update canvas size (in case it changes)
                              if (canvasRect != null)
                              {
                                        canvasSize = canvasRect.rect.size; // Use rect.size instead of sizeDelta
                              }

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

                    /// <summary>
                    /// Display detections on canvas
                    /// </summary>
                    public void ShowDetections(DetectionResponse response)
                    {
                              if (response == null || response.detections == null || !config.showBoundingBoxes)
                                        return;

                              // Clear previous boxes
                              ClearAllBoxes();

                              // Get image dimensions from response
                              if (response.image_size == null || response.image_size.Length != 2)
                                        return;

                              int imageWidth = response.image_size[0];
                              int imageHeight = response.image_size[1];

                              // Display each detection
                              foreach (var detection in response.detections)
                              {
                                        if (detection.bbox == null || detection.bbox.Length != 4)
                                                  continue;

                                        ShowBoundingBox(detection, imageWidth, imageHeight);
                              }
                    }

                    private void ShowBoundingBox(Detection detection, int imageWidth, int imageHeight)
                    {
                              // Get or create box from pool
                              BoundingBoxUI boxUI = GetBoxFromPool();

                              // Convert detection bbox from image coordinates to canvas coordinates
                              Rect bbox = ConvertToCanvasRect(detection.bbox, imageWidth, imageHeight);

                              // Position at center, set size
                              boxUI.rectTransform.anchoredPosition = new Vector2(bbox.center.x, bbox.center.y);
                              boxUI.rectTransform.sizeDelta = new Vector2(bbox.width, bbox.height);

                              Debug.Log($"BBOX: {detection.class_name} | Img:[{detection.bbox[0]:F0},{detection.bbox[1]:F0},{detection.bbox[2]:F0},{detection.bbox[3]:F0}] -> Canvas pos:({bbox.center.x:F0},{bbox.center.y:F0}) size:({bbox.width:F0}x{bbox.height:F0})");

                              // Colors are set in the prefab - no override here

                              // Set label text
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

                    /// <summary>
                    /// Convert detection bbox coordinates (image space) to canvas coordinates
                    /// Detection bbox: [x1, y1, x2, y2] in image pixels
                    /// Canvas: center-based coordinate system
                    /// </summary>
                    private Rect ConvertToCanvasRect(float[] bbox, int imageWidth, int imageHeight)
                    {
                              // Calculate aspect ratios
                              float imageAspect = (float)imageWidth / imageHeight;
                              float canvasAspect = canvasSize.x / canvasSize.y;

                              // Normalize bbox to 0-1 range
                              float x1_norm = bbox[0] / imageWidth;
                              float y1_norm = bbox[1] / imageHeight;
                              float x2_norm = bbox[2] / imageWidth;
                              float y2_norm = bbox[3] / imageHeight;

                              // Account for aspect ratio difference
                              float scaleX = canvasSize.x;
                              float scaleY = canvasSize.y;

                              if (imageAspect > canvasAspect)
                              {
                                        // Image is wider - fit to width, letterbox top/bottom
                                        scaleY = canvasSize.x / imageAspect;
                              }
                              else
                              {
                                        // Image is taller - fit to height, pillarbox left/right
                                        scaleX = canvasSize.y * imageAspect;
                              }

                              // Convert to canvas coordinates (center is 0,0)
                              float canvasX1 = (x1_norm - 0.5f) * scaleX;
                              float canvasY1 = (0.5f - y1_norm) * scaleY; // Flip Y axis
                              float canvasX2 = (x2_norm - 0.5f) * scaleX;
                              float canvasY2 = (0.5f - y2_norm) * scaleY; // Flip Y axis

                              float width = canvasX2 - canvasX1;
                              float height = canvasY1 - canvasY2; // Note: reversed because Y is flipped

                              // Calculate center position
                              float centerX = (canvasX1 + canvasX2) / 2f;
                              float centerY = (canvasY1 + canvasY2) / 2f;

                              return new Rect(centerX - width / 2f, centerY - height / 2f, width, height);
                    }

                    /// <summary>
                    /// Clear all active bounding boxes
                    /// </summary>
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

                              // Set anchors to center for easier positioning
                              boxUI.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
                              boxUI.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                              boxUI.rectTransform.pivot = new Vector2(0.5f, 0.5f);

                              return boxUI;
                    }

                    #endregion

                    /// <summary>
                    /// Helper class to store UI components
                    /// </summary>
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