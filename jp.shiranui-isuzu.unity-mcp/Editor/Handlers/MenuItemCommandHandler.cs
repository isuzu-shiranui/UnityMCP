using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Handlers
{
    /// <summary>
    /// Command handler for executing Unity menu items.
    /// </summary>
    internal sealed class MenuItemCommandHandler : IMcpCommandHandler
    {
        /// <summary>
        /// Gets the command prefix for this handler.
        /// </summary>
        public string CommandPrefix => "menu";

        /// <summary>
        /// Gets the description of this command handler.
        /// </summary>
        public string Description => "Executes Unity Editor menu items (Built-in)";

        /// <summary>
        /// Executes the command with the given parameters.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="parameters">The parameters for the command.</param>
        /// <returns>A JSON object containing the execution result.</returns>
        public JObject Execute(string action, JObject parameters)
        {
            if (action.ToLower() != "execute")
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = $"Unknown action: {action}. Only 'execute' is supported."
                };
            }

            return this.ExecuteMenuItem(parameters);
        }

        /// <summary>
        /// Executes a menu item by name.
        /// </summary>
        /// <param name="parameters">The parameters containing the menu item name.</param>
        /// <returns>A JSON object indicating success or failure.</returns>
        private JObject ExecuteMenuItem(JObject parameters)
        {
            var menuItemPath = parameters["menuItem"]?.ToString();
            if (string.IsNullOrEmpty(menuItemPath))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "MenuItem parameter is required"
                };
            }

            try
            {
                // Execute the menu item
                return new JObject
                {
                    ["success"] = EditorApplication.ExecuteMenuItem(menuItemPath),
                    ["menuItem"] = menuItemPath
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error executing menu item '{menuItemPath}': {ex.Message}");
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        /// <summary>
        /// Checks whether a menu item with the specified path exists in the Unity Editor menu.
        /// </summary>
        /// <param name="menuItemPath">The path of the menu item to check.</param>
        /// <returns><c>true</c> if the menu item exists; otherwise, <c>false</c>.</returns>
        private bool HasMenuItem(string menuItemPath)
        {
            // Check if the menu item exists
            var methodsWithAttribute = TypeCache.GetMethodsWithAttribute<MenuItem>();
            foreach (var method in methodsWithAttribute)
            {
                var attributes = method.GetCustomAttributes(typeof(MenuItem), false);
                foreach (MenuItem attribute in attributes)
                {
                    if (attribute.menuItem == menuItemPath)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
