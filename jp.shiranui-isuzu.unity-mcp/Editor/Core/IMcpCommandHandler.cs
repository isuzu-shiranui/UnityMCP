using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Defines the contract for command handlers that process MCP requests.
    /// </summary>
    public interface IMcpCommandHandler
    {
        /// <summary>
        /// Gets the name of the command prefix this handler is responsible for.
        /// </summary>
        string CommandPrefix { get; }

        /// <summary>
        /// Gets a description of the command handler.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Executes the command with the given parameters.
        /// </summary>
        /// <param name="action">The action to execute within this command prefix.</param>
        /// <param name="parameters">The parameters for the command.</param>
        /// <returns>A JSON object containing the command result.</returns>
        /// <exception cref="System.ArgumentException">Thrown when action or parameters are invalid.</exception>
        JObject Execute(string action, JObject parameters);
    }
}
