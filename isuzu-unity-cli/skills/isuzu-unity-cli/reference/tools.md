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
| `scene_browse_hierarchy --missing_scripts true` | Only objects carrying a component Unity cannot resolve, which is what a removed package leaves behind |
| `inspect_list --object_path Player --component_type Transform` | Discover property paths |
| `inspect_read --object_path Player --component_type Transform --property_path m_LocalPosition` | Read one property |
| `inspect_write ... --json '{"property_path":"m_LocalScale","value":{"x":2,"y":2,"z":2}}'` | Write one property; a single Undo step |
| `capture_screenshot --view scene --max_size 512` | base64 PNG |
| `play_mode_status` / `play_mode_play` / `play_mode_stop` | Play mode |
| `menu_execute --menu_item "File/Save"` | Invoke a menu item |
| `project_packages` / `project_assemblies` | Project metadata |
| `definitions_list` | What JSON-defined tools loaded, and why one did not |

### Authoring

One call is a single undo step for the eight `gameobject_` tools, `inspect_write`,
`prefab_create` and `prefab_instantiate`. The rest are not: `asset_delete` goes to the OS trash
instead, `prefab_apply` rewrites the asset, the `scene_` tools act on files, `menu_execute`
depends on the item it invokes, and the `play_mode_` tools are outside Undo entirely. Two tools
ask for `confirm: true` before they run, `prefab_apply` and `editor_dialog_press`, because
neither can be undone.

For the edits Undo does not reach, `asset_export_package` writes the assets you are about to
change to a `.unitypackage` first. Each one goes in with its `.meta`, so importing the file back
restores the GUIDs and the references that pointed at them survive. Reach for it before a bulk
material conversion, a sprite re-slice, or any import-setting rewrite on a project whose assets
are not committed.

| Tool | Purpose |
|---|---|
| `gameobject_create --primitive Cube --name Enemy --parent_path /Root` | Create; returns the `path` to address it by |
| `gameobject_delete` / `gameobject_duplicate` / `gameobject_reparent` | Undoable, so no confirmation is asked for |
| `gameobject_set_transform --object_path /Root/Enemy --json '{"position":{"y":2}}'` | Only the axes given are changed |
| `gameobject_add_component --component_type Rigidbody` / `gameobject_remove_component` | Returns the component list |
| `asset_find --type Material --folder Assets/Art --limit 20` | Then `asset_info`, `asset_move`, `asset_delete` (to the OS trash) |
| `asset_create_folder --path Assets/Art/Materials` | Creates parents too; calling it twice is not an error |
| `asset_export_package --paths Assets/Art --file C:/tmp/art.unitypackage` | A rollback point that keeps the GUIDs, for edits Undo does not cover |
| `scene_list` / `scene_open` / `scene_save` / `scene_create` | `scene_open` refuses over unsaved changes |
| `prefab_create` / `prefab_instantiate` / `prefab_apply` | `prefab_apply` needs `confirm: true`; it is not undoable and changes every instance |
| `build_settings` then `build_player --output_path C:/out/Game.exe` | A cold build returns a job id; poll `jobs <id>` |

Two things to know before editing:

- The `path` these tools take is the one `scene_browse_hierarchy` returns. It resolves
  inactive objects, and carries an index only when a sibling name repeats: `/Canvas/Button[1]/Text`.
- A scene edit during Play Mode succeeds and is reverted when Play Mode stops. The response
  carries `playModeWarning` in that case. Asset edits made during Play Mode do survive.

### Animator Controllers

`animator_inspect` reads a controller by asset path, or through any component on a scene object
that points at one, which is how a character with one controller per body layer is reached.
Without a `layer` it reports the parameters and one line per layer and no states, because a
twenty-layer controller has hundreds of them. In Play Mode an `object_path` also reports what the
Animator is doing right now - the state each layer is in, how far through, and every parameter's
current value - which the asset cannot say and which is usually why a gimmick does not fire.

| Tool | Purpose |
|---|---|
| `animator_inspect --object_path /Avatar --layer 0` | Parameters, layers, and one layer's states and transitions |
| `animator_audit --path Assets/Anim/Body.controller` | Unreferenced parameters, states with no motion, states unreachable from the default, empty layers, duplicate layer names, transitions with neither a condition nor an exit time, and Write Defaults mixed within a layer |
| `animator_create --path Assets/Anim/Hero.controller --object_path /Hero` | Makes the controller the other eleven `animator_*` tools edit, and hangs it on the object |
| `animation_clip_create --path Assets/Anim/Idle.anim --loop true` | A state needs a motion; nothing else makes one. The clip is created empty |
| `animation_clip_write_curves --path Assets/Anim/Idle.anim --curves '[{"target":"Hips","type":"Transform","property":"m_LocalPosition.y","keys":[{"time":0,"value":1},{"time":0.4,"value":1.08}]}]'` | Puts the motion in it. All the curves in one call; the length follows from the keys |
| `animator_add_layer` / `animator_remove_layer` | Removing a layer destroys its sub-assets |
| `animator_add_state` / `animator_remove_state` / `animator_set_state` | One undo step each |
| `animator_add_transition` / `animator_remove_transition` | Conditions are passed as JSON |
| `animator_add_parameter` / `animator_remove_parameter` | |
| `animator_set_write_defaults` | Applies across a whole layer |

A controller is a shared asset, so a write reaches every scene and character using it, and the
`.controller` file is written before the call returns. Undo restores the controller in memory,
not the file. Run `animator_audit` before editing: it names the states nothing can reach, which
is usually what the person actually wanted fixed.

### Rendering and shader debugging

| Tool | Purpose |
|---|---|
| `shader_errors` | Compilation errors. A broken shader renders magenta and never says so. Run this after every shader edit. Without `--path` the sweep covers Assets only, never packages |
| `material_create --path Assets/Art/Wood.mat` | Omit `shader` and it uses the pipeline's own, so a URP project does not get `Standard` and a magenta result |
| `shader_info` / `material_read` / `material_set` | The values a frame is actually drawn with, not the shader's defaults |
| `render_stats` | Draw calls, SetPass, triangles and what batching collapsed. Take it before and after a change meant to be cheaper, so the claim is measured rather than inferred. Whole Game view, last drawn frame - stale while that view is hidden |
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
ffmpeg -v error -i out.mp4 -vf scale=160:90 f_%03d.png   # as many distinct frames as frames means it moved
```

One failure mode looks like "the tool did nothing": Play Mode defers script compilation. Unity
postpones the domain reload until Play Mode exits, so an edited script keeps running its old
build and `isCompiling` stays true. `play_mode_stop` is itself deferred to the next frame, and a
backgrounded Editor never draws that frame. Check `play_mode_status` first.
