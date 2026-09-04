# Troubleshooting

This page lists symptoms, where to look, and what to do. [Back to the README](../../README.en.md)

## The Package Manager stops with `No 'git' executable was found`

Unity's Package Manager runs a Git client to fetch a package from a git URL. It stops with this error when no Git client is on `PATH`. Install one from [git-scm.com](https://git-scm.com/downloads) and restart Unity.

There is also a route that installs the package without Git. The VRChat Creator Companion (VCC) and ALCOM download a package as a zip, so neither one runs Git.

```
https://unity-mcp.shiranui-isuzu.dev/vpm.json
```

Paste that URL into Add Repository. In VCC it is on the Packages tab of the Settings page. In ALCOM it is on the Repositories page under Resources. The one-click add link is on the [getting started guide](https://unity-mcp.shiranui-isuzu.dev/en/#vpm-title).

Once the repository is added, Unity MCP appears in the project's package list. The repository carries every published version, so an older one can be installed from the same list.

## `isuzu-unity-cli projects` finds nothing

Check that an Editor has a project open and that its server started.

Descriptors live under `%LOCALAPPDATA%\UnityMCP\instances\` on Windows. On macOS and Linux they are under `~/.local/share` or `~/Library/Application Support`.

## 401 responses

The token lives in a file under `%LOCALAPPDATA%\UnityMCP\tokens\`. The CLI reads it for you. A curl call needs the `Authorization: Bearer <token>` header of its own.

After regenerating a token, run `isuzu-unity-cli doctor --fix` to re-register the MCP clients.

## The port looks different than expected

The port is derived from the project path, so it is normally stable across restarts. If that port is already taken, the Editor scans for a free one instead.

It then reports the mismatch as `portMismatch` in `/health` and in the descriptor, and as a warning in Preferences. Re-register any client with the actual port.

## A tool is missing

Adding or removing a Unity package changes the tool list, but the server sends no `tools/list_changed`. Reconnect the client.

## The console reports nothing but you expect output

`console_read_logs` returns what the Editor console currently holds. If you suspect an entry was dropped, read the log file directly with `editor_log_tail`. It works even while the Editor is busy.

## A script edit does not take effect

`AssetDatabase.Refresh()` does not reliably trigger a compile. Use `compile_request`, then check `succeeded` with `compile_status`.

After a failed compile, the Editor keeps running the previous assembly and sets `isCompiling` back to false. The absence of an error does not mean success. `isuzu-unity-cli verify` performs this whole check in one call.

## A call returned a job id

Work slower than `syncWaitMs` (3 s by default) becomes a job. Collect the result with `isuzu-unity-cli jobs <id>` or the `job_status` tool. Do not repeat the call. The work is still running.

## A defined tool does not appear in the list

Check `definitions_list` for its `tools` and `errors`. It reports a missing or wrong `kind`, a wrong required field, and a name that collides with an attribute-based tool.

Only two directories are read: the project directory and the shared directory. A file placed anywhere else is not read at all and appears in neither `tools` nor `errors`. `/health` reports the full path of both directories as `definitionsDir` and `sharedDefinitionsDir`.

## Replay does not move the camera

The target must be a visible Scene View. The tool focuses the window itself before it sends anything, so you do not have to focus it first. It answers `window_not_active` only when the focus does not take, and a tab hidden behind another one in the same dock area is the usual reason.

Coordinates are points, not pixels. Divide screenshot-derived positions by the `pixelsPerPoint` the result reports.

## Claude Desktop cannot connect

It cannot reach a local HTTP endpoint directly. Use the `mcp-stdio --project <name>` bridge instead. The configuration is in [Connecting MCP clients](mcp-clients.md).

## The Editor runs on Windows and the agent inside WSL2

The Editor binds only to the Windows-side `127.0.0.1` and writes its descriptors under the Windows profile. The `UNITY_MCP_STATE_DIR` and `UNITY_MCP_HOST` setup is described in the [CLI reference](cli.md).

## `test_run` refuses with `scene_dirty`, or started but never reports

Before an EditMode run the Test Runner closes the open scenes. With unsaved changes the Editor asks with a save dialog. Until someone closes that dialog the main thread stops, and with it every tool that needs the main thread.

Save with `scene_save`, or discard the changes, before the run. For a run that stopped at the dialog, press Cancel through `editor_dialog_press` as described in the next entry, or press Cancel in the Editor. Start the next run with `force: true`.

## A call stays running

While the Editor shows a modal dialog, the main thread sits inside that dialog's message loop. A save prompt, a package import prompt, an Asset Store dialog and `EditorUtility.DisplayDialog` all do this.

The HTTP server keeps answering from a worker thread, so calls are still accepted. Anything that needs the main thread becomes a job and stays `running` until someone answers the dialog. The work is not stuck. It is waiting.

Three places tell you. `/health` reports `mainThread` with `stalledMs`, which is how long the main thread has not run while work was waiting for it. The open dialog's title, message and buttons appear in the same place, as `dialog`.

Every answer that returns a job, and every `job_status` result while it runs, appends a sentence to `message` naming the dialog's title, message and buttons when one is detected. When no dialog is visible but the main thread has not run for more than 5 seconds, the sentence says so instead. `isuzu-unity-cli call` and `verify` print that sentence on stderr.

To recover, read the dialog with `editor_dialog_list` first. Then press a button with `editor_dialog_press`, passing the button's visible text and `confirm: true`. Buttons like `Don't Save` or `Discard` throw away unsaved work. When unsure press `Cancel`, fix the cause such as an unsaved scene, and repeat the original call.

Once a button is pressed, the waiting job completes and `job_status` returns its result. Dialog detection works on Windows only. Elsewhere `editor_dialog_list` returns `supported: false`, and the dialog has to be answered in the Editor.

## Calls take about 100 ms when the Editor is not focused

An unfocused Editor runs its main loop about every 100 ms. The server wakes it while a request is waiting, so calls normally complete in a few milliseconds.

If `/health` reports `loopWaker` as `unavailable`, this Unity version has no internal wake-up and the wait remains. Multi-frame input tools slow down in the same state.

The default `input_pointer` drag is 33 frames, at `steps` 30 with `frames_per_step` 1. That alone takes about 3 s, exceeds `syncWaitMs`, and therefore returns a job id. Focus the Editor, or collect the result with `job_status`.

## A capture is refused with `window_occluded`

Capture that reads the screen is refused while another application is in front of the Editor. Taken anyway, the picture would be that application's window rather than the Editor, and it would go on to whatever the image is sent to.

Bring the Editor to the front and capture again, or use `game` and `scene`. Bringing it forward is something a person has to do. Windows ignores a foreground change requested by any process other than the one that currently holds the foreground, so an agent cannot do it for you. Unity renders those two through the camera, so nothing in front of the Editor can reach them. The views that read the screen are `inspector`, `hierarchy`, `project`, `console` and every view whose name ends in `_window`.

## Too many tools

Append `?group=diagnostics,authoring` to the MCP URL and `tools/list` returns only those groups. There are seven groups: `diagnostics`, `authoring`, `rendering`, `timeline`, `build`, `code` and `input`.

The CLI applies the same filter with `isuzu-unity-cli tools --group <name>`. Calls themselves are never filtered.
