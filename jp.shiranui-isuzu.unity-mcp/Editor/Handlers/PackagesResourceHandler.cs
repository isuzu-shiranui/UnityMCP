using System.Collections.Generic;
using System.Threading;

using UnityEditor.PackageManager;
using UnityEngine;
using Newtonsoft.Json.Linq;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Resources
{
    /// <summary>
    /// Resource handler for Unity Package Manager information.
    /// Supports limit, offset, and fields for pagination and field filtering.
    /// </summary>
    internal sealed class PackagesResourceHandler
    {
        /// <summary>
        /// Fetches package information with the provided parameters.
        /// </summary>
        public JObject FetchResource(JObject parameters)
        {
            var includeRegistry = parameters?["includeRegistry"]?.Value<bool>() ?? false;
            var limit = parameters?["limit"]?.Value<int>() ?? int.MaxValue;
            var offset = parameters?["offset"]?.Value<int>() ?? 0;
            var fieldsFilter = ListResponseBuilder.ParseFieldsParam(parameters?["fields"]?.ToString());

            var projectPackageList = this.GetProjectPackageList();

            var page = ListResponseBuilder.Build(
                projectPackageList,
                offset,
                limit,
                p => this.PackageToJObject(p, "installed"),
                fieldsFilter);

            var result = new JObject
            {
                ["projectPackages"] = page["items"],
                ["count"] = projectPackageList.Count,
                ["truncated"] = page["truncated"],
                ["next"] = page["next"]
            };

            if (includeRegistry)
            {
                // Paged like the project list. Built outside the builder, the whole registry came
                // back however small a limit the caller asked for.
                var registry = this.GetRegistryPackages();
                var registryPage = ListResponseBuilder.Build(
                    registry,
                    offset,
                    limit,
                    p => this.PackageToJObject(p, "available"),
                    fieldsFilter);

                result["registryPackages"] = registryPage["items"];
                result["registryCount"] = registry.Count;
                result["registryTruncated"] = registryPage["truncated"];
            }

            return result;
        }

        private List<PackageInfo> GetProjectPackageList()
        {
            var result = new List<PackageInfo>();
            var listRequest = Client.List(true);

            while (!listRequest.IsCompleted)
            {
                Thread.Sleep(100);
            }

            if (listRequest.Status == StatusCode.Success)
            {
                foreach (var package in listRequest.Result)
                    result.Add(package);
            }
            else if (listRequest.Status == StatusCode.Failure)
            {
                Debug.LogError($"Failed to list project packages: {listRequest.Error.message}");
            }

            return result;
        }

        private List<PackageInfo> GetRegistryPackages()
        {
            var result = new List<PackageInfo>();
            var searchRequest = Client.SearchAll();

            while (!searchRequest.IsCompleted)
            {
                Thread.Sleep(100);
            }

            if (searchRequest.Status == StatusCode.Success)
            {
                foreach (var package in searchRequest.Result)
                    result.Add(package);
            }
            else if (searchRequest.Status == StatusCode.Failure)
            {
                Debug.LogError($"Failed to search registry packages: {searchRequest.Error.message}");
            }

            return result;
        }

        private JObject PackageToJObject(PackageInfo package, string state)
        {
            var json = new JObject
            {
                ["name"] = package.name,
                ["displayName"] = package.displayName,
                ["version"] = package.version,
                ["description"] = package.description,
                ["category"] = package.category,
                ["source"] = package.source.ToString(),
                ["state"] = state,
            };

            // Registry packages mostly leave the author blank, and three empty strings on every
            // row cost more than the field is worth.
            if (!string.IsNullOrEmpty(package.author?.name)
                || !string.IsNullOrEmpty(package.author?.email)
                || !string.IsNullOrEmpty(package.author?.url))
            {
                json["author"] = new JObject
                {
                    ["name"] = package.author?.name,
                    ["email"] = package.author?.email,
                    ["url"] = package.author?.url
                };
            }

            return json;
        }
    }
}
