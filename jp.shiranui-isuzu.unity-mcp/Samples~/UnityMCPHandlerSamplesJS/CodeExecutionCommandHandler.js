import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";
/**
 * Command handler for executing C# code in the Unity editor.
 */
export class CodeExecutionCommandHandler extends BaseCommandHandler {
    /**
     * Gets the command prefix for this handler.
     */
    get commandPrefix() {
        return "code";
    }
    /**
     * Gets the description of this command handler.
     */
    get description() {
        return "Execute C# code in the Unity editor";
    }
    /**
     * Executes the command with the given parameters.
     * @param action The action to execute.
     * @param parameters The parameters for the command.
     * @returns A Promise that resolves to a JSON object containing the execution result.
     */
    async executeCommand(action, parameters) {
        try {
            // Validate the action
            if (action.toLowerCase() !== "execute") {
                return {
                    success: false,
                    error: `Unknown action: ${action}. Supported actions are 'execute'.`
                };
            }
            // Validate required parameters
            const code = parameters.code;
            if (!code) {
                return {
                    success: false,
                    error: "The 'code' parameter is required and cannot be empty."
                };
            }
            // Forward the request to Unity using the base class helper
            return await this.sendUnityRequest(`${this.commandPrefix}.${action}`, parameters);
        }
        catch (ex) {
            const errorMessage = ex instanceof Error ? ex.message : String(ex);
            console.error(`Error executing code command '${action}': ${errorMessage}`);
            return {
                success: false,
                error: errorMessage
            };
        }
    }
    /**
     * Gets the tool definitions supported by this handler.
     * @returns A map of tool names to their definitions.
     */
    getToolDefinitions() {
        const tools = new Map();
        // Execute code tool
        tools.set("code_execute", {
            description: "Execute C# code in the Unity editor",
            parameterSchema: {
                code: z.string().describe("The C# code to execute")
            }
        });
        return tools;
    }
    /**
     * Gets the prompt definitions supported by this handler.
     * @returns A map of prompt names to their definitions.
     */
    getPromptDefinitions() {
        const prompts = new Map();
        // Add code execution prompt templates
        prompts.set("code_execute", {
            description: "C# code template for Unity code execution",
            template: `Write C# code that follows these format guidelines to ensure proper execution in Unity:

1. Do not include 'using' statements - they are already included
2. Do not wrap your code in a class or method - it will be automatically wrapped
3. Write straight executable code that would be valid inside a method body
4. For returning values, use a 'return' statement at the end of your code
5. You can use Unity and .NET APIs directly (UnityEngine, UnityEditor, System, etc.)
6. Available namespaces include: System, System.Collections, System.Collections.Generic, System.Linq, UnityEngine, UnityEditor

Example valid code:
\`\`\`csharp
var activeObjects = GameObject.FindObjectsOfType<GameObject>()
    .Where(go => go.activeInHierarchy)
    .ToList();
    
Debug.Log($"Found {activeObjects.Count} active GameObjects");
foreach (var obj in activeObjects.Take(5)) {
    Debug.Log($"Object: {obj.name}");
}
return activeObjects.Count;
\`\`\`

This code will be executed in the context of:

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

namespace CodeExecutionContainer
{
    public static class CodeExecutor
    {
        public static object Execute()
        {
            " + code + @"
            return null;
        }
    }
}`
        });
        return prompts;
    }
}
