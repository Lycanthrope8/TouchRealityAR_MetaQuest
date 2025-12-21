using UnityEngine;
using TMPro;
using System.Collections.Generic;
using PassthroughCameraSamples;

namespace ARObjectDetection
{
          /// <summary>
          /// Visualizes object detections as 3D bounding boxes in world space
          /// </summary>
          public class DetectionVisualizer3D : MonoBehaviour
          {
                    [Header("References")]
                    [SerializeField] private GameObject boundingBox3DPrefab;
                    [SerializeField] private DetectionConfig config;
                    [SerializeField] private PassthroughCameraEye cameraEye = PassthroughCameraEye.Left;

                    [Header("3D Settings")]
                    [SerializeField] private float defaultDepth = 2.0f; // Default depth in meters if no raycast hit
                    [SerializeField] private float boxThickness = 0.01f; // Thickness of box lines
                    [SerializeField] private LayerMask raycastLayers = ~0; // What to raycast against
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
                              // Pre-instantiate boxes for object pooling
                              for (int i = 0; i < 10; i++)
                              {
                                        CreatePooledBox();
                              }

                              // Get camera intrinsics
                              try
                              {
                                        cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(cameraEye);
                                        Debug.Log($"[3D Visualizer] Camera intrinsics loaded: {cameraIntrinsics.Value.Resolution}");
                              }
                              catch (System.Exception e)
                              {
                                        Debug.LogError($"[3D Visualizer] Failed to get camera intrinsics: {e.Message}");
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
                              if (response == null || response.detections == null || !config.show3DBoundingBoxes)
                                        return;

                              if (!cameraIntrinsics.HasValue)
                              {
                                        Debug.LogWarning("[3D Visualizer] Camera intrinsics not available");
                                        return;
                              }

                              ClearAllBoxes();

                              if (response.image_size == null || response.image_size.Length != 2)
                                        return;

                              int imageWidth = response.image_size[0];
                              int imageHeight = response.image_size[1];

                              foreach (var detection in response.detections)
                              {
                                        if (detection.bbox == null || detection.bbox.Length != 4)
                                                  continue;

                                        ShowBoundingBox3D(detection, imageWidth, imageHeight);
                              }
                    }

                    private void ShowBoundingBox3D(Detection detection, int imageWidth, int imageHeight)
                    {
                              // Get bounding box corners in image coordinates
                              float x1 = detection.bbox[0];
                              float y1 = detection.bbox[1];
                              float x2 = detection.bbox[2];
                              float y2 = detection.bbox[3];

                              // Calculate center point
                              Vector2Int centerPoint = new Vector2Int(
                                  Mathf.RoundToInt((x1 + x2) / 2f),
                                  Mathf.RoundToInt((y1 + y2) / 2f)
                              );

                              Debug.Log($"[3D Debug] {detection.class_name} | Image bbox:[{x1:F0},{y1:F0},{x2:F0},{y2:F0}] | Center pixel:[{centerPoint.x},{centerPoint.y}] | Image size:[{imageWidth}x{imageHeight}]");

                              // Get camera intrinsics info
                              PassthroughCameraIntrinsics intrinsics = cameraIntrinsics.Value;
                              Debug.Log($"[3D Debug] Camera intrinsics | Focal:[{intrinsics.FocalLength.x:F1},{intrinsics.FocalLength.y:F1}] | Principal:[{intrinsics.PrincipalPoint.x:F1},{intrinsics.PrincipalPoint.y:F1}] | Resolution:[{intrinsics.Resolution.x}x{intrinsics.Resolution.y}]");

                              // Convert to 3D ray in world space
                              Ray centerRay = PassthroughCameraUtils.ScreenPointToRayInWorld(cameraEye, centerPoint);

                              Debug.Log($"[3D Debug] Ray | Origin:[{centerRay.origin.x:F2},{centerRay.origin.y:F2},{centerRay.origin.z:F2}] | Direction:[{centerRay.direction.x:F2},{centerRay.direction.y:F2},{centerRay.direction.z:F2}]");

                              // Try to find actual depth via raycast
                              float depth = defaultDepth;
                              Vector3 worldPosition;
                              bool hitSomething = false;

                              if (Physics.Raycast(centerRay, out RaycastHit hit, maxRaycastDistance, raycastLayers))
                              {
                                        worldPosition = hit.point;
                                        depth = hit.distance;
                                        hitSomething = true;
                                        Debug.Log($"[3D Debug] Raycast HIT | Distance:{depth:F2}m | Hit point:[{worldPosition.x:F2},{worldPosition.y:F2},{worldPosition.z:F2}] | Hit object:{hit.collider.gameObject.name}");
                              }
                              else
                              {
                                        worldPosition = centerRay.origin + centerRay.direction * defaultDepth;
                                        Debug.Log($"[3D Debug] Raycast MISS | Using default depth:{defaultDepth}m | Position:[{worldPosition.x:F2},{worldPosition.y:F2},{worldPosition.z:F2}]");
                              }

                              // Calculate 3D box size based on image bbox size and depth
                              float pixelWidth = x2 - x1;
                              float pixelHeight = y2 - y1;

                              // Angular size to world size conversion
                              float worldWidth = (pixelWidth * depth) / intrinsics.FocalLength.x;
                              float worldHeight = (pixelHeight * depth) / intrinsics.FocalLength.y;

                              Debug.Log($"[3D Debug] Size calculation | Pixel size:[{pixelWidth:F0}x{pixelHeight:F0}] | Depth:{depth:F2}m | World size:[{worldWidth:F3}x{worldHeight:F3}]m");

                              // Get or create box from pool
                              BoundingBox3DInstance boxInstance = GetBoxFromPool();

                              // Position and size the box
                              boxInstance.transform.position = worldPosition;
                              boxInstance.transform.rotation = Quaternion.LookRotation(centerRay.direction);

                              Debug.Log($"[3D Debug] Box placed | Position:[{worldPosition.x:F2},{worldPosition.y:F2},{worldPosition.z:F2}] | Rotation:[{boxInstance.transform.rotation.eulerAngles.x:F0},{boxInstance.transform.rotation.eulerAngles.y:F0},{boxInstance.transform.rotation.eulerAngles.z:F0}]");

                              // Update the box visualization
                              UpdateBox3D(boxInstance, worldWidth, worldHeight, detection);

                              boxInstance.gameObject.SetActive(true);
                              boxInstance.spawnTime = Time.time;
                              activeBoxes.Add(boxInstance);

                              Debug.Log($"[3D Debug] ===== Box created for {detection.class_name} =====\n");
                    }

                    private void UpdateBox3D(BoundingBox3DInstance boxInstance, float width, float height, Detection detection)
                    {
                              // Update line renderers to form a box
                              Vector3 size = new Vector3(width, height, boxThickness);

                              // Draw 12 edges of a box
                              Vector3 halfSize = size / 2f;

                              // Front face (4 edges)
                              DrawBoxEdge(boxInstance.lineRenderers[0], new Vector3(-halfSize.x, -halfSize.y, 0), new Vector3(halfSize.x, -halfSize.y, 0));
                              DrawBoxEdge(boxInstance.lineRenderers[1], new Vector3(halfSize.x, -halfSize.y, 0), new Vector3(halfSize.x, halfSize.y, 0));
                              DrawBoxEdge(boxInstance.lineRenderers[2], new Vector3(halfSize.x, halfSize.y, 0), new Vector3(-halfSize.x, halfSize.y, 0));
                              DrawBoxEdge(boxInstance.lineRenderers[3], new Vector3(-halfSize.x, halfSize.y, 0), new Vector3(-halfSize.x, -halfSize.y, 0));

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

                                        // Position label above box
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
                              else
                              {
                                        return CreatePooledBox();
                              }
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
                                        // Use prefab
                                        boxObj = Instantiate(boundingBox3DPrefab, transform);
                              }
                              else
                              {
                                        // Fallback: Create procedurally if no prefab provided
                                        Debug.LogWarning("[3D Visualizer] No prefab assigned, creating procedural box");
                                        boxObj = new GameObject("BoundingBox3D");
                                        boxObj.transform.SetParent(transform);

                                        // Create 4 line renderers for the 4 edges of the front face
                                        for (int i = 0; i < 4; i++)
                                        {
                                                  GameObject lineObj = new GameObject($"Edge_{i}");
                                                  lineObj.transform.SetParent(boxObj.transform);

                                                  LineRenderer lr = lineObj.AddComponent<LineRenderer>();
                                                  lr.startWidth = 0.01f;
                                                  lr.endWidth = 0.01f;
                                                  lr.material = new Material(Shader.Find("Sprites/Default"));
                                                  lr.startColor = Color.red;
                                                  lr.endColor = Color.red;
                                                  lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                                                  lr.receiveShadows = false;
                                                  lr.useWorldSpace = false;
                                        }

                                        // Create label
                                        GameObject labelObj = new GameObject("Label");
                                        labelObj.transform.SetParent(boxObj.transform);

                                        TextMeshPro tmp = labelObj.AddComponent<TextMeshPro>();
                                        tmp.fontSize = 0.2f;
                                        tmp.alignment = TextAlignmentOptions.Center;
                                        tmp.color = Color.white;

                                        // Make label face camera
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

                              // Validate that we have at least 4 line renderers
                              if (boxInstance.lineRenderers == null || boxInstance.lineRenderers.Length < 4)
                              {
                                        Debug.LogError("[3D Visualizer] Prefab must have at least 4 LineRenderer components as children!");
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