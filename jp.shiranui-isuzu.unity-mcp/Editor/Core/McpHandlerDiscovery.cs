using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Provides automatic discovery and registration of MCP command handlers.
    /// </summary>
    internal sealed class McpHandlerDiscovery
    {
        private readonly McpServer server;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHandlerDiscovery"/> class.
        /// </summary>
        /// <param name="server">The MCP server to register handlers with.</param>
        public McpHandlerDiscovery(McpServer server)
        {
            this.server = server ?? throw new ArgumentNullException(nameof(server));
        }

        /// <summary>
        /// Discovers and registers all command handlers in the current domain.
        /// </summary>
        /// <returns>The number of handlers registered.</returns>
        public int DiscoverAndRegisterHandlers()
        {
            int count = 0;

            try
            {
                // Get all assemblies in the current domain
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                foreach (var assembly in assemblies)
                {
                    // Skip system and Unity assemblies to improve performance
                    if (assembly.FullName.StartsWith("System.") ||
                        assembly.FullName.StartsWith("Unity.") ||
                        assembly.FullName.StartsWith("UnityEngine.") ||
                        assembly.FullName.StartsWith("UnityEditor."))
                    {
                        continue;
                    }

                    try
                    {
                        // Find all non-abstract classes that implement IMcpCommandHandler
                        var handlerTypes = assembly.GetTypes()
                            .Where(t => typeof(IMcpCommandHandler).IsAssignableFrom(t) &&
                                  !t.IsInterface &&
                                  !t.IsAbstract)
                            .ToArray();

                        foreach (var handlerType in handlerTypes)
                        {
                            try
                            {
                                // Create instance and register
                                var handler = (IMcpCommandHandler)Activator.CreateInstance(handlerType);
                                this.server.RegisterHandler(handler);
                                count++;
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"Failed to create instance of handler type {handlerType.Name}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Error scanning assembly {assembly.GetName().Name}: {ex.Message}");
                    }
                }

                Debug.Log($"Discovered and registered {count} MCP command handlers");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in handler discovery: {ex.Message}");
            }

            return count;
        }
    }
}
