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
    /// Represents the server that handles MCP (Model Context Protocol) commands.
    /// </summary>
    internal sealed class McpServer : IDisposable
    {
        // Server configuration
        private string host;
        private int port;
        private bool running;
        private TcpListener listener;
        private TcpClient client;
        private Thread serverThread;
        private readonly byte[] buffer = new byte[8192];
        private string incompleteData = "";

        // Dictionary for command handlers
        private readonly Dictionary<string, HandlerRegistration> commandHandlers = new();

        // Dictionary for resource handlers
        private readonly Dictionary<string, ResourceHandlerRegistration> resourceHandlers = new();
        private readonly Dictionary<string, IMcpResourceHandler> resourceUriMap = new();

        // Queue for main thread execution
        private readonly Queue<Action> mainThreadQueue = new();
        private readonly object queueLock = new();

        private CancellationTokenSource cancellationTokenSource;

        // Events for tracking client connections
        public event EventHandler<ClientConnectedEventArgs> ClientConnected;
        public event EventHandler<EventArgs> ClientDisconnected;
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;
        public event EventHandler<ResourceFetchedEventArgs> ResourceFetched;

        /// <summary>
        /// Gets a value indicating whether the server is currently running.
        /// </summary>
        public bool IsRunning => this.running;

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

            // Load handler settings from McpSettings
            this.LoadHandlerSettings();

            // Register to update event for processing main thread actions
            EditorApplication.update += this.ProcessMainThreadQueue;

            if (DetailedLogs)
            {
                Debug.Log($"[McpServer] Initialized with host={this.host}, port={this.port}");
            }
        }

        /// <summary>
        /// Starts the MCP server.
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

                // Start server on a separate thread
                this.serverThread = new Thread(this.RunServer)
                {
                    IsBackground = true,
                    Name = "McpServerThread"
                };
                this.serverThread.Start(this.cancellationTokenSource.Token);
                this.running = true;
                Debug.Log($"MCP server started on {this.host}:{this.port}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to start MCP server: {e.Message}");
                this.Stop();
            }
        }

        /// <summary>
        /// Stops the MCP server.
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

            if (this.listener != null)
            {
                this.listener.Stop();
                this.listener = null;
            }

            if (this.serverThread is { IsAlive: true })
            {
                this.serverThread.Join(1000); // Wait for the thread to finish
                this.serverThread = null;
            }

            Debug.Log("MCP server stopped");
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
        /// Checks if a command handler is enabled.
        /// </summary>
        /// <param name="commandPrefix">The prefix of the command handler to check.</param>
        /// <returns>true if the handler is found and enabled; otherwise, false.</returns>
        public bool IsHandlerEnabled(string commandPrefix)
        {
            return this.commandHandlers.TryGetValue(commandPrefix, out var registration) && registration.Enabled;
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
        /// Checks if a resource handler is enabled.
        /// </summary>
        /// <param name="resourceName">The name of the resource handler to check.</param>
        /// <returns>true if the handler is found and enabled; otherwise, false.</returns>
        public bool IsResourceHandlerEnabled(string resourceName)
        {
            return this.resourceHandlers.TryGetValue(resourceName, out var registration) && registration.Enabled;
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
        /// Gets a resource handler by name.
        /// </summary>
        /// <param name="resourceName">The name of the resource.</param>
        /// <returns>The resource handler if found, null otherwise.</returns>
        public IMcpResourceHandler GetResourceHandler(string resourceName)
        {
            return this.resourceHandlers.TryGetValue(resourceName, out var registration)
                ? registration.Handler
                : null;
        }

        /// <summary>
        /// Gets a resource handler by URI.
        /// </summary>
        /// <param name="uri">The URI of the resource.</param>
        /// <returns>The resource handler if found, null otherwise.</returns>
        public IMcpResourceHandler GetResourceHandlerByUri(string uri)
        {
            return this.resourceUriMap.TryGetValue(uri, out var handler) ? handler : null;
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
        /// Main server execution loop.
        /// </summary>
        private void RunServer(object token)
        {
            var cancellationToken = (CancellationToken)token;

            try
            {
                var ipAddress = IPAddress.Parse(this.host);
                this.listener = new TcpListener(ipAddress, this.port);
                this.listener.Start();

                while (this.running && !cancellationToken.IsCancellationRequested)
                {
                    // Accept client connections
                    if (this.listener.Pending())
                    {
                        this.client?.Close();
                        this.client = this.listener.AcceptTcpClient();
                        var clientEndPoint = (IPEndPoint)this.client.Client.RemoteEndPoint;
                        Debug.Log($"Client connected: {clientEndPoint.Address}");

                        // Raise client connected event
                        this.OnClientConnected(new ClientConnectedEventArgs(clientEndPoint));
                    }

                    // Process client data
                    if (this.client is { Connected: true, Available: > 0 })
                    {
                        var stream = this.client.GetStream();
                        var bytesRead = stream.Read(this.buffer, 0, this.buffer.Length);

                        if (bytesRead > 0)
                        {
                            var data = Encoding.UTF8.GetString(this.buffer, 0, bytesRead);
                            this.ProcessData(data);
                        }
                    }
                    else if (this.client is { Connected: false })
                    {
                        // Client disconnected
                        this.OnClientDisconnected(EventArgs.Empty);
                        this.client = null;
                    }

                    try
                    {
                        Thread.Sleep(10); // Small delay to prevent high CPU usage
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
                this.listener?.Stop();
                this.listener = null;
            }
        }

        /// <summary>
        /// Process incoming data from the client.
        /// </summary>
        /// <param name="data">The data received from the client.</param>
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
                    Debug.Log($"[McpServer] Received command: {command}");
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
                if (this.client is { Connected: true })
                {
                    var responseJson = JsonConvert.SerializeObject(response);
                    var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                    var stream = this.client.GetStream();
                    stream.Write(responseBytes, 0, responseBytes.Length);

                    if (DetailedLogs)
                    {
                        Debug.Log($"[McpServer] Sent response: {responseJson}");
                    }
                }
            }
            catch (JsonReaderException)
            {
                // If JSON is incomplete, store it for the next receive
                this.incompleteData = fullData;

                if (DetailedLogs)
                {
                    Debug.Log($"[McpServer] Received incomplete JSON data, buffering for next receive");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error processing command: {e.Message}");

                // Send error response
                if (this.client is { Connected: true })
                {
                    var errorResponse = new JObject
                    {
                        ["status"] = "error",
                        ["message"] = e.Message
                    };

                    var errorJson = JsonConvert.SerializeObject(errorResponse);
                    var errorBytes = Encoding.UTF8.GetBytes(errorJson);
                    var stream = this.client.GetStream();
                    stream.Write(errorBytes, 0, errorBytes.Length);

                    if (DetailedLogs)
                    {
                        Debug.Log($"[McpServer] Sent error response: {errorJson}");
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
            this.ExecuteOnMainThread(() => {
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
            this.ExecuteOnMainThread(() => {
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
        /// Raises the ClientConnected event.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnClientConnected(ClientConnectedEventArgs e)
        {
            this.ClientConnected?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the ClientDisconnected event.
        /// </summary>
        /// <param name="e">The event arguments.</param>
        private void OnClientDisconnected(EventArgs e)
        {
            this.ClientDisconnected?.Invoke(this, e);
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
}
