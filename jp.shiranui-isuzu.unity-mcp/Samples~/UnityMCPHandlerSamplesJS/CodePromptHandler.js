import { BasePromptHandler } from "../core/BasePromptHandler.js";
/**
 * Prompt handler providing templates for code execution in Unity.
 */
export class CodePromptHandler extends BasePromptHandler {
    /**
     * Gets the prompt handler name.
     */
    get promptName() {
        return "code";
    }
    /**
     * Gets the description of this prompt handler.
     */
    get description() {
        return "Provides templates for C# code execution in Unity";
    }
    /**
     * Gets the prompt definitions supported by this handler.
     * @returns A map of prompt names to their definitions.
     */
    getPromptDefinitions() {
        const prompts = new Map();
        // Add code execution prompt template
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
            {code}
            return null;
        }
    }
}`
        });
        // Add UI component creation template
        prompts.set("create_ui_component", {
            description: "Template for creating Unity UI components",
            template: `Create a Unity UI component that follows these guidelines:

1. Use Unity's UI system (UnityEngine.UI)
2. Components should be modular and reusable
3. Consider performance implications
4. Add comments to explain complex logic
5. Follow Unity's recommended practices

Example structure:
\`\`\`csharp
// Set up references
RectTransform rectTransform = GetComponent<RectTransform>();
Image backgroundImage = GetComponent<Image>();

// Configure the component
backgroundImage.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
rectTransform.sizeDelta = new Vector2(200, 100);

// Add event listeners
Button button = GetComponent<Button>();
button.onClick.AddListener(() => {
    Debug.Log("Button clicked!");
});
\`\`\`

For more complex UI components, consider using a proper MonoBehaviour class structure.`
        });
        return prompts;
    }
    /**
     * Initialize the handler.
     * @param unityConnection The Unity connection.
     */
    initialize(unityConnection) {
        super.initialize(unityConnection);
    }
}
