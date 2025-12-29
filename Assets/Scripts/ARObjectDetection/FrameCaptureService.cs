// ============================================================================
// FILE 1: FrameCaptureService.cs
// Add frame ID and capture time tracking
// ============================================================================

using UnityEngine;
using System;

namespace ARObjectDetection
{
    /// <summary>
    /// Captured frame data with metadata
    /// </summary>
    public class CapturedFrame
    {
        public byte[] jpegData;
        public int frameId;
        public float captureTime;
    }

    /// <summary>
    /// Captures frames from WebCamTexture WITHOUT RESIZING
    /// </summary>
    public class FrameCaptureService
    {
        private DetectionConfig config;
        private int frameCounter = 0;
        private int nextFrameId = 0;  // NEW: Frame ID counter
        private Texture2D processingTexture;

        public FrameCaptureService(DetectionConfig config)
        {
            this.config = config;
        }

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
        /// Capture frame with metadata
        /// </summary>
        public CapturedFrame CaptureFrameWithMetadata(WebCamTexture webCamTexture)
        {
            byte[] jpegData = CaptureFrame(webCamTexture);

            if (jpegData == null)
                return null;

            return new CapturedFrame
            {
                jpegData = jpegData,
                frameId = nextFrameId++,
                captureTime = Time.realtimeSinceStartup
            };
        }

        /// <summary>
        /// Capture and encode frame WITHOUT resizing
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

                processingTexture.SetPixels(webCamTexture.GetPixels());
                processingTexture.Apply();

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