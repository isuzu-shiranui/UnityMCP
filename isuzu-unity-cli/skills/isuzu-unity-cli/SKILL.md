---
name: isuzu-unity-cli
description: >
  Control Unity Editor from the CLI with the isuzu-unity-cli command. Execute C# code, browse scene
  hierarchy, inspect/modify GameObjects, capture screenshots, read console logs, check compile
  status, control play mode, and execute menu items. Use when: user wants to interact with Unity
  Editor programmatically, run C# code in Unity, debug Unity scenes, capture Unity screenshots,
  check Unity logs, or automate Unity Editor operations via command line.
---

# Unity MCP - CLI Control for Unity Editor

Drive a running Unity Editor with the `isuzu-unity-cli` command. It reads the descriptor file the
Editor publishes, so it needs no port scan, no token handling, and no MCP client running.

```bash
isuzu-unity-cli projects   # which Editors are running
isuzu-unity-cli tools      # what this Editor publishes, with argument names
isuzu-unity-cli health     # server state, queue depth, running jobs
```

## Read the console first

When something does not work or does not show up, read the console errors and warnings before
building any instrumentation of your own (reflection reads, debug counters, synthetic tests).

```bash
isuzu-unity-cli call console_read_logs --type error --limit 30
```

Unity has usually already written down the cause in one line. Order diagnostics by cost:
console, then existing debug displays, then your own instrumentation.

- Check both `--type error` and `--type warning` as soon as a symptom appears. The cause is
  sometimes on the warning side.
- Do not trust an empty console. If entries may have been dropped, read the log file directly
  with `editor_log_tail`. It works while the Editor is busy.
- After a compile, reimport or recompile, confirm `succeeded` with `compile_status`. A failed
  compile leaves the Editor running the previous assembly with `isCompiling` back to false, so
  silence is not success.
- "Fix until the errors are gone" is itself an objective completion criterion.

## Calling tools

```bash
isuzu-unity-cli call <tool>                                # no arguments
isuzu-unity-cli call <tool> --name value --other 3         # individual arguments
isuzu-unity-cli call <tool> --json '{"key":"value"}'       # one JSON object
isuzu-unity-cli call <tool> --project MyGame               # when several Editors are open
isuzu-unity-cli call <tool> --raw                          # whole envelope, not just the result
```

Values are typed automatically. `--limit 20` sends a number and `--active_only true` sends a boolean.
Errors print to stderr and set a non-zero exit code, so the commands can be used in scripts.

Run `isuzu-unity-cli tools` for the authoritative list. It comes from the Editor, so it always
matches the version you are talking to.

## Verify an edit in one call

```bash
isuzu-unity-cli verify                       # recompile, collect errors, read console errors
isuzu-unity-cli verify --test                # also run the EditMode suite and list failures
isuzu-unity-cli verify --test --filter Foo   # narrow the tests (also --assembly / --category)
```

Exit code 0 means the edit compiled and the tests passed; 1 means compile errors or test
failures; 4 means the `--timeout` (300 s by default) was exceeded.

## Execute C# code

Always pass snippets with `--file`.

```bash
cat > /tmp/snippet.cs <<'EOF'
var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
foreach (var l in lights) l.intensity = 2f;
Debug.Log($"adjusted {lights.Length} lights");
return lights.Length;
EOF
isuzu-unity-cli call execute_code --file /tmp/snippet.cs
```

`--file` sends the snippet base64-encoded. Passing C# through a shell and a JSON encoder loses
the backslashes in string literals, and the failure appears as a compile error in generated
source you never see ("Unrecognized escape sequence", "Newline in constant").

Namespaces already imported: `System`, `System.Collections`, `System.Collections.Generic`,
`System.Linq`, `System.Threading.Tasks`, `UnityEngine`, `UnityEditor`. Write statements only,
with no class or method wrapper. `return <expr>;` returns a value. `Debug.Log` output is captured
separately.

A snippet is not undoable. Nothing it changes goes on the undo stack, so authoring belongs in
the dedicated tools.

Return values are serialized structurally, so returning a list gives an array. A snippet that
uses `await` returns no value. The Editor does not block its main thread on an incomplete Task.

Identical snippets are compiled once and reused. Each distinct snippet loads an assembly that
cannot be unloaded, so a long session of one-off snippets grows the domain until the next reload.

## Common tools

| Tool | Purpose |
|---|---|
| `console_read_logs --type error --limit 30` | Console entries; types: `all`, `error`, `warning`, `log` |
| `console_get_count` | Error / warning / log counts, cheap |
| `console_clear` | Clear before an action so later entries are known to come from it |
| `editor_log_tail --pattern "Shader" --lines 50` | `Editor.log` from disk; works while the Editor is wedged |
| `compile_status` | `isCompiling`, `succeeded`, and the error messages |
| `compile_request` | Trigger a recompile (`AssetDatabase.Refresh` alone does not) |
| `test_run --mode edit --assembly MyGame.Tests` | Start a test run; returns immediately |
| `test_results` | Counts and failures; answers while the run holds the main thread |
| `scene_browse_hierarchy --name Player --limit 20` | Hierarchy; also filters `component`, `tag`, `active_only`, `max_depth` |
| `inspect_list --object_path Player --component_type Transform` | Discover property paths |
| `inspect_read --object_path Player --component_type Transform --property_path m_LocalPosition` | Read one property |
| `inspect_write ... --json '{"property_path":"m_LocalScale","value":{"x":2,"y":2,"z":2}}'` | Write one property; a single Undo step |
| `capture_screenshot --view scene --max_size 512` | base64 PNG |
| `play_mode_status` / `play_mode_play` / `play_mode_stop` | Play mode |
| `menu_execute --menu_item "File/Save"` | Invoke a menu item |
| `project_packages` / `project_assemblies` | Project metadata |

### Authoring

One call is a single undo step for the eight `gameobject_` tools, `inspect_write`,
`prefab_create` and `prefab_instantiate`. The rest are not: `asset_delete` goes to the OS trash
instead, `prefab_apply` rewrites the asset, the `scene_` tools act on files, `menu_execute`
depends on the item it invokes, and the `play_mode_` tools are outside Undo entirely. Two tools
ask for `confirm: true` before they run, `prefab_apply` and `editor_dialog_press`, because
neither can be undone.

| Tool | Purpose |
|---|---|
| `gameobject_create --primitive Cube --name Enemy --parent_path /Root` | Create; returns the `path` to address it by |
| `gameobject_delete` / `gameobject_duplicate` / `gameobject_reparent` | Undoable, so no confirmation is asked for |
| `gameobject_set_transform --object_path /Root/Enemy --json '{"position":{"y":2}}'` | Only the axes given are changed |
| `gameobject_add_component --component_type Rigidbody` / `gameobject_remove_component` | Returns the component list |
| `asset_find --type Material --folder Assets/Art --limit 20` | Then `asset_info`, `asset_move`, `asset_delete` (to the OS trash) |
| `asset_create_folder --path Assets/Art/Materials` | Creates parents too; calling it twice is not an error |
| `scene_list` / `scene_open` / `scene_save` / `scene_create` | `scene_open` refuses over unsaved changes |
| `prefab_create` / `prefab_instantiate` / `prefab_apply` | `prefab_apply` needs `confirm: true`; it is not undoable and changes every instance |
| `build_settings` then `build_player --output_path C:/out/Game.exe` | A cold build returns a job id; poll `jobs <id>` |

Two things to know before editing:

- The `path` these tools take is the one `scene_browse_hierarchy` returns. It resolves
  inactive objects, and carries an index only when a sibling name repeats: `/Canvas/Button[1]/Text`.
- A scene edit during Play Mode succeeds and is reverted when Play Mode stops. The response
  carries `playModeWarning` in that case. Asset edits made during Play Mode do survive.

### Rendering and shader debugging

| Tool | Purpose |
|---|---|
| `shader_errors` | Compilation errors. A broken shader renders magenta and never says so. Run this after every shader edit. Without `--path` the sweep covers Assets only, never packages |
| `shader_info` / `material_read` / `material_set` | The values a frame is actually drawn with, not the shader's defaults |
| `render_pipeline_info` | The pipeline actually in force. The quality level overrides graphics settings |
| `render_camera_info` | View, projection and GPU projection matrices, for checking a value against a CPU replica |
| `render_compare --before a.png --after b.png` | Differences as numbers |
| `reflect_read --path "MyPipeline.Manager/ByCamera[0]/levels[2]"` | Live private state without writing a snippet. A getter such as `Renderer.material` instantiates, so read `sharedMaterial` |
| `gpu_readback --path "MyPipeline.Manager/pool" --format uint` | `allZero` answers "did the pass write anything" in one line |

### Timeline and Recorder

These tools are present only when `com.unity.timeline` / `com.unity.recorder` are installed.

| Tool | Purpose |
|---|---|
| `timeline_inspect --object_path /StageDirector --nest_depth 2` | Tracks, clips and bindings. Follows Control tracks into the child timelines they drive |
| `timeline_evaluate --object_path /StageDirector --time 3.5` | Scrub a director to a time or frame without Play Mode |
| `recorder_add_track --object_path /StageDirector --type movie --format mp4 --width 1920 --height 1080` | Add a Recorder track, so playing the director records it |
| `recorder_list --object_path /StageDirector` | What a timeline records, and where it lands |
| `timeline_edit_clip --object_path /StageDirector --track Cameras/Front --clip "Wide" --start 2 --duration 3` | Retime or rename one clip |
| `timeline_shift_clips --object_path /StageDirector --from_time 3 --by 0.5` | Ripple: move everything at or after a time together |
| `timeline_set_track --object_path /StageDirector --track Motion --binding /Cube` | Mute, lock, rename, or bind a track |
| `timeline_delete --object_path /StageDirector --track Shots --clip "Wide"` | Delete a clip, or the whole track |
| `timeline_create --asset_path Assets/Stage/Stage.playable --object_path /Stage` | New timeline, with a director |
| `timeline_create_track --object_path /Stage --type control --name Drive` | Add a track |
| `timeline_create_clip --object_path /Stage --track Drive --control_source /ChildDirector` | Add a clip; nests a child timeline in one call |

Two things to know about the editing tools before trusting a result:

- They report the value that was applied, not the one you asked for. Timeline silently discards
  writes a clip type does not support; an Activation clip accepts a speed multiplier and keeps
  1.0. Anything that was not applied is listed in `ignored` with the reason. Read it.
- Create the timeline before adding tracks to it. `timeline_create_track` refuses on a timeline
  that is not yet an asset, because Timeline would build the track in memory only and drop it at
  the next domain reload. `timeline_create` performs the steps in the right order.

Recording is a track on the timeline, so the frame rate comes from the timeline and is not an
argument here. Sources: `game_view`, `active_camera`, `main_camera`, `tagged_camera`
(`--camera_tag`), `render_texture` (`--render_texture_path`). Omitting `output_path` writes to a
`Recording` folder beside `Assets`, named after the timeline.

### Rendering a timeline, and checking it actually rendered

```bash
isuzu-unity-cli call recorder_add_track --object_path /StageDirector --type movie --format mp4 \
  --source game_view --width 1920 --height 1080
isuzu-unity-cli call play_mode_play
sleep 12                      # the timeline's length, plus encoder flush
isuzu-unity-cli call play_mode_stop
```

Check the content, not the container. Resolution, fps and frame count come from the mp4 header
and say nothing about whether anything moved. A frozen render still reports the full frame count.
Decode the frames and count the distinct ones:

```bash
ffmpeg -v error -i out.mp4 -vf scale=160:90 f_%03d.png   # distinct ≈ frames → moving
```

One failure mode looks like "the tool did nothing": Play Mode defers script compilation. Unity
postpones the domain reload until Play Mode exits, so an edited script keeps running its old
build and `isCompiling` stays true. `play_mode_stop` is itself deferred to the next frame, and a
backgrounded Editor never draws that frame. Check `play_mode_status` first.

## Common workflows

### Debug: find errors, then the object they name

```bash
isuzu-unity-cli call console_read_logs --type error --limit 10
isuzu-unity-cli call scene_browse_hierarchy --name ObjectName
isuzu-unity-cli call inspect_list --object_path ObjectName --component_type Transform
```

### Edit a script and confirm it built

```bash
isuzu-unity-cli call compile_request
sleep 3
isuzu-unity-cli call compile_status          # check succeeded, not just isCompiling
```

`isuzu-unity-cli verify` does the same, waits out the domain reload, and returns an exit code.

### Run the tests

```bash
isuzu-unity-cli call test_run --mode edit --assembly MyGame.Tests
isuzu-unity-cli call test_results            # poll; status goes running -> completed
```

`test_run` does not wait for the outcome, because the run occupies the main thread for its
whole duration. During that window `test_results` is the only tool that answers. Poll it
instead of retrying `test_run`. A `status` of `interrupted` means a domain reload happened
mid-run and the outcome was lost. Start the run again.

### Prove a rendering change did something

```bash
isuzu-unity-cli call capture_screenshot --view game --save_path /tmp/before.png
# toggle the thing under test
isuzu-unity-cli call render_compare --before /tmp/before.png --after /tmp/after.png
```

Compare the images instead of looking at them. Screenshot colours are post-tonemap, so absolute
values are not reliable. Changed-pixel counts and their locations are. Passing `save_path` keeps
both images out of the conversation.

### Reproducing an interaction

Record a human drag, replay it under a fix, then compare:

```bash
isuzu-unity-cli call input_record --action start --view scene_view_window --name look
isuzu-unity-cli call input_record --action stop
isuzu-unity-cli call input_replay --name look --then_capture scene
```

Pass the capture to `render_compare`, or wrap all three calls as one `sequence` defined tool.

### Turning a repeated read into a named tool

A `probe` defined tool turns a reflection path into a one-word call. One JSON file under `%LOCALAPPDATA%\UnityMCP\tools\<projectHash>\`:

```json
{ "name": "camera_probe", "kind": "probe", "description": "Scene View camera position.",
  "reads": [{ "id": "camera", "path": "@sceneview:camera/transform/position" }] }
```

```bash
isuzu-unity-cli call camera_probe
```

### Save a screenshot to a file

```bash
isuzu-unity-cli call capture_screenshot --view scene --max_size 512 \
  | python -c "import sys,json,base64; d=json.load(sys.stdin); open('scene.png','wb').write(base64.b64decode(d['image']))"
```

### The Editor stopped responding

```bash
isuzu-unity-cli health                       # queueDepth climbing with reqCount flat = wedged main thread
isuzu-unity-cli call editor_log_tail --lines 50
isuzu-unity-cli jobs                         # what is queued or running
```

`health`, `jobs` and `editor_log_tail` are answered off the main thread, so they keep working
when nothing else does.

## Jobs

Work slower than about three seconds returns a job id instead of a result:

```json
{"state":"running","jobId":"execute_code-3","poll":"/jobs/execute_code-3"}
```

```bash
isuzu-unity-cli jobs execute_code-3
```

Do not repeat the call. The work is still running, and repeating the call runs it twice.

## Errors

| Message | Meaning |
|---|---|
| `No running Unity Editor found` | No Editor has a project open with the package installed |
| `Several Editors are running` | Pass `--project <name>` |
| `error [invalid_params]` | Argument missing or the value was rejected; the text says which |
| `error [tool_not_found]` | Run `isuzu-unity-cli tools` |
| `error [unauthorized]` | The descriptor is stale; restart the Editor |
