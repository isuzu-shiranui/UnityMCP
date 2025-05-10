using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Settings;
using UnityMCP.Editor.Resources;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Represents the client that communicates with the MCP TypeScript server.
    /// </summary>
    internal sealed class McpServer : IDisposable
    {
        // Server configuration
        private string host;
        private int port;
        private bool running;
        private TcpClient client;
        private Thread clientThread;
        private readonly byte[] buffer = new byte[8192];
        private string incompleteData = "";
        private readonly string clientId;
        private DateTime connectedSince;

        // Dictionary for command handlers
        private readonly Dictionary<string, HandlerRegistration> commandHandlers = new();

        // Dictionary for resource handlers
        private readonly Dictionary<string, ResourceHandlerRegistration> resourceHandlers = new();
        private readonly Dictionary<string, IMcpResourceHandler> resourceUriMap = new();

        // Queue for main thread execution
        private readonly Queue<Action> mainThreadQueue = new();
        private readonly object queueLock = new();

        private CancellationTokenSource cancellationTokenSource;

        // Connection state
        private bool isConnecting;
        private bool isReconnecting;
        private DateTime lastConnectionAttempt = DateTime.MinValue;
        private readonly int reconnectDelay = 5000; // 5 seconds
        private readonly int maxReconnectDelay = 60000; // 1 minute
        private int currentReconnectDelay;

        // Project information (minimal for privacy)
        private readonly string productName = Application.productName;
        private readonly string unityVersion = Application.unityVersion;
        private readonly bool isEditor = Application.isEditor;
        private readonly string projectPath = Application.dataPath;

        // UDP Broadcast receiver
        private UdpClient udpListener;
        private readonly int broadcastPort = 27183; // Port for receiving TS server broadcasts
        private bool isUdpListening = false;
        private readonly object udpLock = new object();

        // Events for tracking client connections
        public event EventHandler<EventArgs> Connected;
        public event EventHandler<EventArgs> Disconnected;
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;
        public event EventHandler<ResourceFetchedEventArgs> ResourceFetched;

        /// <summary>
        /// Gets a value indicating whether the server is currently running.
        /// </summary>
        public bool IsRunning => this.running;

        /// <summary>
        /// Gets a value indicating whether the client is currently connected.
        /// </summary>
        public bool IsConnected => this.client is { Connected: true };

        /// <summary>
        /// Gets the client ID.
        /// </summary>
        public string ClientId => this.clientId;

        /// <summary>
        /// Gets the time when the connection was established.
        /// </summary>
        public DateTime ConnectedSince => this.connectedSince;

        private static bool DetailedLogs => McpSettings.instance.detailedLogs;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpServer"/> class with settings from McpSettings.
        /// </summary>
        /// <param name="port">Optional port override. If not provided, uses the port from settings.</param>
        public McpServer(int port = 0)
        {
            var settings = McpSettings.instance;
            this.host = settings.host;
            this.port = port > 0 ? port : settings.port;
            this.clientId = this.GenerateClientId();
            this.currentReconnectDelay = this.reconnectDelay;
            this.broadcastPort = settings.udpDiscoveryPort;

            // Load handler settings from McpSettings
            this.LoadHandlerSettings();

            // Register to update event for processing main thread actions
            EditorApplication.update += this.ProcessMainThreadQueue;

            if (DetailedLogs)
            {
                Debug.Log($"[McpServer] Initialized with host={this.host}, port={this.port}, clientId={this.clientId}");
            }

            // Start UDP listener if enabled in settings
            if (settings.useUdpDiscovery)
            {
                this.StartUdpListener();
            }
        }

        /// <summary>
        /// Generates a unique client ID based on project information.
        /// Privacy-focused: only uses project info, no device/user specific data.
        /// </summary>
        private string GenerateClientId()
        {
            // Project information only
            var productName = this.productName;

            // Generate hash from project path instead of using full path
            var projectPath = this.projectPath;
            var projectPathHash = Math.Abs(projectPath.GetHashCode());

            // Include editor/player mode as it's useful for classification
            var editorMode = this.isEditor ? "Editor" : "Player";

            // Create a unique identifier with minimal personal information
            return $"{productName}-{editorMode}-{projectPathHash}";
        }

        /// <summary>
        /// Starts the MCP client connection.
        /// </summary>
        public void Start()
        {
            if (this.running)
            {
                return;
            }

            try
            {
                this.cancellationTokenSource = new CancellationTokenSource();

                // Start client on a separate thread
                this.clientThread = new Thread(this.RunClient)
                {
                    IsBackground = true,
                    Name = "McpClientThread"
                };
                this.clientThread.Start(this.cancellationTokenSource.Token);
                this.running = true;
                Debug.Log($"MCP client started, connecting to {this.host}:{this.port}");

                // Start UDP listener if enabled in settings
                if (McpSettings.instance.useUdpDiscovery)
                {
                    this.StartUdpListener();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start MCP client: {e.Message}");
                this.Stop();
            }
        }

        /// <summary>
        /// Stops the MCP client.
        /// </summary>
        public void Stop()
        {
            this.running = false;

            this.cancellationTokenSource?.Cancel();

            if (this.client != null)
            {
                this.client.Close();
                this.client = null;
            }

            if (this.clientThread is { IsAlive: true })
            {
                this.clientThread.Join(1000); // Wait for the thread to finish
                this.clientThread = null;
            }

            // Stop UDP listener
            this.StopUdpListener();

            Debug.Log("MCP client stopped");
        }

        /// <summary>
        /// Starts the UDP listener for TS server broadcasts.
        /// </summary>
        private void StartUdpListener()
        {
            lock (this.udpLock)
            {
                if (this.isUdpListening)
                {
                    return;
                }

                try
                {
                    this.udpListener = new UdpClient();
                    this.udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    this.udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, this.broadcastPort));
                    this.isUdpListening = true;

                    // Start receiving UDP packets asynchronously
                    this.udpListener.BeginReceive(this.ReceiveCallback, this.udpListener);

                    Debug.Log($"[McpServer] Started UDP listener on port {this.broadcastPort}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[McpServer] Failed to start UDP listener: {ex.Message}");
                    this.isUdpListening = false;
                    this.udpListener?.Close();
                    this.udpListener = null;
                }
            }
        }

        /// <summary>
        /// Stops the UDP listener.
        /// </summary>
        private void StopUdpListener()
        {
            lock (this.udpLock)
            {
                if (!this.isUdpListening)
                {
                    return;
                }

                try
                {
                    this.udpListener?.Close();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[McpServer] Error closing UDP listener: {ex.Message}");
                }
                finally
                {
                    this.isUdpListening = false;
                    this.udpListener = null;
                }
            }
        }

        /// <summary>
        /// Callback for receiving UDP broadcast packets.
        /// </summary>
        private void ReceiveCallback(IAsyncResult ar)
        {
            var client = (UdpClient)ar.AsyncState;
            var remoteEP = new IPEndPoint(IPAddress.Any, 0);
            byte[] data = null;

            // Try to receive data
            try
            {
                lock (this.udpLock)
                {
                    if (!this.isUdpListening || client == null)
                    {
                        return;
                    }

                    data = client.EndReceive(ar, ref remoteEP);
                }
            }
            catch (ObjectDisposedException)
            {
                // UDP client has been closed, do nothing
                this.isUdpListening = false;
                return;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[McpServer] Error receiving UDP data: {ex.Message}");
            }

            // Continue receiving packets (even after errors)
            try
            {
                lock (this.udpLock)
                {
                    if (this.isUdpListening && client != null)
                    {
                        client.BeginReceive(this.ReceiveCallback, client);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[McpServer] Error continuing UDP receive: {ex.Message}");
                this.isUdpListening = false;
            }

            // Process received data
            if (data == null || data.Length <= 0) return;

            try
            {
                var jsonStr = Encoding.UTF8.GetString(data);

                if (DetailedLogs)
                {
                    Debug.Log($"[McpServer] Received UDP broadcast from {remoteEP}: {jsonStr}");
                }

                // Parse JSON
                var serverInfo = JObject.Parse(jsonStr);
                var messageType = serverInfo["type"]?.ToString();

                switch (messageType)
                {
                    case "listClients":
                    {
                        var host = serverInfo["host"]?.ToString();
                        var port = serverInfo["port"]?.Value<int>() ?? 0;
                        this.ExecuteOnMainThread(() =>
                        {
                            this.host = host;
                            this.port = port;
                            this.TryConnect();
                        });
                        return;
                    }
                    // Verify this is an MCP server announcement
                    case "mcp_server_announce":
                    {
                        var host = serverInfo["host"]?.ToString();
                        var port = serverInfo["port"]?.Value<int>() ?? 0;
                        var version = serverInfo["version"]?.ToString();

                        if (!string.IsNullOrEmpty(host) && port > 0)
                        {
                            // Execute on main thread
                            this.ExecuteOnMainThread(() =>
                            {
                                Debug.Log($"[McpServer] Detected MCP TypeScript server at {host}:{port} (v{version}), connecting...");

                                // Update host and port from broadcast
                                this.host = host;
                                this.port = port;
                                this.TryConnect();
                            });
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[McpServer] Failed to process UDP broadcast: {ex.Message}");
            }
        }

        /// <summary>
        /// Main client execution loop.
        /// </summary>
        private void RunClient(object token)
        {
            var cancellationToken = (CancellationToken)token;

            try
            {
                while (this.running && !cancellationToken.IsCancellationRequested)
                {
                    // Check connection state
                    if (!this.IsConnected && !this.isConnecting)
                    {
                        // Try to connect if not already connecting
                        this.TryConnect();
                    }
                    else if (this.IsConnected)
                    {
                        // Process incoming data
                        this.ProcessIncomingData();
                    }

                    // Small delay to prevent high CPU usage
                    try
                    {
                        Thread.Sleep(10);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation, just exit
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                this.client?.Close();
                this.client = null;
            }
        }

        /// <summary>
        /// Tries to connect to the MCP TypeScript server.
        /// </summary>
        private void TryConnect()
        {
            // Check if we need to wait before reconnecting
            if (this.isReconnecting)
            {
                var elapsed = (DateTime.Now - this.lastConnectionAttempt).TotalMilliseconds;
                if (elapsed < this.currentReconnectDelay)
                {
                    return;
                }
            }

            this.isConnecting = true;
            this.lastConnectionAttempt = DateTime.Now;

            try
            {
                // Close any existing connection
                if (this.client != null)
                {
                    this.client.Close();
                    this.client = null;
                }

                // Create new client
                this.client = new TcpClient();

                // Try to connect with timeout
                var result = this.client.BeginConnect(this.host, this.port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(5000); // 5 second timeout

                if (success && this.client.Connected)
                {
                    // Connected successfully
                    this.client.EndConnect(result);

                    // Reset reconnect delay after successful connection
                    this.currentReconnectDelay = this.reconnectDelay;
                    this.isReconnecting = false;
                    this.connectedSince = DateTime.Now;

                    Debug.Log($"Connected to MCP TypeScript server at {this.host}:{this.port}");

                    // Send client registration
                    this.SendClientRegistration();

                    // Raise connected event on main thread
                    this.ExecuteOnMainThread(() => this.OnConnected(EventArgs.Empty));
                }
                else
                {
                    // Connection failed
                    this.client.Close();
                    this.client = null;

                    if (!this.isReconnecting)
                    {
                        Debug.LogWarning($"Failed to connect to MCP TypeScript server at {this.host}:{this.port}. Will retry...");
                        this.isReconnecting = true;
                    }

                    // Increase reconnect delay with exponential backoff (capped)
                    this.currentReconnectDelay = Math.Min(this.currentReconnectDelay * 2, this.maxReconnectDelay);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error connecting to MCP TypeScript server: {e.Message}");
                this.client?.Close();
                this.client = null;
                this.isReconnecting = true;
            }
            finally
            {
                this.isConnecting = false;
            }
        }

        /// <summary>
        /// Sends client registration information to the MCP TypeScript server.
        /// Updated to respect privacy concerns.
        /// </summary>
        private void SendClientRegistration()
        {
            try
            {
                var registrationInfo = new JObject
                {
                    ["type"] = "registration",
                    ["clientId"] = this.clientId,
                    ["clientInfo"] = new JObject
                    {
                        ["productName"] = this.productName,
                        ["unityVersion"] = this.unityVersion,
                        ["isEditor"] = this.isEditor,
                        ["projectPathHash"] = Math.Abs(this.projectPath.GetHashCode())
                    }
                };

                // Send registration
                var responseJson = JsonConvert.SerializeObject(registrationInfo);
                var responseBytes = Encoding.UTF8.GetBytes(responseJson + "\n");
                var stream = this.client.GetStream();
                stream.Write(responseBytes, 0, responseBytes.Length);

                if (DetailedLogs)
                {
                    Debug.Log($"[McpServer] Sent client registration: {registrationInfo}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error sending client registration: {e.Message}");
            }
        }

        /// <summary>
        /// Processes incoming data from the MCP TypeScript server.
        /// </summary>
        private void ProcessIncomingData()
        {
            try
            {
                // Check if there's data available
                var stream = this.client.GetStream();
                if (!stream.DataAvailable) return;

                // Read available data
                var bytesRead = stream.Read(this.buffer, 0, this.buffer.Length);
                if (bytesRead > 0)
                {
                    var data = Encoding.UTF8.GetString(this.buffer, 0, bytesRead);
                    this.ProcessData(data);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error processing incoming data: {e.Message}");

                // Close connection on error
                this.client?.Close();
                this.client = null;

                // Notify disconnection on main thread
                this.ExecuteOnMainThread(() => this.OnDisconnected(EventArgs.Empty));
            }
        }

        /// <summary>
        /// Process incoming data from the TypeScript server.
        /// </summary>
        /// <param name="data">The data received.</param>
        private void ProcessData(string data)
        {
            // Add incoming data to any incomplete data from previous receives
            var fullData = this.incompleteData + data;

            try
            {
                // Try to parse the data as JSON
                var command = JObject.Parse(fullData);
                this.incompleteData = ""; // Reset incomplete data if successful

                if (DetailedLogs)
                {
                    Debug.Log($"[McpClient] Received command: {command}");
                }

                // Process the command based on its type
                var responseType = command["type"]?.ToString();
                JObject response;

                if (responseType == "resource")
                {
                    // Handle resource request
                    response = this.ProcessResourceRequest(command);
                }
                else
                {
                    // Default to command execution
                    response = this.ExecuteCommand(command);
                }

                // Send response
                if (!this.IsConnected) return;

                var responseJson = JsonConvert.SerializeObject(response);
                var responseBytes = Encoding.UTF8.GetBytes(responseJson + "\n");
                var stream = this.client.GetStream();
                stream.Write(responseBytes, 0, responseBytes.Length);

                if (DetailedLogs)
                {
                    Debug.Log($"[McpClient] Sent response: {responseJson}");
                }
            }
            catch (JsonReaderException)
            {
                // If JSON is incomplete, store it for the next receive
                this.incompleteData = fullData;

                if (DetailedLogs)
                {
                    Debug.Log($"[McpClient] Received incomplete JSON data, buffering for next receive");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error processing command: {e.Message}");

                // Send error response
                if (this.IsConnected)
                {
                    var errorResponse = new JObject
                    {
                        ["status"] = "error",
                        ["message"] = e.Message
                    };

                    var errorJson = JsonConvert.SerializeObject(errorResponse);
                    var errorBytes = Encoding.UTF8.GetBytes(errorJson + "\n");
                    var stream = this.client.GetStream();
                    stream.Write(errorBytes, 0, errorBytes.Length);

                    if (DetailedLogs)
                    {
                        Debug.Log($"[McpClient] Sent error response: {errorJson}");
                    }
                }

                this.incompleteData = ""; // Reset incomplete data
            }
        }

        /// <summary>
        /// Processes a resource request.
        /// </summary>
        /// <param name="request">The resource request.</param>
        /// <returns>The response to the request.</returns>
        private JObject ProcessResourceRequest(JObject request)
        {
            var command = request["command"]?.ToString();
            if (string.IsNullOrEmpty(command))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Missing command in resource request"
                };
            }

            var split = command.Split('.');
            if (split.Length < 2)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = $"Invalid command format: {command}. Expected format: 'prefix.action'"
                };
            }

            var resourceName = split[0];

            var id = request["id"]?.ToString();
            var parameters = request["params"] as JObject ?? new JObject();

            if (string.IsNullOrEmpty(resourceName))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Missing resource name",
                    ["id"] = id
                };
            }

            // Execute the resource fetch in the main thread
            JObject result = null;
            var waitHandle = new ManualResetEvent(false);

            // Queue the resource fetch for the main thread
            this.ExecuteOnMainThread(() =>
            {
                try
                {
                    result = this.FetchResourceData(resourceName, parameters);
                }
                catch (Exception e)
                {
                    result = new JObject
                    {
                        ["status"] = "error",
                        ["message"] = $"Error in {resourceName}: {e.Message}",
                        ["id"] = id
                    };
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            // Wait for the command to be executed on the main thread
            // Timeout after 5 seconds to prevent hanging
            if (!waitHandle.WaitOne(5000))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Timed out waiting for resource fetch on main thread",
                    ["id"] = id
                };
            }

            // If result is still null, execution failed
            if (result == null)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Failed to fetch resource in main thread",
                    ["id"] = id
                };
            }

            // Add the ID to the result
            result["id"] = id;
            return result;
        }

        /// <summary>
        /// Process actions in the main thread queue.
        /// </summary>
        private void ProcessMainThreadQueue()
        {
            lock (this.queueLock)
            {
                while (this.mainThreadQueue.Count > 0)
                {
                    var action = this.mainThreadQueue.Dequeue();
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Error executing action on main thread: {e.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Queue an action to be executed on the main thread.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        private void ExecuteOnMainThread(Action action)
        {
            lock (this.queueLock)
            {
                this.mainThreadQueue.Enqueue(action);
            }
        }

        /// <summary>
        /// Execute a command and return the result.
        /// </summary>
        /// <param name="command">The command to execute.</param>
        /// <returns>The result of the command execution.</returns>
        private JObject ExecuteCommand(JObject command)
        {
            var commandType = command["command"]?.ToString();
            var parameters = command["params"] as JObject ?? new JObject();
            var id = command["id"]?.ToString();

            if (string.IsNullOrEmpty(commandType))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Missing command type",
                    ["id"] = id
                };
            }

            // Execute the command in the main thread
            JObject result = null;
            var waitHandle = new ManualResetEvent(false);

            // Queue the command execution for the main thread
            this.ExecuteOnMainThread(() =>
            {
                try
                {
                    // Parse command format: "prefix.action"
                    var parts = commandType.Split('.');
                    if (parts.Length < 2)
                    {
                        result = new JObject
                        {
                            ["status"] = "error",
                            ["message"] = $"Invalid command format: {commandType}. Expected format: 'prefix.action'",
                            ["id"] = id
                        };
                    }
                    else
                    {
                        var prefix = parts[0];
                        var action = parts[1];

                        if (this.commandHandlers.TryGetValue(prefix, out var registration) && registration.Enabled)
                        {
                            result = registration.Handler.Execute(action, parameters);

                            // Raise command executed event
                            this.OnCommandExecuted(new CommandExecutedEventArgs(prefix, action, parameters, result));
                        }
                        else if (this.commandHandlers.TryGetValue(prefix, out _))
                        {
                            result = new JObject
                            {
                                ["status"] = "error",
                                ["message"] = $"Command prefix '{prefix}' is disabled",
                                ["id"] = id
                            };
                        }
                        else
                        {
                            result = new JObject
                            {
                                ["status"] = "error",
                                ["message"] = $"Unknown command prefix: {prefix}",
                                ["id"] = id
                            };
                        }
                    }
                }
                catch (Exception e)
                {
                    result = new JObject
                    {
                        ["status"] = "error",
                        ["message"] = $"Error in {commandType}: {e.Message}",
                        ["id"] = id
                    };
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            // Wait for the command to be executed on the main thread
            // Timeout after 5 seconds to prevent hanging
            if (!waitHandle.WaitOne(5000))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Timed out waiting for command execution on main thread",
                    ["id"] = id
                };
            }

            // If result is still null, execution failed
            if (result == null)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Failed to execute command in main thread",
                    ["id"] = id
                };
            }

            return new JObject
            {
                ["status"] = "success",
                ["result"] = result,
                ["id"] = id
            };
        }

        /// <summary>
        /// Fetches data from a resource handler.
        /// </summary>
        /// <param name="resourceName">The name of the resource.</param>
        /// <param name="parameters">The parameters for the resource.</param>
        /// <returns>The resource data as a JObject, or an error if the resource is not found or disabled.</returns>
        public JObject FetchResourceData(string resourceName, JObject parameters)
        {
            if (!this.resourceHandlers.TryGetValue(resourceName, out var registration))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = $"Resource not found: {resourceName}"
                };
            }

            if (!registration.Enabled)
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = $"Resource '{resourceName}' is disabled"
                };
            }

            try
            {
                var result = registration.Handler.FetchResource(parameters);

                // Raise resource fetched event
                this.OnResourceFetched(new ResourceFetchedEventArgs(resourceName, parameters, result));

                return new JObject
                {
                    ["status"] = "success",
                    ["result"] = result
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error fetching resource '{resourceName}': {ex.Message}");
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = $"Error fetching resource: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Loads the enabled state of all handlers from McpSettings.
        /// </summary>
        private void LoadHandlerSettings()
        {
            var settings = McpSettings.instance;

            if (DetailedLogs)
            {
                Debug.Log($"[McpServer] Loading handler settings from McpSettings. Found {settings.handlerEnabledStates.Count} command entries and {settings.resourceHandlerEnabledStates.Count} resource entries.");
            }
        }

        /// <summary>
        /// Registers a command handler with the server.
        /// </summary>
        /// <param name="handler">The command handler to register.</param>
        /// <param name="enabled">Whether the handler is enabled initially.</param>
        public void RegisterHandler(IMcpCommandHandler handler, bool enabled = true)
        {
            if (handler == null)
            {
                Debug.LogError("Cannot register null handler");
                return;
            }

            var commandPrefix = handler.CommandPrefix;
            if (string.IsNullOrEmpty(commandPrefix))
            {
                Debug.LogError($"Handler {handler.GetType().Name} has invalid command prefix");
                return;
            }

            // Check if a handler for this command already exists
            if (this.commandHandlers.ContainsKey(commandPrefix))
            {
                Debug.LogWarning($"Replacing existing handler for command '{commandPrefix}'");
            }

            // Check settings for enabled state
            if (McpSettings.instance.handlerEnabledStates.TryGetValue(commandPrefix, out var savedEnabled))
            {
                enabled = savedEnabled;
            }

            this.commandHandlers[commandPrefix] = new HandlerRegistration(handler, enabled);
            Debug.Log($"Registered handler for command: {commandPrefix} (Enabled: {enabled})");

            // Save the handler state to settings
            McpSettings.instance.UpdateHandlerEnabledState(commandPrefix, enabled);
        }

        /// <summary>
        /// Enables or disables a command handler.
        /// </summary>
        /// <param name="commandPrefix">The prefix of the command handler to enable or disable.</param>
        /// <param name="enabled">Whether the handler should be enabled.</param>
        /// <returns>true if the handler was found and its enabled state was set; otherwise, false.</returns>
        public bool SetHandlerEnabled(string commandPrefix, bool enabled)
        {
            if (!this.commandHandlers.TryGetValue(commandPrefix, out var registration))
            {
                Debug.LogWarning($"Handler for command '{commandPrefix}' not found");
                return false;
            }

            registration.Enabled = enabled;
            Debug.Log($"Handler for '{commandPrefix}' is now {(enabled ? "enabled" : "disabled")}");

            // Update settings
            McpSettings.instance.UpdateHandlerEnabledState(commandPrefix, enabled);

            return true;
        }

        /// <summary>
        /// Gets all registered command handlers.
        /// </summary>
        /// <returns>A dictionary of command prefixes and their handler registrations.</returns>
        public IReadOnlyDictionary<string, HandlerRegistration> GetRegisteredHandlers()
        {
            return this.commandHandlers;
        }

        /// <summary>
        /// Registers a resource handler with the server.
        /// </summary>
        /// <param name="handler">The resource handler to register.</param>
        /// <param name="enabled">Whether the handler is enabled initially.</param>
        /// <returns>True if registration succeeded, false if failed.</returns>
        public bool RegisterResourceHandler(IMcpResourceHandler handler, bool enabled = true)
        {
            if (handler == null)
            {
                Debug.LogError("Cannot register null resource handler");
                return false;
            }

            var resourceName = handler.ResourceName;
            if (string.IsNullOrEmpty(resourceName))
            {
                Debug.LogError($"Handler {handler.GetType().Name} has invalid resource name");
                return false;
            }

            // Check if a handler for this resource already exists
            if (this.resourceHandlers.ContainsKey(resourceName))
            {
                Debug.LogWarning($"Replacing existing handler for resource '{resourceName}'");
                this.resourceHandlers.Remove(resourceName);
            }

            // Check settings for enabled state
            if (McpSettings.instance.resourceHandlerEnabledStates.TryGetValue(resourceName, out var savedEnabled))
            {
                enabled = savedEnabled;
            }

            // Register with the resource name and URI maps
            this.resourceHandlers[resourceName] = new ResourceHandlerRegistration(handler, enabled);

            if (!string.IsNullOrEmpty(handler.ResourceUri))
            {
                this.resourceUriMap[handler.ResourceUri] = handler;
            }

            Debug.Log($"Registered resource handler: {resourceName} (URI: {handler.ResourceUri}, Enabled: {enabled})");

            // Save the handler state to settings
            McpSettings.instance.UpdateResourceHandlerEnabledState(resourceName, enabled);

            return true;
        }

        /// <summary>
        /// Enables or disables a resource handler.
        /// </summary>
        /// <param name="resourceName">The name of the resource handler to enable or disable.</param>
        /// <param name="enabled">Whether the handler should be enabled.</param>
        /// <returns>true if the handler was found and its enabled state was set; otherwise, false.</returns>
        public bool SetResourceHandlerEnabled(string resourceName, bool enabled)
        {
            if (!this.resourceHandlers.TryGetValue(resourceName, out var registration))
            {
                Debug.LogWarning($"Resource handler for '{resourceName}' not found");
                return false;
            }

            registration.Enabled = enabled;
            Debug.Log($"Resource handler for '{resourceName}' is now {(enabled ? "enabled" : "disabled")}");

            // Update settings
            McpSettings.instance.UpdateResourceHandlerEnabledState(resourceName, enabled);

            return true;
        }

        /// <summary>
        /// Gets all registered resource handlers.
        /// </summary>
        /// <returns>A dictionary of resource names and their handler registrations.</returns>
        public IReadOnlyDictionary<string, ResourceHandlerRegistration> GetRegisteredResourceHandlers()
        {
            return this.resourceHandlers;
        }

        /// <summary>
        /// Raises the Connected event.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnConnected(EventArgs e)
        {
            this.Connected?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the Disconnected event.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnDisconnected(EventArgs e)
        {
            this.Disconnected?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the CommandExecuted event.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnCommandExecuted(CommandExecutedEventArgs e)
        {
            this.CommandExecuted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the ResourceFetched event.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnResourceFetched(ResourceFetchedEventArgs e)
        {
            this.ResourceFetched?.Invoke(this, e);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Stop();

            // Unregister from the update event
            EditorApplication.update -= this.ProcessMainThreadQueue;

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Represents a registered command handler.
        /// </summary>
        public class HandlerRegistration
        {
            /// <summary>
            /// Gets the command handler.
            /// </summary>
            public IMcpCommandHandler Handler { get; }

            /// <summary>
            /// Gets or sets a value indicating whether the handler is enabled.
            /// </summary>
            public bool Enabled { get; set; }

            /// <summary>
            /// Gets the description of the handler.
            /// </summary>
            public string Description => this.Handler.Description;

            /// <summary>
            /// Gets the assembly name of the handler.
            /// </summary>
            public string AssemblyName { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="HandlerRegistration"/> class.
            /// </summary>
            /// <param name="handler">The command handler.</param>
            /// <param name="enabled">Whether the handler is enabled initially.</param>
            public HandlerRegistration(IMcpCommandHandler handler, bool enabled = true)
            {
                this.Handler = handler;
                this.Enabled = enabled;
                this.AssemblyName = handler.GetType().Assembly.GetName().Name;
            }
        }

        /// <summary>
        /// Represents a registered resource handler.
        /// </summary>
        public class ResourceHandlerRegistration
        {
            /// <summary>
            /// Gets the resource handler.
            /// </summary>
            public IMcpResourceHandler Handler { get; }

            /// <summary>
            /// Gets or sets a value indicating whether the handler is enabled.
            /// </summary>
            public bool Enabled { get; set; }

            /// <summary>
            /// Gets the description of the handler.
            /// </summary>
            public string Description => this.Handler.Description;

            /// <summary>
            /// Gets the assembly name of the handler.
            /// </summary>
            public string AssemblyName { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="ResourceHandlerRegistration"/> class.
            /// </summary>
            /// <param name="handler">The resource handler.</param>
            /// <param name="enabled">Whether the handler is enabled initially.</param>
            public ResourceHandlerRegistration(IMcpResourceHandler handler, bool enabled = true)
            {
                this.Handler = handler;
                this.Enabled = enabled;
                this.AssemblyName = handler.GetType().Assembly.GetName().Name;
            }
        }

        /// <summary>
        /// Provides data for the ClientConnected event.
        /// </summary>
        public class ClientConnectedEventArgs : EventArgs
        {
            /// <summary>
            /// Gets the client's IP endpoint.
            /// </summary>
            public IPEndPoint ClientEndPoint { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="ClientConnectedEventArgs"/> class.
            /// </summary>
            /// <param name="clientEndPoint">The client's IP endpoint.</param>
            public ClientConnectedEventArgs(IPEndPoint clientEndPoint)
            {
                this.ClientEndPoint = clientEndPoint;
            }
        }

        /// <summary>
        /// Provides data for the CommandExecuted event.
        /// </summary>
        public class CommandExecutedEventArgs : EventArgs
        {
            /// <summary>
            /// Gets the command prefix.
            /// </summary>
            public string Prefix { get; }

            /// <summary>
            /// Gets the command action.
            /// </summary>
            public string Action { get; }

            /// <summary>
            /// Gets the command parameters.
            /// </summary>
            public JObject Parameters { get; }

            /// <summary>
            /// Gets the command result.
            /// </summary>
            public JObject Result { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="CommandExecutedEventArgs"/> class.
            /// </summary>
            /// <param name="prefix">The command prefix.</param>
            /// <param name="action">The command action.</param>
            /// <param name="parameters">The command parameters.</param>
            /// <param name="result">The command result.</param>
            public CommandExecutedEventArgs(string prefix, string action, JObject parameters, JObject result)
            {
                this.Prefix = prefix;
                this.Action = action;
                this.Parameters = parameters;
                this.Result = result;
            }
        }

        /// <summary>
        /// Provides data for the ResourceFetched event.
        /// </summary>
        public class ResourceFetchedEventArgs : EventArgs
        {
            /// <summary>
            /// Gets the resource name.
            /// </summary>
            public string ResourceName { get; }

            /// <summary>
            /// Gets the resource parameters.
            /// </summary>
            public JObject Parameters { get; }

            /// <summary>
            /// Gets the resource result.
            /// </summary>
            public JObject Result { get; }

            /// <summary>
            /// Initializes a new instance of the <see cref="ResourceFetchedEventArgs"/> class.
            /// </summary>
            /// <param name="resourceName">The resource name.</param>
            /// <param name="parameters">The resource parameters.</param>
            /// <param name="result">The resource result.</param>
            public ResourceFetchedEventArgs(string resourceName, JObject parameters, JObject result)
            {
                this.ResourceName = resourceName;
                this.Parameters = parameters;
                this.Result = result;
            }
        }

        /// <summary>
        /// Server announcement data from TS broadcast
        /// </summary>
        [Serializable]
        private class ServerAnnouncement
        {
            public string type;
            public string host;
            public int port;
            public string version;
            public string protocol;
            public long timestamp;
        }
    }
}
