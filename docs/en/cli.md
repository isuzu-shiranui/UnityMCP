# CLI reference

This page covers every `isuzu-unity-cli` command, how a project is selected, the exit codes, and what the CLI puts on your machine. [Back to the README](../../README.en.md)

The CLI reads the descriptor file the Editor publishes. It therefore needs no port scan and no token handling.

## Commands

```bash
isuzu-unity-cli projects                 # Editors currently running
isuzu-unity-cli health                   # server state, queue depth, running jobs
isuzu-unity-cli tools                    # what this Editor publishes, with argument names
isuzu-unity-cli tools --group <name>     # filter by group (comma-separated for several)
isuzu-unity-cli call <tool> [...]        # invoke a tool
isuzu-unity-cli verify [...]             # recompile, test and summarise in one call
isuzu-unity-cli jobs [id]                # list jobs, or report one by id
isuzu-unity-cli setup [--mcp] [...]      # install the skill and register the MCP endpoint
isuzu-unity-cli doctor [--fix]           # what is installed, where, and what is stale
isuzu-unity-cli upgrade [--version vX]   # update the CLI
isuzu-unity-cli uninstall [--yes]        # list what would be removed, then remove it
isuzu-unity-cli mcp-stdio --project <n>  # stdio bridge for Claude Desktop
```

## call

```bash
isuzu-unity-cli call play_mode_status
isuzu-unity-cli call console_read_logs --type error --limit 20
isuzu-unity-cli call scene_browse_hierarchy --json '{"name":"Player","limit":5}'
isuzu-unity-cli call execute_code --file snippet.cs
isuzu-unity-cli call play_mode_status --project MyGame
isuzu-unity-cli call play_mode_status --raw          # the whole envelope, not just the result
```

Values are typed automatically. `--limit 20` sends a number. `--active_only true` sends a boolean.

Pass C# snippets with `--file`. Passing a snippet through both a shell and a JSON encoder loses the backslashes in its string literals. The result is a compile error inside generated source that the caller never sees. `--file` sends the snippet base64-encoded and avoids both layers.

## Project selection

Inside a project, `--project` is unnecessary. That holds with several Editors open as well. When the working directory sits under exactly one project, the CLI selects that project.

```bash
cd "/work/UnityProjects/MyGame/Assets/Scripts"
isuzu-unity-cli call play_mode_status
# goes to the MyGame Editor, the project this directory belongs to
```

`isuzu-unity-cli projects` marks that project with `containsWorkingDirectory`.

Run from outside every project, the CLI does not guess. It lists the candidates and stops with exit code 3.

`--project` matches an exact project name first. When nothing matches exactly, it falls back to a unique substring match.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | error (for `verify`: compile errors or test failures) |
| 2 | bad arguments. `call` without a tool name returns it. So does a `verify` `--timeout` that is not a positive number, or a `verify` `--logs` that is not a count |
| 3 | no Editor found, or the choice is ambiguous |
| 4 | `verify` exceeded `--timeout` |
| 130 | interrupted with Ctrl+C |

Errors go to stderr. That makes the CLI usable in scripts.

## verify

`verify` collects the steps that follow a script edit into a single call. It requests a recompile and waits for the domain reload to finish. It then collects the errors, runs the tests and summarises the result.

```bash
isuzu-unity-cli verify                       # recompile, collect errors, read console errors
isuzu-unity-cli verify --test                # also run the EditMode suite and list failures
isuzu-unity-cli verify --test --filter Foo   # narrow the tests (also --assembly / --category)
isuzu-unity-cli verify --no-compile --test   # skip the compile, tests only
isuzu-unity-cli verify --raw                 # the summary as JSON
```

The Editor's server goes down during the compile. `verify` expects the connection errors in that window and waits. It re-reads the descriptor before continuing. `--timeout` defaults to 300 seconds.

Console errors are counted. They do not decide the result, because old entries can linger.

## jobs

Work slower than `syncWaitMs` (3 seconds by default) returns a job id instead of a result.

```json
{"state":"running","jobId":"execute_code-3","poll":"/jobs/execute_code-3"}
```

`isuzu-unity-cli jobs` lists jobs. `isuzu-unity-cli jobs <id>` reports one job's state and result.

Do not repeat a call that returned a job id. The work is still running, and repeating the call runs it twice.

## tools --group

`isuzu-unity-cli tools --group <name>[,<name>]` filters the tool list by group. The groups are `diagnostics`, `authoring`, `rendering`, `timeline`, `build`, `code` and `input`.

## setup

```bash
isuzu-unity-cli setup                                            # install the agent skill for Claude Code and Codex
isuzu-unity-cli setup --mcp --agent claude-code --scope project  # also register the MCP endpoint
```

`--mcp` needs a running Editor. The URL and the token come from that Editor's descriptor. The flags are described in [Connecting MCP clients](mcp-clients.md). A leftover v3 skill folder is removed.

## doctor / upgrade / uninstall

```bash
isuzu-unity-cli doctor          # what is installed, where, and what is stale
isuzu-unity-cli doctor --fix    # repairs what it finds, e.g. re-registering clients after a token regeneration
isuzu-unity-cli upgrade         # updates the CLI; --version pins one
isuzu-unity-cli uninstall       # lists what would go
isuzu-unity-cli uninstall --yes # removes it
```

`uninstall` removes only the `isuzu-unity` entry from your MCP client configs. It touches no other server and no other setting.

It refuses to run while an Editor is running, because that Editor would republish its descriptor right after. The refusal names the Editors that are still open and asks you to close them first. The Unity package itself is removed through the Package Manager.

## Where the time goes

Set `UNITY_MCP_TRACE=1` and the CLI prints the elapsed time at each stage on stderr. Each time is measured from the process start.

```
trace runtime-start      14.6 ms
trace main               16.1 ms
trace parsed             16.3 ms
trace resolved           16.7 ms
trace request-built      16.9 ms
trace connected          18.4 ms
trace response           20.2 ms
trace reported           20.4 ms
```

`runtime-start` is the time from the process start the OS recorded until `Main` runs. That is the executable's own start-up.

Everything up to `resolved` is reading the descriptor. `connected` to `response` is the Editor's side. That span grows on an unfocused Editor. See the `loopWaker` entry in [Troubleshooting](troubleshooting.md).

## WSL2 agent, Windows Editor

The Editor binds only to the Windows-side `127.0.0.1`, and it writes its descriptors under the Windows profile. A CLI inside WSL2 therefore sees neither by default.

Point `UNITY_MCP_STATE_DIR=/mnt/c/Users/<you>/AppData/Local/UnityMCP` at the descriptors. Set `UNITY_MCP_HOST` to the Windows host address.

On the Windows side, forward the port with `netsh interface portproxy` or enable WSL2 mirrored networking. Beyond these workarounds, this setup is not supported.

## What this puts on your machine

All state lives under a single root.

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\UnityMCP\instances\` | Descriptors for running Editors, carrying the port, the MCP URL and where to find the token. A descriptor is removed when its Editor quits. On start, descriptors whose process has exited are removed as well |
| `%LOCALAPPDATA%\UnityMCP\tokens\` | The bearer token for each project |
| `%LOCALAPPDATA%\UnityMCP\cache\` | Cached tool catalog |
| `%LOCALAPPDATA%\UnityMCP\tools\` | JSON files for [defined tools](defined-tools.md) |
| `%LOCALAPPDATA%\UnityMCP\recordings\` | Recordings made by the [input tools](input-tools.md) |
| The CLI binary | `dotnet tool install` puts it in the global tool location. The install script puts it in the per-user executable location |
| `~/.claude/skills/isuzu-unity-cli/` | The Claude Code skill, installed by `setup`. It goes under `CLAUDE_CONFIG_DIR` when that is set |
| `~/.codex/skills/isuzu-unity-cli/` | The Codex skill, installed by `setup`. It goes under `CODEX_HOME` when that is set |
| The `isuzu-unity` entry in your MCP client config | Added by `setup --mcp` |

On macOS and Linux the root is under `~/.local/share` or `~/Library/Application Support` instead. `isuzu-unity-cli doctor` prints the real locations.
