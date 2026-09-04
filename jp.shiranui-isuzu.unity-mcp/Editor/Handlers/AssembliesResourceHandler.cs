using System;
using System.Collections.Generic;
using System.Reflection;

using UnityEngine;
using Newtonsoft.Json.Linq;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Resources
{
    /// <summary>
    /// Resource handler for assembly information in the Unity project.
    /// </summary>
    internal sealed class AssembliesResourceHandler
    {
        /// <summary>
        /// Fetches assembly information with the provided parameters.
        /// Supports limit, offset, and fields for pagination and field filtering.
        /// </summary>
        public JObject FetchResource(JObject parameters)
        {
            try
            {
                var includeSystemAssemblies = parameters?["includeSystemAssemblies"]?.Value<bool>() ?? false;
                var includeUnityAssemblies = parameters?["includeUnityAssemblies"]?.Value<bool>() ?? true;
                var includeProjectAssemblies = parameters?["includeProjectAssemblies"]?.Value<bool>() ?? true;
                var limit = parameters?["limit"]?.Value<int>() ?? int.MaxValue;
                var offset = parameters?["offset"]?.Value<int>() ?? 0;
                var fieldsFilter = ListResponseBuilder.ParseFieldsParam(parameters?["fields"]?.ToString());

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                var filtered = new List<Assembly>();
                foreach (var a in assemblies)
                {
                    if (a.IsDynamic) continue;
                    if (!includeSystemAssemblies && this.IsSystemAssembly(a)) continue;
                    if (!includeUnityAssemblies && this.IsUnityAssembly(a)) continue;
                    if (!includeProjectAssemblies && this.IsProjectAssembly(a)) continue;
                    filtered.Add(a);
                }

                var page = ListResponseBuilder.Build(
                    filtered,
                    offset,
                    limit,
                    a => this.CreateAssemblyObject(a),
                    fieldsFilter);

                return new JObject
                {
                    ["assemblies"] = page["items"],
                    ["count"] = filtered.Count,
                    ["truncated"] = page["truncated"],
                    ["next"] = page["next"]
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error retrieving assemblies: {ex.Message}");
                return new JObject
                {
                    ["error"] = $"Error retrieving assemblies: {ex.Message}"
                };
            }
        }

        private JObject CreateAssemblyObject(Assembly assembly)
        {
            var assemblyName = assembly.GetName();
            return new JObject
            {
                ["name"] = assemblyName.Name,
                ["fullName"] = assembly.FullName,
                ["version"] = assemblyName.Version?.ToString(),
                ["assemblyType"] = this.GetAssemblyType(assembly)
            };
        }

        private string GetAssemblyType(Assembly assembly)
        {
            if (this.IsSystemAssembly(assembly)) return "System";
            if (this.IsUnityAssembly(assembly)) return "Unity";
            return "Project";
        }

        private bool IsSystemAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            return name.StartsWith("System.") ||
                   name == "System" ||
                   name == "mscorlib" ||
                   name.StartsWith("Microsoft.") ||
                   name.StartsWith("Mono.");
        }

        private bool IsUnityAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            return name.StartsWith("Unity") ||
                   name.StartsWith("UnityEngine") ||
                   name.StartsWith("UnityEditor");
        }

        private bool IsProjectAssembly(Assembly assembly)
        {
            return !this.IsSystemAssembly(assembly) && !this.IsUnityAssembly(assembly);
        }
    }
}
