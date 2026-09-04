# scripts

## `run-editmode-tests.ps1`

Runs the package's EditMode tests in a real Unity Editor and records the result.

```powershell
pwsh scripts/run-editmode-tests.ps1
```

It creates a scratch project under `%TEMP%` on first use — a manifest and a junction back to
this repository, nothing else — reuses it afterwards, and writes `editmode-attestation.json`
naming the sources it ran against.

**Commit that file with the change it covers.** CI and the release both refuse Editor sources
that no recorded run covers.

### Which Unity it runs

A 6000.0.x Editor by default, not "whichever Editor is newest here": a gate whose meaning depends
on what happens to be installed on one machine is not a gate. `-Unity` runs the same tests on
another Editor, with Timeline, Recorder and the test framework taken from that Editor's own
bundled package set. The version actually used is recorded in the attestation.

Options: `-Unity <path>` to pick an Editor, `-ProjectPath <dir>` to put the scratch project
elsewhere, `-KeepProject` to keep it after a first run.

## `build-mcpb.sh`

Assembles `isuzu-unity-cli.mcpb`, the Claude Desktop Extension bundle attached to each release.

```sh
scripts/build-mcpb.sh <version> <directory holding the binaries> [output directory]
```

The version replaces the `0.0.0` placeholder in `isuzu-unity-cli/mcpb/manifest.json`, so
`IsuzuUnityCli.csproj` stays the only place a version number is written. The second argument is
the directory holding `isuzu-unity-cli-win-x64.exe`, `-osx-arm64`, `-osx-x64` and
`-linux-x64`; all four go into the bundle, alongside the checked-in `isuzu-unity-cli-osx`
shim that picks an architecture from `uname -m`. A missing or empty binary fails the run. The
output directory defaults to `dist/out`.

Needs `node`, and fetches the mcpb command line tool with `npx` on each run, pinned to
`@anthropic-ai/mcpb@2.1.2` so that a new release of that tool cannot break a release of this
one. It validates the manifest, packs the bundle and signs it with a self-signed certificate.

The release runs this in the `release` job before `Create tag`, so a failure here aborts the
run with nothing tagged and nothing published. The checksums are generated after it, so
`SHA256SUMS` covers the bundle as well as the binaries.

## `source-hash.cs`

Hashes every `.cs` and `.asmdef` under the Unity package.

```sh
dotnet run scripts/source-hash.cs
```

A .NET 10 file-based app, so there is no project to restore; `dotnet run` compiles it on the
spot. The script above, CI and the release all call this one implementation: two answers to
"which sources are these" would disagree the first time either changed, and the check would then
either block a good release or wave a bad one through.

`dotnet run` sets the working directory to the folder holding the `.cs` file, not the one the
command was typed in, so a relative repository root passed as an argument is resolved against
`scripts/`. Pass an absolute path, or pass nothing and let it find the repository from its own
location.

Line endings are stripped before hashing, because the repository stores LF, a Windows checkout
has CRLF, and a Linux runner has LF. Paths are sorted as forward-slash relative paths rather
than native ones, because `/` sorts below letters and `\` sorts above them, which would put
`Core/X.cs` and `CoreThing.cs` in opposite orders on Windows and Linux.

## Why an attestation rather than CI

No runner in this repository has a Unity licence, so nothing automated compiles a line of the
Editor's C#. Adding one is possible — `game-ci` plus a `UNITY_LICENSE` secret — but for a
project with one maintainer it buys less than it costs: there are no pull requests from
strangers to guard.

What it does not fix is release time. The release builds and tests the .NET command line tool
and then publishes, so a regression in the Editor assemblies could reach a release without a
single test having run. This closes that specific hole for the price of one command, and leaves
the day-to-day loop where it already was: run the tests on the machine that has Unity on it.

The attestation names the *sources*, not the commit, so a documentation change releases without
re-running anything — while any edit to a `.cs` or `.asmdef` file requires a fresh run. CI checks
it too, which makes a stale attestation a failed pull request rather than a failed publish.

## Publishing to NuGet

The release does not hold a NuGet API key. It asks nuget.org to trade this workflow's OpenID
Connect token for a key that lasts an hour, which means there is no long-lived secret to leak or
rotate. Two things have to exist for that trade to succeed.

A trusted publishing policy on nuget.org, under the account name, at
<https://www.nuget.org/account/trustedpublishing>:

| Field | Value |
|---|---|
| Repository owner | `isuzu-shiranui` |
| Repository | `UnityMCP` |
| Workflow file | `auto-build-release.yml` (the file name only, without the directory) |
| Environment | leave empty |
| Scope | one that allows publishing a package that does not exist yet, for the first release |

A repository secret `NUGET_USER` holding the nuget.org profile name that owns that policy, not an
email address. The release checks for it before it tags anything and prints these instructions
when it is missing.

A policy on a private repository stays provisional for seven days and lapses if nothing is
published in that window; the window can be restarted from the same page. This repository is
public, so the policy activates on the first successful publish.

## Once 4.0.0 is on the registries

One manual step, not automated because it happens exactly once. Nothing installs the npm package
any more, so point the people who already have it at what replaced it:

```sh
npm deprecate @shiranui_isuzu/unity-mcp "v4 moved to isuzu-unity-cli: https://github.com/isuzu-shiranui/UnityMCP#installation"
```

## `bench-cli-vs-mcp.ps1`

Compares the two ways of driving a running Editor — the CLI, one process per call, and the
`/mcp` Streamable HTTP endpoint, one persistent connection — against the same four calls, plus a
direct-REST control that isolates transport cost from process-start cost.

```powershell
powershell -File scripts\bench-cli-vs-mcp.ps1 -Project MyProject
powershell -File scripts\bench-cli-vs-mcp.ps1 -DryRun
```

Requires Windows PowerShell 5.1 and a running Editor with the package installed (discovered the
same way the CLI discovers it, from the descriptor files under
`%LOCALAPPDATA%\UnityMCP\instances`).

### What it measures

Four calls, each run through all three paths:

- `play_mode_status` with no arguments — the cheapest call, dominated by transport and process
  start.
- `scene_browse_hierarchy` with `{"limit":5,"max_depth":1}` — a medium read.
- `console_read_logs` with `{"type":"error","limit":20}`.
- the tool catalog (`GET /tools` / `tools --raw` / `tools/list`) — kept separate because its
  Editor-side allocation was optimised and is worth watching on its own.

Before any timing, it runs an equivalence pass: for each of the first three calls, the REST
`result`, the MCP `structuredContent`, and the parsed CLI stdout must match once the `truncated`
and `next` pagination keys are stripped from both sides (MCP's `structuredContent` carries them
where the REST/CLI envelope hoists them out, so leaving them in would report a mismatch that
is not one). For the catalog, only the REST and CLI catalogs are compared byte-for-byte and only
the tool *names* are compared against MCP's `tools/list` — its JSON Schema shape legitimately
differs from the REST/CLI catalog's. Any mismatch, or an unreachable Editor, ends the run with a
non-zero exit code before a single timed request is sent.

Around each path's timed loop it snapshots `GC.CollectionCount(0)` and `GC.GetTotalMemory`
through `execute_code`, collecting once before the loop so the heap starts from a floor, and
reports the heap growth per 100 requests as the allocation proxy. The Editor's Mono reports the
same collection count for every generation and a hundred requests rarely trigger a collection, so
the count alone says nothing; when one did run inside the loop the growth is marked as a floor.
`GC.GetTotalAllocatedBytes` is looked up by reflection, because a direct reference fails to
compile on Editor versions whose Mono lacks it, and is reported as `null` when absent.

The CLI path includes a fresh process and a fresh TCP connection per call, so its Editor-side
heap growth is higher than MCP's by the listener's per-connection buffers; that difference is
the cost of one process per call, not of the tools.

### Parameters

- `-Project <name>` — which running Editor to use, matched the same way the CLI matches it: an
  exact name, then a unique substring. Omit it when exactly one Editor is running.
- `-Cli <path>` — path to `isuzu-unity-cli.exe`. Defaults to the one on `PATH`, then
  `%LOCALAPPDATA%\Programs\isuzu-unity-cli\isuzu-unity-cli.exe`.
- `-Iterations <n>` (default 30) and `-Warmup <n>` (default 3) — timed and untimed repeats per
  step, per path.
- `-OutJson <path>` (default `%TEMP%\bench-cli-vs-mcp.json`) — where the raw samples and the
  summary are written.
- `-DryRun` — resolves the Editor descriptor and the CLI path, prints the plan with the token
  masked, and exits without sending a single request. Works with no Editor running.

### Output

A console table of mean/p50/p95/min per path per step, the CLI's process-start baseline
(`isuzu-unity-cli --version`, no Editor round trip), and the Gen0-per-100-requests figure per
path. The same numbers, plus every raw sample in milliseconds, go to `-OutJson`.
