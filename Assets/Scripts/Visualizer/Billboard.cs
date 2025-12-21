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
        }

        private void LateUpdate()
        {
            if (mainCamera != null)
            {
                transform.rotation = mainCamera.transform.rotation;
            }
        }
    }
}