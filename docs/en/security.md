# Security

This page covers the server's access control, where the credentials live, and the guarantee that nothing ships in a player build. [Back to the README](../../README.en.md)

## Access control

- The server binds `127.0.0.1` only and requires a bearer token on every request except the CORS preflight `OPTIONS`. Binding to loopback is not access control. `OPTIONS` is answered 204 with no body before the token is checked, and it carries no header granting anything, so the browser never sends the real request.
- No CORS headers are sent. A web page open in the user's browser therefore cannot POST to the server and run C# in the Editor.
- Only the MCP endpoint `/mcp` checks the `Origin` header, and a request with a foreign `Origin` answers 403 there. The REST routes such as `/health` and `/tools` do not look at `Origin`; they rely on the bearer token alone.
- `execute_code` and `menu_execute` run with full Editor privileges. Do not feed them untrusted code.
- The defined-tools directories are a code-execution surface as well. The next section covers them.

### The defined-tools directories

The shared directory is `<root>/UnityMCP/tools/shared/`. On Windows that is `%LOCALAPPDATA%\UnityMCP\tools\shared\`.

A JSON file placed there is loaded automatically, whatever its file name. The tools it defines are available in every project.

A `script` definition may point `file` at any absolute `.cs` path. Any process that can write to that directory can therefore run code in the Editor. Treat it at the same trust level as the token file.

The project directory `<root>/UnityMCP/tools/<projectHash>/` has the same property. Only its reach is different, being limited to one project.

## Credentials

Treat the descriptor file and the token file as credentials. Anything that can read them can run code in the Editor.

The token lives under `%LOCALAPPDATA%\UnityMCP\tokens\`. On macOS and Linux it is `~/.local/share/UnityMCP/tokens/`, written with owner-only permissions.

Preferences has a "Regenerate" action for the token. Re-register clients afterward with `isuzu-unity-cli doctor --fix`.

`setup --mcp --scope project` writes `.mcp.json` with `${UNITY_MCP_TOKEN}`, never a raw token, so a configuration file committed to a repository never contains the token.

## It cannot ship in a build

This package runs an HTTP server that compiles and executes arbitrary C#. In the Editor that is its purpose. In a shipped game it would be a remote code execution hole. Two independent guarantees keep it out of builds:

1. The assembly definition is `"includePlatforms": ["Editor"]`, so it is never compiled into a player build.
2. Every source file and binary lives under an `Editor/` folder, which Unity excludes from builds regardless of importer settings.

**Nothing reaches a player, Development Build included.** There is no runtime assembly at all. If runtime functionality is ever added, gate it explicitly on `DEVELOPMENT_BUILD`.

CI asserts both guarantees on every change. Each check fails the build when violated: the first when a script is added under `Runtime/`, the second when `includePlatforms` is emptied.

## A panel capture takes what is on the screen

With `inspector`, `hierarchy`, `project`, `console`, `game_view_window`, `scene_view_window` or `window:<title>`, `capture_screenshot` reads that rectangle of the screen rather than something Unity drew. Every view whose name ends in `_window` works this way. Another application sitting over the Editor ends up in the image, and in whatever the image is sent to. When what is in front belongs to another process, the call is refused with `window_occluded`. A window of the same Editor covering the target is not detected, which a floating Package Manager or Preferences window is. What lands in the image is then Unity's own screen, but not the part that was asked for.

Use `game` or `scene`, which Unity renders, or clear the windows in front before capturing.
