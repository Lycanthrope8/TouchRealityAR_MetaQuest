using UnityEngine;

namespace ARObjectDetection
{
    /// <summary>
    /// Makes object always face the camera
    /// </summary>
    public class Billboard : MonoBehaviour
    {
        private Camera mainCamera;

        private void Start()
        {
            mainCamera = Camera.main;

            if (mainCamera == null)
            {
                Debug.LogWarning("[Billboard] Camera.main not found! Looking for active camera...");
                mainCamera = Camera.current;
            }

            if (mainCamera == null)
            {
                Debug.LogError("[Billboard] No camera found!");
            }
        }

        private void LateUpdate()
        {
            if (mainCamera == null)
            {
                // Try to find camera again if it was null
                mainCamera = Camera.main;
                if (mainCamera == null)
                    return;
            }

            // Make the label face the camera
            transform.rotation = mainCamera.transform.rotation;

            // Alternative approach - look at camera (uncomment if above doesn't work)
            // transform.LookAt(transform.position + mainCamera.transform.rotation * Vector3.forward,
            //                  mainCamera.transform.rotation * Vector3.up);
        }
    }
}