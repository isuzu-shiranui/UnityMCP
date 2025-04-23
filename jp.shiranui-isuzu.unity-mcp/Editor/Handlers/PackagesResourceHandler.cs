using System.Threading;
using UnityEditor.PackageManager;
using UnityEngine;
using Newtonsoft.Json.Linq;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityMCP.Editor.Resources
{
    /// <summary>
    /// Resource handler for Unity Package Manager information.
    /// </summary>
    internal sealed class PackagesResourceHandler : IMcpResourceHandler
    {
        /// <summary>
        /// Gets the name of the resource this handler is responsible for.
        /// </summary>
        public string ResourceName => "packages";

        /// <summary>
        /// Gets a description of the resource handler.
        /// </summary>
        public string Description => "Provides information about Unity packages installed and available";

        /// <summary>
        /// Gets the URI for this resource.
        /// </summary>
        public string ResourceUri => "unity://packages";

        /// <summary>
        /// Fetches package information with the provided parameters.
        /// </summary>
        /// <param name="parameters">Resource parameters as a JObject.</param>
        /// <returns>A JSON object containing package information.</returns>
        public JObject FetchResource(JObject parameters)
        {
            // Get project packages (installed)
            var projectPackages = this.GetProjectPackages();

            // Check if we should include registry packages
            var includeRegistry = parameters?["includeRegistry"]?.Value<bool>() ?? false;
            JArray registryPackages = null;

            if (includeRegistry)
            {
                registryPackages = this.GetRegistryPackages();
            }

            // Return combined result
            var result = new JObject
            {
                ["success"] = true,
                ["projectPackages"] = projectPackages
            };

            if (registryPackages != null)
            {
                result["registryPackages"] = registryPackages;
            }

            return result;
        }

        /// <summary>
        /// Gets packages installed in the current project.
        /// </summary>
        /// <returns>A JArray containing information about installed packages.</returns>
        private JArray GetProjectPackages()
        {
            var result = new JArray();

            // List installed packages
            var listRequest = Client.List(true);

            // Wait for the request to complete
            while (!listRequest.IsCompleted)
            {
                Thread.Sleep(100);
            }

            if (listRequest.Status == StatusCode.Success)
            {
                foreach (var package in listRequest.Result)
                {
                    result.Add(this.PackageToJObject(package, "installed"));
                }
            }
            else if (listRequest.Status == StatusCode.Failure)
            {
                Debug.LogError($"Failed to list project packages: {listRequest.Error.message}");
            }

            return result;
        }

        /// <summary>
        /// Gets packages available from the Unity Registry.
        /// </summary>
        /// <returns>A JArray containing information about registry packages.</returns>
        private JArray GetRegistryPackages()
        {
            var result = new JArray();

            // Search Unity registry packages
            var searchRequest = Client.SearchAll();

            // Wait for the request to complete
            while (!searchRequest.IsCompleted)
            {
                Thread.Sleep(100);
            }

            if (searchRequest.Status == StatusCode.Success)
            {
                foreach (var package in searchRequest.Result)
                {
                    // Check if package is already installed
                    var state = "available";
                    result.Add(this.PackageToJObject(package, state));
                }
            }
            else if (searchRequest.Status == StatusCode.Failure)
            {
                Debug.LogError($"Failed to search registry packages: {searchRequest.Error.message}");
            }

            return result;
        }

        /// <summary>
        /// Convert a package info object to JObject.
        /// </summary>
        /// <param name="package">Package info.</param>
        /// <param name="state">Installation state.</param>
        /// <returns>JObject with package info.</returns>
        private JObject PackageToJObject(PackageInfo package, string state)
        {
            return new JObject
            {
                ["name"] = package.name,
                ["displayName"] = package.displayName,
                ["version"] = package.version,
                ["description"] = package.description,
                ["category"] = package.category,
                ["source"] = package.source.ToString(),
                ["state"] = state,
                ["author"] = new JObject
                {
                    ["name"] = package.author?.name,
                    ["email"] = package.author?.email,
                    ["url"] = package.author?.url
                }
            };
        }
    }
}
