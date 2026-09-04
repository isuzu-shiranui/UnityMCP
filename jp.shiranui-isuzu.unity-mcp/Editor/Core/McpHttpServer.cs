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
        /// The advertised version is read from the package manifest, not from this constant: a
        /// hand-written number goes stale against the package on the next release, and a comment
        /// asking for the two to be kept in step is not a mechanism. This literal is only the
        /// answer for an assembly that is not loaded from a package, and CI checks that it too
        /// stays in step.
        /// </remarks>
        private const string FallbackVersion = "4.0.0";

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

        /// <summary>
        /// How long a request waits for its main-thread work before switching to a job id.
        /// <para>
        /// The wait is short on purpose. Anything slower is better served by a job the caller
        /// can poll than by a blocked socket: a timeout would leave the work queued, and a
        /// client that retried on it would run the work twice.
        /// </para>
        /// </summary>
        private static int SyncWaitMs => Math.Max(250, McpSettings.instance.syncWaitMs);

        // HTTP server
        private HttpListener httpListener;
        private Thread listenerThread;
        private int boundPort;
        private bool running;

        /// <summary>
        /// Bearer token clients must present. Fixed per project and stored in the user's
        /// profile (<see cref="McpAuthToken"/>), because MCP clients keep it in configuration
        /// that is written once.
        /// </summary>
        private string authToken;

        // Main thread marshalling and long-running job tracking
        private readonly McpMainThreadDispatcher dispatcher = new();
        private readonly McpJobRegistry jobs = new();
        private readonly ToolCallRunner toolCalls;
        private readonly McpStreamableHttpEndpoint mcpEndpoint;
        private readonly EditorLoopWaker loopWaker;
        private readonly MainThreadWatch mainThread = new();

        /// <summary>Jobs tracked for calls that outlived the sync window.</summary>
        public McpJobRegistry Jobs => this.jobs;

        /// <summary>
        /// How long the main thread has been away while work waited for it; zero when it is
        /// keeping up or nothing is waiting. Readable from any thread.
        /// </summary>
        public long MainThreadStalledMs =>
            this.mainThread.StalledMs(this.dispatcher.PendingCount > 0 || this.jobs.RunningCount > 0);

        // Attribute-declared tools, discovered lazily on first use and after an explicit refresh.
        private ToolCatalog toolCatalog;
        private readonly object catalogLock = new();

        // Tools defined by JSON files, loaded with the catalog; the directories are computed on the
        // main thread here because the catalog is rebuilt from an HTTP worker.
        private readonly string definitionsDir;
        private readonly string sharedDefinitionsDir;
        private DefinedToolSet definedTools = DefinedToolSet.Empty;
        private DefinedToolsWatcher definitionsWatcher;

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

        /// <summary>Gets whether the server is running.</summary>
        public bool IsRunning => this.running;

        /// <summary>Gets whether the server is listening (alias for IsRunning for HTTP server).</summary>
        public bool IsConnected => this.running;

        /// <summary>Gets the port the server is bound to.</summary>
        public int BoundPort => this.boundPort;

        /// <summary>The port this project's clients are configured for.</summary>
        public int PreferredPort { get; private set; }

        /// <summary>
        /// True when the preferred port was busy and the server had to scan. Clients configured
        /// with the preferred URL cannot reach this instance until it is resolved.
        /// </summary>
        public bool PortMismatch => this.running && this.boundPort != this.PreferredPort;

        /// <summary>The MCP endpoint URL for this Editor.</summary>
        public string McpUrl => $"http://127.0.0.1:{this.boundPort}/mcp";

        /// <summary>The bearer token clients present. A credential; never log it.</summary>
        public string Token => this.authToken;

        /// <summary>
        /// See <see cref="McpSettings.keepEditorAwake"/>. The always-on pump runs only while the
        /// server does; a stopped server has no requests for it to serve.
        /// </summary>
        public bool KeepEditorAwake
        {
            get => this.loopWaker.AlwaysOn;
            set => this.loopWaker.AlwaysOn = value && this.running;
        }

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
            this.PreferredPort = McpPortPolicy.Resolve(McpSettings.instance, this.projectPath);
            this.boundPort = this.PreferredPort;
            this.authToken = McpAuthToken.Load(this.projectPath);
            this.definitionsDir = DefinedTools.ProjectDirectory(this.projectPath);
            this.sharedDefinitionsDir = DefinedTools.SharedDirectory;
            this.toolCalls = new ToolCallRunner(this.dispatcher, this.jobs, () => SyncWaitMs);
            this.mcpEndpoint = new McpStreamableHttpEndpoint(
                () => this.GetToolCatalog(forceRefresh: false),
                this.toolCalls.Run,
                () => ProtocolVersion,
                this.RunningNotice);

            this.loopWaker = new EditorLoopWaker(() => this.dispatcher.PendingCount > 0 || FrameSequencer.ActiveCount > 0);
            this.dispatcher.WorkQueued = this.loopWaker.Demand;
            this.dispatcher.Pumped = this.mainThread.MarkPumped;

            EditorApplication.update += this.dispatcher.Pump;
            EditorApplication.update += this.mainThread.MarkPumped;

            if (DetailedLogs)
            {
                Debug.Log($"[McpHttpServer] Initialized, preferred port={this.PreferredPort}");
            }
        }

        // ──────────────────────────────────────────────
        //  Lifecycle  (race-free Start per design §2.1)
        // ──────────────────────────────────────────────

        /// <summary>
        /// Starts the HTTP server and UDP broadcaster.
        /// </summary>
        /// <param name="preferredPort">
        /// The port to try first. Defaults to the project's stable port
        /// (<see cref="McpPortPolicy"/>); a domain reload passes the port it held before so the
        /// URL clients already use stays valid.
        /// </param>
        public void Start(int? preferredPort = null)
        {
            if (this.running) return;

            this.PreferredPort = McpPortPolicy.Resolve(McpSettings.instance, this.projectPath);
            var startPort = preferredPort ?? this.PreferredPort;

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

                // The only place the bound URL reaches the user, and an MCP client cannot be
                // configured without it, so it stays out of the detailed-logs gate.
                Debug.Log($"[McpHttpServer] HTTP server listening on http://127.0.0.1:{this.boundPort}/");

                // Sweep first so a descriptor left by a killed Editor is not mistaken for a
                // second live instance.
                McpInstanceDescriptor.RemoveStale();
                this.PublishDescriptor();

                this.definitionsWatcher = new DefinedToolsWatcher(
                    new[] { this.definitionsDir, this.sharedDefinitionsDir },
                    () => this.GetToolCatalog(forceRefresh: true),
                    this.dispatcher.LogError);

                this.loopWaker.AlwaysOn = McpSettings.instance.keepEditorAwake;

                if (this.PortMismatch)
                {
                    Debug.LogWarning(
                        $"[McpHttpServer] Port {this.PreferredPort} was busy, so this Editor is on {this.boundPort}. " +
                        "MCP clients configured for the usual URL will not reach it until the other process " +
                        "releases the port or an explicit port is set in Preferences > Unity MCP.");
                }
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
            this.loopWaker.AlwaysOn = false;

            this.definitionsWatcher?.Dispose();
            this.definitionsWatcher = null;

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

            // Multi-frame work is driven by EditorApplication.update, which keeps ticking after
            // the listener is gone. Cancelling releases the requests waiting on it, which no
            // longer have a route to answer on.
            FrameSequencer.CancelAll("Unity MCP server stopped.");

            // IMPORTANT: Stop() does NOT touch SessionState.
            // Reload path (OnBeforeAssemblyReload) must preserve WasRunning=true,
            // so it writes SessionState before calling Dispose() → Stop().
            // User-initiated stop callers are responsible for persisting WasRunning=false explicitly.

            if (DetailedLogs)
            {
                Debug.Log("[McpHttpServer] Server stopped");
            }

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
        /// <see cref="HttpListenerException"/> is not enough: the Editor runs on Mono, whose
        /// HttpListener is implemented over managed sockets, so a busy port surfaces as a
        /// <see cref="SocketException"/> instead. One that escapes the catch aborts the whole
        /// scan on its first candidate, and a second Editor — or the same Editor rebinding
        /// after a domain reload while its previous socket is still in TIME_WAIT — then
        /// reports "only one usage of each socket address is normally permitted" and starts
        /// no server at all, with nineteen free ports in the range.
        /// <para>
        /// Only the 127.0.0.1 prefix is registered. Adding a `localhost` prefix for the same
        /// port binds nothing extra — clients connect to the loopback address — while giving
        /// the bind a second way to fail.
        /// </para>
        /// </remarks>
        private int StartHttpListener(int startPort)
        {
            var firstFailure = string.Empty;

            // The port a project was on a moment ago is usually still in TIME_WAIT right after
            // a domain reload; a short retry keeps the project on the URL its clients hold.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (TryBind(startPort, out var bound, ref firstFailure))
                {
                    this.httpListener = bound;
                    return startPort;
                }

                Thread.Sleep(200);
            }

            for (var port = McpPortPolicy.RangeStart; port <= McpPortPolicy.RangeEnd; port++)
            {
                if (port == startPort)
                {
                    continue;
                }

                if (TryBind(port, out var bound, ref firstFailure))
                {
                    this.httpListener = bound;
                    return port;
                }
            }

            throw new InvalidOperationException(
                $"No free port among {startPort} and {McpPortPolicy.RangeStart}-{McpPortPolicy.RangeEnd}. First failure was {firstFailure}");
        }

        private static bool TryBind(int port, out HttpListener bound, ref string firstFailure)
        {
            {
                HttpListener listener = null;

                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();
                    bound = listener;
                    return true;
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
                        Debug.Log($"[McpHttpServer] Port {port} unavailable ({e.GetType().Name})");
                    }

                    bound = null;
                    return false;
                }
            }
        }

        private void PublishDescriptor()
        {
            McpInstanceDescriptor.Write(
                this.projectPath,
                this.productName,
                this.unityVersion,
                this.boundPort,
                this.PreferredPort,
                this.authToken,
                ProtocolVersion,
                McpStreamableHttpEndpoint.SupportedProtocolVersions);
        }

        /// <summary>
        /// Replaces this project's token. Every client registered with the old one has to be
        /// registered again (<c>isuzu-unity-cli doctor --fix</c>).
        /// </summary>
        public void RegenerateToken()
        {
            this.authToken = McpAuthToken.Regenerate(this.projectPath);

            if (this.running)
            {
                this.PublishDescriptor();
            }
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

            // No Access-Control-Allow-Origin. Sending "*" would let any web page the user has
            // open POST to /tools/execute_code and run arbitrary C# in their Editor. Omitting
            // the header makes the browser refuse to hand the response to page script, and the
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

                if (path == "/mcp")
                {
                    this.HandleMcp(request, response);
                    return;
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

                    default:
                        this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown endpoint: {method} {path}");
                        break;
                }
            }
            catch (ObjectDisposedException) when (!this.running)
            {
                // The listener closed under a request that was already in flight, which is what
                // a domain reload looks like from here. Logging it would put an error in the
                // console on every script compile, and console_read_logs would hand it to the
                // agent as if the project had failed.
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

        /// <summary>
        /// Writes an exception as an error envelope, preserving the code and HTTP status of
        /// exceptions that carry their own (so a missing window stays 400/window_not_found
        /// rather than collapsing into a generic 500).
        /// </summary>
        private void WriteError(HttpListenerResponse response, Exception error)
        {
            switch (error)
            {
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
        /// This payload is the entire tool surface; the CLI and the MCP endpoint both read it.
        /// </para>
        /// Pass <c>?refresh=1</c> to rediscover — needed only when assemblies were loaded
        /// after the catalog was first built.
        /// </summary>
        private void HandleToolCatalog(HttpListenerRequest request, HttpListenerResponse response)
        {
            var refresh = request.QueryString["refresh"];
            var catalog = this.GetToolCatalog(forceRefresh: refresh == "1" || refresh == "true");

            var groups = McpToolGroups.Parse(request.QueryString["group"], out var unknownGroups);
            if (unknownGroups.Count > 0)
            {
                this.WriteEnvelope(
                    response,
                    400,
                    null,
                    errorCode: "invalid_params",
                    errorMessage: $"Unknown tool group '{unknownGroups[0]}'. Known: {string.Join(", ", McpToolGroups.Known)}.");
                return;
            }

            var cached = catalog.CatalogEnvelopeUtf8(groups);
            if (cached != null)
            {
                this.WriteRawBytes(response, 200, cached);
                return;
            }

            var payload = new JObject { ["tools"] = new JRaw(catalog.ToolsArrayJson(groups, mcpShape: false)) };

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

            var outcome = this.toolCalls.Run(descriptor, arguments);

            switch (outcome.State)
            {
                case ToolCallOutcome.Kind.Completed:
                    this.WriteEnvelope(response, 200, outcome.Result);
                    break;

                case ToolCallOutcome.Kind.Failed:
                    this.WriteError(response, outcome.Error);
                    break;

                default:
                    this.WriteEnvelope(response, 202, this.RunningEnvelope(name, outcome.JobId));
                    break;
            }
        }

        /// <summary>
        /// <c>/mcp</c>: copies the request into the socket-free endpoint and writes what it returns.
        /// </summary>
        private void HandleMcp(HttpListenerRequest request, HttpListenerResponse response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in request.Headers.AllKeys)
            {
                if (key != null)
                {
                    headers[key] = request.Headers[key];
                }
            }

            string body = null;
            if (request.HasEntityBody)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                body = reader.ReadToEnd();
            }

            var result = this.mcpEndpoint.Handle(request.HttpMethod, headers, body, request.QueryString["group"]);

            if (result.Allow != null)
            {
                response.AddHeader("Allow", result.Allow);
            }

            // Mono's HttpListener can hold the connection open waiting for a body on an empty
            // reply, so empty replies close the connection outright.
            if (!result.HasContent)
            {
                response.KeepAlive = false;
            }

            if (result.Segments != null)
            {
                this.WriteRawBytes(response, result.Status, result.Segments);
                return;
            }

            this.WriteRaw(response, result.Status, result.Body);
        }

        /// <summary>Writes pre-rendered UTF-8 segments as one JSON body without joining them first.</summary>
        private void WriteRawBytes(HttpListenerResponse response, int statusCode, params byte[][] segments)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";

            long total = 0;
            foreach (var segment in segments)
            {
                total += segment.Length;
            }

            response.ContentLength64 = total;
            foreach (var segment in segments)
            {
                response.OutputStream.Write(segment, 0, segment.Length);
            }

            response.Close();
        }

        /// <summary>
        /// The 202 body for a call that outlived the sync window. The work is not cancelled; its
        /// outcome lands in the job, so the message tells the caller to poll rather than retry.
        /// </summary>
        private JObject RunningEnvelope(string label, string jobId)
        {
            var message =
                $"'{label}' is still running on the Editor main thread. " +
                $"Poll GET /jobs/{jobId} for the result. Do not retry this call — " +
                "the work is still in progress and retrying would run it twice.";

            var envelope = new JObject
            {
                ["state"] = "running",
                ["jobId"] = jobId,
                ["poll"] = $"/jobs/{jobId}",
            };

            var notice = this.RunningNotice();
            if (notice != null)
            {
                message += " " + notice;
                envelope["notice"] = notice;
            }

            envelope["message"] = message;
            return envelope;
        }

        /// <summary>
        /// What is keeping the main thread, for every answer that says a call is still running:
        /// the front-most dialog when there is one, otherwise a long absence. Null when neither.
        /// </summary>
        public string RunningNotice()
        {
            var dialogs = EditorDialogs.List();
            return MainThreadWatch.RunningNotice(dialogs.Length > 0 ? dialogs[0] : null, this.MainThreadStalledMs);
        }

        /// <summary>
        /// A job as reported to callers. A running job carries the notice about what blocks it,
        /// as <c>message</c> for a reader and as <c>notice</c> for a client that prints only that.
        /// </summary>
        public JObject JobDetail(McpJobRegistry.JobEntry entry)
        {
            var payload = entry.ToDetailJson();

            if (entry.Status == "running")
            {
                var notice = this.RunningNotice();
                if (notice != null)
                {
                    payload["message"] = notice;
                    payload["notice"] = notice;
                }
            }

            return payload;
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

                this.toolCatalog = this.BuildCatalog();
                this.toolCatalog.ReportErrors(this.dispatcher.LogError);
                return this.toolCatalog;
            }
        }

        /// <summary>
        /// Assembles the catalog this server serves over <c>/tools</c> and <c>tools/list</c>.
        /// </summary>
        private ToolCatalog BuildCatalog()
        {
            var loaded = DefinedToolSet.Empty;

            var catalog = ToolCatalog.Build((attributeTools, errors) =>
            {
                loaded = DefinedTools.Load(
                    new[] { this.definitionsDir, this.sharedDefinitionsDir }, errors, attributeTools);
                return loaded.Descriptors;
            });

            // Registration refusals name the file too, but land in the catalog's list; folding
            // them in keeps definitions_list the one place that says why a definition is absent.
            this.definedTools = loaded.WithErrors(
                catalog.Errors.Where(e => e.StartsWith("Defined tool", StringComparison.Ordinal)));

            return catalog;
        }

        /// <summary>
        /// What <c>definitions_list</c> reports: the definition directories, the tools loaded
        /// from them, and every refusal.
        /// </summary>
        public JObject DescribeDefinedTools()
        {
            this.GetToolCatalog(forceRefresh: false);

            DefinedToolSet set;
            lock (this.catalogLock)
            {
                set = this.definedTools;
            }

            return new JObject
            {
                ["definitionsDir"] = this.definitionsDir,
                ["sharedDefinitionsDir"] = this.sharedDefinitionsDir,
                ["tools"] = new JArray(set.Entries.Select(e => (object)new JObject
                {
                    ["name"] = e.Name,
                    ["kind"] = e.Kind,
                    ["file"] = e.File,
                }).ToArray()),
                ["errors"] = new JArray(set.Errors.Cast<object>().ToArray()),
            };
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

                this.WriteEnvelope(response, 200, this.JobDetail(entry));
                return;
            }

            if (method != "POST")
            {
                this.WriteEnvelope(response, 404, null, errorCode: "handler_not_found", errorMessage: $"Unknown endpoint: {method} {path}");
                return;
            }

            var cancelled = entry.Item.TryAbandon();
            var payload = this.JobDetail(entry);

            // Distinguishing these two matters: "cancelled" means the work provably never ran,
            // "already_started" means the caller must assume its side effects happened.
            payload["cancelled"] = cancelled;
            payload["outcome"] = cancelled ? "cancelled_before_start" : "already_started";

            this.WriteEnvelope(response, 200, payload);
        }

        // ──────────────────────────────────────────────
        //  /capture_screenshot — specialised error handling
        // ──────────────────────────────────────────────

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
        /// </summary>
        private JObject BuildHealthResponse()
        {
            var uptimeSec = (DateTime.Now - this.ConnectedSince).TotalSeconds;

            return new JObject
            {
                ["v"] = ProtocolVersion,
                ["project"] = this.productName,
                ["unity"] = this.unityVersion,
                ["port"] = this.boundPort,
                ["preferredPort"] = this.PreferredPort,
                ["portMismatch"] = this.PortMismatch,
                ["mcpUrl"] = this.McpUrl,
                ["clientId"] = this.ClientId,
                ["state"] = "running",
                ["uptimeSec"] = uptimeSec,
                ["reqCount"] = Interlocked.Read(ref this.requestCount),
                // Both are read without touching the main thread, so they stay meaningful
                // precisely when the Editor is too busy to answer anything else. A climbing
                // queueDepth with a static reqCount is the signature of a wedged main thread.
                ["queueDepth"] = this.dispatcher.PendingCount,
                ["runningJobs"] = this.jobs.RunningCount,
                ["toolCount"] = this.GetToolCatalog(forceRefresh: false).Tools.Count(),
                // Where JSON tool definitions are read from; see definitions_list for what loaded.
                ["definitionsDir"] = this.definitionsDir,
                ["sharedDefinitionsDir"] = this.sharedDefinitionsDir,
                // Whether calls that need the main thread are answered promptly while the Editor
                // is unfocused, or wait for its background tick.
                ["loopWaker"] = this.loopWaker.IsAvailable ? (this.loopWaker.AlwaysOn ? "always" : "on-demand") : "unavailable",
                ["mainThread"] = this.MainThreadHealth(),
            };
        }

        /// <summary>
        /// Whether the main thread is keeping up with the work queued for it, and the dialog
        /// holding it when there is one. Read entirely off the main thread, like the counters
        /// above, so it is available exactly when the main thread is not.
        /// </summary>
        private JObject MainThreadHealth()
        {
            var dialogs = EditorDialogs.List();

            return new JObject
            {
                ["stalledMs"] = this.MainThreadStalledMs,
                ["dialog"] = dialogs.Length > 0 ? dialogs[0].ToJson() : null,
                ["dialogDetection"] = EditorDialogs.IsSupported ? "windows" : "unavailable",
            };
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
                // A handler that reports failure as `{"error": "msg"}` instead of throwing.
                // Promote it to a proper error envelope so status and HTTP code reflect the
                // failure.
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

        // Discovery is published through McpInstanceDescriptor.

        // ──────────────────────────────────────────────
        //  Main Thread Queue
        // ──────────────────────────────────────────────

        // Main-thread marshalling lives in McpMainThreadDispatcher.

        // ──────────────────────────────────────────────
        //  IDisposable
        // ──────────────────────────────────────────────

        public void Dispose() => this.Dispose(withdrawDescriptor: true);

        /// <param name="withdrawDescriptor">See <see cref="Stop"/>.</param>
        public void Dispose(bool withdrawDescriptor)
        {
            this.Stop(withdrawDescriptor);
            EditorApplication.update -= this.dispatcher.Pump;
            EditorApplication.update -= this.mainThread.MarkPumped;
            this.loopWaker.Dispose();

            // Nothing will pump the queue after this point, so release anything still waiting
            // rather than letting those requests block for their full sync window.
            this.dispatcher.DrainAndFail("Unity MCP server shut down before this work started.");
            GC.SuppressFinalize(this);
        }
    }
}
