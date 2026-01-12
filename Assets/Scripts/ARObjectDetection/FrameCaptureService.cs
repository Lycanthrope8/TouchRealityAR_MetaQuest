// ============================================================================
// FILE 1: FrameCaptureService.cs
// FIXED: Now actually resizes to targetResolution before JPEG encoding
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
        public int width;   // NEW: actual sent resolution
        public int height;  // NEW: actual sent resolution
    }

    /// <summary>
    /// Captures frames from WebCamTexture WITH GPU-ACCELERATED RESIZING
    /// </summary>
    public class FrameCaptureService
    {
        private DetectionConfig config;
        private int frameCounter = 0;
        private int nextFrameId = 0;

        // GPU resize resources (cached, NOT allocated per-frame)
        private RenderTexture _resizeRT;
        private Texture2D _resizeTex;
        private Rect _resizeRect;

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
        /// Capture frame with metadata and GPU downscaling
        /// </summary>
        public CapturedFrame CaptureFrameWithMetadata(WebCamTexture webCamTexture)
        {
            int targetW = config.targetResolution.x;
            int targetH = config.targetResolution.y;

            byte[] jpegData = CaptureAndResizeToJpeg(webCamTexture, targetW, targetH, config.jpegQuality);

            if (jpegData == null)
                return null;

            return new CapturedFrame
            {
                jpegData = jpegData,
                frameId = nextFrameId++,
                captureTime = Time.realtimeSinceStartup,
                width = targetW,   // CRITICAL: set to what we actually sent
                height = targetH   // CRITICAL: set to what we actually sent
            };
        }

        /// <summary>
        /// GPU-accelerated resize + JPEG encode
        /// </summary>
        private byte[] CaptureAndResizeToJpeg(Texture sourceTex, int targetW, int targetH, int jpegQuality)
        {
            if (sourceTex == null)
            {
                Debug.LogWarning("Source texture is null");
                return null;
            }

            try
            {
                float t0 = Time.realtimeSinceStartup;

                // Ensure resize resources exist
                EnsureResizeResources(targetW, targetH);

                float t1 = Time.realtimeSinceStartup;

                // GPU downscale (FAST)
                Graphics.Blit(sourceTex, _resizeRT);

                float t2 = Time.realtimeSinceStartup;

                // CPU readback
                var prev = RenderTexture.active;
                RenderTexture.active = _resizeRT;
                _resizeTex.ReadPixels(_resizeRect, 0, 0, false);
                _resizeTex.Apply(false, false);
                RenderTexture.active = prev;

                float t3 = Time.realtimeSinceStartup;

                // Encode JPEG
                byte[] jpegData = _resizeTex.EncodeToJPG(jpegQuality);

                float t4 = Time.realtimeSinceStartup;

                float blitTime = (t2 - t1) * 1000f;
                float readbackTime = (t3 - t2) * 1000f;
                float encodeTime = (t4 - t3) * 1000f;
                float totalTime = (t4 - t0) * 1000f;

                if (config.enablePerformanceLogging)
                {
                    Debug.Log($"[Capture Timing] Blit:{blitTime:F1}ms ReadPixels:{readbackTime:F1}ms Encode:{encodeTime:F1}ms Total:{totalTime:F1}ms Size:{jpegData.Length / 1024f:F1}KB");
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
        /// Ensure resize resources are allocated (cached)
        /// </summary>
        private void EnsureResizeResources(int w, int h)
        {
            // RenderTexture
            if (_resizeRT == null || _resizeRT.width != w || _resizeRT.height != h)
            {
                if (_resizeRT != null)
                {
                    _resizeRT.Release();
                    UnityEngine.Object.Destroy(_resizeRT);
                }

                _resizeRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
                _resizeRT.Create();
                _resizeRect = new Rect(0, 0, w, h);

                Debug.Log($"[FrameCapture] Created RenderTexture: {w}×{h}");
            }

            // Texture2D for readback
            if (_resizeTex == null || _resizeTex.width != w || _resizeTex.height != h)
            {
                if (_resizeTex != null)
                    UnityEngine.Object.Destroy(_resizeTex);

                _resizeTex = new Texture2D(w, h, TextureFormat.RGB24, false);

                Debug.Log($"[FrameCapture] Created Texture2D: {w}×{h}");
            }
        }

        public void Dispose()
        {
            if (_resizeRT != null)
            {
                _resizeRT.Release();
                UnityEngine.Object.Destroy(_resizeRT);
                _resizeRT = null;
            }

            if (_resizeTex != null)
            {
                UnityEngine.Object.Destroy(_resizeTex);
                _resizeTex = null;
            }
        }
    }
}