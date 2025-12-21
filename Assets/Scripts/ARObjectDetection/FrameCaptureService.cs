using UnityEngine;
using System;

namespace ARObjectDetection
{
    /// <summary>
    /// Captures frames from WebCamTexture WITHOUT RESIZING
    /// Following Meta's approach - send full resolution to server
    /// </summary>
    public class FrameCaptureService
    {
        private DetectionConfig config;
        private int frameCounter = 0;
        private Texture2D processingTexture;

        public FrameCaptureService(DetectionConfig config)
        {
            this.config = config;
        }

        /// <summary>
        /// Check if this frame should be captured based on interval
        /// </summary>
        public bool ShouldCaptureFrame()
        {
            frameCounter++;
            if (frameCounter >= config.frameCaptureInterval)
            {
                frameCounter = 0;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Capture and encode frame WITHOUT resizing
        /// Meta's approach: Send full camera resolution (1280x1280)
        /// </summary>
        public byte[] CaptureFrame(WebCamTexture webCamTexture)
        {
            if (webCamTexture == null || !webCamTexture.isPlaying)
            {
                Debug.LogWarning("WebCamTexture is not ready for capture");
                return null;
            }

            try
            {
                int width = webCamTexture.width;
                int height = webCamTexture.height;

                // Create processing texture if needed (SAME size as camera)
                if (processingTexture == null ||
                    processingTexture.width != width ||
                    processingTexture.height != height)
                {
                    if (processingTexture != null)
                        UnityEngine.Object.Destroy(processingTexture);

                    processingTexture = new Texture2D(
                        width,
                        height,
                        TextureFormat.RGB24,
                        false
                    );

                    Debug.Log($"[FrameCapture] Created texture matching camera: {width}×{height}");
                }

                // Copy WebCamTexture to Texture2D
                processingTexture.SetPixels(webCamTexture.GetPixels());
                processingTexture.Apply();

                // Encode to JPEG at full resolution
                byte[] jpegData = processingTexture.EncodeToJPG(config.jpegQuality);

                if (config.enablePerformanceLogging)
                {
                    Debug.Log($"[FrameCapture] Captured: {width}×{height}, Size: {jpegData.Length / 1024f:F2} KB");
                }

                return jpegData;
            }
            catch (Exception e)
            {
                Debug.LogError($"Frame capture failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public void Dispose()
        {
            if (processingTexture != null)
            {
                UnityEngine.Object.Destroy(processingTexture);
                processingTexture = null;
            }
        }
    }
}