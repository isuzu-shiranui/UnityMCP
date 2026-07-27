import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';

/**
 * Wrapper the Editor puts around snippets passed to `execute_code`.
 *
 * Kept verbatim from `CodeExecutor.cs`. v2's version of this text named a different namespace
 * and class than the Editor actually uses, which is exactly the kind of drift v3 exists to
 * remove — if this ever changes on the C# side, change it here in the same commit.
 */
const EXECUTION_CONTEXT = `using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace McpCodeExecution
{
    public static class Runner
    {
        public static object Execute()
        {
            {code}
            return null;
        }
    }
}`;

const TEMPLATE = `Write C# that will be compiled and run inside the Unity Editor.

1. Do not write \`using\` statements — the listed namespaces are already imported.
2. Do not declare a class or method — your code is placed directly in a method body.
3. Use \`return <expr>;\` to surface a value. Without it the call returns null.
4. \`Debug.Log\` output is captured and returned alongside the value.
5. Avoid literal newlines inside string literals: the snippet travels as JSON, and an
   unescaped newline becomes a real line break in the generated source, which fails to
   compile with "Newline in constant".

Example:
\`\`\`csharp
var active = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None)
    .Where(go => go.activeInHierarchy)
    .ToList();

Debug.Log($"Found {active.Count} active GameObjects");
return active.Count;
\`\`\`

Your code is compiled into:

${EXECUTION_CONTEXT}`;

/**
 * Registers the single built-in prompt.
 *
 * Registered directly rather than through a discovery scan: v2 walked the handlers directory
 * and duck-typed every export to find one prompt, which cost a whole subsystem to locate a
 * constant.
 */
export function registerCodePrompt(server: McpServer): void {
    server.prompt(
        'code_execute',
        'How to write C# for the execute_code tool',
        async () => ({
            messages: [
                {
                    role: 'user' as const,
                    content: { type: 'text' as const, text: TEMPLATE },
                },
            ],
        })
    );
}
