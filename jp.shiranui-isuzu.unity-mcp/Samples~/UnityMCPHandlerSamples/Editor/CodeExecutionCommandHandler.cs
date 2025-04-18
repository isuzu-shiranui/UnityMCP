using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCPHandlerSamples.Editor
{
    /// <summary>
    /// Command handler for executing C# code through the MCP interface.
    /// </summary>
    internal sealed class CodeExecutionCommandHandler : IMcpCommandHandler
    {
        /// <summary>
        /// The C# code executer instance.
        /// </summary>
        private readonly CSharpCodeRunner codeCodeRunner;

        /// <summary>
        /// Initializes a new instance of the <see cref="CodeExecutionCommandHandler"/> class.
        /// </summary>
        public CodeExecutionCommandHandler()
        {
            this.codeCodeRunner = new CSharpCodeRunner();
        }

        /// <summary>
        /// Gets the command prefix for this handler.
        /// </summary>
        public string CommandPrefix => "code";

        /// <summary>
        /// Gets the description of this command handler.
        /// </summary>
        public string Description => "Execute C# code in the Unity editor";

        /// <summary>
        /// Executes the command with the given parameters.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="parameters">The parameters for the command.</param>
        /// <returns>A JSON object containing the execution result.</returns>
        public JObject Execute(string action, JObject parameters)
        {
            return action.ToLower() switch
            {
                "execute" => this.ExecuteCode(parameters),
                _ => new JObject
                {
                    ["success"] = false,
                    ["error"] = $"Unknown action: {action}. Supported actions are 'execute' and 'evaluate'."
                }
            };
        }

        /// <summary>
        /// Executes C# code.
        /// </summary>
        /// <param name="parameters">The parameters containing the code to execute.</param>
        /// <returns>A JSON object containing the execution result.</returns>
        private JObject ExecuteCode(JObject parameters)
        {
            var code = parameters["code"]?.ToString();

            if (string.IsNullOrWhiteSpace(code))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "The 'code' parameter is required and cannot be empty."
                };
            }

            try
            {
                // Capture logs during execution
                var logMessages = new List<string>();
                var logHandler = new Application.LogCallback((logString, stackTrace, type) =>
                {
                    var prefix = type switch
                    {
                        LogType.Error or LogType.Exception => "[ERROR] ",
                        LogType.Warning => "[WARNING] ",
                        _ => "[INFO] "
                    };
                    logMessages.Add(prefix + logString);
                });

                Application.logMessageReceived += logHandler;

                // Execute on main thread
                this.codeCodeRunner.RunAsync(code);

                // Remove log handler
                Application.logMessageReceived -= logHandler;

                return new JObject
                {
                    ["success"] = true,
                    ["output"] = string.Join("\n", logMessages)
                };
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = $"Error executing code: {ex.Message}",
                    ["stackTrace"] = ex.StackTrace
                };
            }
        }
    }
}
