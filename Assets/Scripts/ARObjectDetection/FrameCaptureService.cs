using UnityEngine;
using System;

namespace ARObjectDetection
{
          /// <summary>
          /// Captures and processes frames from WebCamTexture for detection
          /// </summary>
          public class FrameCaptureService
          {
                    private DetectionConfig config;
                    private int frameCounter = 0;
                    private Texture2D processingTexture;
                    private Texture2D resizedTexture;

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
                    /// Capture and encode frame from WebCamTexture
                    /// </summary>
                    /// <param name="webCamTexture">Source camera texture</param>
                    /// <returns>JPEG encoded byte array</returns>
                    public byte[] CaptureFrame(WebCamTexture webCamTexture)
                    {
                              if (webCamTexture == null || !webCamTexture.isPlaying)
                              {
                                        Debug.LogWarning("WebCamTexture is not ready for capture");
                                        return null;
                              }

                              try
                              {
                                        // Create processing texture if needed (same size as source)
                                        if (processingTexture == null ||
                                            processingTexture.width != webCamTexture.width ||
                                            processingTexture.height != webCamTexture.height)
                                        {
                                                  if (processingTexture != null)
                                                            UnityEngine.Object.Destroy(processingTexture);

                                                  processingTexture = new Texture2D(
                                                      webCamTexture.width,
                                                      webCamTexture.height,
                                                      TextureFormat.RGB24,
                                                      false
                                                  );
                                        }

                                        // Copy WebCamTexture to Texture2D
                                        processingTexture.SetPixels(webCamTexture.GetPixels());
                                        processingTexture.Apply();

                                        // Resize if needed
                                        Texture2D textureToEncode;
                                        if (config.targetResolution.x != webCamTexture.width ||
                                            config.targetResolution.y != webCamTexture.height)
                                        {
                                                  textureToEncode = ResizeTexture(processingTexture, config.targetResolution);
                                        }
                                        else
                                        {
                                                  textureToEncode = processingTexture;
                                        }

                                        // Encode to JPEG
                                        byte[] jpegData = textureToEncode.EncodeToJPG(config.jpegQuality);

                                        if (config.enablePerformanceLogging)
                                        {
                                                  Debug.Log($"Frame captured: {textureToEncode.width}x{textureToEncode.height}, " +
                                                            $"Size: {jpegData.Length / 1024f:F2} KB");
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
                    /// Resize texture to target resolution
                    /// </summary>
                    private Texture2D ResizeTexture(Texture2D source, Vector2Int targetSize)
                    {
                              // Create or reuse resized texture
                              if (resizedTexture == null ||
                                  resizedTexture.width != targetSize.x ||
                                  resizedTexture.height != targetSize.y)
                              {
                                        if (resizedTexture != null)
                                                  UnityEngine.Object.Destroy(resizedTexture);

                                        resizedTexture = new Texture2D(targetSize.x, targetSize.y, TextureFormat.RGB24, false);
                              }

                              // Simple bilinear resize
                              float scaleX = (float)source.width / targetSize.x;
                              float scaleY = (float)source.height / targetSize.y;

                              Color[] sourcePixels = source.GetPixels();
                              Color[] resizedPixels = new Color[targetSize.x * targetSize.y];

                              for (int y = 0; y < targetSize.y; y++)
                              {
                                        for (int x = 0; x < targetSize.x; x++)
                                        {
                                                  float srcX = x * scaleX;
                                                  float srcY = y * scaleY;

                                                  int x1 = Mathf.FloorToInt(srcX);
                                                  int y1 = Mathf.FloorToInt(srcY);
                                                  int x2 = Mathf.Min(x1 + 1, source.width - 1);
                                                  int y2 = Mathf.Min(y1 + 1, source.height - 1);

                                                  float fracX = srcX - x1;
                                                  float fracY = srcY - y1;

                                                  Color c1 = sourcePixels[y1 * source.width + x1];
                                                  Color c2 = sourcePixels[y1 * source.width + x2];
                                                  Color c3 = sourcePixels[y2 * source.width + x1];
                                                  Color c4 = sourcePixels[y2 * source.width + x2];

                                                  Color top = Color.Lerp(c1, c2, fracX);
                                                  Color bottom = Color.Lerp(c3, c4, fracX);
                                                  Color result = Color.Lerp(top, bottom, fracY);

                                                  resizedPixels[y * targetSize.x + x] = result;
                                        }
                              }

                              resizedTexture.SetPixels(resizedPixels);
                              resizedTexture.Apply();

                              return resizedTexture;
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

                              if (resizedTexture != null)
                              {
                                        UnityEngine.Object.Destroy(resizedTexture);
                                        resizedTexture = null;
                              }
                    }
          }
}