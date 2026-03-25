using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using CoBuddy.Editor.Models;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// HTTP bridge server for CoBuddy app. Runs on port 38472.
    /// Receives: /ping, /index, /patches. Executes patch application on main thread.
    /// </summary>
    [InitializeOnLoad]
    public static class BridgeServer
    {
        private const int Port = 38472;
        private static HttpListener _listener;
        private static Thread _thread;
        private static bool _running;
        private static readonly Queue<Action> MainThreadQueue = new Queue<Action>();
        private static readonly object QueueLock = new object();
        private static List<PatchResult> _pendingPatchResults;
        private static ManualResetEvent _patchDoneEvent;
        private static ActionResult[] _pendingActionResults;
        private static ManualResetEvent _actionsDoneEvent;
        private static List<string> _sceneRootsResult;
        private static ManualResetEvent _sceneRootsEvent;
        private static bool _isPlayModeResult;
        private static List<ComponentEntry> _componentsResult;
        private static ManualResetEvent _componentsEvent;

        // EditorContext fields (gathered on main thread)
        private static string[] _editorTags;
        private static string[] _editorLayers;
        private static string[] _editorSortingLayers;
        private static string _editorRenderPipeline;
        private static string _editorRendererType; // "2D" or "3D"
        private static readonly List<List<LastAppliedChange>> _batchStack = new List<List<LastAppliedChange>>();
        private static readonly List<List<ActionRevertRecord>> _actionBatchStack = new List<List<ActionRevertRecord>>();
        private static ManualResetEvent _revertActionsDoneEvent;
        private static List<string> _unusedAssetsResult;
        private static List<UnusedAssetEntry> _unusedAssetsEntries;
        private static ManualResetEvent _unusedAssetsEvent;


        // Phase 32: Progress and cancel
        private static CancellationTokenSource _currentActionsCts;
        private static volatile bool _operationInProgress;
        private static volatile float _operationProgress;
        private static volatile string _operationStatus;

        private static bool _restartScheduled;
        private static DateTime _lastStartAttempt;
        private const double RestartDelaySeconds = 2.0;

        // WebSocket client — connects to Electron app's WS server on port 38473
        private static WSClient _wsClient;

        // Plugin version — resolved once on the main thread at startup; returned by /version
        private static string _cachedPluginVersion = "0.1.0";

        // /index response cache — avoids re-scanning all project assets on every request
        private static string _cachedIndexJson;
        private static DateTime _indexCachedAt = DateTime.MinValue;
        private static readonly object IndexCacheLock = new object();
        private const double IndexCacheTtlSeconds = 5.0;

        /// <summary>Invalidate the /index cache. Called when project files or play mode change.</summary>
        public static void InvalidateIndexCache()
        {
            lock (IndexCacheLock) { _cachedIndexJson = null; }
        }

        // Server-Sent Events — push selection and compilation events to connected JS clients
        private static readonly List<System.IO.Stream> _sseClients = new List<System.IO.Stream>();
        private static readonly object SseClientsLock = new object();

        /// <summary>Push an event to all connected SSE and WebSocket clients. Safe to call from any thread.</summary>
        public static void PushEvent(string type, string payloadJson)
        {
            // SSE broadcast
            string sseMessage = $"data: {{\"type\":\"{type}\",\"payload\":{payloadJson}}}\n\n";
            byte[] sseBytes = Encoding.UTF8.GetBytes(sseMessage);
            lock (SseClientsLock)
            {
                var dead = new List<System.IO.Stream>();
                foreach (var stream in _sseClients)
                {
                    try { stream.Write(sseBytes, 0, sseBytes.Length); stream.Flush(); }
                    catch { dead.Add(stream); }
                }
                foreach (var d in dead) _sseClients.Remove(d);
            }

            // WebSocket push via client
            if (_wsClient != null && _wsClient.IsConnected)
            {
                _wsClient.SendEvent(type, payloadJson);
            }
        }

        public static void PushCompileStart()
        {
            PushEvent("compileStart", "{}");
        }

        public static void PushRuntimeError(string message, string stackTrace, string logType)
        {
            var payload = "{\"message\":" + "\"" + EscapeJson(message ?? "") + "\"" +
                          ",\"stackTrace\":" + "\"" + EscapeJson(stackTrace ?? "") + "\"" +
                          ",\"logType\":" + "\"" + EscapeJson(logType ?? "Error") + "\"}";
            PushEvent("runtimeError", payload);
        }

        public static void PushCompileFinish(CompilationStatusTracker.CompilationErrorEntry[] errors)
        {
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < errors.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"file\":\"{EscapeJson(errors[i].file)}\",\"line\":{errors[i].line},\"column\":{errors[i].column},\"message\":\"{EscapeJson(errors[i].message)}\"}}");
            }
            sb.Append("]");
            PushEvent("compileFinish", $"{{\"errors\":{sb}}}");
        }

        public static string EscapeJson(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        static BridgeServer()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += ProcessMainThreadQueue;
            EditorApplication.update += CheckBridgeHealth;
            UnityEditor.Selection.selectionChanged += OnSelectionChanged;
            Start();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
            {
                InvalidateIndexCache(); // scene state changes on play mode transitions
                _running = false;
                try
                {
                    _listener?.Stop();
                    _listener?.Close();
                }
                catch { }
                _listener = null;
                _thread = null;
                Start();
            }
        }

        /// <summary>Called on main thread whenever the Unity selection changes. Pushes the new selection to all SSE clients.</summary>
        private static void OnSelectionChanged()
        {
            try
            {
                var objects = UnityEditor.Selection.objects;
                if (objects == null) objects = new UnityEngine.Object[0];
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < objects.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    var obj = objects[i];
                    string name = EscapeJson(obj != null ? obj.name : "");
                    string typeName = EscapeJson(obj != null ? obj.GetType().Name : "");
                    string assetPath = "";
                    if (obj != null)
                        assetPath = EscapeJson(UnityEditor.AssetDatabase.GetAssetPath(obj) ?? "");
                    sb.Append($"{{\"name\":\"{name}\",\"type\":\"{typeName}\",\"path\":\"{assetPath}\"}}");
                }
                sb.Append("]");
                PushEvent("selectionChanged", $"{{\"selection\":{sb}}}");
            }
            catch { }
        }

        /// <summary>Bridge must stay up. Restart when listener dies or fails to start.</summary>
        private static void CheckBridgeHealth()
        {
            if (_restartScheduled && (DateTime.UtcNow - _lastStartAttempt).TotalSeconds >= RestartDelaySeconds)
            {
                _restartScheduled = false;
                Start();
            }
            if (_running && (_thread == null || !_thread.IsAlive))
            {
                _running = false;
                _listener = null;
                _thread = null;
                ScheduleRestart();
            }
        }

        /// <summary>Safe to call from any thread. Uses DateTime.UtcNow instead of EditorApplication.timeSinceStartup.</summary>
        private static void ScheduleRestart()
        {
            if (_restartScheduled) return;
            _restartScheduled = true;
            _lastStartAttempt = DateTime.UtcNow;
            Debug.Log("[CoBuddy] Bridge will restart in 2 seconds");
        }

        /// <summary>Resolves the installed plugin version using Unity APIs. Must be called on the main thread.</summary>
        private static void ResolvePluginVersion()
        {
            try
            {
                // 1. PackageInfo — most reliable when package is registered with Unity
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.opengate.cobuddy")
                    ?? UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeServer).Assembly);
                if (pkg != null && !string.IsNullOrWhiteSpace(pkg.version))
                {
                    _cachedPluginVersion = pkg.version;
                    return;
                }

                // 2. Scan Packages/ directory for a package.json containing our package name
                var packagesPath = Path.Combine(Application.dataPath, "..", "Packages");
                if (Directory.Exists(packagesPath))
                {
                    foreach (var dir in Directory.GetDirectories(packagesPath))
                    {
                        var pj = Path.Combine(dir, "package.json");
                        if (!File.Exists(pj)) continue;
                        try
                        {
                            var json = File.ReadAllText(pj);
                            if (!json.Contains("com.opengate.cobuddy")) continue;
                            var m = System.Text.RegularExpressions.Regex.Match(json, @"""version""\s*:\s*""([^""]+)""");
                            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                            {
                                _cachedPluginVersion = m.Groups[1].Value.Trim();
                                return;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void Start()
        {
            if (_running && _thread != null && _thread.IsAlive)
                return;

            ResolvePluginVersion();
            _running = false;
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }
            _listener = null;
            _thread = null;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _running = true;
                _thread = new Thread(ListenLoop) { IsBackground = true };
                _thread.Start();
                _restartScheduled = false;
                Debug.Log($"[CoBuddy] Bridge server started on port {Port}");

                // Start WebSocket client (connects to Electron app's WS server)
                StartWebSocketClient();
            }
            catch (HttpListenerException ex) when (ex.Message?.Contains("another listener") == true || ex.ErrorCode == 183 || ex.ErrorCode == 48)
            {
                try { _listener?.Close(); } catch { }
                _listener = null;
                Debug.LogWarning($"[CoBuddy] Port {Port} in use, will retry in 2 seconds");
                ScheduleRestart();
            }
            catch (Exception ex)
            {
                try { _listener?.Close(); } catch { }
                _listener = null;
                Debug.LogWarning($"[CoBuddy] Failed to start bridge: " + ex.Message);
                ScheduleRestart();
            }
        }

        public static void Stop()
        {
            _running = false;
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch { }
            _listener = null;
            if (_wsClient != null)
            {
                _wsClient.Dispose();
                _wsClient = null;
            }
        }

        private static void ListenLoop()
        {
            try
            {
                while (_running && _listener != null)
                {
                    try
                    {
                        var context = _listener.GetContext();
                        // Dispatch each request to the ThreadPool so the listener stays free
                        // to accept new connections (required for long-lived SSE connections)
                        ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CoBuddy] Bridge error: {ex.Message}");
                    }
                }
            }
            finally
            {
                _running = false;
                _listener = null;
                _thread = null;
                ScheduleRestart();
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            string method = context.Request.HttpMethod ?? "GET";

            try
            {
                if (path.EndsWith("/ping", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandlePing(context);
                    return;
                }
                if (path.EndsWith("/index", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleIndex(context);
                    return;
                }
                if (path.EndsWith("/patches", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandlePatches(context);
                    return;
                }
                if (path.EndsWith("/revert", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleRevert(context);
                    return;
                }
                if (path.EndsWith("/actions", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleActions(context);
                    return;
                }
                if (path.EndsWith("/actions/status", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleActionsStatus(context);
                    return;
                }
                if (path.EndsWith("/actions/cancel", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleActionsCancel(context);
                    return;
                }
                if (path.EndsWith("/revert-actions", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleRevertActions(context);
                    return;
                }
                if (path.EndsWith("/undo-group", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleUndoGroup(context);
                    return;
                }
                if (path.EndsWith("/compile-status", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleCompileStatus(context);
                    return;
                }
                if (path.EndsWith("/console", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleConsole(context);
                    return;
                }
                if (path.EndsWith("/version", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleVersion(context);
                    return;
                }
                if (path.EndsWith("/unused-assets", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleUnusedAssets(context);
                    return;
                }
                if (path.EndsWith("/dependencies", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleDependencies(context);
                    return;
                }
                if (path.EndsWith("/selection", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleSelection(context);
                    return;
                }
                if (path.EndsWith("/inspector", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleInspector(context);
                    return;
                }
                if (path.EndsWith("/events", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleEvents(context);
                    return;
                }
                if (path.EndsWith("/validate", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleValidate(context);
                    return;
                }
                if (path.EndsWith("/validate-actions", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleValidateActions(context);
                    return;
                }
                if (path.EndsWith("/action-schema", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    HandleActionSchema(context);
                    return;
                }
                if (path.EndsWith("/screenshot", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleScreenshot(context);
                    return;
                }
                if (path.EndsWith("/monobehaviour-code", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleMonoBehaviourCode(context);
                    return;
                }
                if (path.EndsWith("/serialize", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleSerialize(context);
                    return;
                }
                if (path.EndsWith("/decompile-type", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    HandleDecompileType(context);
                    return;
                }

                SendJson(context.Response, 404, new ErrorResponse { error = "Not found" });
            }
            catch (Exception ex)
            {
                try
                {
                    SendJson(context.Response, 500, new ErrorResponse { error = ex.Message });
                }
                catch
                {
                    // Response may already have been sent (e.g. handler threw after SendJson) - avoid "Cannot be changed after headers are sent"
                }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private static void HandlePing(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            string projectPath = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var json = JsonUtility.FromJson<PingRequest>(body);
                    projectPath = json?.projectPath;
                }
                catch { }
            }

            string currentProject = Path.GetFullPath(".").Replace("\\", "/");
            bool match = string.IsNullOrWhiteSpace(projectPath) ||
                         string.Equals(currentProject, projectPath.Replace("\\", "/"), StringComparison.OrdinalIgnoreCase);

            SendJson(context.Response, 200, new PingResponse
            {
                connected = true,
                projectPath = currentProject,
                projectMatch = match
            });
        }

        private static void HandleIndex(HttpListenerContext context)
        {
            // Check for ?summary=true query param
            string queryString = context.Request.Url?.Query ?? "";
            bool useSummary = queryString.IndexOf("summary=true", StringComparison.OrdinalIgnoreCase) >= 0;

            // Serve cached index if still fresh — avoids re-scanning all project assets every request
            // Only use cache for non-summary requests (summary has its own cache)
            if (!useSummary)
            {
                lock (IndexCacheLock)
                {
                    if (_cachedIndexJson != null && (DateTime.UtcNow - _indexCachedAt).TotalSeconds < IndexCacheTtlSeconds)
                    {
                        byte[] cachedBytes = Encoding.UTF8.GetBytes(_cachedIndexJson);
                        context.Response.StatusCode = 200;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentEncoding = Encoding.UTF8;
                        context.Response.ContentLength64 = cachedBytes.Length;
                        context.Response.OutputStream.Write(cachedBytes, 0, cachedBytes.Length);
                        return;
                    }
                }
            }

            var snapshot = CodeIndex.GetSnapshot();
            var scripts = ProjectScanner.GetAllScriptsInAssets();
            var asmdefs = ProjectScanner.GetAllAsmdefs();
            string manifest = ProjectScanner.GetManifestJson() ?? "";

            var scriptEntries = new List<IndexScriptEntry>();
            foreach (var s in scripts)
            {
                string content;
                if (useSummary && s.path != null && s.path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    // Use Roslyn summarizer: signatures only, no method bodies
                    var summary = CodeSummarizer.Summarize(s.content, s.path, 4000);
                    content = summary.summary ?? "";
                }
                else
                {
                    content = s.content ?? "";
                }
                scriptEntries.Add(new IndexScriptEntry
                {
                    path = s.path,
                    content = content,
                    contentLength = s.content?.Length ?? 0
                });
            }

            var asmdefEntries = new List<IndexAsmdefEntry>();
            foreach (var a in asmdefs)
            {
                asmdefEntries.Add(new IndexAsmdefEntry { path = a.path });
            }

            var prefabs = ProjectScanner.GetAllPrefabsInAssets();
            var prefabEntries = new List<IndexPrefabEntry>();
            foreach (var p in prefabs)
            {
                prefabEntries.Add(new IndexPrefabEntry
                {
                    path = p.path,
                    content = p.content ?? ""
                });
            }

            var materials = ProjectScanner.GetAllMaterialsInAssets();
            var materialEntries = new List<IndexMaterialEntry>();
            foreach (var m in materials)
            {
                materialEntries.Add(new IndexMaterialEntry { path = m.path, content = m.content ?? "" });
            }

            var shaders = ProjectScanner.GetAllShadersInAssets();
            var shaderEntries = new List<IndexShaderEntry>();
            foreach (var s in shaders)
            {
                shaderEntries.Add(new IndexShaderEntry { path = s.path, content = s.content ?? "" });
            }

            var inputActions = ProjectScanner.GetAllInputActionsInAssets();
            var inputActionEntries = new List<IndexInputActionEntry>();
            foreach (var ia in inputActions)
            {
                inputActionEntries.Add(new IndexInputActionEntry { path = ia.path, content = ia.content ?? "" });
            }

            var scriptableObjects = ProjectScanner.GetAllScriptableObjectsInAssets();
            var scriptableObjectEntries = new List<IndexScriptableObjectEntry>();
            foreach (var so in scriptableObjects)
            {
                scriptableObjectEntries.Add(new IndexScriptableObjectEntry
                {
                    path = so.path,
                    content = so.content ?? "",
                    assetType = so.assetType ?? ""
                });
            }

            var packages = ProjectScanner.GetPackagesInfo();
            var packageEntries = new List<IndexPackageEntry>();
            foreach (var pkg in packages)
            {
                packageEntries.Add(new IndexPackageEntry
                {
                    name = pkg.name ?? "",
                    version = pkg.version ?? "",
                    path = pkg.path ?? ""
                });
            }

            string[] sceneRoots = Array.Empty<string>();
            _isPlayModeResult = false;
            try
            {
                _sceneRootsResult = null;
                _sceneRootsEvent = new ManualResetEvent(false);
                lock (QueueLock)
                {
                    MainThreadQueue.Enqueue(() =>
                    {
                        try
                        {
                            _isPlayModeResult = Application.isPlaying;
                            _sceneRootsResult = SceneHierarchyScanner.GetActiveScenePaths();

                            // Gather EditorContext: tags, layers, sorting layers, render pipeline
                            try
                            {
                                _editorTags = UnityEditorInternal.InternalEditorUtility.tags ?? Array.Empty<string>();
                                var layerList = new List<string>();
                                for (int i = 0; i < 32; i++)
                                {
                                    string name = LayerMask.LayerToName(i);
                                    if (!string.IsNullOrEmpty(name)) layerList.Add(name);
                                }
                                _editorLayers = layerList.ToArray();
                                _editorSortingLayers = SortingLayer.layers != null
                                    ? Array.ConvertAll(SortingLayer.layers, l => l.name)
                                    : Array.Empty<string>();

                                // Detect render pipeline
                                var rpAsset = GraphicsSettings.currentRenderPipeline;
                                if (rpAsset != null)
                                {
                                    string rpType = rpAsset.GetType().Name;
                                    if (rpType.Contains("HDRenderPipeline") || rpType.Contains("HDRP"))
                                        _editorRenderPipeline = "HDRP";
                                    else if (rpType.Contains("Universal") || rpType.Contains("URP"))
                                        _editorRenderPipeline = "URP";
                                    else
                                        _editorRenderPipeline = rpType;
                                }
                                else
                                {
                                    _editorRenderPipeline = "Built-in";
                                }

                                // Detect 2D vs 3D from SceneView
                                var sv = SceneView.lastActiveSceneView;
                                _editorRendererType = (sv != null && sv.in2DMode) ? "2D" : "3D";
                            }
                            catch
                            {
                                _editorTags = _editorTags ?? Array.Empty<string>();
                                _editorLayers = _editorLayers ?? Array.Empty<string>();
                                _editorSortingLayers = _editorSortingLayers ?? Array.Empty<string>();
                                _editorRenderPipeline = _editorRenderPipeline ?? "Unknown";
                                _editorRendererType = _editorRendererType ?? "3D";
                            }
                        }
                        catch
                        {
                            _isPlayModeResult = false;
                            _sceneRootsResult = new List<string>();
                        }
                        finally
                        {
                            _sceneRootsEvent?.Set();
                        }
                    });
                }
                _sceneRootsEvent.WaitOne(5000);
                sceneRoots = _sceneRootsResult?.ToArray() ?? Array.Empty<string>();
            }
            catch
            {
                sceneRoots = Array.Empty<string>();
            }

            ComponentEntry[] components = Array.Empty<ComponentEntry>();
            try
            {
                _componentsResult = null;
                _componentsEvent = new ManualResetEvent(false);
                lock (QueueLock)
                {
                    MainThreadQueue.Enqueue(() =>
                    {
                        try
                        {
                            _componentsResult = ComponentScanner.GetActiveSceneComponents();
                        }
                        catch
                        {
                            _componentsResult = new List<ComponentEntry>();
                        }
                        finally
                        {
                            _componentsEvent?.Set();
                        }
                    });
                }
                _componentsEvent.WaitOne(5000);
                components = _componentsResult?.ToArray() ?? Array.Empty<ComponentEntry>();
            }
            catch
            {
                components = Array.Empty<ComponentEntry>();
            }

            var componentEntries = new List<IndexComponentEntry>();
            foreach (var c in components)
            {
                componentEntries.Add(new IndexComponentEntry
                {
                    gameObjectPath = c.gameObjectPath ?? "",
                    componentType = c.componentType ?? "",
                    serializedFields = c.serializedFields ?? Array.Empty<string>()
                });
            }

            var response = new IndexResponse
            {
                scripts = scriptEntries.ToArray(),
                asmdefs = asmdefEntries.ToArray(),
                prefabs = prefabEntries.ToArray(),
                materials = materialEntries.ToArray(),
                shaders = shaderEntries.ToArray(),
                inputActions = inputActionEntries.ToArray(),
                scriptableObjects = scriptableObjectEntries.ToArray(),
                packages = packageEntries.ToArray(),
                components = componentEntries.ToArray(),
                manifest = manifest,
                scriptCount = snapshot.scripts?.Length ?? 0,
                edgeCount = snapshot.edges?.Length ?? 0,
                sceneRoots = sceneRoots,
                isPlayMode = _isPlayModeResult,
                renderPipeline = _editorRenderPipeline ?? "Unknown",
                rendererType = _editorRendererType ?? "3D",
                tags = _editorTags ?? Array.Empty<string>(),
                layers = _editorLayers ?? Array.Empty<string>(),
                sortingLayers = _editorSortingLayers ?? Array.Empty<string>()
            };

            // Serialize, cache, then send
            string responseJson = JsonUtility.ToJson(response);
            lock (IndexCacheLock)
            {
                _cachedIndexJson = responseJson;
                _indexCachedAt = DateTime.UtcNow;
            }
            byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = responseBytes.Length;
            context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
        }

        private static void HandlePatches(HttpListenerContext context)
        {
            // Play mode guard: block script compilation during Play mode (Bezi parity)
            if (EditorApplication.isPlaying)
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Cannot apply patches during Play Mode. Exit Play Mode first." });
                return;
            }

            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body" });
                return;
            }

            FilePatch[] patches;
            try
            {
                var wrapper = JsonUtility.FromJson<PatchRequestWrapper>(body);
                patches = wrapper?.patches ?? Array.Empty<FilePatch>();
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 400, new ErrorResponse { error = $"Invalid JSON: {ex.Message}" });
                return;
            }

            _pendingPatchResults = new List<PatchResult>();
            _patchDoneEvent = new ManualResetEvent(false);

            var patchesToApply = new List<FilePatch>();
            foreach (var p in patches)
            {
                if (p != null && !string.IsNullOrWhiteSpace(p.filePath))
                    patchesToApply.Add(p);
            }

            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() => ApplyPatchesOnMainThread(patchesToApply));
            }

            _patchDoneEvent.WaitOne(30000);

            SendJson(context.Response, 200, new PatchesResponse { results = _pendingPatchResults.ToArray() });
        }

        /// <summary>
        /// POST /validate — validates patches without writing to disk.
        /// Returns syntax errors for each .cs file patch.
        /// </summary>
        private static void HandleValidate(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body" });
                return;
            }

            FilePatch[] patches;
            bool semantic = false;
            try
            {
                var wrapper = JsonUtility.FromJson<ValidateRequestWrapper>(body);
                patches = wrapper?.patches ?? Array.Empty<FilePatch>();
                semantic = wrapper?.semantic ?? false;
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 400, new ErrorResponse { error = $"Invalid JSON: {ex.Message}" });
                return;
            }

            ValidationFileResult[] results;
            if (semantic)
                results = CodeValidator.ValidatePatchesCombined(patches);
            else
                results = CodeValidator.ValidatePatches(patches);
            SendJson(context.Response, 200, new ValidateResponse { results = results });
        }

        /// <summary>
        /// GET /action-schema — returns typed action schema definitions for LLM prompts.
        /// </summary>
        private static void HandleActionSchema(HttpListenerContext context)
        {
            string format = context.Request.QueryString["format"] ?? "typescript";
            string result;
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                result = ActionSchemaGenerator.GenerateJsonSchema(SupportedActions);
            else
                result = ActionSchemaGenerator.GenerateDefinitions(SupportedActions);
            SendJson(context.Response, 200, new ActionSchemaResponse { schema = result, format = format });
        }

        /// <summary>
        /// POST /validate-actions — validates actions without executing them.
        /// Runs on main thread to query scene hierarchy.
        /// </summary>
        private static void HandleValidateActions(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body" });
                return;
            }

            EditorAction[] actions;
            try
            {
                var wrapper = JsonUtility.FromJson<ActionsRequestWrapper>(body);
                actions = wrapper?.actions ?? Array.Empty<EditorAction>();
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 400, new ErrorResponse { error = $"Invalid JSON: {ex.Message}" });
                return;
            }

            // Must run on main thread to access scene hierarchy
            ActionValidationResult[] validationResults = null;
            var doneEvent = new ManualResetEvent(false);
            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        validationResults = ActionValidator.ValidateActions(actions);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CoBuddy] Action validation error: {ex.Message}");
                        validationResults = Array.Empty<ActionValidationResult>();
                    }
                    finally
                    {
                        doneEvent.Set();
                    }
                });
            }
            doneEvent.WaitOne(10000);

            SendJson(context.Response, 200, new ValidateActionsResponse
            {
                results = validationResults ?? Array.Empty<ActionValidationResult>()
            });
        }

        /// <summary>
        /// POST /screenshot — captures Game View, Scene View, or asset preview.
        /// Body: { "type": "scene" | "game" | "asset", "assetPath": "Assets/..." }
        /// Returns: { "success": true, "base64": "...", "width": N, "height": N, "format": "jpeg" }
        /// </summary>
        private static void HandleScreenshot(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            ScreenshotRequest req = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { req = JsonUtility.FromJson<ScreenshotRequest>(body); } catch { }
            }

            string captureType = req?.type ?? "scene";
            ScreenshotResult result = null;
            var doneEvent = new ManualResetEvent(false);

            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        switch (captureType.ToLowerInvariant())
                        {
                            case "game":
                                result = ImageCapture.CaptureGameView();
                                break;
                            case "asset":
                                result = ImageCapture.CaptureAssetPreview(req?.assetPath);
                                break;
                            case "scene":
                            default:
                                result = ImageCapture.CaptureSceneView();
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        result = new ScreenshotResult { success = false, message = ex.Message };
                    }
                    finally
                    {
                        doneEvent.Set();
                    }
                });
            }
            doneEvent.WaitOne(10000);

            if (result == null)
                result = new ScreenshotResult { success = false, message = "Capture timed out" };

            SendJson(context.Response, 200, result);
        }

        [Serializable]
        private class ScreenshotRequest
        {
            public string type;      // "scene", "game", "asset"
            public string assetPath; // For asset preview
        }

        private static void HandleDecompileType(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body" });
                return;
            }
            try
            {
                string resultJson = DispatchWsRpc("decompileType", body);
                context.Response.ContentType = "application/json";
                byte[] bytes = Encoding.UTF8.GetBytes(resultJson);
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 500, new ErrorResponse { error = ex.Message });
            }
        }

        private static void HandleSerialize(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body" });
                return;
            }
            try
            {
                string resultJson = DispatchWsRpc("serializeAssets", body);
                context.Response.ContentType = "application/json";
                byte[] bytes = Encoding.UTF8.GetBytes(resultJson);
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 500, new ErrorResponse { error = ex.Message });
            }
        }

        private static void HandleMonoBehaviourCode(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body" });
                return;
            }
            try
            {
                var req = JsonUtility.FromJson<MonoBehaviourCodeRequest>(body);
                var types = req?.componentTypes ?? Array.Empty<string>();
                // Delegate to same logic as WS RPC via DispatchWsRpc
                string resultJson = DispatchWsRpc("getMonoBehaviourCode", body);
                context.Response.ContentType = "application/json";
                byte[] bytes = Encoding.UTF8.GetBytes(resultJson);
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 500, new ErrorResponse { error = ex.Message });
            }
        }

        private static void ApplyPatchesOnMainThread(List<FilePatch> patches)
        {
            try
            {
                RuntimeConsoleTracker.Clear();
                var batch = new List<LastAppliedChange>();
                bool deferRefresh = patches.Count > 1;
                foreach (var patch in patches)
                {
                    try
                    {
                        var change = PatchApplier.ApplyPatch(patch, deferRefresh);
                        batch.Add(change);
                        _pendingPatchResults.Add(new PatchResult
                        {
                            filePath = patch.filePath,
                            success = true,
                            message = change.syntaxErrors != null && change.syntaxErrors.Length > 0
                                ? $"Applied (with {change.syntaxErrors.Length} syntax warning(s))"
                                : "Applied",
                            originalContent = change.previousContent ?? "",
                            newContent = patch.newContent ?? "",
                            syntaxErrors = change.syntaxErrors
                        });
                    }
                    catch (Exception ex)
                    {
                        _pendingPatchResults.Add(new PatchResult
                        {
                            filePath = patch.filePath ?? "?",
                            success = false,
                            message = ex.Message
                        });
                    }
                }
                if (batch.Count > 0)
                {
                    _batchStack.Add(batch);
                    if (deferRefresh)
                    {
                        // Use targeted imports instead of global Refresh — much faster for 1-5 files
                        try
                        {
                            AssetDatabase.StartAssetEditing(); // Batch mode — suppress auto-import during loop
                            foreach (var change in batch)
                            {
                                string unityPath = (change.filePath ?? "").Replace('\\', '/');
                                if (!string.IsNullOrEmpty(unityPath))
                                    AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.Default);
                            }
                        }
                        finally
                        {
                            AssetDatabase.StopAssetEditing(); // Triggers single batched import
                        }
                    }
                }
            }
            finally
            {
                _patchDoneEvent?.Set();
            }
        }

        private static void HandleActions(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body" });
                return;
            }

            EditorAction[] actions;
            try
            {
                var wrapper = JsonUtility.FromJson<ActionsRequestWrapper>(body);
                actions = wrapper?.actions ?? Array.Empty<EditorAction>();
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 400, new ErrorResponse { error = $"Invalid JSON: {ex.Message}" });
                return;
            }

            var results = ExecuteActionsCore(actions);
            SendJson(context.Response, 200, new ActionsResponse
            {
                results = results,
                undoGroup = SceneActionExecutor.LastUndoGroup,
                lastSuccessfulActionIndex = SceneActionExecutor.LastSuccessfulActionIndex
            });
        }

        /// <summary>
        /// Core action execution — shared by HTTP and WebSocket handlers.
        /// Queues actions to main thread, waits for completion (including deferred), returns results.
        /// </summary>
        private static ActionResult[] ExecuteActionsCore(EditorAction[] actions)
        {
            _pendingActionResults = null;
            _actionsDoneEvent = new ManualResetEvent(false);

            _currentActionsCts?.Dispose();
            _currentActionsCts = new CancellationTokenSource();
            _operationInProgress = true;
            _operationProgress = 0f;
            _operationStatus = "Starting...";

            var actionsToRun = new List<EditorAction>();
            foreach (var a in actions)
            {
                if (a != null && !string.IsNullOrWhiteSpace(a.action))
                    actionsToRun.Add(a);
            }

            var actionsArray = actionsToRun.ToArray();
            Func<bool> cancelCheck = () => _currentActionsCts?.Token.IsCancellationRequested == true;
            Action<float, string> onProgress = (p, s) => { _operationProgress = p; _operationStatus = s ?? ""; };

            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    // Capture editor state so we can restore on catastrophic error
                    var snapshot = EditorSnapshot.Capture(restoreOnDispose: false);
                    bool anyFatalError = false;
                    try
                    {
                        var (results, revertRecords, deferred) = SceneActionExecutor.ExecuteActions(actionsArray, cancelCheck, onProgress);
                        _pendingActionResults = results;
                        if (revertRecords != null && revertRecords.Count > 0)
                            _actionBatchStack.Add(revertRecords);

                        // Auto-save scenes and assets after successful actions (Bezi parity)
                        bool anySuccess = results != null && results.Any(r => r.success);
                        if (anySuccess && deferred == null || (deferred != null && deferred.Count == 0))
                        {
                            try
                            {
                                EditorSceneManager.SaveOpenScenes();
                                AssetDatabase.SaveAssets();
                            }
                            catch (Exception saveEx)
                            {
                                Debug.LogWarning($"[CoBuddy] Auto-save after actions failed: {saveEx.Message}");
                            }
                        }

                        if (deferred != null && deferred.Count > 0)
                        {
                            // Capture the undo group from the initial batch so deferred actions join it
                            int deferredUndoGroup = SceneActionExecutor.LastUndoGroup;
                            void RunDeferred(object _)
                            {
                                CompilationPipeline.compilationFinished -= RunDeferred;
                                SceneActionExecutor.CurrentCancelCheck = cancelCheck;
                                try
                                {
                                    // Continue in the same undo group as the initial batch
                                    if (deferredUndoGroup >= 0)
                                        Undo.SetCurrentGroupName("CoBuddy Actions (deferred)");

                                    for (int i = 0; i < deferred.Count; i++)
                                    {
                                        var (idx, action) = deferred[i];
                                        if (cancelCheck())
                                        {
                                            for (int j = i; j < deferred.Count; j++)
                                                _pendingActionResults[deferred[j].Item1] = new ActionResult { success = false, message = "Operation cancelled", path = "" };
                                            break;
                                        }
                                        onProgress((i + 1) / (float)deferred.Count, $"Deferred: {action?.action ?? ""}");
                                        var pr = idx > 0 ? _pendingActionResults.Take(idx).ToArray() : Array.Empty<ActionResult>();
                                        var (result, record) = SceneActionExecutor.ExecuteSingleAction(action, pr);
                                        _pendingActionResults[idx] = result;
                                        if (record != null && _actionBatchStack.Count > 0)
                                            _actionBatchStack[_actionBatchStack.Count - 1].Add(record);
                                    }

                                    // Collapse deferred ops into the same group
                                    if (deferredUndoGroup >= 0)
                                        Undo.CollapseUndoOperations(deferredUndoGroup);

                                    // Auto-save after deferred actions
                                    try
                                    {
                                        EditorSceneManager.SaveOpenScenes();
                                        AssetDatabase.SaveAssets();
                                    }
                                    catch { }
                                }
                                catch (Exception ex)
                                {
                                    foreach (var (idx, _) in deferred)
                                        _pendingActionResults[idx] = new ActionResult { success = false, message = ex.Message, path = "" };
                                }
                                finally
                                {
                                    SceneActionExecutor.CurrentCancelCheck = null;
                                    ClearOperationState();
                                    _actionsDoneEvent?.Set();
                                }
                            }
                            if (EditorApplication.isCompiling)
                                CompilationPipeline.compilationFinished += RunDeferred;
                            else
                                EditorApplication.delayCall += () => RunDeferred(null);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        anyFatalError = true;
                        _pendingActionResults = new[] { new ActionResult { success = false, message = ex.Message, path = "" } };
                    }
                    finally
                    {
                        // Restore editor state if actions caused a fatal error
                        if (anyFatalError)
                        {
                            try { snapshot.Restore(); }
                            catch (Exception snapEx) { Debug.LogWarning($"[CoBuddy] Snapshot restore failed: {snapEx.Message}"); }
                        }
                        ClearOperationState();
                    }
                    _actionsDoneEvent?.Set();
                });
            }

            _actionsDoneEvent.WaitOne(60000);
            return _pendingActionResults ?? Array.Empty<ActionResult>();
        }

        private static void HandleActionsStatus(HttpListenerContext context)
        {
            var cts = _currentActionsCts;
            SendJson(context.Response, 200, new ActionsStatusResponse
            {
                inProgress = _operationInProgress,
                progress = _operationProgress,
                status = _operationStatus ?? "",
                cancellationRequested = cts != null && cts.Token.IsCancellationRequested
            });
        }

        private static void HandleActionsCancel(HttpListenerContext context)
        {
            var cts = _currentActionsCts;
            if (cts != null && !cts.Token.IsCancellationRequested)
            {
                try { cts.Cancel(); } catch { }
            }
            SendJson(context.Response, 200, new ActionsCancelResponse { cancelled = true });
        }

        private static void ClearOperationState()
        {
            _operationInProgress = false;
            _operationProgress = 0f;
            _operationStatus = "";
            _currentActionsCts?.Dispose();
            _currentActionsCts = null;
        }

        private static void HandleRevertActions(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            int revertCount = 1;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var req = JsonUtility.FromJson<RevertRequest>(body);
                    if (req != null && req.count > 0)
                        revertCount = req.count;
                }
                catch { }
            }

            int batchesToRevert = Math.Min(revertCount, _actionBatchStack.Count);
            if (batchesToRevert == 0)
            {
                SendJson(context.Response, 200, new RevertResponse { reverted = true, count = 0, batchesReverted = 0 });
                return;
            }

            _revertActionsDoneEvent = new ManualResetEvent(false);
            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        for (int b = 0; b < batchesToRevert && _actionBatchStack.Count > 0; b++)
                        {
                            var batch = _actionBatchStack[_actionBatchStack.Count - 1];
                            _actionBatchStack.RemoveAt(_actionBatchStack.Count - 1);
                            SceneActionExecutor.RevertActions(batch);
                        }
                    }
                    finally
                    {
                        _revertActionsDoneEvent?.Set();
                    }
                });
            }
            _revertActionsDoneEvent.WaitOne(30000);
            SendJson(context.Response, 200, new RevertResponse { reverted = true, batchesReverted = batchesToRevert });
        }

        private static void HandleUndoGroup(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            int undoGroup = -1;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var req = JsonUtility.FromJson<UndoGroupRequest>(body);
                    if (req != null) undoGroup = req.undoGroup;
                }
                catch { }
            }

            _revertActionsDoneEvent = new ManualResetEvent(false);
            bool undoResult = false;
            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        undoResult = SceneActionExecutor.RevertUndoGroup(undoGroup);
                    }
                    finally
                    {
                        _revertActionsDoneEvent?.Set();
                    }
                });
            }
            _revertActionsDoneEvent.WaitOne(30000);
            SendJson(context.Response, 200, new UndoGroupResponse { success = undoResult, undoGroup = undoGroup >= 0 ? undoGroup : SceneActionExecutor.LastUndoGroup });
        }

        private static void HandleRevert(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            int revertCount = 1;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var req = JsonUtility.FromJson<RevertRequest>(body);
                    if (req != null && req.count > 0)
                        revertCount = req.count;
                }
                catch { }
            }

            int batchesToRevert = Math.Min(revertCount, _batchStack.Count);
            if (batchesToRevert == 0)
            {
                SendJson(context.Response, 200, new RevertResponse { reverted = true, count = 0, batchesReverted = 0 });
                return;
            }

            _patchDoneEvent = new ManualResetEvent(false);
            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() => RevertOnMainThread(batchesToRevert));
            }
            _patchDoneEvent.WaitOne(30000);
            SendJson(context.Response, 200, new RevertResponse { reverted = true, batchesReverted = batchesToRevert });
        }

        private static void RevertOnMainThread(int batchesToRevert)
        {
            try
            {
                for (int b = 0; b < batchesToRevert && _batchStack.Count > 0; b++)
                {
                    var batch = _batchStack[_batchStack.Count - 1];
                    _batchStack.RemoveAt(_batchStack.Count - 1);
                    for (int i = batch.Count - 1; i >= 0; i--)
                    {
                        PatchApplier.RevertLastChange(batch[i]);
                    }
                }
            }
            finally
            {
                _patchDoneEvent?.Set();
            }
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            if (request.InputStream == null)
                return "";
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return reader.ReadToEnd();
        }

        private static void SendJson(HttpListenerResponse response, int statusCode, object obj)
        {
            string json = JsonUtility.ToJson(obj);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private static void ProcessMainThreadQueue()
        {
            lock (QueueLock)
            {
                while (MainThreadQueue.Count > 0)
                {
                    var action = MainThreadQueue.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
        }

        [Serializable]
        private class PingRequest
        {
            public string projectPath;
        }

        [Serializable]
        private class PingResponse
        {
            public bool connected;
            public string projectPath;
            public bool projectMatch;
        }

        [Serializable]
        private class PatchRequestWrapper
        {
            public FilePatch[] patches;
        }

        [Serializable]
        private class PatchResult
        {
            public string filePath;
            public bool success;
            public string message;
            public string originalContent;
            public string newContent;
            public ValidationError[] syntaxErrors;
        }

        [Serializable]
        private class IndexScriptEntry
        {
            public string path;
            public string content;
            public int contentLength;
        }

        [Serializable]
        private class IndexAsmdefEntry
        {
            public string path;
        }

        [Serializable]
        private class IndexPrefabEntry
        {
            public string path;
            public string content;
        }

        [Serializable]
        private class IndexMaterialEntry
        {
            public string path;
            public string content;
        }

        [Serializable]
        private class IndexShaderEntry
        {
            public string path;
            public string content;
        }

        [Serializable]
        private class IndexInputActionEntry
        {
            public string path;
            public string content;
        }

        [Serializable]
        private class IndexScriptableObjectEntry
        {
            public string path;
            public string content;
            public string assetType;
        }

        [Serializable]
        private class IndexPackageEntry
        {
            public string name;
            public string version;
            public string path;
        }

        [Serializable]
        private class IndexComponentEntry
        {
            public string gameObjectPath;
            public string componentType;
            public string[] serializedFields;
        }

        [Serializable]
        private class IndexResponse
        {
            public IndexScriptEntry[] scripts;
            public IndexAsmdefEntry[] asmdefs;
            public IndexPrefabEntry[] prefabs;
            public IndexMaterialEntry[] materials;
            public IndexShaderEntry[] shaders;
            public IndexInputActionEntry[] inputActions;
            public IndexScriptableObjectEntry[] scriptableObjects;
            public IndexPackageEntry[] packages;
            public IndexComponentEntry[] components;
            public string manifest;
            public int scriptCount;
            public int edgeCount;
            public string[] sceneRoots;
            public bool isPlayMode;
            // EditorContext — richer project metadata
            public string renderPipeline;
            public string rendererType;
            public string[] tags;
            public string[] layers;
            public string[] sortingLayers;
        }

        [Serializable]
        private class PatchesResponse
        {
            public PatchResult[] results;
        }

        [Serializable]
        private class ValidateResponse
        {
            public ValidationFileResult[] results;
        }

        [Serializable]
        private class ValidateRequestWrapper
        {
            public FilePatch[] patches;
            public bool semantic;
        }

        [Serializable]
        private class ActionSchemaResponse
        {
            public string schema;
            public string format;
        }

        [Serializable]
        private class ActionSchemaRequest
        {
            public string format;
        }

        [Serializable]
        private class ValidateActionsResponse
        {
            public ActionValidationResult[] results;
        }

        [Serializable]
        private class ActionsRequestWrapper
        {
            public EditorAction[] actions;
        }

        [Serializable]
        private class ActionsResponse
        {
            public ActionResult[] results;
            public int undoGroup = -1;
            public int lastSuccessfulActionIndex = -1;
        }

        [Serializable]
        private class ActionsStatusResponse
        {
            public bool inProgress;
            public float progress;
            public string status;
            public bool cancellationRequested;
        }

        [Serializable]
        private class ActionsCancelResponse
        {
            public bool cancelled;
        }

        [Serializable]
        private class ErrorResponse
        {
            public string error;
        }

        [Serializable]
        private class CompileStatusError
        {
            public string file;
            public int line;
            public int column;
            public string message;
        }

        [Serializable]
        private class RuntimeConsoleError
        {
            public string message;
            public string stackTrace;
            public string logType;
        }

        [Serializable]
        private class CompileStatusResponse
        {
            public bool isCompiling;
            public bool isPlayMode;
            public CompileStatusError[] errors;
            public RuntimeConsoleError[] runtimeErrors;
        }

        private static void HandleCompileStatus(HttpListenerContext context)
        {
            var errors = CompilationStatusTracker.GetErrors();
            var errorEntries = new List<CompileStatusError>();
            foreach (var e in errors)
            {
                errorEntries.Add(new CompileStatusError
                {
                    file = e.file ?? "",
                    line = e.line,
                    column = e.column,
                    message = e.message ?? ""
                });
            }
            var runtimeEntries = RuntimeConsoleTracker.GetRecentErrors();
            var runtimeErrorEntries = new List<RuntimeConsoleError>();
            foreach (var e in runtimeEntries)
            {
                runtimeErrorEntries.Add(new RuntimeConsoleError
                {
                    message = e.message ?? "",
                    stackTrace = e.stackTrace ?? "",
                    logType = e.logType ?? ""
                });
            }
            SendJson(context.Response, 200, new CompileStatusResponse
            {
                isCompiling = CompilationStatusTracker.IsCompiling,
                isPlayMode = Application.isPlaying,
                errors = errorEntries.ToArray(),
                runtimeErrors = runtimeErrorEntries.ToArray()
            });
        }

        [Serializable]
        private class ConsoleResponse
        {
            public string content;
            public string logFilePath;
        }

        private static void HandleConsole(HttpListenerContext context)
        {
            var content = ConsoleLogExporter.GetLogContent();
            var logFilePath = ConsoleLogExporter.GetLogFilePath() ?? "";
            SendJson(context.Response, 200, new ConsoleResponse { content = content ?? "", logFilePath = logFilePath });
        }

        [Serializable]
        private class RevertRequest
        {
            public int count;
        }

        [Serializable]
        private class RevertResponse
        {
            public bool reverted;
            public int count;
            public int batchesReverted;
        }

        [Serializable]
        private class DependencyRequest
        {
            public string assetPath;
            public bool buildIndex;
        }

        [Serializable]
        private class DependencyResponse
        {
            public bool ready;
            public int assetCount;
        }

        [Serializable]
        private class DependencyQueryResponse
        {
            public string assetPath;
            public string[] referencedBy;
            public string[] dependsOn;
            public bool ready;
        }

        [Serializable]
        private class UndoGroupRequest
        {
            public int undoGroup = -1;
        }

        [Serializable]
        private class UndoGroupResponse
        {
            public bool success;
            public int undoGroup;
        }

        [Serializable]
        private class CheckpointRequest
        {
            public string[] assetPaths;
            public int[] undoGroups;
        }

        [Serializable]
        private class CheckpointResponse
        {
            public bool success;
            public int revertedAssets;
            public int revertedUndoGroups;
        }

        [Serializable]
        private class VersionResponse
        {
            public string version;
            public string[] supportedActions;
        }

        [Serializable]
        private class UnusedAssetEntry
        {
            public string path;
            public string extension;
            public string assetType;
            public bool isFolder;
            public string category;
        }

        [Serializable]
        private class UnusedAssetsResponse
        {
            public string[] unusedPaths;
            public UnusedAssetEntry[] unusedAssets;
        }

        private static readonly string[] SupportedActions = new[]
        {
            "createGameObject", "addComponent", "instantiatePrefab", "createPrefab", "openScene", "createScene",
            "destroyGameObject", "createTag", "createPrimitive", "reparentGameObject", "updateGameObject",
            "removeComponent", "setRectTransformLayout", "createScriptableObject", "createMaterial", "createShader",
            "updateMaterial", "printSceneHierarchy", "printGameObjects", "printAssets", "updateAssetImporter",
            "updateCanvas", "updateUIImage", "updateUIText", "createCircleSprite",
            "createUXML", "createUSS", "createUIDocument",
            "createAnimationClip", "addAnimator",
            "createUILoopingScript",
            "copyAsset", "moveAsset", "deleteAsset", "createFolder", "importAsset", "createTexture", "createSprite",
            "executeMenuItem", "enterPlayMode", "selectObject", "focusSceneView",
            "addRigidbody", "addCollider", "setColliderSize", "createPhysicMaterial",
            "createInputActions", "assignInputActions",
            "createScript", "createAssemblyDefinition", "createPackageManifest",
            "createTilemap", "createTile", "setTile", "setTiles",
            "createTimeline", "addTimelineTrack", "addTimelineClip", "addPlayableDirector",
            "createAudioClip", "addAudioSource", "createAudioMixer",
            "createLight", "updateLight", "createRenderTexture",
            "addParticleSystem", "updateParticleSystem",
            "addCamera", "addCinemachineVirtualCamera",
            "setBuildScenes", "setPlayerSettings", "switchPlatform", "executeBuild",
            "createPrefabVariant", "unpackPrefab",
            "createPanelSettings", "createThemeStyleSheet",
            "addRigidbody2D", "addCollider2D", "addSpriteRenderer", "setSortingLayer", "createSortingLayer", "createSpriteAtlas",
            "createCanvas", "addEventSystem", "addLayoutGroup", "addScrollRect",
            "createLayer",
            "addLineRenderer", "addTrailRenderer", "addReflectionProbe", "addLightProbeGroup",
            "bakeNavMesh", "addNavMeshAgent", "addNavMeshObstacle",
            "createAnimatorOverrideController", "createBlendTree", "addAvatarMask",
            "addJoint", "addCharacterController", "addCloth",
            "createTerrain", "addTerrainLayer",
            "addVideoPlayer", "addTextMeshPro", "createTMPFontAsset",
            "addPackage", "removePackage",
            "loadSceneAdditive", "unloadScene",
            "createVFXGraph", "createShaderGraph", "createSpriteAnimation", "createRuleTile",
            "markAddressable", "createAddressablesGroup", "createLocalizationTable",
            "setComponentProperty", "assignMaterial",
            "generateInputActionsCSharp"
        };

        private static void HandleDependencies(HttpListenerContext context)
        {
            string body = ReadBody(context.Request);
            if (string.IsNullOrWhiteSpace(body))
            {
                SendJson(context.Response, 400, new ErrorResponse { error = "Empty body. Send {\"assetPath\":\"...\"} or {\"buildIndex\":true}" });
                return;
            }

            try
            {
                var req = JsonUtility.FromJson<DependencyRequest>(body);

                // Build/rebuild index on demand
                if (req.buildIndex)
                {
                    _dependencyDoneEvent = new ManualResetEvent(false);
                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            try { AssetDependencyIndex.BuildIndex(); }
                            catch { }
                            finally { _dependencyDoneEvent?.Set(); }
                        });
                    }
                    _dependencyDoneEvent.WaitOne(60000);
                    SendJson(context.Response, 200, new DependencyResponse
                    {
                        ready = AssetDependencyIndex.IsReady,
                        assetCount = AssetDependencyIndex.AssetCount
                    });
                    return;
                }

                // Query dependencies for a specific asset
                if (!string.IsNullOrWhiteSpace(req.assetPath))
                {
                    var referencedBy = AssetDependencyIndex.GetReferencedBy(req.assetPath, triggerBuild: true);
                    var dependsOn = AssetDependencyIndex.GetDependencies(req.assetPath);
                    SendJson(context.Response, 200, new DependencyQueryResponse
                    {
                        assetPath = req.assetPath,
                        referencedBy = referencedBy,
                        dependsOn = dependsOn,
                        ready = AssetDependencyIndex.IsReady
                    });
                    return;
                }

                SendJson(context.Response, 400, new ErrorResponse { error = "Provide 'assetPath' or 'buildIndex'" });
            }
            catch (Exception ex)
            {
                SendJson(context.Response, 500, new ErrorResponse { error = ex.Message });
            }
        }

        private static ManualResetEvent _dependencyDoneEvent;

        private static void HandleUnusedAssets(HttpListenerContext context)
        {
            _unusedAssetsResult = null;
            _unusedAssetsEvent = new ManualResetEvent(false);
            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        var guids = AssetDatabase.FindAssets("t:Object", new[] { "Assets" });
                        foreach (var guid in guids)
                        {
                            var p = AssetDatabase.GUIDToAssetPath(guid);
                            if (string.IsNullOrEmpty(p) || p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                                continue;
                            allPaths.Add(p);
                        }

                        foreach (var p in allPaths.ToList())
                        {
                            try
                            {
                                var deps = AssetDatabase.GetDependencies(p, true);
                                foreach (var d in deps ?? Array.Empty<string>())
                                {
                                    if (string.IsNullOrEmpty(d) || d.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    if (string.Equals(d, p, StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    referenced.Add(d);
                                }
                            }
                            catch { }
                        }

                        foreach (var scene in EditorBuildSettings.scenes ?? Array.Empty<EditorBuildSettingsScene>())
                        {
                            if (scene == null || !scene.enabled || string.IsNullOrEmpty(scene.path))
                                continue;
                            try
                            {
                                var deps = AssetDatabase.GetDependencies(scene.path, true);
                                foreach (var d in deps ?? Array.Empty<string>())
                                {
                                    if (!string.IsNullOrEmpty(d) && !d.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                                        referenced.Add(d);
                                }
                            }
                            catch { }
                        }

                        var unused = allPaths.Where(x => !referenced.Contains(x)).OrderBy(x => x).ToList();
                        _unusedAssetsResult = unused;
                        _unusedAssetsEntries = BuildUnusedAssetEntries(unused);
                    }
                    catch (Exception ex)
                    {
                        _unusedAssetsResult = new List<string>();
                        _unusedAssetsEntries = new List<UnusedAssetEntry>();
                        Debug.LogWarning($"[CoBuddy] Unused assets scan failed: {ex.Message}");
                    }
                    finally
                    {
                        _unusedAssetsEvent?.Set();
                    }
                });
            }
            _unusedAssetsEvent.WaitOne(120000);
            var paths = _unusedAssetsResult?.ToArray() ?? Array.Empty<string>();
            var entries = _unusedAssetsEntries?.ToArray() ?? Array.Empty<UnusedAssetEntry>();
            SendJson(context.Response, 200, new UnusedAssetsResponse { unusedPaths = paths, unusedAssets = entries });
        }

        [Serializable]
        private class SelectionItem
        {
            public string type;
            public string path;
            public string title;
        }

        [Serializable]
        private class SelectionResponse
        {
            public SelectionItem[] items;
        }

        private static void HandleSelection(HttpListenerContext context)
        {
            // Use local variables captured by closure to avoid race conditions when
            // multiple concurrent /selection requests overwrite the shared static fields.
            SelectionItem[] localResult = null;
            var localEvent = new ManualResetEvent(false);
            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        var items = new List<SelectionItem>();
                        var go = Selection.activeGameObject;
                        if (go != null)
                        {
                            var path = GetGameObjectPath(go);
                            if (!string.IsNullOrEmpty(path))
                            {
                                items.Add(new SelectionItem { type = "sceneObject", path = path, title = go.name });
                            }
                        }
                        var obj = Selection.activeObject;
                        if (obj != null && items.Count < 2)
                        {
                            var assetPath = AssetDatabase.GetAssetPath(obj);
                            if (!string.IsNullOrEmpty(assetPath) && !assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                            {
                                var assetType = GetAssetTypeFromExtension(Path.GetExtension(assetPath));
                                var title = Path.GetFileNameWithoutExtension(assetPath);
                                if (assetType != null && !items.Any(i => i.type == assetType && string.Equals(i.path, assetPath, StringComparison.OrdinalIgnoreCase)))
                                {
                                    items.Add(new SelectionItem { type = assetType, path = assetPath.Replace('\\', '/'), title = title ?? assetPath });
                                }
                            }
                        }
                        localResult = items.Take(2).ToArray();
                    }
                    catch (Exception ex)
                    {
                        localResult = Array.Empty<SelectionItem>();
                        Debug.LogWarning($"[CoBuddy] Selection failed: {ex.Message}");
                    }
                    finally
                    {
                        localEvent.Set();
                    }
                });
            }
            localEvent.WaitOne(5000);
            var result = localResult ?? Array.Empty<SelectionItem>();
            SendJson(context.Response, 200, new SelectionResponse { items = result });
        }

        private static void HandleInspector(HttpListenerContext context)
        {
            // Use local variables captured by closure — same pattern as HandleSelection.
            string localInspectorResult = null;
            var localInspectorEvent = new ManualResetEvent(false);
            lock (QueueLock)
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        var go = Selection.activeGameObject;
                        if (go == null)
                        {
                            localInspectorResult = "{\"name\":null,\"components\":[]}";
                            return;
                        }

                        var sb = new System.Text.StringBuilder();
                        sb.Append("{");
                        sb.Append("\"name\":\"").Append(EscapeJson(go.name)).Append("\",");
                        sb.Append("\"path\":\"").Append(EscapeJson(GetGameObjectPath(go))).Append("\",");
                        sb.Append("\"active\":").Append(go.activeInHierarchy ? "true" : "false").Append(",");
                        sb.Append("\"tag\":\"").Append(EscapeJson(go.tag)).Append("\",");
                        sb.Append("\"layer\":").Append(go.layer).Append(",");
                        sb.Append("\"components\":[");

                        var components = go.GetComponents<Component>();
                        bool firstComp = true;
                        foreach (var comp in components)
                        {
                            if (comp == null) continue;
                            var compType = comp.GetType();
                            string typeName = compType.Name;

                            if (!firstComp) sb.Append(",");
                            firstComp = false;
                            sb.Append("{");
                            sb.Append("\"type\":\"").Append(EscapeJson(typeName)).Append("\",");

                            // enabled field: null for Transform (no enabled property), bool otherwise
                            var behaviourComp = comp as UnityEngine.Behaviour;
                            if (behaviourComp != null)
                                sb.Append("\"enabled\":").Append(behaviourComp.enabled ? "true" : "false").Append(",");
                            else
                                sb.Append("\"enabled\":null,");

                            sb.Append("\"fields\":[");

                            if (comp is Transform t)
                            {
                                // Transform: read directly to avoid quaternion internals
                                var pos = t.localPosition;
                                var rot = t.localEulerAngles;
                                var scale = t.localScale;
                                sb.Append("{\"name\":\"position\",\"displayName\":\"Position\",\"type\":\"vector3\",\"value\":{\"x\":").Append(pos.x).Append(",\"y\":").Append(pos.y).Append(",\"z\":").Append(pos.z).Append("}}");
                                sb.Append(",{\"name\":\"rotation\",\"displayName\":\"Rotation\",\"type\":\"vector3\",\"value\":{\"x\":").Append(rot.x).Append(",\"y\":").Append(rot.y).Append(",\"z\":").Append(rot.z).Append("}}");
                                sb.Append(",{\"name\":\"scale\",\"displayName\":\"Scale\",\"type\":\"vector3\",\"value\":{\"x\":").Append(scale.x).Append(",\"y\":").Append(scale.y).Append(",\"z\":").Append(scale.z).Append("}}");
                            }
                            else
                            {
                                var so = new SerializedObject(comp);
                                var prop = so.GetIterator();
                                bool enterChildren = true;
                                int fieldCount = 0;
                                bool firstField = true;
                                while (prop.NextVisible(enterChildren) && fieldCount < 30)
                                {
                                    enterChildren = false;
                                    if (prop.name == "m_Script") continue;
                                    if (prop.depth != 0) continue;

                                    string fieldJson = null;
                                    switch (prop.propertyType)
                                    {
                                        case SerializedPropertyType.Float:
                                            fieldJson = "\"type\":\"float\",\"value\":" + prop.floatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                                            break;
                                        case SerializedPropertyType.Integer:
                                        case SerializedPropertyType.LayerMask:
                                            fieldJson = "\"type\":\"int\",\"value\":" + prop.intValue;
                                            break;
                                        case SerializedPropertyType.Boolean:
                                            fieldJson = "\"type\":\"bool\",\"value\":" + (prop.boolValue ? "true" : "false");
                                            break;
                                        case SerializedPropertyType.String:
                                            fieldJson = "\"type\":\"string\",\"value\":\"" + EscapeJson(prop.stringValue) + "\"";
                                            break;
                                        case SerializedPropertyType.Color:
                                            var c = prop.colorValue;
                                            fieldJson = "\"type\":\"color\",\"value\":{\"r\":" + c.r.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"g\":" + c.g.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"b\":" + c.b.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"a\":" + c.a.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}";
                                            break;
                                        case SerializedPropertyType.Vector2:
                                            var v2 = prop.vector2Value;
                                            fieldJson = "\"type\":\"vector2\",\"value\":{\"x\":" + v2.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"y\":" + v2.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}";
                                            break;
                                        case SerializedPropertyType.Vector3:
                                            var v3 = prop.vector3Value;
                                            fieldJson = "\"type\":\"vector3\",\"value\":{\"x\":" + v3.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"y\":" + v3.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"z\":" + v3.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}";
                                            break;
                                        case SerializedPropertyType.Vector4:
                                            var v4 = prop.vector4Value;
                                            fieldJson = "\"type\":\"vector4\",\"value\":{\"x\":" + v4.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"y\":" + v4.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"z\":" + v4.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ",\"w\":" + v4.w.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}";
                                            break;
                                        case SerializedPropertyType.Enum:
                                            var enumNames = prop.enumDisplayNames;
                                            var enumIdx = prop.enumValueIndex;
                                            var enumName = (enumNames != null && enumIdx >= 0 && enumIdx < enumNames.Length) ? enumNames[enumIdx] : prop.enumValueIndex.ToString();
                                            fieldJson = "\"type\":\"enum\",\"value\":\"" + EscapeJson(enumName) + "\"";
                                            break;
                                        case SerializedPropertyType.ObjectReference:
                                            var objRef = prop.objectReferenceValue;
                                            if (objRef != null)
                                                fieldJson = "\"type\":\"objectRef\",\"value\":{\"name\":\"" + EscapeJson(objRef.name) + "\",\"objType\":\"" + EscapeJson(objRef.GetType().Name) + "\"}";
                                            else
                                                fieldJson = "\"type\":\"objectRef\",\"value\":null";
                                            break;
                                        default:
                                            continue;
                                    }

                                    if (!firstField) sb.Append(",");
                                    firstField = false;
                                    sb.Append("{\"name\":\"").Append(EscapeJson(prop.name)).Append("\",");
                                    sb.Append("\"displayName\":\"").Append(EscapeJson(prop.displayName)).Append("\",");
                                    sb.Append(fieldJson).Append("}");
                                    fieldCount++;
                                }
                            }

                            sb.Append("]}");
                        }

                        sb.Append("]}");
                        localInspectorResult = sb.ToString();
                    }
                    catch (Exception ex)
                    {
                        localInspectorResult = "{\"name\":null,\"components\":[]}";
                        Debug.LogWarning($"[CoBuddy] Inspector failed: {ex.Message}");
                    }
                    finally
                    {
                        localInspectorEvent.Set();
                    }
                });
            }
            localInspectorEvent.WaitOne(5000);
            var json = localInspectorResult ?? "{\"name\":null,\"components\":[]}";
            byte[] responseBytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.ContentLength64 = responseBytes.Length;
            context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
        }

        /// <summary>
        /// SSE endpoint — holds the connection open and pushes events as they occur.
        /// The ThreadPool dispatch in ListenLoop ensures this doesn't block other requests.
        /// </summary>
        private static void HandleEvents(HttpListenerContext context)
        {
            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["Connection"] = "keep-alive";
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.StatusCode = 200;

            System.IO.Stream stream = null;
            try
            {
                stream = response.OutputStream;
                // Send initial keepalive so the client knows the connection is live
                byte[] hello = Encoding.UTF8.GetBytes(": connected\n\n");
                stream.Write(hello, 0, hello.Length);
                stream.Flush();

                lock (SseClientsLock) { _sseClients.Add(stream); }

                // Keep the connection open until the client disconnects or the server stops.
                // Poll every 15s with a keepalive comment to prevent proxy timeouts.
                while (_running)
                {
                    Thread.Sleep(15000);
                    try
                    {
                        byte[] ping = Encoding.UTF8.GetBytes(": keepalive\n\n");
                        stream.Write(ping, 0, ping.Length);
                        stream.Flush();
                    }
                    catch
                    {
                        break; // client disconnected
                    }
                }
            }
            catch { }
            finally
            {
                lock (SseClientsLock) { if (stream != null) _sseClients.Remove(stream); }
                try { stream?.Close(); } catch { }
                try { response.Close(); } catch { }
            }
        }

        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return "";
            var parts = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                parts.Insert(0, t.gameObject.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        private static string GetAssetTypeFromExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return null;
            var e = ext.ToLowerInvariant();
            if (e == ".cs" || e == ".asmdef") return "script";
            if (e == ".prefab") return "prefab";
            if (e == ".mat") return "material";
            if (e == ".shader" || e == ".cginc" || e == ".hlsl") return "shader";
            if (e == ".inputactions") return "inputActions";
            if (e == ".asset") return "scriptableObject";
            return null;
        }

        private static List<UnusedAssetEntry> BuildUnusedAssetEntries(List<string> paths)
        {
            var entries = new List<UnusedAssetEntry>();
            var generatedPatterns = new[] { "/Temp/", "/Backup/", "/Obsolete/", "/Cache/", "/Old/", "/Deprecated/", "\\Temp\\", "\\Backup\\", "\\Obsolete\\", "\\Cache\\", "\\Old\\", "\\Deprecated\\" };
            var generatedPathPatterns = new[] { "packages/com.unity.", "textmeshpro", "text mesh pro", "cinemachine", "unity.inputsystem", "post processing", "unity.probuilder", "probuilder", "pro grid builder" };
            foreach (var p in paths)
            {
                var ext = Path.GetExtension(p);
                var isFolder = AssetDatabase.IsValidFolder(p);
                var pathLower = p.Replace('\\', '/').ToLowerInvariant();
                var category = "user";
                foreach (var pat in generatedPatterns)
                {
                    if (pathLower.Contains(pat.ToLowerInvariant()))
                    {
                        category = "generated";
                        break;
                    }
                }
                if (category == "user" && (pathLower.Contains("/temp") || pathLower.Contains("backup") || pathLower.Contains("obsolete") || pathLower.Contains("cached")))
                    category = "generated";
                foreach (var pat in generatedPathPatterns)
                {
                    if (pathLower.Contains(pat))
                    {
                        category = "generated";
                        break;
                    }
                }
                if (category == "user" && isFolder)
                {
                    var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    var dir = Path.Combine(projectRoot, p.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        category = "generated";
                }
                var assetType = GetAssetType(p, ext, isFolder);
                entries.Add(new UnusedAssetEntry { path = p, extension = ext ?? "", assetType = assetType, isFolder = isFolder, category = category });
            }
            return entries;
        }

        private static string GetAssetType(string path, string ext, bool isFolder)
        {
            if (isFolder) return "Folder";
            var pathLower = (path ?? "").Replace('\\', '/').ToLowerInvariant();
            try
            {
                var unityType = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (unityType != null)
                {
                    var typeName = unityType.FullName ?? unityType.Name ?? "";
                    if (typeName.Contains("Texture") || typeName.Contains("Texture2D") || typeName.Contains("RenderTexture") || typeName.Contains("Cubemap")) return "Texture";
                    if (typeName.Contains("Sprite")) return "Vector/Sprite";
                    if (typeName.Contains("AudioClip") || typeName.Contains("Audio")) return "Audio";
                    if (typeName.Contains("Material")) return "Material";
                    if (typeName.Contains("GameObject") && pathLower.EndsWith(".prefab")) return "Prefab";
                    if (typeName.Contains("SceneAsset")) return "Scene";
                    if (typeName.Contains("MonoScript")) return "Script";
                    if (typeName.Contains("Shader") || typeName.Contains("ComputeShader")) return "Shader";
                    if (typeName.Contains("AnimationClip") || typeName.Contains("AnimatorController") || typeName.Contains("AnimatorOverrideController")) return "Animation";
                    if (typeName.Contains("ScriptableObject")) return "ScriptableObject";
                    if (typeName.Contains("Model") || typeName.Contains("Mesh") || typeName.Contains("MeshFilter")) return "Model";
                    if (typeName.Contains("Font") || typeName.Contains("TMP_FontAsset") || typeName.Contains("TextMeshPro")) return pathLower.Contains("textmeshpro") ? "TextMeshPro" : "Font";
                    if (typeName.Contains("AudioMixer")) return "AudioMixer";
                    if (typeName.Contains("Playable")) return "Playable";
                    if (typeName.Contains("SpriteMask") || typeName.Contains("GUISkin")) return "UI";
                }
            }
            catch { }
            return GetAssetTypeFromExtension(ext, isFolder);
        }

        private static string GetAssetTypeFromExtension(string ext, bool isFolder)
        {
            if (isFolder) return "Folder";
            if (string.IsNullOrEmpty(ext)) return "Other";
            var e = ext.ToLowerInvariant();
            if (e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".tga" || e == ".psd" || e == ".bmp" || e == ".gif" || e == ".tif" || e == ".tiff" || e == ".exr" || e == ".hdr" || e == ".iff" || e == ".pict") return "Texture";
            if (e == ".svg" || e == ".vector") return "Vector/Sprite";
            if (e == ".wav" || e == ".mp3" || e == ".ogg" || e == ".aiff" || e == ".mod" || e == ".it" || e == ".s3m" || e == ".xm") return "Audio";
            if (e == ".mat") return "Material";
            if (e == ".prefab") return "Prefab";
            if (e == ".unity") return "Scene";
            if (e == ".cs" || e == ".asmdef") return "Script";
            if (e == ".shader" || e == ".cginc" || e == ".hlsl") return "Shader";
            if (e == ".anim" || e == ".controller" || e == ".overrideController") return "Animation";
            if (e == ".asset") return "ScriptableObject";
            if (e == ".fbx" || e == ".obj" || e == ".dae" || e == ".3ds" || e == ".dxf" || e == ".blend") return "Model";
            if (e == ".font" || e == ".ttf" || e == ".otf") return "Font";
            if (e == ".mixer") return "AudioMixer";
            if (e == ".playable") return "Playable";
            if (e == ".mask" || e == ".guiskin") return "UI";
            return "Other";
        }

        private static void HandleVersion(HttpListenerContext context)
        {
            SendJson(context.Response, 200, new VersionResponse
            {
                version = _cachedPluginVersion,
                supportedActions = SupportedActions
            });
        }

        // ──────────────────────────────────────────────────
        // WebSocket client (connects to Electron app's WS server)
        // ──────────────────────────────────────────────────

        private static void StartWebSocketClient()
        {
            if (_wsClient != null) return;
            _wsClient = new WSClient("");
            _wsClient.OnRpcRequest = HandleWsRpcRequest;
            _wsClient.Connect();
        }

        /// <summary>Pause WS client before domain reload (called by CompilationStatusTracker).</summary>
        public static void PauseWebSocket()
        {
            _wsClient?.Pause();
        }

        /// <summary>Resume WS client after domain reload.</summary>
        public static void ResumeWebSocket()
        {
            if (_wsClient == null)
            {
                StartWebSocketClient();
            }
            else
            {
                _wsClient.Resume();
            }
        }

        /// <summary>Update the project path on the WS client (for connection query param).</summary>
        public static void UpdateWebSocketProjectPath(string projectPath)
        {
            _wsClient?.UpdateProjectPath(projectPath);
        }

        /// <summary>Handle an incoming RPC request from the Electron app via WebSocket.</summary>
        private static void HandleWsRpcRequest(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // Quick pre-check: only process messages that look like RPC requests
            if (!message.Contains("\"type\":\"request\"") && !message.Contains("\"type\": \"request\""))
                return;

            try
            {
                var req = JsonUtility.FromJson<WsRpcRequest>(message);
                if (req == null || req.type != "request" || string.IsNullOrWhiteSpace(req.name))
                    return;

                string argsJson = ExtractJsonField(message, "arguments");
                string resultJson = DispatchWsRpc(req.name, argsJson);
                _wsClient?.SendResponse(req.id, resultJson);
            }
            catch (Exception ex)
            {
                if (!WSClient.IsThreadAbortedByDomainReload(ex))
                    Debug.LogWarning($"[CoBuddy] WebSocket RPC error: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts a JSON field value as a raw string from a JSON object.
        /// Handles nested objects/arrays by brace/bracket counting.
        /// </summary>
        private static string ExtractJsonField(string json, string fieldName)
        {
            string key = $"\"{fieldName}\"";
            int keyIdx = json.IndexOf(key, StringComparison.Ordinal);
            if (keyIdx < 0) return null;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && json[start] == ' ') start++;
            if (start >= json.Length) return null;

            char first = json[start];
            if (first == '{' || first == '[')
            {
                char open = first;
                char close = first == '{' ? '}' : ']';
                int depth = 1;
                bool inString = false;
                int i = start + 1;
                while (i < json.Length && depth > 0)
                {
                    char c = json[i];
                    if (inString)
                    {
                        if (c == '\\') { i++; }
                        else if (c == '"') inString = false;
                    }
                    else
                    {
                        if (c == '"') inString = true;
                        else if (c == open) depth++;
                        else if (c == close) depth--;
                    }
                    i++;
                }
                return json.Substring(start, i - start);
            }
            else if (first == '"')
            {
                int end = start + 1;
                while (end < json.Length)
                {
                    if (json[end] == '\\') { end += 2; continue; }
                    if (json[end] == '"') { end++; break; }
                    end++;
                }
                return json.Substring(start, end - start);
            }
            else
            {
                int end = start;
                while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']') end++;
                return json.Substring(start, end - start).Trim();
            }
        }

        /// <summary>
        /// Dispatches a WebSocket RPC call to the appropriate handler.
        /// Returns result as JSON string.
        /// </summary>
        private static string DispatchWsRpc(string name, string arguments)
        {
            switch (name)
            {
                case "ping":
                    return "{\"connected\":true}";

                case "validate":
                {
                    if (string.IsNullOrWhiteSpace(arguments))
                        return "{\"error\":\"Empty arguments\"}";
                    var wrapper = JsonUtility.FromJson<ValidateRequestWrapper>(arguments);
                    var patches = wrapper?.patches ?? Array.Empty<FilePatch>();
                    bool semantic = wrapper?.semantic ?? false;
                    ValidationFileResult[] results;
                    if (semantic)
                        results = CodeValidator.ValidatePatchesCombined(patches);
                    else
                        results = CodeValidator.ValidatePatches(patches);
                    return JsonUtility.ToJson(new ValidateResponse { results = results });
                }

                case "actionSchema":
                {
                    string fmt = "typescript";
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        try
                        {
                            var req = JsonUtility.FromJson<ActionSchemaRequest>(arguments);
                            if (!string.IsNullOrWhiteSpace(req?.format)) fmt = req.format;
                        }
                        catch { }
                    }
                    string schema;
                    if (fmt.Equals("json", StringComparison.OrdinalIgnoreCase))
                        schema = ActionSchemaGenerator.GenerateJsonSchema(SupportedActions);
                    else
                        schema = ActionSchemaGenerator.GenerateDefinitions(SupportedActions);
                    return JsonUtility.ToJson(new ActionSchemaResponse { schema = schema, format = fmt });
                }

                case "applyPatches":
                {
                    if (EditorApplication.isPlaying)
                        return "{\"error\":\"Cannot apply patches during Play Mode. Exit Play Mode first.\"}";
                    if (string.IsNullOrWhiteSpace(arguments))
                        return "{\"error\":\"Empty arguments\"}";
                    var wrapper = JsonUtility.FromJson<PatchRequestWrapper>(arguments);
                    var patches = wrapper?.patches ?? Array.Empty<FilePatch>();

                    _pendingPatchResults = new List<PatchResult>();
                    _patchDoneEvent = new ManualResetEvent(false);

                    var patchesToApply = new List<FilePatch>();
                    foreach (var p in patches)
                    {
                        if (p != null && !string.IsNullOrWhiteSpace(p.filePath))
                            patchesToApply.Add(p);
                    }

                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() => ApplyPatchesOnMainThread(patchesToApply));
                    }
                    _patchDoneEvent.WaitOne(30000);
                    return JsonUtility.ToJson(new PatchesResponse { results = _pendingPatchResults.ToArray() });
                }

                case "applyActions":
                {
                    if (string.IsNullOrWhiteSpace(arguments))
                        return "{\"error\":\"Empty arguments\"}";
                    var actionsWrapper = JsonUtility.FromJson<ActionsRequestWrapper>(arguments);
                    var actionsArr = actionsWrapper?.actions ?? Array.Empty<EditorAction>();
                    var actionResults = ExecuteActionsCore(actionsArr);
                    return JsonUtility.ToJson(new ActionsResponse
                    {
                        results = actionResults,
                        undoGroup = SceneActionExecutor.LastUndoGroup,
                        lastSuccessfulActionIndex = SceneActionExecutor.LastSuccessfulActionIndex
                    });
                }

                case "validateActions":
                {
                    if (string.IsNullOrWhiteSpace(arguments))
                        return "{\"error\":\"Empty arguments\"}";
                    var wrapper = JsonUtility.FromJson<ActionsRequestWrapper>(arguments);
                    var actions = wrapper?.actions ?? Array.Empty<EditorAction>();

                    ActionValidationResult[] valResults = null;
                    var valDone = new ManualResetEvent(false);
                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            try { valResults = ActionValidator.ValidateActions(actions); }
                            catch { valResults = Array.Empty<ActionValidationResult>(); }
                            finally { valDone.Set(); }
                        });
                    }
                    valDone.WaitOne(10000);
                    return JsonUtility.ToJson(new ValidateActionsResponse { results = valResults ?? Array.Empty<ActionValidationResult>() });
                }

                case "requestScriptCompilation":
                {
                    if (EditorApplication.isPlaying)
                        return "{\"error\":\"Cannot compile scripts during Play Mode. Exit Play Mode first.\"}";
                    var compileDone = new ManualResetEvent(false);
                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            try
                            {
                                AssetDatabase.Refresh();
                                CompilationPipeline.RequestScriptCompilation();
                            }
                            catch { }
                            finally { compileDone.Set(); }
                        });
                    }
                    compileDone.WaitOne(10000);
                    return "{\"ok\":true}";
                }

                case "compileStatus":
                {
                    bool isCompiling = CompilationStatusTracker.IsCompiling;
                    var errors = CompilationStatusTracker.GetErrors();
                    var errorList = new List<CompileStatusError>();
                    if (errors != null)
                    {
                        foreach (var e in errors)
                        {
                            errorList.Add(new CompileStatusError
                            {
                                file = e.file ?? "",
                                line = e.line,
                                column = e.column,
                                message = e.message ?? ""
                            });
                        }
                    }
                    return JsonUtility.ToJson(new CompileStatusResponse
                    {
                        isCompiling = isCompiling,
                        errors = errorList.ToArray()
                    });
                }

                case "screenshot":
                {
                    string captureType = "scene";
                    string assetPath = null;
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        try
                        {
                            var screenshotReq = JsonUtility.FromJson<ScreenshotRequest>(arguments);
                            captureType = screenshotReq?.type ?? "scene";
                            assetPath = screenshotReq?.assetPath;
                        }
                        catch { }
                    }

                    ScreenshotResult screenshotResult = null;
                    var screenshotDone = new ManualResetEvent(false);
                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            try
                            {
                                switch ((captureType ?? "scene").ToLowerInvariant())
                                {
                                    case "game":
                                        screenshotResult = ImageCapture.CaptureGameView();
                                        break;
                                    case "asset":
                                        screenshotResult = ImageCapture.CaptureAssetPreview(assetPath);
                                        break;
                                    default:
                                        screenshotResult = ImageCapture.CaptureSceneView();
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                screenshotResult = new ScreenshotResult { success = false, message = ex.Message };
                            }
                            finally { screenshotDone.Set(); }
                        });
                    }
                    screenshotDone.WaitOne(10000);
                    return JsonUtility.ToJson(screenshotResult ?? new ScreenshotResult { success = false, message = "Timeout" });
                }

                case "dependencies":
                {
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        try
                        {
                            var req = JsonUtility.FromJson<DependencyRequest>(arguments);
                            if (req.buildIndex)
                            {
                                var buildDone = new ManualResetEvent(false);
                                lock (QueueLock)
                                {
                                    MainThreadQueue.Enqueue(() =>
                                    {
                                        try { AssetDependencyIndex.BuildIndex(); }
                                        catch { }
                                        finally { buildDone.Set(); }
                                    });
                                }
                                buildDone.WaitOne(60000);
                                return JsonUtility.ToJson(new DependencyResponse
                                {
                                    ready = AssetDependencyIndex.IsReady,
                                    assetCount = AssetDependencyIndex.AssetCount
                                });
                            }
                            if (!string.IsNullOrWhiteSpace(req.assetPath))
                            {
                                return JsonUtility.ToJson(new DependencyQueryResponse
                                {
                                    assetPath = req.assetPath,
                                    referencedBy = AssetDependencyIndex.GetReferencedBy(req.assetPath),
                                    dependsOn = AssetDependencyIndex.GetDependencies(req.assetPath),
                                    ready = AssetDependencyIndex.IsReady
                                });
                            }
                        }
                        catch { }
                    }
                    return "{\"error\":\"Provide assetPath or buildIndex\"}";
                }

                case "undoGroup":
                {
                    int reqUndoGroup = -1;
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        try
                        {
                            var req = JsonUtility.FromJson<UndoGroupRequest>(arguments);
                            if (req != null) reqUndoGroup = req.undoGroup;
                        }
                        catch { }
                    }

                    bool undoResult = false;
                    var undoDone = new ManualResetEvent(false);
                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            try { undoResult = SceneActionExecutor.RevertUndoGroup(reqUndoGroup); }
                            catch { }
                            finally { undoDone.Set(); }
                        });
                    }
                    undoDone.WaitOne(10000);
                    int finalGroup = reqUndoGroup >= 0 ? reqUndoGroup : SceneActionExecutor.LastUndoGroup;
                    return JsonUtility.ToJson(new UndoGroupResponse { success = undoResult, undoGroup = finalGroup });
                }

                case "reloadFromCheckpoint":
                {
                    string[] assetPaths = Array.Empty<string>();
                    int[] undoGroups = Array.Empty<int>();
                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        try
                        {
                            var req = JsonUtility.FromJson<CheckpointRequest>(arguments);
                            assetPaths = req?.assetPaths ?? Array.Empty<string>();
                            undoGroups = req?.undoGroups ?? Array.Empty<int>();
                        }
                        catch { }
                    }

                    bool revertOk = true;
                    var checkpointDone = new ManualResetEvent(false);
                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            try
                            {
                                if (undoGroups != null)
                                {
                                    for (int i = undoGroups.Length - 1; i >= 0; i--)
                                    {
                                        try { SceneActionExecutor.RevertUndoGroup(undoGroups[i]); }
                                        catch { revertOk = false; }
                                    }
                                }
                                if (assetPaths != null && assetPaths.Length > 0)
                                {
                                    AssetDatabase.StartAssetEditing();
                                    try
                                    {
                                        foreach (var p in assetPaths)
                                        {
                                            if (!string.IsNullOrWhiteSpace(p))
                                                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                                        }
                                    }
                                    finally { AssetDatabase.StopAssetEditing(); }
                                    AssetDatabase.Refresh();
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"[CoBuddy] ReloadFromCheckpoint error: {ex.Message}");
                                revertOk = false;
                            }
                            finally { checkpointDone.Set(); }
                        });
                    }
                    checkpointDone.WaitOne(30000);
                    return JsonUtility.ToJson(new CheckpointResponse
                    {
                        success = revertOk,
                        revertedAssets = assetPaths?.Length ?? 0,
                        revertedUndoGroups = undoGroups?.Length ?? 0
                    });
                }

                case "serializeAssets":
                {
                    if (string.IsNullOrWhiteSpace(arguments))
                        return "{\"error\":\"Empty arguments\"}";

                    SerializeAssetsRequest serReq;
                    try { serReq = JsonUtility.FromJson<SerializeAssetsRequest>(arguments); }
                    catch { return "{\"error\":\"Invalid JSON\"}"; }

                    var assetPaths = serReq?.assetPaths ?? Array.Empty<string>();
                    string detail = serReq?.detail ?? "props"; // "path", "props", "full"
                    int maxPerAsset = detail == "full" ? 20000 : 8000;

                    var serResults = new List<SerializeAssetResult>();
                    var serDone = new ManualResetEvent(false);
                    lock (QueueLock)
                    {
                        MainThreadQueue.Enqueue(() =>
                        {
                            try
                            {
                                foreach (var ap in assetPaths)
                                {
                                    if (string.IsNullOrWhiteSpace(ap))
                                    {
                                        serResults.Add(new SerializeAssetResult { assetPath = ap ?? "", error = "Empty path" });
                                        continue;
                                    }
                                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ap);
                                    if (asset == null)
                                    {
                                        serResults.Add(new SerializeAssetResult { assetPath = ap, error = "Asset not found" });
                                        continue;
                                    }
                                    try
                                    {
                                        string json = EditorJsonUtility.ToJson(asset, true);
                                        if (json.Length > maxPerAsset)
                                            json = json.Substring(0, maxPerAsset) + "...(truncated)";
                                        serResults.Add(new SerializeAssetResult
                                        {
                                            assetPath = ap,
                                            typeName = asset.GetType().Name,
                                            json = json
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        serResults.Add(new SerializeAssetResult { assetPath = ap, typeName = asset.GetType().Name, error = ex.Message });
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                serResults.Add(new SerializeAssetResult { assetPath = "", error = ex.Message });
                            }
                            finally { serDone.Set(); }
                        });
                    }
                    serDone.WaitOne(15000);
                    return JsonUtility.ToJson(new SerializeAssetsResponse { results = serResults.ToArray() });
                }

                case "decompileType":
                {
                    // Uses reflection to extract public API from compiled assemblies (no ICSharpCode.Decompiler needed)
                    if (string.IsNullOrWhiteSpace(arguments))
                        return "{\"error\":\"Empty arguments\"}";

                    DecompileTypeRequest decompReq;
                    try { decompReq = JsonUtility.FromJson<DecompileTypeRequest>(arguments); }
                    catch { return "{\"error\":\"Invalid JSON\"}"; }

                    var typeNames = decompReq?.typeNames ?? Array.Empty<string>();
                    var decompResults = new List<DecompileTypeResult>();

                    foreach (var typeName in typeNames)
                    {
                        if (string.IsNullOrWhiteSpace(typeName))
                        {
                            decompResults.Add(new DecompileTypeResult { typeName = typeName ?? "", error = "Empty type name" });
                            continue;
                        }

                        // Search all loaded assemblies for the type
                        System.Type foundType = null;
                        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                foundType = asm.GetType(typeName, false);
                                if (foundType == null)
                                {
                                    // Try without namespace
                                    foreach (var t in asm.GetExportedTypes())
                                    {
                                        if (t.Name == typeName || t.FullName == typeName)
                                        {
                                            foundType = t;
                                            break;
                                        }
                                    }
                                }
                                if (foundType != null) break;
                            }
                            catch { continue; }
                        }

                        if (foundType == null)
                        {
                            decompResults.Add(new DecompileTypeResult { typeName = typeName, error = "Type not found in loaded assemblies" });
                            continue;
                        }

                        try
                        {
                            var sb = new StringBuilder();
                            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;

                            // Type declaration
                            string baseType = foundType.BaseType != null && foundType.BaseType != typeof(object) ? $" : {foundType.BaseType.Name}" : "";
                            var interfaces = foundType.GetInterfaces();
                            string ifaces = interfaces.Length > 0 ? (baseType == "" ? " : " : ", ") + string.Join(", ", interfaces.Select(i => i.Name).Take(10)) : "";
                            string modifier = foundType.IsAbstract && !foundType.IsInterface ? "abstract " : foundType.IsSealed ? "sealed " : "";
                            string kind = foundType.IsInterface ? "interface" : foundType.IsEnum ? "enum" : foundType.IsValueType ? "struct" : "class";
                            sb.AppendLine($"// Assembly: {foundType.Assembly.GetName().Name}");
                            sb.AppendLine($"namespace {foundType.Namespace ?? "(global)"}");
                            sb.AppendLine($"public {modifier}{kind} {foundType.Name}{baseType}{ifaces}");
                            sb.AppendLine("{");

                            // Enums
                            if (foundType.IsEnum)
                            {
                                foreach (var val in System.Enum.GetNames(foundType).Take(50))
                                    sb.AppendLine($"    {val},");
                            }
                            else
                            {
                                // Fields
                                foreach (var f in foundType.GetFields(flags).Take(30))
                                {
                                    string fmod = f.IsStatic ? "static " : "";
                                    string fro = f.IsInitOnly ? "readonly " : "";
                                    sb.AppendLine($"    public {fmod}{fro}{f.FieldType.Name} {f.Name};");
                                }

                                // Properties
                                foreach (var p in foundType.GetProperties(flags).Take(30))
                                {
                                    string pmod = p.GetGetMethod()?.IsStatic == true ? "static " : "";
                                    string getter = p.CanRead ? "get; " : "";
                                    string setter = p.CanWrite ? "set; " : "";
                                    sb.AppendLine($"    public {pmod}{p.PropertyType.Name} {p.Name} {{ {getter}{setter}}}");
                                }

                                // Methods
                                foreach (var m in foundType.GetMethods(flags).Where(m => !m.IsSpecialName).Take(40))
                                {
                                    string mmod = m.IsStatic ? "static " : m.IsVirtual ? "virtual " : m.IsAbstract ? "abstract " : "";
                                    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                                    sb.AppendLine($"    public {mmod}{m.ReturnType.Name} {m.Name}({parms});");
                                }

                                // Events
                                foreach (var e in foundType.GetEvents(flags).Take(10))
                                    sb.AppendLine($"    public event {e.EventHandlerType?.Name ?? "EventHandler"} {e.Name};");
                            }

                            sb.AppendLine("}");

                            decompResults.Add(new DecompileTypeResult
                            {
                                typeName = foundType.FullName ?? typeName,
                                assemblyName = foundType.Assembly.GetName().Name,
                                apiSignature = sb.ToString()
                            });
                        }
                        catch (Exception ex)
                        {
                            decompResults.Add(new DecompileTypeResult { typeName = typeName, error = ex.Message });
                        }
                    }

                    return JsonUtility.ToJson(new DecompileTypeResponse { results = decompResults.ToArray() });
                }

                case "getMonoBehaviourCode":
                {
                    if (string.IsNullOrWhiteSpace(arguments))
                        return "{\"error\":\"Empty arguments\"}";
                    string[] componentTypes;
                    try
                    {
                        var req = JsonUtility.FromJson<MonoBehaviourCodeRequest>(arguments);
                        componentTypes = req?.componentTypes ?? Array.Empty<string>();
                    }
                    catch { return "{\"error\":\"Invalid JSON\"}"; }

                    var codeResults = new List<MonoBehaviourCodeResult>();
                    foreach (var typeName in componentTypes)
                    {
                        if (string.IsNullOrWhiteSpace(typeName))
                        {
                            codeResults.Add(new MonoBehaviourCodeResult { componentType = typeName ?? "", error = "Empty type name" });
                            continue;
                        }
                        // Find MonoScript by iterating all scripts and matching type name
                        string code = null;
                        string foundPath = null;
                        var guids = AssetDatabase.FindAssets("t:MonoScript");
                        foreach (var guid in guids)
                        {
                            var path = AssetDatabase.GUIDToAssetPath(guid);
                            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                            if (script == null) continue;
                            var scriptClass = script.GetClass();
                            if (scriptClass != null && (scriptClass.Name == typeName || scriptClass.FullName == typeName))
                            {
                                code = script.text;
                                foundPath = path;
                                break;
                            }
                        }
                        if (code != null)
                            codeResults.Add(new MonoBehaviourCodeResult { componentType = typeName, code = code, path = foundPath });
                        else
                            codeResults.Add(new MonoBehaviourCodeResult { componentType = typeName, error = $"Script not found for type: {typeName}" });
                    }
                    return JsonUtility.ToJson(new MonoBehaviourCodeResponse { results = codeResults.ToArray() });
                }

                default:
                    return $"{{\"error\":\"Unknown RPC: {EscapeJson(name)}\"}}";
            }
        }

        [Serializable]
        private class WsRpcRequest
        {
            public string type;
            public string id;
            public string name;
            public string arguments;
        }

        [Serializable]
        private class DecompileTypeRequest
        {
            public string[] typeNames;
        }

        [Serializable]
        private class DecompileTypeResult
        {
            public string typeName;
            public string assemblyName;
            public string apiSignature;
            public string error;
        }

        [Serializable]
        private class DecompileTypeResponse
        {
            public DecompileTypeResult[] results;
        }

        [Serializable]
        private class SerializeAssetsRequest
        {
            public string[] assetPaths;
            public string detail; // "path", "props", "full"
        }

        [Serializable]
        private class SerializeAssetResult
        {
            public string assetPath;
            public string typeName;
            public string json;
            public string error;
        }

        [Serializable]
        private class SerializeAssetsResponse
        {
            public SerializeAssetResult[] results;
        }

        [Serializable]
        private class MonoBehaviourCodeRequest
        {
            public string[] componentTypes;
        }

        [Serializable]
        private class MonoBehaviourCodeResult
        {
            public string componentType;
            public string code;
            public string path;
            public string error;
        }

        [Serializable]
        private class MonoBehaviourCodeResponse
        {
            public MonoBehaviourCodeResult[] results;
        }
    }
}
