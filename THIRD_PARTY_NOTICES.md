# Third-Party Notices

UnityMCP is distributed under the MIT License (see [LICENSE](LICENSE)).

Two artefacts ship: the Unity package `jp.shiranui-isuzu.unity-mcp` and the command line tool
`isuzu-unity-cli`. They are covered separately below.

## Redistributed inside the Unity package

Everything that package ships is listed in
**[jp.shiranui-isuzu.unity-mcp/Third Party Notices.md](jp.shiranui-isuzu.unity-mcp/Third%20Party%20Notices.md)**,
which travels with the package. That file is the canonical list; it is not duplicated
here, because two copies of the same list is how one of them goes stale.

In short: Roslyn 3.7.0 and three .NET runtime libraries, all MIT, all
© .NET Foundation and Contributors. They are bundled because `execute_code` compiles C# at
runtime and Unity does not expose a compiler for that.

## Redistributed inside the CLI

`isuzu-unity-cli` has one direct dependency, and a NativeAOT single-file binary statically
links what it references, so the release binaries carry it.

| Package | Version | Licence |
|---|---|---|
| `Tomlyn` | 2.10.1 | BSD-2-Clause, © Alexandre Mutel |

Tomlyn parses Codex's `config.toml`, which `setup --mcp`, `doctor` and `uninstall` read and
edit in place. The rest of the binary is the .NET runtime and base class libraries (MIT,
© .NET Foundation and Contributors), linked in by `PublishAot`.

For the transitive set behind that one entry:

```sh
dotnet list isuzu-unity-cli/src/IsuzuUnityCli/IsuzuUnityCli.csproj package --include-transitive
```

## Fetched at install time

Not included in either artefact; the package manager retrieves it, and it keeps its own
licence and notices.

| Fetched by | Package | Licence |
|---|---|---|
| UPM | `com.unity.nuget.newtonsoft-json` 3.2.1 | MIT (Json.NET, © James Newton-King) |

## Removed in v3.0.0

`Microsoft.CodeAnalysis.Scripting.dll` and `Microsoft.CodeAnalysis.CSharp.Scripting.dll` were
bundled up to v2 but never referenced — `CodeExecutor` drives `CSharpCompilation` directly and
does not use the scripting API. They were removed rather than attributed for something the
package does not use.
