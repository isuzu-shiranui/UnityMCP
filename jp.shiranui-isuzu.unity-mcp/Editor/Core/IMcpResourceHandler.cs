// IMcpResourceHandler.cs
using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Resources
{
    /// <summary>
    /// Defines the contract for resource handlers that provide data from the Unity Editor.
    /// </summary>
    public interface IMcpResourceHandler
    {
        /// <summary>
        /// Gets the name of the resource this handler is responsible for.
        /// </summary>
        string ResourceName { get; }

        /// <summary>
        /// Gets a description of the resource handler.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the URI for this resource.
        /// </summary>
        string ResourceUri { get; }

        /// <summary>
        /// Fetches the resource data with the provided parameters.
        /// </summary>
        /// <param name="parameters">Resource parameters as a JObject.</param>
        /// <returns>A JSON object containing the resource data.</returns>
        JObject FetchResource(JObject parameters);
    }
}
