# CLI reference

This page covers every `isuzu-unity-cli` command, how a project is selected, the exit codes, and what the CLI puts on your machine. [Back to the README](../../README.en.md)

The CLI reads the descriptor file the Editor publishes. It therefore needs no port scan and no token handling.

## Commands

```bash
isuzu-unity-cli projects                 # Editors currently running
isuzu-unity-cli health                   # server state, queue depth, running jobs
isuzu-unity-cli tools                    # what this Editor publishes, with argument names
isuzu-unity-cli tools --group <name>     # filter by group (comma-separated for several)
isuzu-unity-cli tools <tool>             # one tool's description and arguments
isuzu-unity-cli mcp-stdio --group <name> # narrow what the MCP client is offered
isuzu-unity-cli call <tool> [...]        # invoke a tool
isuzu-unity-cli verify [...]             # recompile, test and summarise in one call
isuzu-unity-cli jobs [id]                # list jobs, or report one by id
isuzu-unity-cli jobs <id> --wait         # poll until it ends, then print its last answer
isuzu-unity-cli setup [--mcp] [...]      # install the skill and register the MCP endpoint
isuzu-unity-cli doctor [--fix]           # what is installed, where, and what is stale
isuzu-unity-cli update [--dry-run]       # bring the CLI and every project's package up together
isuzu-unity-cli upgrade [--release vX]   # update the CLI
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

Values are typed automatically. `--limit 20` sends a number. `--active_only true` sends a boolean. Naming the same option twice or more sends a list (`--paths one --paths two`), which is how an array is typed in a shell that eats quotes.

JSON is indented in a terminal and packed when the output is piped or redirected. `--compact` packs it in a terminal too.

Pass C# snippets with `--file`. Passing a snippet through both a shell and a JSON encoder loses the backslashes in its string literals. The result is a compile error inside generated source that the caller never sees. `--file` sends the snippet base64-encoded and avoids both layers.

## Project selection

Inside a project, `--project` is unnecessary. That holds with several Editors open as well. When the working directory sits under exactly one project, the CLI selects that project.

```bash
cd "/work/UnityProjects/MyGame/Assets/Scripts"
isuzu-unity-cli call play_mode_status
# goes to the MyGame Editor, the project this directory belongs to
```

`isuzu-unity-cli projects` marks that project with `containsWorkingDirectory`.

Run from outside every project, the CLI selects the only running Editor. If several Editors are running, it lists the candidates and stops with exit code 3.

Run from inside a Unity project folder that no running Editor has open, it stops with exit code 3 as well, rather than defaulting to whichever Editor happens to be open. Open that project, or pass `--project` to choose another.

`--project` is matched in this order:

1. The product name (Player Settings > Product Name), exactly, ignoring case.
2. The folder name shown in the Editor's title bar, exactly, ignoring case.
3. A unique substring of either name. `mcp-stdio`, `setup --mcp` and `update --project` skip this step, because they bind a session to the project or modify its files.

Any value that contains a slash (`/` or `\`), or is exactly `.` or `..`, is treated as a path. A relative path is resolved against the working directory, and only the project at that exact path is selected; no name is tried. Paths are compared case-sensitively, except for a Windows drive letter.

After selection, `verify`, `jobs --wait` and `mcp-stdio` reconnect only to the same project path. They accept a new port, token or product name, but never switch to another project. When several descriptors name that path, each one is asked for `/health` with its own token, and the one that answers is used. When not exactly one answers, or no Editor has that path open, the command stops with the reason and how to switch. To use another project, run a new command, or restart the MCP server for `mcp-stdio`.

`mcp-stdio` can start before an Editor is open. It becomes bound when it first selects a project. Descriptors without an absolute project path can be used initially, but cannot be rediscovered safely after a connection failure.

A reconnect is refused even for the same project when its path is written differently:

- The project was reopened through a directory junction, a `subst` drive or an 8.3 short name.
- The project was reopened through a path with different casing, other than the drive letter.
- The project was moved or copied.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | error (for `verify`: compile errors or test failures) |
| 2 | bad arguments. An option the command does not have, an option missing its value, and `call` without a tool name all return it. So does a `verify` `--timeout` that is not a positive number, or a `verify` `--logs` that is not a count |
| 3 | no Editor found, the choice is ambiguous, the selected project cannot be reconnected to, or the Editor kept rejecting the token |
| 4 | `verify` or `jobs --wait` exceeded `--timeout` |
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

When the token is rejected, the descriptor is read again as well. If the token there has not changed, it keeps reading for 15 seconds, then stops with exit code 3. With `--raw`, a summary of the steps that ran is still printed, with `ok: false`.

Console errors are counted. They do not decide the result, because old entries can linger.

## jobs

Work slower than `syncWaitMs` (3 seconds by default) returns a job id instead of a result.

```json
{"state":"running","jobId":"execute_code-3","poll":"/jobs/execute_code-3"}
```

`isuzu-unity-cli jobs` lists jobs. `isuzu-unity-cli jobs <id>` reports one job's state and result.

Do not repeat a call that returned a job id. The work is still running, and repeating the call runs it twice.

`--wait` polls until the job ends and prints only its last answer. Whatever is holding the Editor up while the wait lasts — a compilation, a dialog, a main thread that has not come back — is reported on stderr. Exit codes: 0 the job completed, 1 it failed or was cancelled, 2 an option is wrong, 3 no Editor or an ambiguous one, 4 the wait timed out.

`--timeout <seconds>` (300 by default) gives up waiting. The job itself keeps running in the Editor.

## tools --group

`isuzu-unity-cli tools --group <name>[,<name>]` filters the tool list by group. The groups are `diagnostics`, `authoring`, `rendering`, `timeline`, `build`, `code` and `input`. Name one tool instead and only its description and arguments are printed, which is far smaller than the whole list.

## setup

```bash
isuzu-unity-cli setup                                            # install the agent skill for Claude Code and Codex
isuzu-unity-cli setup --mcp --agent claude-code --scope project  # also register the MCP endpoint
```

`--mcp` needs a running Editor. The URL and the token come from that Editor's descriptor. The flags are described in [Connecting MCP clients](mcp-clients.md). A leftover v3 skill folder is removed.

`--project` is matched only by exact name or by path. When nothing matches, the reason is printed.

## update

The CLI and the Unity package are released under one version, and the release refuses to publish them apart. On a machine they drift anyway: `upgrade` replaces the CLI and the package is updated somewhere else entirely.

```bash
isuzu-unity-cli update --dry-run   # what would change
isuzu-unity-cli update             # the CLI, then every running Editor's package
isuzu-unity-cli update --project X # only that project's package
```

Four of the six ways the package can be installed cannot be updated from here, and each is named with the step that does move it:

| How it is installed | What `update` does |
|---|---|
| A git URL in `Packages/manifest.json` | Rewrites the tag in the dependency |
| A version from a registry | Rewrites the version |
| `file:` — a working copy | Refuses. Pulling it forward is git's job |
| A folder under `Packages/` | Refuses. Unity loads that and ignores the manifest, so editing the manifest would change a line nothing reads |
| VCC or ALCOM | Refuses. Those keep their own record in `vpm-manifest.json`; update it there |

After a rewrite, Unity resolves the manifest when the Editor next has focus. `package_resolve` does it without waiting.

If the CLI was installed with winget or as a dotnet tool, `update` and `upgrade` do not replace it. They print that tool's update command instead, and refuse `--release`. Until the CLI has been updated that way, each project's package is moved only as far as the version the CLI runs, and a project already on a newer version is left unchanged.

Every other command says once, on stderr, when a newer release exists. That line is read from what `doctor` or `update` last found out and never from the network: a whole `call` finishes in about 20 ms, and asking GitHub carries a five-second timeout.

## doctor / upgrade / uninstall

```bash
isuzu-unity-cli doctor          # what is installed, where, and what is stale
isuzu-unity-cli doctor --fix    # repairs what it finds, e.g. a stale skill or an entry whose port moved
isuzu-unity-cli upgrade         # updates the CLI alone; --release pins one
isuzu-unity-cli uninstall       # lists what would go
isuzu-unity-cli uninstall --yes # removes it
```

`doctor --fix` rewrites an entry whose token ties it to a running Editor, and a Claude Code entry filed under that Editor's project path. An entry that points at the same URL with a different token is left alone, since there is no reliable way to tell which project it belongs to, and `setup --mcp` is suggested instead.

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
