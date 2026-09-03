using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

using UnityMCP.Editor.Handlers;
using UnityMCP.Editor.Resources;
using UnityMCP.Editor.Settings;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// HTTP server that exposes Unity Editor functionality via REST endpoints.
    /// All endpoints return the unified envelope: {status, result, truncated?, next?}.
    /// </summary>
    internal sealed class McpHttpServer : IDisposable
    {
        /// <summary>
        /// Version advertised in <c>/health</c> and in the instance descriptor.
        /// </summary>
        /// <remarks>
        /// Read from the package manifest rather than written here. v2 left a hand-written
        /// constant at 2.1.0 while the package moved to 2.1.1, so the number clients saw meant
        /// nothing; v3 kept the constant and a comment saying to keep it in step, and it went
        /// stale again on the very next release. A comment is not a mechanism. The literal
        /// remains only as the answer for an assembly that is not loaded from a package, and CI
        /// checks that it too stays in step.
        /// </remarks>
        private const string FallbackVersion = "3.3.1";

        private static string ProtocolVersion
        {
            get
            {
                if (cachedVersion != null)
                {
                    return cachedVersion;
                }

                string version = null;

                try
                {
                    version = UnityEditor.PackageManager.PackageInfo
                        .FindForAssembly(typeof(McpHttpServer).Assembly)?.version;
                }
                catch
                {
                    // Resolution can fail while the package database is still loading.
                }

                cachedVersion = string.IsNullOrEmpty(version) ? FallbackVersion : version;

                return cachedVersion;
            }
        }

        private static string cachedVersion;

        // ── Built-in endpoint/action idempotency table ──
        // Granular per-action entries exposed via /health.handlers[].
        // Uses `:` as the action separator: "/inspect:write", "/play_mode:step", etc.
        private static readonly (string Name, McpIdempotency Idem)[] BuiltinHandlerEntries =
        {
            ("/health",                McpIdempotency.Safe),
            ("/resource",              McpIdempotency.Safe),
            ("/read_logs",             McpIdempotency.Safe),
            ("/browse_hierarchy",      McpIdempotency.Safe),
            ("/capture_screenshot",    McpIdempotency.Safe),
            ("/inspect:read",          McpIdempotency.Safe),
            ("/inspect:list",          McpIdempotency.Safe),
            ("/inspect:write",         McpIdempotency.Unsafe),
            ("/play_mode:status",      McpIdempotency.Safe),
            ("/play_mode:play",        McpIdempotency.Unsafe),
            ("/play_mode:stop",        McpIdempotency.Unsafe),
            ("/play_mode:pause",       McpIdempotency.Unsafe),
            ("/play_mode:unpause",     McpIdempotency.Unsafe),
            ("/play_mode:step",        McpIdempotency.Unsafe),
            ("/execute_code",          McpIdempotency.Unsafe),
            ("/jobs",                  McpIdempotency.Safe),
            ("/jobs:cancel",           McpIdempotency.Unsafe),
            ("/tools",                 McpIdempotency.Safe),
            // Individual tool calls are not listed here: each one publishes its own
            // idempotency in the /tools catalog, which is the point of the attribute.
        };

        /// <summary>
        /// How long a request waits for its main-thread work before switching to a job id.
        /// <para>
        /// v2 waited 10 seconds and then returned 504 while leaving the work queued, so a
        /// client that retried executed it twice. The wait is now short on purpose: anything
        /// slower is better served by a job the caller can poll than by a blocked socket.
        /// </para>
        /// </summary>
        private static int SyncWaitMs => Math.Max(250, McpSettings.instance.syncWaitMs);

        // HTTP server
        private HttpListener httpListener;
        private Thread listenerThread;
        private int boundPort;
        private bool running;

        /// <summary>
        /// Bearer token clients must present. Held in SessionState so it survives a domain
        /// reload: Unity's own pipeline package regenerated its token on reload, which made
        /// every call 401 after entering play mode until the client was restarted.
        /// </summary>
        private readonly string authToken;

        private const string SessionKeyAuthToken = "UnityMCP.AuthToken";

        // Command & resource handlers
        private readonly Dictionary<string, HandlerRegistration> commandHandlers = new();
        private readonly Dictionary<string, ResourceHandlerRegistration> resourceHandlers = new();
        private readonly Dictionary<string, IMcpResourceHandler> resourceUriMap = new();

        // Main thread marshalling and long-running job tracking
        private readonly McpMainThreadDispatcher dispatcher = new();
        private readonly McpJobRegistry jobs = new();

        // Attribute-declared tools, discovered lazily on first use and after an explicit refresh.
        private ToolCatalog toolCatalog;
        private readonly object catalogLock = new();

        private CancellationTokenSource cancellationTokenSource;

        // Request counter (thread-safe)
        private long requestCount;

        // Project info (captured once at construction time)
        private readonly string productName = Application.productName;
        private readonly string unityVersion = Application.unityVersion;
        private readonly string projectPath = Application.dataPath;

        // Events
        public event EventHandler<EventArgs> Started;
        public event EventHandler<EventArgs> Stopped;
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;
        public event EventHandler<ResourceFetchedEventArgs> ResourceFetched;

        /// <summary>Gets whether the server is running.</summary>
        public bool IsRunning => this.running;

        /// <summary>Gets whether the server is listening (alias for IsRunning for HTTP server).</summary>
        public bool IsConnected => this.running;

        /// <summary>Gets the port the server is bound to.</summary>
        public int BoundPort => this.boundPort;

        /// <summary>Gets the server identifier (project name + port).</summary>
        public string ClientId => $"{this.productName}-{this.boundPort}";

        /// <summary>Gets the time when the server was last started.</summary>
        public DateTime ConnectedSince { get; private set; }

        private static bool DetailedLogs => McpSettings.instance.detailedLogs;

        /// <summary>
        /// Creates a new McpHttpServer using settings from McpSettings.
        /// </summary>
        public McpHttpServer()
        {
            var settings = McpSettings.instance;
            this.boundPort = settings.httpPort;
            // Reuse the token across domain reloads; only mint one when there is none.
            var existingToken = SessionState.GetString(SessionKeyAuthToken, string.Empty);
            if (string.IsNullOrEmpty(existingToken))
            {
                existingToken = McpInstanceDescriptor.GenerateToken();
                SessionState.SetString(SessionKeyAuthToken, existingToken);
            }

            this.authToken = existingToken;

            EditorApplication.update += this.dispatcher.Pump;

            if (DetailedLogs)
            {
                Debug.Log($"[McpHttpServer] Initialized, target port={this.boundPort}");
            }
        }

        // ──────────────────────────────────────────────
        //  Lifecycle  (race-free Start per design §2.1)
        // ──────────────────────────────────────────────

        /// <summary>
        /// Starts the HTTP server and UDP broadcaster.
        /// </summary>
        /// <param name="preferredPort">
        /// If provided, attempts to bind this port first before scanning the range.
        /// Defaults to <c>McpSettings.instance.httpPort</c> when null.
        /// </param>
        public void Start(int? preferredPort = null)
        {
            if (this.running) return;

            var startPort = preferredPort ?? McpSettings.instance.httpPort;

            // Step 1: bind listener — throws on total failure, confirms actual port.
            this.boundPort = this.StartHttpListener(startPort);

            // Step 2: persist actual port immediately after successful bind (SessionState).
            SessionState.SetInt("UnityMCP.BoundPort", this.boundPort);
            SessionState.SetBool("UnityMCP.WasRunning", true);

            // Step 3: set state flags BEFORE thread start so ListenerLoop and /health
            //         both observe running=true from the very first iteration.
            this.cancellationTokenSource = new CancellationTokenSource();
            this.ConnectedSince = DateTime.Now;
            this.requestCount = 0;
            this.running = true;  // ← must precede thread start

            // Step 4: start background threads inside try/catch.
            try
            {
                this.listenerThread = new Thread(this.ListenerLoop)
                {
                    IsBackground = true,
                    Name = "McpHttpListenerThread"
                };
                this.listenerThread.Start();

                Debug.Log($"[McpHttpServer] HTTP server listening on http://127.0.0.1:{this.boundPort}/");

                // Sweep first so a descriptor left by a killed Editor is not mistaken for a
                // second live instance.
                McpInstanceDescriptor.RemoveStale();
                McpInstanceDescriptor.Write(
                    this.projectPath,
                    this.productName,
                    this.unityVersion,
                    this.boundPort,
                    this.authToken,
                    ProtocolVersion);
            }
            catch (Exception ex)
            {
                // Roll back — threads/broadcaster failed to start.
                this.running = false;
                try { this.cancellationTokenSource?.Cancel(); this.cancellationTokenSource?.Dispose(); }
                catch { }
                this.cancellationTokenSource = null;
                try { this.httpListener?.Close(); }
                catch { }
                this.httpListener = null;

                // Keep BoundPort as a hint for the next retry attempt; only clear WasRunning.
                SessionState.SetBool("UnityMCP.WasRunning", false);

                Debug.LogError($"[McpHttpServer] Failed to start listener threads: {ex.Message}");
                throw;
            }

            // Step 5: fire Started event.
            this.Started?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Stops the HTTP server and UDP broadcaster.
        /// </summary>
        /// <param name="withdrawDescriptor">
        /// Whether to remove this Editor's descriptor.
        /// <para>
        /// False for a domain reload: the server is about to come back on the same port, and
        /// removing the file would make clients unregister the instance and lose the active
        /// selection, when riding the reload out is exactly what the reloading state is for.
        /// </para>
        /// </param>
        public void Stop(bool withdrawDescriptor = true)
        {
            this.running = false;  // signals ListenerLoop to exit
            this.cancellationTokenSource?.Cancel();

            if (withdrawDescriptor)
            {
                // Withdrawn first: a client reading it after this point would dial a port
                // that is already closing.
                McpInstanceDescriptor.Delete(this.projectPath);
            }

            // Stop listener
            try
            {
                this.httpListener?.Stop();
                this.httpListener?.Close();
            }
            catch (Exception ex)
            {
                if (DetailedLogs) Debug.LogWarning($"[McpHttpServer] Error closing listener: {ex.Message}");
            }
            finally
            {
                this.httpListener = null;
            }

            if (this.listenerThread is { IsAlive: true })
            {
                this.listenerThread.Join(2000);
                this.listenerThread = null;
            }

            // IMPORTANT: Stop() does NOT touch SessionState.
            // Reload path (OnBeforeAssemblyReload) must preserve WasRunning=true,
            // so it writes SessionState before calling Dispose() → Stop().
            // User-initiated stop callers are responsible for persisting WasRunning=false explicitly.

            Debug.Log("[McpHttpServer] Server stopped");
            this.Stopped?.Invoke(this, EventArgs.Empty);
        }

        // ──────────────────────────────────────────────
        //  HTTP Listener
        // ──────────────────────────────────────────────

        /// <summary>
        /// Binds the first free port at or after <paramref name="startPort"/>.
        /// </summary>
        /// <remarks>
        /// Every per-port failure is swallowed so the scan continues. Catching only
        /// <see cref="HttpListenerException"/> was not enough: the Editor runs on Mono, whose
        /// HttpListener is implemented over managed sockets, so a busy port surfaces as a
        /// <see cref="SocketException"/> instead. That escaped the catch and aborted the whole
        /// scan on its first candidate, which is why a second Editor — or the same Editor
        /// rebinding after a domain reload while its previous socket was still in TIME_WAIT —
        /// reported "only one usage of each socket address is normally permitted" and started
        /// no server at all, with nineteen free ports in the range.
        /// <para>
        /// Only the 127.0.0.1 prefix is registered. The old code also added a `localhost`
        /// prefix for the same port, which binds nothing extra — clients connect to the
        /// loopback address — while giving the bind a second way to fail.
        /// </para>
        /// </summary>
        private int StartHttpListener(int startPort)
        {
            const int maxPort = 27199;
            var firstFailure = string.Empty;

            for (var port = Math.Max(startPort, 1); port <= maxPort; port++)
            {
                HttpListener listener = null;

                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();
                    this.httpListener = listener;
                    return port;
                }
                catch (Exception e)
                {
                    // Port unavailable for any reason at all — move on. Which exception type a
                    // busy port produces depends on the runtime, and guessing wrong here costs
                    // the whole scan.
                    try { listener?.Close(); }
                    catch { }

                    if (firstFailure.Length == 0)
                    {
                        firstFailure = $"{port}: {e.GetType().Name}: {e.Message}";
                    }

                    if (DetailedLogs)
                    {
                        Debug.Log($"[McpHttpServer] Port {port} unavailable ({e.GetType().Name}), trying the next one");
                    }
                }
            }

            throw new InvalidOperationException(
                $"No free port in {startPort}-{maxPort}. First failure was {firstFailure}");
        }

        private void ListenerLoop()
        {
            try
            {
                while (this.running && !this.cancellationTokenSource.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = this.httpListener.GetContext();
                    }
                    catch (HttpListenerException)
                    {
                        break; // Listener was stopped
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    // Handle each request on a thread pool thread
                    ThreadPool.QueueUserWorkItem(_ => this.HandleRequest(context));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception e)
            {
                Debug.LogError($"[McpHttpServer] Listener loop error: {e.Message}");
            }
        }

        // ──────────────────────────────────────────────
        //  Request Routing
        // ──────────────────────────────────────────────

        private void HandleRequest(HttpListenerContext context)
        {
            Interlocked.Increment(ref this.requestCount);

            var request = context.Request;
            var response = context.Response;

            // No Access-Control-Allow-Origin. v2 sent "*", which let any web page the user had
            // open POST to /execute_code and run arbitrary C# in their Editor. Omitting the
            // header makes the browser refuse to hand the response to page script, and the
            // bearer token below blocks the request outright.
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            if (!this.IsAuthorized(request))
            {
                this.WriteEnvelope(
                    response,
                    401,
                    null,
                    errorCode: "unauthorized",
                    errorMessage:
                        "Missing or invalid bearer token. The token is published in this project's " +
                        $"descriptor at {McpInstanceDescriptor.PathFor(this.projectPath)}.");
                return;
            }

            try
            {
                var path = request.Url.AbsolutePath.TrimEnd('/');
                var method = request.HttpMethod;

                if (DetailedLogs)
                {
                    // Buffered rather than logged directly: Debug.Log takes a Unity-internal
                    // lock, and taking it from the request path would couple every response to
                    // whatever the main thread is doing — the opposite of what off-thread
                    // endpoints exist for.
                    this.dispatcher.Log($"[McpHttpServer] {method} {path}");
                }

                // Job and tool routes carry a name in the path, so they are matched before
                // the exact-match switch below.
                if (path.StartsWith("/jobs/", StringComparison.Ordinal))
                {
                    this.HandleJobById(response, path, method);
                    return;
                }

                if (path.StartsWith("/tools/", StringComparison.Ordinal))
                {
                    this.HandleToolCall(request, response, path, method);
                    return;
                }

                switch (path)
                {
                    case "/health":
                        this.HandleHealth(response);
                        break;

                    case "/jobs" when method == "GET":
                        this.WriteEnvelope(response, 200, new JObject { ["jobs"] = this.jobs.ToJson() });
                        break;

                    case "/tools" when method == "GET":
                        this.HandleToolCatalog(request, response);
                        break;

                    case "/command" when method == "POST":
                        this.HandleCommand(request, response);
                        break;

                    case "/resource" when method == "GET":
                        this.HandleResource(request, response);
                        break;

                    // ── Built-in shortcuts ──
                    case "/read_logs" when method == "POST":
                        this.HandleBuiltinCommand(request, response, LogReader.ReadLogs, "/read_logs");
                        break;

                    case "/execute_code" when method == "POST":
                        this.HandleBuiltinCommand(request, response, CodeExecutor.Execute, "/execute_code");
                        break;

                    case "/browse_hierarchy" when method == "POST":
                        this.HandleBuiltinCommand(request, response, SceneHierarchy.Browse, "/browse_hierarchy");
                        break;

                    case "/capture_screenshot" when method == "POST":
                        this.HandleCaptureScreenshot(request, response);
                        break;

                    case "/play_mode" when method == "POST":
                        this.HandleBuiltinCommand(request, response, PlayModeControl.Control, "/play_mode");
                        break;

                    case "/inspect" when method == "POST":
                        this.HandleBuiltinCommand(request, response, InspectorAccess.Access, "/inspect");
                        break;

                    // /compile/* is implemented as the compile_status and compile_request
                    // tools, /test/* as test_run and test_results; /eval duplicated
                    // /execute_code and is withdrawn. What is left is still unbuilt, and says
                    // so with a pointer rather than a bare "not implemented".
                    case "/hlsl/errors" when method == "GET":
                        this.WriteEnvelope(
                            response,
                            501,
                            null,
                            errorCode: "not_implemented",
                            errorMessage: $"{path} is not implemented. GET /tools lists what this Editor does offer.");
                        break;

                    default:
                        this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown endpoint: {method} {path}");
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[McpHttpServer] Request error: {e.Message}");
                try
                {
                    this.WriteEnvelope(response, 500, null, errorCode: "internal_error", errorMessage: e.Message);
                }
                catch
                {
                    // Response may already be closed
                }
            }
        }

        // ──────────────────────────────────────────────
        //  Built-in Command Handler
        // ──────────────────────────────────────────────

        /// <summary>
        /// Reads and parses the JSON request body. Returns false and writes a 400 error
        /// envelope ("invalid_params" with parse error detail) on malformed JSON.
        /// Empty bodies are treated as an empty JObject (not an error).
        /// </summary>
        private bool TryReadJsonBody(HttpListenerRequest request, HttpListenerResponse response, out JObject parameters)
        {
            string json;
            try
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                json = reader.ReadToEnd();
            }
            catch (Exception ioEx)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Failed to read request body: {ioEx.Message}");
                parameters = null;
                return false;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                parameters = new JObject();
                return true;
            }

            try
            {
                parameters = JObject.Parse(json);
                return true;
            }
            catch (JsonReaderException jre)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Invalid JSON: {jre.Message}");
                parameters = null;
                return false;
            }
            catch (Exception e)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Invalid JSON body: {e.Message}");
                parameters = null;
                return false;
            }
        }

        /// <summary>
        /// Checks the Authorization header against this Editor's token.
        /// </summary>
        /// <remarks>
        /// Binding to loopback is not by itself access control: every process on the machine
        /// can reach it, and <c>/execute_code</c> runs arbitrary C# with full Editor
        /// privileges. Comparison is length-constant to avoid leaking the token a byte at a
        /// time through timing.
        /// </remarks>
        private bool IsAuthorized(HttpListenerRequest request)
        {
            var header = request.Headers["Authorization"];

            if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                return false;
            }

            var presented = header.Substring("Bearer ".Length).Trim();

            if (presented.Length != this.authToken.Length)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < presented.Length; i++)
            {
                difference |= presented[i] ^ this.authToken[i];
            }

            return difference == 0;
        }

        private void HandleBuiltinCommand(
            HttpListenerRequest request,
            HttpListenerResponse response,
            Func<JObject, JObject> handler,
            string label)
        {
            if (!this.TryReadJsonBody(request, response, out var parameters))
            {
                return;
            }

            this.RunOnMainThread(response, label, () => handler(parameters));
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the Editor main thread and answers the request.
        /// </summary>
        /// <remarks>
        /// Single path for every main-thread endpoint. v2 repeated this wait-and-timeout block
        /// four times with slightly different error handling, which is how
        /// <c>/capture_screenshot</c> ended up being the only one that reported its own error
        /// codes. Three outcomes:
        /// <list type="bullet">
        /// <item>Finished inside the sync window: 200 with the result.</item>
        /// <item>Threw: the handler's own code and status if it carries one, else 500.</item>
        /// <item>Still running: 202 with a job id. Note that the work is <em>not</em>
        /// cancelled — it keeps going and its outcome lands in the job.</item>
        /// </list>
        /// </remarks>
        private void RunOnMainThread(HttpListenerResponse response, string label, Func<JObject> work)
        {
            var item = this.dispatcher.Submit(work);

            if (!item.Wait(SyncWaitMs))
            {
                var jobId = this.jobs.Track(item, label);

                this.WriteEnvelope(response, 202, new JObject
                {
                    ["state"] = "running",
                    ["jobId"] = jobId,
                    ["poll"] = $"/jobs/{jobId}",
                    ["message"] =
                        $"'{label}' is still running on the Editor main thread. " +
                        $"Poll GET /jobs/{jobId} for the result. Do not retry this call — " +
                        "the work is still in progress and retrying would run it twice.",
                });

                return;
            }

            if (item.Error != null)
            {
                this.WriteError(response, item.Error);
                return;
            }

            this.WriteEnvelope(response, 200, item.Result);
        }

        /// <summary>
        /// Writes an exception as an error envelope, preserving the code and HTTP status of
        /// exceptions that carry their own (so a missing window stays 400/window_not_found
        /// rather than collapsing into a generic 500).
        /// </summary>
        private void WriteError(HttpListenerResponse response, Exception error)
        {
            switch (error)
            {
                case McpScreenshotException screenshot:
                    this.WriteEnvelope(response, screenshot.HttpStatus, null, errorCode: screenshot.Code, errorMessage: screenshot.Message);
                    break;

                case McpToolException tool:
                    this.WriteEnvelope(response, tool.HttpStatus, null, errorCode: tool.Code, errorMessage: tool.Message);
                    break;

                default:
                    this.WriteEnvelope(response, 500, null, errorCode: "internal_error", errorMessage: error.Message);
                    break;
            }
        }

        /// <summary>
        /// Returns the catalog of attribute-declared tools with their generated JSON Schemas.
        /// <para>
        /// This payload is the entire tool surface. The TypeScript server registers MCP tools
        /// from it rather than declaring them a second time, which is what stops the two
        /// definitions drifting the way v2's did.
        /// </para>
        /// Pass <c>?refresh=1</c> to rediscover — needed only when assemblies were loaded
        /// after the catalog was first built.
        /// </summary>
        private void HandleToolCatalog(HttpListenerRequest request, HttpListenerResponse response)
        {
            var refresh = request.QueryString["refresh"];
            var catalog = this.GetToolCatalog(forceRefresh: refresh == "1" || refresh == "true");

            var payload = catalog.ToJson();

            if (catalog.Errors.Count > 0)
            {
                // Surfaced rather than swallowed: a tool that failed to register is
                // indistinguishable from one that was never written.
                payload["discoveryErrors"] = new JArray(catalog.Errors.Cast<object>().ToArray());
            }

            this.WriteEnvelope(response, 200, payload);
        }

        /// <summary>
        /// Invokes an attribute-declared tool: <c>POST /tools/&lt;name&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Tools declaring <c>MainThread = false</c> run directly on the worker thread and
        /// never touch the dispatcher queue, so they keep answering while the Editor main
        /// thread is blocked. That is the whole reason the flag exists.
        /// </remarks>
        private void HandleToolCall(HttpListenerRequest request, HttpListenerResponse response, string path, string method)
        {
            var name = path.Substring("/tools/".Length);

            if (method != "POST")
            {
                this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown endpoint: {method} {path}");
                return;
            }

            var catalog = this.GetToolCatalog(forceRefresh: false);

            if (!catalog.TryGet(name, out var descriptor))
            {
                this.WriteEnvelope(
                    response,
                    404,
                    null,
                    errorCode: "tool_not_found",
                    errorMessage: $"No tool named '{name}'. GET /tools lists the available tools.");
                return;
            }

            if (!this.TryReadJsonBody(request, response, out var arguments))
            {
                return;
            }

            if (!descriptor.MainThread)
            {
                try
                {
                    this.WriteEnvelope(response, 200, ToolInvoker.Invoke(descriptor, arguments));
                }
                catch (Exception e)
                {
                    this.WriteError(response, e);
                }

                return;
            }

            this.RunOnMainThread(response, name, () => ToolInvoker.Invoke(descriptor, arguments));
        }

        /// <summary>
        /// Returns the tool catalog, discovering it on first use.
        /// </summary>
        private ToolCatalog GetToolCatalog(bool forceRefresh)
        {
            lock (this.catalogLock)
            {
                if (this.toolCatalog != null && !forceRefresh)
                {
                    return this.toolCatalog;
                }

                this.toolCatalog = ToolCatalog.Build();
                this.toolCatalog.ReportErrors(this.dispatcher.LogError);
                return this.toolCatalog;
            }
        }

        /// <summary>
        /// Handles <c>GET /jobs/&lt;id&gt;</c> and <c>POST /jobs/&lt;id&gt;/cancel</c>.
        /// Both answer from the worker thread, so job state stays observable even when the
        /// main thread is wedged — which is exactly when a caller most needs to look.
        /// </summary>
        private void HandleJobById(HttpListenerResponse response, string path, string method)
        {
            var remainder = path.Substring("/jobs/".Length);
            var cancelling = remainder.EndsWith("/cancel", StringComparison.Ordinal);
            var id = cancelling ? remainder.Substring(0, remainder.Length - "/cancel".Length) : remainder;

            if (!this.jobs.TryGet(id, out var entry))
            {
                this.WriteEnvelope(response, 404, null, errorCode: "job_not_found", errorMessage: $"No job with id '{id}'. It may have completed long enough ago to be evicted.");
                return;
            }

            if (!cancelling)
            {
                if (method != "GET")
                {
                    this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown endpoint: {method} {path}");
                    return;
                }

                this.WriteEnvelope(response, 200, entry.ToDetailJson());
                return;
            }

            if (method != "POST")
            {
                this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown endpoint: {method} {path}");
                return;
            }

            var cancelled = entry.Item.TryAbandon();
            var payload = entry.ToDetailJson();

            // Distinguishing these two matters: "cancelled" means the work provably never ran,
            // "already_started" means the caller must assume its side effects happened.
            payload["cancelled"] = cancelled;
            payload["outcome"] = cancelled ? "cancelled_before_start" : "already_started";

            this.WriteEnvelope(response, 200, payload);
        }

        // ──────────────────────────────────────────────
        //  /capture_screenshot — specialised error handling
        // ──────────────────────────────────────────────

        /// <summary>
        /// Dedicated handler for /capture_screenshot that translates
        /// <see cref="McpScreenshotException"/> into an error envelope with the
        /// handler-supplied code and HTTP status (e.g. window_not_found=400,
        /// unsupported_platform=501).
        /// </summary>
        private void HandleCaptureScreenshot(HttpListenerRequest request, HttpListenerResponse response)
        {
            // No longer special-cased: RunOnMainThread preserves McpScreenshotException's own
            // code and status via WriteError.
            this.HandleBuiltinCommand(request, response, ScreenshotCapture.Capture, "/capture_screenshot");
        }

        // ──────────────────────────────────────────────
        //  Endpoint Handlers
        // ──────────────────────────────────────────────

        /// <summary>
        /// /health returns the unified envelope with a `result` payload describing
        /// the server state, available handlers, and idempotency classification.
        /// Per spec R1.4 all responses (including /health) use the envelope.
        /// </summary>
        private void HandleHealth(HttpListenerResponse response)
        {
            var body = this.BuildHealthResponse();
            this.WriteEnvelope(response, 200, body);
        }

        /// <summary>
        /// Builds the /health response payload (goes under the envelope's `result`).
        /// Includes built-in HTTP shortcuts with per-action granularity plus any
        /// registered IMcpCommandHandler entries (prefixed with "/command:").
        /// </summary>
        private JObject BuildHealthResponse()
        {
            var uptimeSec = (DateTime.Now - this.ConnectedSince).TotalSeconds;

            var handlerArray = new JArray();

            // 1. Built-in HTTP shortcuts with per-action granularity.
            foreach (var entry in BuiltinHandlerEntries)
            {
                handlerArray.Add(new JObject
                {
                    ["name"] = entry.Name,
                    ["idempotency"] = entry.Idem.ToString().ToLowerInvariant()
                });
            }

            // 2. Registered IMcpCommandHandler plugins — "/command:<prefix>" or
            //    "/command:<prefix>.<action>" when the handler declares per-action
            //    overrides via IMcpCommandHandler.Actions.
            foreach (var kv in this.commandHandlers)
            {
                var prefix = kv.Key;
                var handler = kv.Value.Handler;
                var actions = handler.Actions;

                if (actions != null && actions.Count > 0)
                {
                    // Emit per-action entries — class-level Idempotency is ignored
                    // because the handler opted into fine-grained declaration.
                    foreach (var a in actions)
                    {
                        handlerArray.Add(new JObject
                        {
                            ["name"] = $"/command:{prefix}.{a.Action}",
                            ["idempotency"] = a.Idempotency.ToString().ToLowerInvariant()
                        });
                    }
                }
                else
                {
                    // Fall back to the class-level Idempotency.
                    handlerArray.Add(new JObject
                    {
                        ["name"] = $"/command:{prefix}",
                        ["idempotency"] = handler.Idempotency.ToString().ToLowerInvariant()
                    });
                }
            }

            var resourceArray = new JArray();
            foreach (var kv in this.resourceHandlers)
            {
                resourceArray.Add(kv.Value.Handler.ResourceUri);
            }

            return new JObject
            {
                ["v"] = ProtocolVersion,
                ["project"] = this.productName,
                ["unity"] = this.unityVersion,
                ["port"] = this.boundPort,
                ["clientId"] = this.ClientId,
                // state is always "running" when the listener can respond (design §2.1)
                ["state"] = "running",
                ["uptimeSec"] = uptimeSec,
                ["reqCount"] = Interlocked.Read(ref this.requestCount),
                // Both are read without touching the main thread, so they stay meaningful
                // precisely when the Editor is too busy to answer anything else. A climbing
                // queueDepth with a static reqCount is the signature of a wedged main thread.
                ["queueDepth"] = this.dispatcher.PendingCount,
                ["runningJobs"] = this.jobs.RunningCount,
                ["handlers"] = handlerArray,
                ["resources"] = resourceArray
            };
        }

        private void HandleCommand(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!this.TryReadJsonBody(request, response, out var body))
            {
                return;
            }

            var commandType = body["command"]?.ToString();
            var parameters = body["params"] as JObject ?? new JObject();

            if (string.IsNullOrEmpty(commandType))
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: "Missing 'command' field");
                return;
            }

            var parts = commandType.Split('.');
            if (parts.Length < 2)
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: $"Invalid command format: {commandType}. Expected: 'prefix.action'");
                return;
            }

            var prefix = parts[0];
            var action = parts[1];

            if (!this.commandHandlers.TryGetValue(prefix, out var registration))
            {
                this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown command prefix: {prefix}");
                return;
            }

            if (!registration.Enabled)
            {
                this.WriteEnvelope(response, 409, null, errorCode: "handler_disabled", errorMessage: $"Command prefix '{prefix}' is disabled");
                return;
            }

            this.RunOnMainThread(response, $"/command:{prefix}.{action}", () =>
            {
                var result = registration.Handler.Execute(action, parameters);
                this.OnCommandExecuted(new CommandExecutedEventArgs(prefix, action, parameters, result));
                return result;
            });
        }

        private void HandleResource(HttpListenerRequest request, HttpListenerResponse response)
        {
            var resourceName = request.QueryString["name"];
            if (string.IsNullOrEmpty(resourceName))
            {
                this.WriteEnvelope(response, 400, null, errorCode: "invalid_params", errorMessage: "Missing 'name' query parameter");
                return;
            }

            var parameters = new JObject();
            foreach (string key in request.QueryString)
            {
                if (key != "name")
                    parameters[key] = request.QueryString[key];
            }

            // FetchResourceData may return a result with truncated/next already set by handlers;
            // RunOnMainThread passes it through to WriteEnvelope untouched.
            this.RunOnMainThread(response, $"/resource:{resourceName}", () => this.FetchResourceData(resourceName, parameters));
        }

        // ──────────────────────────────────────────────
        //  Unified Envelope Writer (A1)
        // ──────────────────────────────────────────────

        /// <summary>
        /// Writes a unified response envelope:
        ///   Success: {status:"success", result:{...}, truncated?, next?}
        ///   Error:   {status:"error", error:{code, message}}
        /// </summary>
        private void WriteEnvelope(
            HttpListenerResponse response,
            int statusCode,
            JObject result,
            string errorCode = null,
            string errorMessage = null)
        {
            JObject envelope;

            if (errorCode != null || statusCode >= 400)
            {
                envelope = new JObject
                {
                    ["status"] = "error",
                    ["error"] = new JObject
                    {
                        ["code"] = errorCode ?? "internal_error",
                        ["message"] = errorMessage ?? "An error occurred"
                    }
                };
            }
            else if (result != null
                     && result["error"] != null
                     && result["error"].Type == JTokenType.String
                     && result["result"] == null)
            {
                // Legacy handler pattern: `{"error": "msg"}` returned from a handler.
                // Promote to proper error envelope so status/HTTP code reflect the failure.
                envelope = new JObject
                {
                    ["status"] = "error",
                    ["error"] = new JObject
                    {
                        ["code"] = "invalid_params",
                        ["message"] = result["error"].ToString()
                    }
                };
                statusCode = 400;
            }
            else
            {
                // Hoist truncated/next from the result object if present (set by ListResponseBuilder)
                var truncated = result?["truncated"];
                var next = result?["next"];

                // Build a clean result without the pagination keys at top level
                // (they belong on the envelope, not inside result)
                JObject cleanResult = null;
                if (result != null)
                {
                    cleanResult = new JObject();
                    foreach (var prop in result.Properties())
                    {
                        if (prop.Name == "truncated" || prop.Name == "next")
                            continue;
                        cleanResult[prop.Name] = prop.Value;
                    }
                }

                envelope = new JObject { ["status"] = "success" };
                if (cleanResult != null)
                    envelope["result"] = cleanResult;

                if (truncated != null)
                    envelope["truncated"] = truncated;
                if (next != null && next.Type != JTokenType.Null)
                    envelope["next"] = next;
            }

            this.WriteRaw(response, statusCode, envelope);
        }

        /// <summary>
        /// Writes a raw JObject body to the HTTP response. Internal helper used by
        /// WriteEnvelope after building the unified envelope.
        /// </summary>
        private void WriteRaw(HttpListenerResponse response, int statusCode, JObject body)
        {
            response.StatusCode = statusCode;

            // The charset is stated rather than left to the client's default. Tool descriptions
            // contain non-ASCII punctuation, and a client that falls back to Latin-1 on a bare
            // application/json turns each of those into mojibake — measured while auditing this
            // catalogue, where an em dash came back as three characters.
            response.ContentType = "application/json; charset=utf-8";

            if (body == null || statusCode == 204)
            {
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            var json = JsonConvert.SerializeObject(body);
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        // Discovery is published through McpInstanceDescriptor. The UDP broadcaster it
        // replaced could not tell a local Editor from one on another machine — a remote
        // announce registered as a dead local instance and made every call fail with
        // "target required" — and it could not carry the auth token.

        // ──────────────────────────────────────────────
        //  Main Thread Queue
        // ──────────────────────────────────────────────

        // Main-thread marshalling now lives in McpMainThreadDispatcher, which dequeues under
        // the lock but runs outside it and supports abandoning work that has not started.

        // ──────────────────────────────────────────────
        //  Handler Registration
        // ──────────────────────────────────────────────

        /// <summary>Registers a command handler.</summary>
        public void RegisterHandler(IMcpCommandHandler handler, bool enabled = true)
        {
            if (handler == null)
            {
                Debug.LogError("[McpHttpServer] Cannot register null handler");
                return;
            }

            var commandPrefix = handler.CommandPrefix;
            if (string.IsNullOrEmpty(commandPrefix))
            {
                Debug.LogError($"[McpHttpServer] Handler {handler.GetType().Name} has invalid command prefix");
                return;
            }

            if (this.commandHandlers.ContainsKey(commandPrefix))
            {
                Debug.LogWarning($"[McpHttpServer] Replacing existing handler for '{commandPrefix}'");
            }

            if (McpSettings.instance.handlerEnabledStates.TryGetValue(commandPrefix, out var savedEnabled))
            {
                enabled = savedEnabled;
            }

            this.commandHandlers[commandPrefix] = new HandlerRegistration(handler, enabled);
            Debug.Log($"[McpHttpServer] Registered command handler: {commandPrefix} (Enabled: {enabled})");

            McpSettings.instance.UpdateHandlerEnabledState(commandPrefix, enabled);
        }

        /// <summary>Enables or disables a command handler.</summary>
        public bool SetHandlerEnabled(string commandPrefix, bool enabled)
        {
            if (!this.commandHandlers.TryGetValue(commandPrefix, out var registration))
            {
                Debug.LogWarning($"[McpHttpServer] Handler '{commandPrefix}' not found");
                return false;
            }

            registration.Enabled = enabled;
            McpSettings.instance.UpdateHandlerEnabledState(commandPrefix, enabled);
            return true;
        }

        /// <summary>Gets all registered command handlers.</summary>
        public IReadOnlyDictionary<string, HandlerRegistration> GetRegisteredHandlers()
        {
            return this.commandHandlers;
        }

        /// <summary>Registers a resource handler.</summary>
        public bool RegisterResourceHandler(IMcpResourceHandler handler, bool enabled = true)
        {
            if (handler == null)
            {
                Debug.LogError("[McpHttpServer] Cannot register null resource handler");
                return false;
            }

            var resourceName = handler.ResourceName;
            if (string.IsNullOrEmpty(resourceName))
            {
                Debug.LogError($"[McpHttpServer] Handler {handler.GetType().Name} has invalid resource name");
                return false;
            }

            if (this.resourceHandlers.ContainsKey(resourceName))
            {
                Debug.LogWarning($"[McpHttpServer] Replacing existing resource handler '{resourceName}'");
                this.resourceHandlers.Remove(resourceName);
            }

            if (McpSettings.instance.resourceHandlerEnabledStates.TryGetValue(resourceName, out var savedEnabled))
            {
                enabled = savedEnabled;
            }

            this.resourceHandlers[resourceName] = new ResourceHandlerRegistration(handler, enabled);

            if (!string.IsNullOrEmpty(handler.ResourceUri))
            {
                this.resourceUriMap[handler.ResourceUri] = handler;
            }

            Debug.Log($"[McpHttpServer] Registered resource handler: {resourceName} (Enabled: {enabled})");
            McpSettings.instance.UpdateResourceHandlerEnabledState(resourceName, enabled);

            return true;
        }

        /// <summary>Enables or disables a resource handler.</summary>
        public bool SetResourceHandlerEnabled(string resourceName, bool enabled)
        {
            if (!this.resourceHandlers.TryGetValue(resourceName, out var registration))
            {
                Debug.LogWarning($"[McpHttpServer] Resource handler '{resourceName}' not found");
                return false;
            }

            registration.Enabled = enabled;
            McpSettings.instance.UpdateResourceHandlerEnabledState(resourceName, enabled);
            return true;
        }

        /// <summary>Gets all registered resource handlers.</summary>
        public IReadOnlyDictionary<string, ResourceHandlerRegistration> GetRegisteredResourceHandlers()
        {
            return this.resourceHandlers;
        }

        // ──────────────────────────────────────────────
        //  Resource Fetching
        // ──────────────────────────────────────────────

        private JObject FetchResourceData(string resourceName, JObject parameters)
        {
            if (!this.resourceHandlers.TryGetValue(resourceName, out var registration))
            {
                // Return an error payload — WriteEnvelope will detect the missing result and treat as error
                throw new InvalidOperationException($"Resource not found: {resourceName}");
            }

            if (!registration.Enabled)
            {
                throw new InvalidOperationException($"Resource '{resourceName}' is disabled");
            }

            var result = registration.Handler.FetchResource(parameters);
            this.OnResourceFetched(new ResourceFetchedEventArgs(resourceName, parameters, result));
            return result;
        }

        // ──────────────────────────────────────────────
        //  Events
        // ──────────────────────────────────────────────

        private void OnCommandExecuted(CommandExecutedEventArgs e) => this.CommandExecuted?.Invoke(this, e);
        private void OnResourceFetched(ResourceFetchedEventArgs e) => this.ResourceFetched?.Invoke(this, e);

        // ──────────────────────────────────────────────
        //  IDisposable
        // ──────────────────────────────────────────────

        public void Dispose() => this.Dispose(withdrawDescriptor: true);

        /// <param name="withdrawDescriptor">See <see cref="Stop"/>.</param>
        public void Dispose(bool withdrawDescriptor)
        {
            this.Stop(withdrawDescriptor);
            EditorApplication.update -= this.dispatcher.Pump;

            // Nothing will pump the queue after this point, so release anything still waiting
            // rather than letting those requests block for their full sync window.
            this.dispatcher.DrainAndFail("Unity MCP server shut down before this work started.");
            GC.SuppressFinalize(this);
        }

        // ──────────────────────────────────────────────
        //  Inner Types
        // ──────────────────────────────────────────────

        public class HandlerRegistration
        {
            public IMcpCommandHandler Handler { get; }
            public bool Enabled { get; set; }
            public string Description => this.Handler.Description;
            public string AssemblyName { get; }

            public HandlerRegistration(IMcpCommandHandler handler, bool enabled = true)
            {
                this.Handler = handler;
                this.Enabled = enabled;
                this.AssemblyName = handler.GetType().Assembly.GetName().Name;
            }
        }

        public class ResourceHandlerRegistration
        {
            public IMcpResourceHandler Handler { get; }
            public bool Enabled { get; set; }
            public string Description => this.Handler.Description;
            public string AssemblyName { get; }

            public ResourceHandlerRegistration(IMcpResourceHandler handler, bool enabled = true)
            {
                this.Handler = handler;
                this.Enabled = enabled;
                this.AssemblyName = handler.GetType().Assembly.GetName().Name;
            }
        }

        public class CommandExecutedEventArgs : EventArgs
        {
            public string Prefix { get; }
            public string Action { get; }
            public JObject Parameters { get; }
            public JObject Result { get; }

            public CommandExecutedEventArgs(string prefix, string action, JObject parameters, JObject result)
            {
                this.Prefix = prefix;
                this.Action = action;
                this.Parameters = parameters;
                this.Result = result;
            }
        }

        public class ResourceFetchedEventArgs : EventArgs
        {
            public string ResourceName { get; }
            public JObject Parameters { get; }
            public JObject Result { get; }

            public ResourceFetchedEventArgs(string resourceName, JObject parameters, JObject result)
            {
                this.ResourceName = resourceName;
                this.Parameters = parameters;
                this.Result = result;
            }
        }
    }
}
