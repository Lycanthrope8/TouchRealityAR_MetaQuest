using UnityEngine;
using System.Collections.Generic;

namespace ARObjectDetection
{
    /// <summary>
    /// Test script to verify SiteFrame coordinate conversions are working.
    /// Spawns test objects at known positions relative to the SiteFrame origin (marker).
    /// 
    /// Usage:
    /// 1. Attach to any GameObject in scene
    /// 2. Assign siteFrameManager reference
    /// 3. Run app, point at marker until SiteFrame locks
    /// 4. Press trigger (or call SpawnTestObjects()) to spawn cubes
    /// 5. Cubes should appear at predictable positions relative to marker
    /// </summary>
    public class SiteFrameTest : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private SiteFrameManager siteFrameManager;

        [Header("Test Settings")]
        [SerializeField] private bool spawnOnTrigger = true;
        [SerializeField] private OVRInput.Button spawnButton = OVRInput.Button.PrimaryIndexTrigger;
        [SerializeField] private float cubeSize = 0.05f; // 5cm cubes

        [Header("Debug")]
        [SerializeField] private bool logConversions = true;

        private List<GameObject> spawnedObjects = new List<GameObject>();
        private bool hasSpawned = false;

        void Update()
        {
            // Spawn test objects on trigger press when SiteFrame is valid
            if (spawnOnTrigger && !hasSpawned && siteFrameManager != null && siteFrameManager.IsValid)
            {
                if (OVRInput.GetDown(spawnButton))
                {
                    SpawnTestObjects();
                    hasSpawned = true;
                }
            }

            // Destroy cubes ONLY when SiteFrame becomes truly invalid
            // (e.g., XR tracking lost, explicit invalidation)
            // With persistence enabled, this should rarely happen during normal use
            if (siteFrameManager != null && !siteFrameManager.IsValid && hasSpawned)
            {
                Debug.Log("[SiteFrameTest] SiteFrame invalidated - destroying test objects");
                ClearTestObjects();
            }

            // Periodic diagnostic log
            if (Time.frameCount % 300 == 0 && siteFrameManager != null)
            {
                Debug.Log($"[SiteFrameTest] Diag: valid={siteFrameManager.IsValid}, hasSpawned={hasSpawned}, " +
                         $"cubeCount={spawnedObjects.Count}, lastSeen={siteFrameManager.SecondsSinceLastSeen:F1}s ago");
            }
        }

        /// <summary>
        /// Spawns test cubes at known SiteFrame positions to verify coordinate system.
        /// Call this when SiteFrame is valid.
        /// </summary>
        [ContextMenu("Spawn Test Objects")]
        public void SpawnTestObjects()
        {
            if (siteFrameManager == null)
            {
                Debug.LogError("[SiteFrameTest] SiteFrameManager not assigned!");
                return;
            }

            if (!siteFrameManager.IsValid)
            {
                Debug.LogWarning("[SiteFrameTest] SiteFrame not valid yet. Point at marker first.");
                return;
            }

            // Clear previous test objects
            ClearTestObjects();

            Debug.Log("[SiteFrameTest] === SPAWNING TEST OBJECTS ===");
            Debug.Log($"[SiteFrameTest] SiteFrame WorldFromSite: pos={siteFrameManager.WorldFromSite.position}, rot={siteFrameManager.WorldFromSite.rotation.eulerAngles}");

            // Define test positions in SITE coordinates (relative to marker)
            // These are positions where we EXPECT the cubes to appear relative to the marker
            var testPositions = new (Vector3 sitePos, Color color, string label)[]
            {
                // Origin - should appear exactly at the marker
                (new Vector3(0, 0, 0), Color.white, "ORIGIN (at marker)"),
                
                // X axis - should appear to the RIGHT of marker (when facing it)
                (new Vector3(0.2f, 0, 0), Color.red, "X+ (20cm right)"),
                (new Vector3(-0.2f, 0, 0), Color.red * 0.5f, "X- (20cm left)"),
                
                // Y axis - should appear ABOVE/BELOW marker
                (new Vector3(0, 0.2f, 0), Color.green, "Y+ (20cm up)"),
                (new Vector3(0, -0.2f, 0), Color.green * 0.5f, "Y- (20cm down)"),
                
                // Z axis - should appear IN FRONT/BEHIND marker
                (new Vector3(0, 0, 0.2f), Color.blue, "Z+ (20cm forward)"),
                (new Vector3(0, 0, -0.2f), Color.blue * 0.5f, "Z- (20cm back)"),
                
                // Combined - should appear up and to the right of marker
                (new Vector3(0.15f, 0.15f, 0), Color.yellow, "XY+ (15cm right+up)"),
            };

            foreach (var (sitePos, color, label) in testPositions)
            {
                // Convert site position to world position
                Pose sitePose = new Pose(sitePos, Quaternion.identity);
                Pose worldPose = siteFrameManager.WorldPoseFromSitePose(sitePose);

                // Create cube at world position
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = $"SiteFrameTest_{label}";
                cube.transform.position = worldPose.position;
                cube.transform.rotation = worldPose.rotation;
                cube.transform.localScale = Vector3.one * cubeSize;

                // Color it
                var renderer = cube.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    renderer.material.color = color;
                }

                // Remove collider to avoid physics issues
                var collider = cube.GetComponent<Collider>();
                if (collider != null) Destroy(collider);

                spawnedObjects.Add(cube);

                if (logConversions)
                {
                    Debug.Log($"[SiteFrameTest] {label}: site={sitePos} -> world={worldPose.position}");
                }
            }

            Debug.Log($"[SiteFrameTest] Spawned {spawnedObjects.Count} test objects");
            Debug.Log("[SiteFrameTest] White cube should be AT the marker");
            Debug.Log("[SiteFrameTest] Red cubes should be LEFT/RIGHT of marker");
            Debug.Log("[SiteFrameTest] Green cubes should be ABOVE/BELOW marker");
            Debug.Log("[SiteFrameTest] Blue cubes should be IN FRONT/BEHIND marker");
        }

        /// <summary>
        /// Test the reverse conversion: world position to site position.
        /// </summary>
        [ContextMenu("Test Reverse Conversion")]
        public void TestReverseConversion()
        {
            if (siteFrameManager == null || !siteFrameManager.IsValid)
            {
                Debug.LogWarning("[SiteFrameTest] SiteFrame not valid");
                return;
            }

            // Get current head position in world
            Vector3 headWorldPos = Camera.main.transform.position;

            // Convert to site coordinates
            Pose worldPose = new Pose(headWorldPos, Camera.main.transform.rotation);
            Pose sitePose = siteFrameManager.SitePoseFromWorldPose(worldPose);

            Debug.Log($"[SiteFrameTest] Head world: {headWorldPos}");
            Debug.Log($"[SiteFrameTest] Head in site coords: {sitePose.position}");
            Debug.Log($"[SiteFrameTest] This tells you where your head is relative to the marker");
        }

        [ContextMenu("Clear Test Objects")]
        public void ClearTestObjects()
        {
            foreach (var obj in spawnedObjects)
            {
                if (obj != null) Destroy(obj);
            }
            spawnedObjects.Clear();
            hasSpawned = false;
        }

        void OnDestroy()
        {
            ClearTestObjects();
        }

        void OnGUI()
        {
            if (siteFrameManager == null) return;

            GUILayout.BeginArea(new Rect(10, 450, 400, 150));
            GUILayout.Label("=== SITEFRAME TEST ===");
            GUILayout.Label($"SiteFrame Valid: {siteFrameManager.IsValid}");

            if (siteFrameManager.IsValid)
            {
                GUILayout.Label($"Position: {siteFrameManager.WorldFromSite.position:F2}");
                GUILayout.Label($"Test objects: {spawnedObjects.Count}");
                GUILayout.Label("Press TRIGGER to spawn test cubes");

                // Test: where is head in site coords?
                if (Camera.main != null)
                {
                    Pose headSite = siteFrameManager.SitePoseFromWorldPose(
                        new Pose(Camera.main.transform.position, Camera.main.transform.rotation));
                    GUILayout.Label($"Head in site coords: {headSite.position:F2}");
                }
            }
            else
            {
                GUILayout.Label("Point at marker to lock SiteFrame");
            }
            GUILayout.EndArea();
        }
    }
}