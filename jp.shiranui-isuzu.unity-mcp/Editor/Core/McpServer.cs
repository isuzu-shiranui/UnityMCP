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

        // Queue for main thread execution
        private readonly Queue<Action> mainThreadQueue = new();
        private readonly object queueLock = new();

        private CancellationTokenSource cancellationTokenSource;

        // Events for tracking client connections
        public event EventHandler<ClientConnectedEventArgs> ClientConnected;
        public event EventHandler<EventArgs> ClientDisconnected;
        public event EventHandler<CommandExecutedEventArgs> CommandExecuted;

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
        /// Loads the enabled state of all handlers from McpSettings.
        /// </summary>
        private void LoadHandlerSettings()
        {
            var settings = McpSettings.instance;

            if (DetailedLogs)
            {
                Debug.Log($"[McpServer] Loading handler settings from McpSettings. Found {settings.handlerEnabledStates.Count} entries.");
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
                    catch (OperationCanceledException )
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

                // Execute the command
                var response = this.ExecuteCommand(command);

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
}
