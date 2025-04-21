using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Resources
{
    /// <summary>
    /// Resource handler for assembly information in the Unity project.
    /// </summary>
    internal sealed class AssembliesResourceHandler : IMcpResourceHandler
    {
        /// <summary>
        /// Gets the name of the resource this handler is responsible for.
        /// </summary>
        public string ResourceName => "assemblies";

        /// <summary>
        /// Gets a description of the resource handler.
        /// </summary>
        public string Description => "Provides information about assemblies loaded in the Unity project";

        /// <summary>
        /// Gets the URI for this resource.
        /// </summary>
        public string ResourceUri => "unity://assemblies";

        /// <summary>
        /// Fetches assembly information with the provided parameters.
        /// </summary>
        /// <param name="parameters">Resource parameters as a JObject.</param>
        /// <returns>A JSON object containing assembly information.</returns>
        public JObject FetchResource(JObject parameters)
        {
            try
            {
                // Get filtering options from parameters
                var includeSystemAssemblies = parameters?["includeSystemAssemblies"]?.Value<bool>() ?? false;
                var includeUnityAssemblies = parameters?["includeUnityAssemblies"]?.Value<bool>() ?? true;
                var includeProjectAssemblies = parameters?["includeProjectAssemblies"]?.Value<bool>() ?? true;

                // Get assemblies
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var filteredAssemblies = assemblies.Where(a =>
                    (includeSystemAssemblies || !this.IsSystemAssembly(a)) &&
                    (includeUnityAssemblies || !this.IsUnityAssembly(a)) &&
                    (includeProjectAssemblies || !this.IsProjectAssembly(a))
                );

                // Convert to JSON
                var assembliesArray = new JArray();
                foreach (var assembly in filteredAssemblies)
                {
                    if (assembly.IsDynamic)
                    {
                        continue; // Skip dynamic assemblies
                    }
                    assembliesArray.Add(this.CreateAssemblyObject(assembly));
                }

                return new JObject
                {
                    ["success"] = true,
                    ["assemblies"] = assembliesArray,
                    ["count"] = assembliesArray.Count
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error retrieving assemblies: {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = $"Error retrieving assemblies: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Creates a JSON object with assembly information.
        /// </summary>
        /// <param name="assembly">The assembly to describe.</param>
        /// <returns>A JObject containing formatted assembly data.</returns>
        private JObject CreateAssemblyObject(Assembly assembly)
        {
            var assemblyName = assembly.GetName();
            var result = new JObject
            {
                ["name"] = assemblyName.Name,
                ["fullName"] = assembly.FullName,
                ["version"] = assemblyName.Version?.ToString(),
                // ["location"] = string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location,
                // ["codeBase"] = assemblyName.CodeBase,
                ["assemblyType"] = this.GetAssemblyType(assembly)
            };

            // Add referenced assemblies
            // var referencedAssemblies = new JArray();
            // foreach (var referencedAssembly in assembly.GetReferencedAssemblies())
            // {
            //     referencedAssemblies.Add(new JObject
            //     {
            //         ["name"] = referencedAssembly.Name,
            //         ["version"] = referencedAssembly.Version.ToString()
            //     });
            // }
            // result["referencedAssemblies"] = referencedAssemblies;

            // Add types count
            // try
            // {
            //     result["typesCount"] = assembly.GetTypes().Length;
            // }
            // catch
            // {
            //     // Some assemblies might throw exceptions when getting types
            //     result["typesCount"] = -1;
            // }

            return result;
        }

        /// <summary>
        /// Determines the type of an assembly (System, Unity, or Project).
        /// </summary>
        /// <param name="assembly">The assembly to check.</param>
        /// <returns>A string describing the assembly type.</returns>
        private string GetAssemblyType(Assembly assembly)
        {
            if (this.IsSystemAssembly(assembly))
            {
                return "System";
            }
            else if (this.IsUnityAssembly(assembly))
            {
                return "Unity";
            }
            else
            {
                return "Project";
            }
        }

        /// <summary>
        /// Checks if an assembly is a system assembly.
        /// </summary>
        /// <param name="assembly">The assembly to check.</param>
        /// <returns>True if the assembly is a system assembly, false otherwise.</returns>
        private bool IsSystemAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            return name.StartsWith("System.") ||
                   name == "System" ||
                   name == "mscorlib" ||
                   name.StartsWith("Microsoft.") ||
                   name.StartsWith("Mono.");
        }

        /// <summary>
        /// Checks if an assembly is a Unity assembly.
        /// </summary>
        /// <param name="assembly">The assembly to check.</param>
        /// <returns>True if the assembly is a Unity assembly, false otherwise.</returns>
        private bool IsUnityAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            return name.StartsWith("Unity") ||
                   name.StartsWith("UnityEngine") ||
                   name.StartsWith("UnityEditor");
        }

        /// <summary>
        /// Checks if an assembly is a project assembly.
        /// </summary>
        /// <param name="assembly">The assembly to check.</param>
        /// <returns>True if the assembly is a project assembly, false otherwise.</returns>
        private bool IsProjectAssembly(Assembly assembly)
        {
            return !this.IsSystemAssembly(assembly) && !this.IsUnityAssembly(assembly);
        }
    }
}
