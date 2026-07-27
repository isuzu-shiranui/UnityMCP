# Third-Party Notices

UnityMCP is distributed under the MIT License (see [LICENSE](LICENSE)).

## Redistributed components

Everything this project ships inside a package is listed in
**[jp.shiranui-isuzu.unity-mcp/Third Party Notices.md](jp.shiranui-isuzu.unity-mcp/Third%20Party%20Notices.md)**,
which travels with the Unity package. That file is the canonical list; it is not duplicated
here, because two copies of the same list is how one of them goes stale.

In short: Roslyn 3.7.0 and three .NET runtime libraries, all MIT, all
© .NET Foundation and Contributors. They are bundled because `execute_code` compiles C# at
runtime and Unity does not expose a compiler for that.

The npm package `unity-mcp-ts` redistributes nothing.

## Fetched at install time

Neither package includes these; package managers retrieve them, and each keeps its own
licence and notices.

| Fetched by | Package | Licence |
|---|---|---|
| UPM | `com.unity.nuget.newtonsoft-json` 3.2.1 | MIT (Json.NET, © James Newton-King) |
| npm | `@modelcontextprotocol/sdk` | MIT |
| npm | `zod` | MIT |

For the full transitive set, run `npm ls --omit=dev` in `unity-mcp-ts`, or
`npx license-checker --production` for their licences.

## Removed in v3.0.0

`Microsoft.CodeAnalysis.Scripting.dll` and `Microsoft.CodeAnalysis.CSharp.Scripting.dll` were
bundled up to v2 but never referenced — `CodeExecutor` drives `CSharpCompilation` directly and
does not use the scripting API. They were removed rather than attributed for something the
package does not use.
