using UnityEngine;

namespace ARObjectDetection
{
    /// <summary>
    /// Utility class for converting OpenCV pose representations (rvec, tvec from solvePnP)
    /// to Unity Pose objects.
    /// 
    /// OpenCV camera coordinate system: +X right, +Y down, +Z forward (into scene)
    /// Unity camera coordinate system:  +X right, +Y up,   +Z forward (into scene)
    /// 
    /// IMPORTANT: Quest passthrough camera appears to have inverted Z convention
    /// compared to standard OpenCV, so we negate Z in the translation.
    /// </summary>
    public static class OpenCvPoseConversion
    {
        /// <summary>
        /// Convert OpenCV solvePnP output (rvec, tvec) to a Unity Pose representing
        /// the tag/marker pose in camera coordinates.
        /// </summary>
        public static Pose TagInCamera_FromOpenCvRvecTvec(float[] rvec, float[] tvec)
        {
            if (rvec == null || rvec.Length != 3 || tvec == null || tvec.Length != 3)
            {
                Debug.LogError("[OpenCvPoseConversion] Invalid rvec/tvec: expected length 3 arrays");
                return Pose.identity;
            }

            // === TRANSLATION ===
            // OpenCV: +X right, +Y down, +Z forward
            // Unity:  +X right, +Y up,   +Z forward
            // 
            // Standard conversion would be: flip Y only
            // BUT: Quest passthrough camera has inverted Z, so we also negate Z
            // This places markers IN FRONT of the camera (positive Z) instead of behind
            Vector3 translationUnity = new Vector3(
                tvec[0],      // X: same (right is right)
                -tvec[1],     // Y: flip (OpenCV down -> Unity up)
                -tvec[2]      // Z: NEGATE (fixes Quest passthrough camera convention)
            );

            // === ROTATION ===
            Quaternion rotationOpenCv = RodriguesToQuaternion(rvec);
            Quaternion rotationUnity = ConvertRotationOpenCvToUnity(rotationOpenCv);

            return new Pose(translationUnity, rotationUnity);
        }

        private static Quaternion RodriguesToQuaternion(float[] rvec)
        {
            Vector3 axis = new Vector3(rvec[0], rvec[1], rvec[2]);
            float angle = axis.magnitude;

            if (angle < 1e-6f)
            {
                return Quaternion.identity;
            }

            axis = axis / angle;
            float angleDegs = angle * Mathf.Rad2Deg;

            return Quaternion.AngleAxis(angleDegs, axis);
        }

        private static Quaternion ConvertRotationOpenCvToUnity(Quaternion rotOpenCv)
        {
            // For Y-axis flip: negate x and z components
            // For Z-axis flip: also need to adjust rotation
            // Combined Y and Z flip: negate x and y components, keep z and w
            return new Quaternion(
                -rotOpenCv.x,  // Flip
                -rotOpenCv.y,  // Flip (for Z axis inversion)
                rotOpenCv.z,   // Keep
                rotOpenCv.w    // Keep
            );
        }

        public static Pose InvertPose(Pose pose)
        {
            Quaternion invRot = Quaternion.Inverse(pose.rotation);
            Vector3 invPos = invRot * (-pose.position);
            return new Pose(invPos, invRot);
        }

        /// <summary>
        /// Compute world pose of tag.
        /// T_world_tag = T_world_camera * T_camera_tag
        /// </summary>
        public static Pose ComputeTagWorldPose(Pose tagInCamera, Pose cameraWorld)
        {
            Vector3 worldPos = cameraWorld.position + cameraWorld.rotation * tagInCamera.position;
            Quaternion worldRot = cameraWorld.rotation * tagInCamera.rotation;
            return new Pose(worldPos, worldRot);
        }

        /// <summary>
        /// Compute the SiteFrame world pose from a tag observation.
        /// </summary>
        public static Pose ComputeSiteFrameWorldPose(Pose tagInCamera, Pose cameraWorldPoseAtCapture)
        {
            return ComputeTagWorldPose(tagInCamera, cameraWorldPoseAtCapture);
        }
    }
}