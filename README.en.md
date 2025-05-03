# Unity MCP Integration Framework

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
![Unity](https://img.shields.io/badge/Unity-2022.3.22--Unity6.1-black.svg)
![.NET](https://img.shields.io/badge/.NET-C%23_9.0-purple.svg)
![GitHub Stars](https://img.shields.io/github/stars/isuzu-shiranui/UnityMCP?style=social)

[日本語版](./README.md)

An extensible framework for integrating Unity with the Model Context Protocol (MCP). This framework enables AI language models like Claude to interact directly with the Unity Editor through a scalable handler architecture.

## 🌟 Features

- **Extensible Plugin Architecture**: Create and register custom handlers to extend functionality
- **Complete MCP Integration**: Supports all MCP core features including tools, resources, and prompts
- **TypeScript & C# Support**: Server component in TypeScript, Unity component in C#
- **Editor Integration**: Functions as an editor tool with customizable settings
- **Auto-discovery**: Automatic detection and registration of various handlers
- **Communication**: TCP/IP-based communication between Unity and external AI services

## 📋 Requirements

- Unity 2022.3.22f1 or later (also compatible with Unity 6.1)
   - Tested with 2022.3.22f1, 2023.2.19f1, 6000.0.35f1, and 6000.1.0f1
- .NET/C# 9.0
- Node.js 18.0.0 or later with npm (for TypeScript server)
   - Install from the [Node.js official site](https://nodejs.org/)

## 🚀 Getting Started

### Installation

1. Install using the Unity Package Manager:
   - Open Package Manager (Window > Package Manager)
   - Click the "+" button
   - Select "Add package from git URL..."
   - Enter: `https://github.com/isuzu-shiranui/UnityMCP.git?path=jp.shiranui-isuzu.unity-mcp`

### Quick Setup

1. Open Unity, navigate to Edit > Preferences > Unity MCP
2. Configure connection settings (host and port)
3. Click "Connect" to start listening for connections

### Integration with Claude Desktop

1. Download the latest ZIP file from the releases page and extract it
2. Note the full path to the `build/index.js` file
3. Open Claude Desktop's configuration file `claude_desktop_config.json`
4. Add the following content and save:

```json
{
   "mcpServers": {
      "unity-mcp": {
         "command": "node",
         "args": [
            "path/to/index.js"
         ]
      }
   }
}
```
Note: Replace `path/to/index.js` with the actual path (for Windows, either escape backslashes "\\\\" or use forward slashes "/")

## 🔌 Architecture

The Unity MCP framework consists of two main components:

### 1. Unity C# Plugin

- **McpServer**: Core server that listens for TCP connections and routes commands
- **IMcpCommandHandler**: Interface for creating custom command handlers
- **IMcpResourceHandler**: Interface for creating data-providing resources
- **McpSettings**: Manages plugin settings
- **McpServiceManager**: Dependency injection system for service management
- **McpHandlerDiscovery**: Automatically discovers and registers various handlers

### 2. TypeScript MCP Client

- **HandlerAdapter**: Adapts various handlers to the MCP SDK
- **HandlerDiscovery**: Discovers and registers handler implementations
- **UnityConnection**: Manages TCP/IP communication with Unity
- **BaseCommandHandler**: Base class for command handler implementations
- **BaseResourceHandler**: Base class for resource handler implementations
- **BasePromptHandler**: Base class for prompt handler implementations

## 📄 MCP Handler Types

Unity MCP supports three types of handlers based on the Model Context Protocol (MCP):

### 1. Command Handlers (Tools)

- **Purpose**: Tools for executing actions (making Unity perform operations)
- **Control**: Model-controlled - AI models can automatically invoke them
- **Implementation**: Implement the IMcpCommandHandler interface

### 2. Resource Handlers (Resources)

- **Purpose**: Resources for accessing data within Unity (information providers)
- **Control**: Application-controlled - Client applications decide their use
- **Implementation**: Implement the IMcpResourceHandler interface

### 3. Prompt Handlers (Prompts)

- **Purpose**: Reusable prompt templates and workflows
- **Control**: User-controlled - Users explicitly select them for use
- **Implementation**: Implement the IPromptHandler interface (TypeScript side only)

## 🔬 Sample Code

The package includes the following samples:

1. **Unity MCP Handler Samples**
   - Sample code with C# implementations
   - Can be imported directly into your project

2. **Unity MCP Handler Samples JavaScript**
   - Sample code with JavaScript implementations
   - Copy the JS files from this sample to the `build/handlers` directory for use

> ⚠️ **Caution**: Sample code includes code execution functionality. Use with caution in production environments.

To import samples:
1. Select this package in Unity Package Manager
2. Click the "Samples" tab
3. Click the "Import" button for the samples you need

## 🛠️ Creating Custom Handlers

### Command Handler (C#)

Create a new class implementing `IMcpCommandHandler`:

```csharp
using Newtonsoft.Json.Linq;
using UnityMCP.Editor.Core;

namespace YourNamespace.Handlers
{
    internal sealed class YourCommandHandler : IMcpCommandHandler
    {
        public string CommandPrefix => "yourprefix";
        public string Description => "Description of your handler";

        public JObject Execute(string action, JObject parameters)
        {
            // Implement command logic
            if (action == "yourAction")
            {
                // Execute something with parameters
                return new JObject
                {
                    ["success"] = true,
                    ["result"] = "Result data"
                };
            }

            return new JObject
            {
                ["success"] = false,
                ["error"] = $"Unknown action: {action}"
            };
        }
    }
}
```

### Resource Handler (C#)

Create a new class implementing `IMcpResourceHandler`:

```csharp
using Newtonsoft.Json.Linq;
using UnityMCP.Editor.Resources;

namespace YourNamespace.Resources
{
    internal sealed class YourResourceHandler : IMcpResourceHandler
    {
        public string ResourceName => "yourresource";
        public string Description => "Description of your resource";
        public string ResourceUri => "unity://yourresource";

        public JObject FetchResource(JObject parameters)
        {
            // Implement resource data retrieval
            var data = new JArray();
            
            // Get and process data to add to JArray
            data.Add(new JObject
            {
                ["name"] = "Item 1",
                ["value"] = "Value 1"
            });

            return new JObject
            {
                ["success"] = true,
                ["items"] = data
            };
        }
    }
}
```

### Command Handler (TypeScript)

Extend `BaseCommandHandler` to create a new handler:

```typescript
import { IMcpToolDefinition } from "../core/interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";

export class YourCommandHandler extends BaseCommandHandler {
   public get commandPrefix(): string {
      return "yourprefix";
   }

   public get description(): string {
      return "Description of your handler";
   }

   public getToolDefinitions(): Map<string, IMcpToolDefinition> {
      const tools = new Map<string, IMcpToolDefinition>();

      // Define tools
      tools.set("yourprefix_yourAction", {
         description: "Description of the action",
         parameterSchema: {
            param1: z.string().describe("Description of parameter"),
            param2: z.number().optional().describe("Optional parameter")
         },
         annotations: {
            title: "Tool title",
            readOnlyHint: true,
            openWorldHint: false
         }
      });

      return tools;
   }

   protected async executeCommand(action: string, parameters: JObject): Promise<JObject> {
      // Implement command logic
      // Forward request to Unity
      return await this.sendUnityRequest(
              `${this.commandPrefix}.${action}`,
              parameters
      );
   }
}
```

### Resource Handler (TypeScript)

Extend `BaseResourceHandler` to create a new resource handler:

```typescript
import { BaseResourceHandler } from "../core/BaseResourceHandler.js";
import { JObject } from "../types/index.js";
import { URL } from "url";

export class YourResourceHandler extends BaseResourceHandler {
   public get resourceName(): string {
      return "yourresource";
   }

   public get description(): string {
      return "Description of your resource";
   }

   public get resourceUriTemplate(): string {
      return "unity://yourresource";
   }

   protected async fetchResourceData(uri: URL, parameters?: JObject): Promise<JObject | string> {
      // Process request parameters
      const param1 = parameters?.param1 as string;

      // Send request to Unity
      const response = await this.sendUnityRequest("yourresource.get", {
         param1: param1
      });

      if (!response.success) {
         throw new Error(response.error as string || "Failed to retrieve resource");
      }

      // Format and return response data
      return {
         items: response.items || []
      };
   }
}
```

### Prompt Handler (TypeScript)

Extend `BasePromptHandler` to create a new prompt handler:

```typescript
import { BasePromptHandler } from "../core/BasePromptHandler.js";
import { IMcpPromptDefinition } from "../core/interfaces/IPromptHandler.js";
import { z } from "zod";

export class YourPromptHandler extends BasePromptHandler {
   public get promptName(): string {
      return "yourprompt";
   }

   public get description(): string {
      return "Description of your prompt";
   }

   public getPromptDefinitions(): Map<string, IMcpPromptDefinition> {
      const prompts = new Map<string, IMcpPromptDefinition>();

      // Register prompt definitions
      prompts.set("analyze-component", {
         description: "Analyze a Unity component",
         template: "Please analyze the following Unity component in detail and suggest improvements:\n\n```csharp\n{code}\n```",
         additionalProperties: {
            code: z.string().describe("C# code to analyze")
         }
      });

      return prompts;
   }
}
```

**Note**: Classes implementing `IMcpCommandHandler` or `IMcpResourceHandler` in C# will be automatically detected and registered anywhere in your project through assembly scanning. Similarly, TypeScript handlers are automatically detected when placed in the `handlers` directory.

## 🔄 Communication Flow

1. Claude (or other AI) calls one of the MCP features (tool/resource/prompt)
2. TypeScript server forwards the request to Unity via TCP
3. Unity's McpServer receives the request and finds the appropriate handler
4. The handler processes the request on Unity's main thread
5. Results are returned to the TypeScript server through the TCP connection
6. TypeScript server formats and returns the results to Claude

## ⚙️ Configuration

### Unity Settings

Access settings via Edit > Preferences > Unity MCP:

- **Host**: IP address to bind the server to (default: 127.0.0.1)
- **Port**: Port to listen on (default: 27182)
- **UDP Discovery**: Enable automatic discovery of TypeScript server
- **Auto-start on Launch**: Automatically start server when Unity launches
- **Auto-restart on Play Mode Change**: Restart server when play mode starts/ends
- **Detailed Logs**: Enable detailed logs for debugging

### TypeScript Settings

Environment variables for the TypeScript server:

- `MCP_HOST`: Unity server host (default: 127.0.0.1)
- `MCP_PORT`: Unity server port (default: 27182)

## 🔍 Troubleshooting

### Common Issues

1. **Connection Errors**
   - Check firewall settings on Unity side
   - Verify port number is correctly configured
   - Check if another process is using the same port

2. **Handlers Not Registering**
   - Verify handler classes implement the correct interface
   - Check if C# handlers have public or internal access level
   - Check logs in Unity for errors during registration process

3. **Resources Not Found**
   - Verify resource name and URI match
   - Check if resource handler is properly enabled

### Checking Logs

- Unity Console: Check log messages from McpServer
- TypeScript Server: Use console output or MCP Inspector to check for communication errors

## 📚 Built-in Handlers

### Unity (C#)

- **MenuItemCommandHandler**: Execute Unity editor menu items
- **ConsoleCommandHandler**: Operate Unity console logs
- **AssembliesResourceHandler**: Retrieve assembly information
- **PackagesResourceHandler**: Retrieve package information

### TypeScript

- **MenuItemCommandHandler**: Execute menu items
- **ConsoleCommandHandler**: Operate console logs
- **AssemblyResourceHandler**: Retrieve assembly information
- **PackageResourceHandler**: Retrieve package information

## 📖 External Resources

- [Model Context Protocol (MCP) Specification](https://modelcontextprotocol.io/introduction)

## ⚠️ Security Notes

1. **Do not run untrusted handlers**: Review third-party handler code for security before use.
2. **Restrict code execution permissions**: Especially consider disabling handlers with `code_execute` command in production environments as they can execute arbitrary code.

## 📄 License

This project is provided under the MIT License - see the license file for details.

---

Shiranui-Isuzu
