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
        /// 
        /// CRITICAL FIX: Use standard OpenCV→Unity conversion (keep +Z forward).
        /// Previous negation of Z was incorrect and caused tags to be placed behind camera.
        /// </summary>
        public static Pose TagInCamera_FromOpenCvRvecTvec(float[] rvec, float[] tvec)
        {
            if (rvec == null || rvec.Length != 3 || tvec == null || tvec.Length != 3)
            {
                Debug.LogError("[OpenCvPoseConversion] Invalid rvec/tvec: expected length 3 arrays");
                return Pose.identity;
            }

            // === TRANSLATION ===
            // OpenCV: +X right, +Y down, +Z forward (into scene)
            // Unity:  +X right, +Y up,   +Z forward (into scene)
            // 
            // STANDARD CONVERSION: Only flip Y axis
            // DO NOT negate Z - this would place markers behind camera!
            Vector3 translationUnity = new Vector3(
                tvec[0],      // X: same (right is right)
                -tvec[1],     // Y: flip (OpenCV down -> Unity up)
                tvec[2]       // Z: KEEP POSITIVE (forward is forward)
            );

            // === ROTATION ===
            Quaternion rotationOpenCv = RodriguesToQuaternion(rvec);
            Quaternion rotationUnity = ConvertRotationOpenCvToUnity(rotationOpenCv);

            // === SANITY CHECK ===
            // Tag pose in camera space MUST have positive Z (in front of camera)
            if (translationUnity.z < 0.01f)
            {
                Debug.LogWarning($"[OpenCvPoseConversion] Tag has negative/zero Z in camera space: {translationUnity.z:F3}. " +
                               $"This means tag is BEHIND camera - likely a pose computation error. " +
                               $"Raw tvec=[{tvec[0]:F3}, {tvec[1]:F3}, {tvec[2]:F3}]");
            }

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
            // For Y-axis flip only (standard OpenCV→Unity):
            // Negate x and z components
            return new Quaternion(
                -rotOpenCv.x,  // Flip
                rotOpenCv.y,   // Keep
                -rotOpenCv.z,  // Flip
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