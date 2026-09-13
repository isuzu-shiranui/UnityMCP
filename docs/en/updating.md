# Updating

What to run to move from the version you have to a newer one. [Back to the README](../../README.en.md)

The CLI and the Unity package are released under one version, and the release refuses to publish them apart, so move both. `isuzu-unity-cli doctor` says what is installed.

## From 4.3.0 or later

```sh
isuzu-unity-cli update
```

It installs the CLI first and then brings every running Editor's package to the same version. A CLI that cannot be installed leaves the manifests untouched.

If the CLI was installed with winget or as a dotnet tool, `update` does not replace it. It prints that tool's update command instead; update it that way, then run `update` again.

## From 4.2.0

4.2.0's CLI has no `update`; that arrived in 4.3.0. Replace the CLI first.

```sh
isuzu-unity-cli upgrade      # the CLI
isuzu-unity-cli doctor --fix # the agent skill
isuzu-unity-cli update       # every project's package
```

`doctor --fix` is there because 4.2.0's `upgrade` checks the skill from the executable it just replaced, which finds its own copy current. From 4.3.2 onward `upgrade` runs the check through the newly installed executable, so this step is not needed.

4.3.0 changed what an MCP client sees:

- A reply larger than its tool's limit is refused with `isError` rather than sent.
- Replies no longer carry `structuredContent`. The text content holds the same JSON.
- `capture_screenshot`, `reflect_read` and `gpu_readback` are no longer marked read-only, because they are not: `reflect_read` runs getters, and reading `Renderer.material` replaces the shared material with a copy; `capture_screenshot` raises a window over whatever the person at the Editor was looking at; `gpu_readback` holds the main thread until the transfer finishes. While they claimed to be read-only, clients ran them without asking and retried them on their own.
- An argument a tool does not declare is refused instead of ignored.
- `asset_export_package` names its destination `destination` rather than `file`.
- `console_read_logs` leaves the stack trace out unless `stack_trace` asks for it.
- The CLI refuses an option it does not have, and an option missing its value, with exit code 2. An option given twice is sent as a list.

## From 4.1.x or earlier

The steps are the ones for 4.2.0. On top of the above, 4.2.0 and 4.1.0 changed:

- `project_assemblies` no longer returns `fullName`.
- A `scene_browse_hierarchy` node no longer carries `id`, and leaves out any key sitting at its default.
- The CLI does not indent JSON when its output is piped or redirected.

## From v3

The CLI's name and the way it is installed both changed. See [Migrating from v3](migration-v3.md).

## The package on its own

To move the Unity package without the CLI, the step depends on how it was installed.

| How it is installed | What to do |
|---|---|
| A git URL | In the Package Manager, add the URL again with `#v<version>` |
| VCC or ALCOM | Pick the new version from the listing |
| A folder under `Packages/` | Replace its contents with the release zip. Unity loads that and ignores the manifest |
| `file:` — a working copy | Pull it forward with git |

## Checking afterwards

```sh
isuzu-unity-cli doctor
```

It reports both versions, how each was installed, and whether they are out of step — and which one to move if they are.
