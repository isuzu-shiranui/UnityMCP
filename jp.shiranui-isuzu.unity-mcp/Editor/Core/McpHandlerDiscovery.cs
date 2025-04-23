using System;
using System.Linq;
using UnityEngine;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Generic discovery class for finding and registering MCP handlers and resources.
    /// </summary>
    /// <typeparam name="T">The type of handler to discover.</typeparam>
    internal sealed class McpHandlerDiscovery<T> where T : class
    {
        private readonly Action<T> registerAction;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHandlerDiscovery{T}"/> class.
        /// </summary>
        /// <param name="registerAction">The action to perform for registering discovered instances.</param>
        public McpHandlerDiscovery(Action<T> registerAction)
        {
            this.registerAction = registerAction ?? throw new ArgumentNullException(nameof(registerAction));
        }

        /// <summary>
        /// Discovers and registers all implementations of type T in the current domain.
        /// </summary>
        /// <returns>The number of instances registered.</returns>
        public int DiscoverAndRegister()
        {
            var count = 0;

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
                        // Find all non-abstract classes that implement T
                        var handlerTypes = assembly.GetTypes()
                            .Where(t => typeof(T).IsAssignableFrom(t) &&
                                  !t.IsInterface &&
                                  !t.IsAbstract)
                            .ToArray();

                        foreach (var handlerType in handlerTypes)
                        {
                            try
                            {
                                // Create instance and register
                                var instance = (T)Activator.CreateInstance(handlerType);
                                this.registerAction(instance);
                                count++;
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"Failed to create instance of type {handlerType.Name}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Error scanning assembly {assembly.GetName().Name}: {ex.Message}");
                    }
                }

                Debug.Log($"Discovered and registered {count} instances of {typeof(T).Name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in discovery process: {ex.Message}");
            }

            return count;
        }
    }
}
