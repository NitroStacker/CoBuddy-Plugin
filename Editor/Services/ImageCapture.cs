using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Captures Game View and Scene View screenshots as JPEG images.
    /// All capture methods must run on the main thread.
    /// Returns base64-encoded JPEG data for transport over HTTP/WebSocket.
    /// </summary>
    public static class ImageCapture
    {
        private const int JpegQuality = 85;
        private const int MaxDimension = 1024; // Downscale if larger

        private static readonly string TempDir = Path.Combine(Application.temporaryCachePath, "CoBuddyCaptures");

        /// <summary>
        /// Captures the Game View (what the player sees). Works in both Edit and Play mode.
        /// Uses the Game view's RenderTexture for synchronous capture. Falls back to Scene View if unavailable.
        /// </summary>
        public static ScreenshotResult CaptureGameView()
        {
            try
            {
                // Try to find the main camera and render synchronously
                var cam = Camera.main;
                if (cam == null)
                {
                    // Fallback: find any camera
                    cam = UnityEngine.Object.FindObjectOfType<Camera>();
                }
                if (cam != null)
                {
                    int width = Mathf.Min(cam.pixelWidth > 0 ? cam.pixelWidth : 1920, MaxDimension);
                    int height = Mathf.Min(cam.pixelHeight > 0 ? cam.pixelHeight : 1080, MaxDimension);
                    if (width <= 0) width = 960;
                    if (height <= 0) height = 540;

                    var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                    var previousRT = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = previousRT;

                    var previousActive = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = previousActive;

                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);

                    var result = EncodeAndReturn(tex);
                    UnityEngine.Object.DestroyImmediate(tex);
                    return result;
                }

                // No camera — fall back to scene view
                return CaptureSceneView();
            }
            catch (Exception ex)
            {
                // Fall back to scene view on any error
                try { return CaptureSceneView(); }
                catch { return new ScreenshotResult { success = false, message = ex.Message }; }
            }
        }

        /// <summary>
        /// Captures the Scene View (editor camera). Must be called from the main thread.
        /// Returns base64-encoded JPEG immediately (synchronous RenderTexture capture).
        /// </summary>
        public static ScreenshotResult CaptureSceneView()
        {
            try
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                    return new ScreenshotResult { success = false, message = "No active Scene View" };

                var camera = sceneView.camera;
                if (camera == null)
                    return new ScreenshotResult { success = false, message = "Scene View camera not available" };

                // Determine capture size (clamp to MaxDimension)
                int width = Mathf.Min((int)sceneView.position.width, MaxDimension);
                int height = Mathf.Min((int)sceneView.position.height, MaxDimension);
                if (width <= 0 || height <= 0)
                    return new ScreenshotResult { success = false, message = "Scene View has zero size" };

                // Force repaint to ensure camera is up to date
                sceneView.Repaint();

                // Create render texture and capture
                var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                var previousRT = camera.targetTexture;
                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = previousRT;

                // Read pixels from render texture
                var previousActive = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = previousActive;

                // Cleanup render texture
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);

                var result = EncodeAndReturn(tex);
                UnityEngine.Object.DestroyImmediate(tex);
                return result;
            }
            catch (Exception ex)
            {
                return new ScreenshotResult { success = false, message = ex.Message };
            }
        }

        /// <summary>
        /// Captures a preview image of an asset at the given path.
        /// Works for prefabs, materials, textures, models, etc.
        /// </summary>
        public static ScreenshotResult CaptureAssetPreview(string assetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(assetPath))
                    return new ScreenshotResult { success = false, message = "No asset path provided" };

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                if (asset == null)
                    return new ScreenshotResult { success = false, message = $"Asset not found: {assetPath}" };

                // Request asset preview (Unity caches these)
                var preview = AssetPreview.GetAssetPreview(asset);
                if (preview == null)
                {
                    // Try mini thumbnail as fallback
                    preview = AssetPreview.GetMiniThumbnail(asset);
                }
                if (preview == null)
                    return new ScreenshotResult { success = false, message = "No preview available for this asset" };

                // AssetPreview textures are read-only, we need to copy to a new texture
                var rt = RenderTexture.GetTemporary(preview.width, preview.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(preview, rt);
                var previousActive = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(preview.width, preview.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, preview.width, preview.height), 0, 0);
                tex.Apply();
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);

                var result = EncodeAndReturn(tex);
                UnityEngine.Object.DestroyImmediate(tex);
                return result;
            }
            catch (Exception ex)
            {
                return new ScreenshotResult { success = false, message = ex.Message };
            }
        }

        /// <summary>
        /// Downscales if needed, encodes to JPEG, returns base64 result.
        /// </summary>
        private static ScreenshotResult EncodeAndReturn(Texture2D tex)
        {
            // Downscale if too large
            if (tex.width > MaxDimension || tex.height > MaxDimension)
            {
                float scale = Mathf.Min((float)MaxDimension / tex.width, (float)MaxDimension / tex.height);
                int newW = Mathf.Max(1, Mathf.RoundToInt(tex.width * scale));
                int newH = Mathf.Max(1, Mathf.RoundToInt(tex.height * scale));

                var rt = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var downscaled = new Texture2D(newW, newH, TextureFormat.RGB24, false);
                downscaled.ReadPixels(new Rect(0, 0, newW, newH), 0, 0);
                downscaled.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                byte[] jpg = ImageConversion.EncodeToJPG(downscaled, JpegQuality);
                int w = downscaled.width, h = downscaled.height;
                UnityEngine.Object.DestroyImmediate(downscaled);

                return new ScreenshotResult
                {
                    success = true,
                    base64 = Convert.ToBase64String(jpg),
                    width = w,
                    height = h,
                    format = "jpeg"
                };
            }
            else
            {
                byte[] jpg = ImageConversion.EncodeToJPG(tex, JpegQuality);
                return new ScreenshotResult
                {
                    success = true,
                    base64 = Convert.ToBase64String(jpg),
                    width = tex.width,
                    height = tex.height,
                    format = "jpeg"
                };
            }
        }

        private static string GetRelativePath(string absolutePath)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (!string.IsNullOrEmpty(projectRoot) && absolutePath.StartsWith(projectRoot))
            {
                string relative = absolutePath.Substring(projectRoot.Length);
                if (relative.StartsWith(Path.DirectorySeparatorChar.ToString()) || relative.StartsWith("/"))
                    relative = relative.Substring(1);
                return relative.Replace("\\", "/");
            }
            return absolutePath;
        }
    }

    [Serializable]
    public class ScreenshotResult
    {
        public bool success;
        public string base64;
        public int width;
        public int height;
        public string format;
        public string message;
        public string pendingPath; // For async game view capture
    }
}
