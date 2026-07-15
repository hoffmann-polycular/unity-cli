// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCliConnector.Tools
{
    [UnityCliTool(Name = "screenshot", Description = "Capture a screenshot. View: game (default, the real Game View incl. UI/post), scene, or a hierarchy path to a Camera.")]
    public static class EditorScreenshot
    {
        private const int DefaultWidth = 1920;
        private const int DefaultHeight = 1080;
        private const int GameViewTimeoutSeconds = 15;

        public class Parameters
        {
            [ToolParameter("View to capture: 'game' (default, the actual Game View incl. UI + post-processing), 'scene', or a hierarchy path to a GameObject with a Camera", Required = false)]
            public string View { get; set; }

            [ToolParameter("Resolution multiplier for game/scene captures (integer >= 1, default 1). Keeps native aspect — no distortion.", Required = false)]
            public int SuperSize { get; set; }

            [ToolParameter("Render width in pixels (camera-path view only; default 1920). Ignored for game/scene.", Required = false)]
            public int Width { get; set; }

            [ToolParameter("Render height in pixels (camera-path view only; default 1080). Ignored for game/scene.", Required = false)]
            public int Height { get; set; }

            [ToolParameter("Output file path, absolute or relative to project root (default: Screenshots/<view>_<timestamp>.png)", Required = false)]
            public string OutputPath { get; set; }
        }

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
                @params = new JObject();

            var p = new ToolParams(@params);
            var view = (p.Get("view", "game") ?? "game").Trim();
            var viewLower = view.ToLowerInvariant();
            var width = p.GetInt("width", DefaultWidth).Value;
            var height = p.GetInt("height", DefaultHeight).Value;
            var superSize = Mathf.Max(1, p.GetInt("supersize", 1).Value);
            var sizeFlagsProvided = @params["width"] != null || @params["height"] != null;

            try
            {
                switch (viewLower)
                {
                    case "game":
                    {
                        var outputPath = ResolveOutputPath(p.Get("output_path"), "game");
                        EnsureDirectory(outputPath);
                        return await CaptureGameViewAsync(superSize, outputPath, sizeFlagsProvided);
                    }
                    case "scene":
                    {
                        var sceneView = SceneView.lastActiveSceneView;
                        if (!sceneView)
                            return new ErrorResponse("No active SceneView found.");
                        var cam = sceneView.camera;
                        if (!cam)
                            return new ErrorResponse("SceneView camera is null.");

                        var outputPath = ResolveOutputPath(p.Get("output_path"), "scene");
                        EnsureDirectory(outputPath);

                        int baseW = cam.pixelWidth > 0 ? cam.pixelWidth : Mathf.CeilToInt(sceneView.position.width);
                        int baseH = cam.pixelHeight > 0 ? cam.pixelHeight : Mathf.CeilToInt(sceneView.position.height);
                        return CaptureCameraOffscreen(cam, baseW * superSize, baseH * superSize, outputPath, "scene");
                    }
                    default:
                    {
                        // Any other --view value is a hierarchy path to a Camera.
                        var camRes = ResolveCameraByPath(view);
                        if (!camRes.IsSuccess)
                            return ErrorResponse.FromResult(camRes);

                        var outputPath = ResolveOutputPath(p.Get("output_path"), camRes.Value.gameObject.name);
                        EnsureDirectory(outputPath);
                        return CaptureCameraOffscreen(camRes.Value, width, height, outputPath, "camera");
                    }
                }
            }
            catch (Exception e)
            {
                return new ErrorResponse($"Screenshot failed: {e.Message}");
            }
        }

        // ---- game view (faithful: includes Overlay UI + post-processing, correct under SRP) ----

        private static async Task<object> CaptureGameViewAsync(int superSize, string outputPath, bool sizeFlagsProvided)
        {
            var gameView = GetMainGameView();
            if (gameView == null)
            {
                // No Game View open — try to open one, then re-fetch.
                EditorApplication.ExecuteMenuItem("Window/General/Game");
                gameView = GetMainGameView();
                if (gameView == null)
                    return new ErrorResponse("No Game View window available to capture.");
            }

            // ScreenCapture grabs the game view exactly as displayed at the end of
            // a rendered frame — the one thing camera.Render() into a RenderTexture
            // cannot do (it misses Screen-Space-Overlay UI and mis-renders under
            // URP/HDRP). Prod the view so a frame actually renders, then poll for
            // the file to be written and stable.
            gameView.Repaint();
            EditorApplication.QueuePlayerLoopUpdate();
            ScreenCapture.CaptureScreenshot(outputPath, superSize);

            var ok = await WaitForFileStableAsync(outputPath, TimeSpan.FromSeconds(GameViewTimeoutSeconds));
            if (!ok)
                return new ErrorResponse($"Timed out waiting for the Game View screenshot to be written to {outputPath}.");

            var (w, h) = ReadPngSize(outputPath);
            var note = sizeFlagsProvided ? " (--width/--height ignored for game view; use --supersize)" : "";
            return new SuccessResponse($"Screenshot saved to {outputPath}{note}",
                new { path = outputPath, view = "game", width = w, height = h });
        }

        /// <summary>
        /// Reflection lookup of the main Play Mode (Game) view window. The name
        /// of the static accessor changed across Unity versions
        /// (PlayModeView.GetMainPlayModeView in 2019.3+, GameView.GetMainGameView
        /// earlier), so probe both.
        /// </summary>
        private static EditorWindow GetMainGameView()
        {
            var asm = typeof(EditorWindow).Assembly;
            var candidates = new[]
            {
                ("UnityEditor.PlayModeView", "GetMainPlayModeView"),
                ("UnityEditor.GameView", "GetMainGameView"),
            };
            foreach (var (typeName, methodName) in candidates)
            {
                var t = asm.GetType(typeName);
                var mi = t?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (mi != null && mi.Invoke(null, null) is EditorWindow win && win)
                    return win;
            }
            return null;
        }

        /// <summary>
        /// Waits (across editor update ticks) until <paramref name="path"/> exists
        /// and its length is non-zero and unchanged across two consecutive ticks,
        /// so we don't read a half-written PNG. Returns false on timeout.
        /// </summary>
        private static Task<bool> WaitForFileStableAsync(string path, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var start = DateTime.UtcNow;
            long lastLen = -1;
            int stableTicks = 0;

            void Tick()
            {
                try
                {
                    if ((DateTime.UtcNow - start) > timeout)
                    {
                        EditorApplication.update -= Tick;
                        tcs.TrySetResult(false);
                        return;
                    }

                    if (File.Exists(path))
                    {
                        long len = new FileInfo(path).Length;
                        if (len > 0 && len == lastLen)
                        {
                            if (++stableTicks >= 2)
                            {
                                EditorApplication.update -= Tick;
                                tcs.TrySetResult(true);
                                return;
                            }
                        }
                        else
                        {
                            stableTicks = 0;
                        }
                        lastLen = len;
                    }

                    // Keep driving frames so CaptureScreenshot completes even
                    // in edit mode (where nothing repaints on its own).
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                catch (Exception e)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetException(e);
                }
            }

            EditorApplication.update += Tick;
            return tcs.Task;
        }

        // ---- offscreen camera render (scene view + explicit camera path) ----

        private static Result<Camera> ResolveCameraByPath(string path)
        {
            var parsed = PathParser.Parse(path);
            if (!parsed.IsSuccess)
                return Result<Camera>.Error(
                    $"Invalid --view value '{path}': {parsed.ErrorMessage} " +
                    "(expected 'game', 'scene', or a hierarchy path to a Camera).",
                    ErrorKind.Usage);

            var goRes = PathResolver.ResolveGameObject(parsed.Value);
            if (!goRes.IsSuccess)
                return Result<Camera>.Error(goRes.ErrorMessage, goRes.ErrorKind);

            var go = goRes.Value;
            var cam = go.GetComponent<Camera>() ?? go.GetComponentInChildren<Camera>();
            if (cam == null)
                return Result<Camera>.Error(
                    $"No Camera component found on '{PathResolver.GetCanonicalPath(go)}'.",
                    ErrorKind.NotFound);
            return Result<Camera>.Success(cam);
        }

        private static object CaptureCameraOffscreen(Camera camera, int width, int height, string outputPath, string label)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);

            var previousRT = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tex = null;

            try
            {
                // In a linear-color-space project the render target must be sRGB so
                // the pixels we read back (and PNG-encode) are gamma-encoded — else
                // the image comes out washed-out/dark.
                var readWrite = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? RenderTextureReadWrite.sRGB
                    : RenderTextureReadWrite.Default;
                rt = new RenderTexture(width, height, 24, RenderTextureFormat.Default, readWrite);

                bool rendered = false;
#if UNITY_2022_2_OR_NEWER
                // Under a Scriptable Render Pipeline (URP/HDRP), the render-request
                // API is the supported way to render a camera off-screen. Falling
                // back to camera.Render() (built-in pipeline) otherwise.
                var request = new RenderPipeline.StandardRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(camera, request))
                {
                    RenderPipeline.SubmitRenderRequest(camera, request);
                    rendered = true;
                }
#endif
                if (!rendered)
                {
                    camera.targetTexture = rt;
                    camera.Render();
                }

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                File.WriteAllBytes(outputPath, tex.EncodeToPNG());

                return new SuccessResponse($"Screenshot saved to {outputPath}",
                    new { path = outputPath, view = label, width, height });
            }
            finally
            {
                camera.targetTexture = previousRT;
                RenderTexture.active = previousActive;
                if (rt) UnityEngine.Object.DestroyImmediate(rt);
                if (tex) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // ---- helpers ----

        private static string ResolveOutputPath(string userPath, string label)
        {
            if (string.IsNullOrEmpty(userPath))
            {
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                userPath = $"Screenshots/{SanitizeLabel(label)}_{stamp}.png";
            }

            if (Path.IsPathRooted(userPath))
                return Path.GetFullPath(userPath);

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, userPath));
        }

        private static string SanitizeLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
                return "screenshot";
            foreach (var c in Path.GetInvalidFileNameChars())
                label = label.Replace(c, '_');
            return label.Replace(' ', '_');
        }

        private static void EnsureDirectory(string outputPath)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>Reads width/height from a PNG's IHDR chunk without decoding pixels.</summary>
        private static (int width, int height) ReadPngSize(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var b = new byte[24];
                if (fs.Read(b, 0, 24) == 24)
                {
                    int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                    int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                    return (w, h);
                }
            }
            catch
            {
                // Fall through to unknown dimensions.
            }
            return (0, 0);
        }
    }
}
