using UnityEngine;

namespace ARObjectDetection
{
    public class Billboard : MonoBehaviour
    {
        private Camera mainCamera;

        void Start()
        {
            mainCamera = Camera.main;
        }

        void LateUpdate()
        {
            if (mainCamera == null)
            {
                mainCamera = Camera.main;
                if (mainCamera == null) return;
            }

            Vector3 toCam = mainCamera.transform.position - transform.position;
            if (toCam.sqrMagnitude < 1e-6f) return;

            // Upright billboard (no roll)
            transform.rotation = Quaternion.LookRotation(toCam, Vector3.up);

            // Fix mirrored/inverted text (common with TMP 3D)
            transform.rotation *= Quaternion.Euler(0f, 180f, 0f);
        }
    }
}
