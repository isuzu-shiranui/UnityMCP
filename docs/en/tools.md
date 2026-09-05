# Tool reference

This page lists the 88 tools the Editor publishes, one table per group. [Back to the README](../../README.en.md)

That number is the maximum, reached only when every optional package is present. The nine Timeline entries and the two Recorder entries need `com.unity.timeline` and `com.unity.recorder`. `test_run` and `test_results` need `com.unity.test-framework`. A project with none of those packages publishes 75 tools.

The idempotency column says only whether a call may be retried automatically after a connection failure. `safe` does not mean free of side effects. `reflect_read` can run a getter that changes the scene, and `capture_screenshot` focuses a window and writes a file.

`isuzu-unity-cli tools` prints the authoritative list. It comes from the Editor, so it always matches the version you are talking to.

## Diagnostics (looking)

| Tool | Idempotency | Purpose |
|---|---|---|
| `console_read_logs` | safe | Console entries, with `total`, `errors` and `warnings` counts |
| `console_get_count` | safe | Error / warning / log counts |
| `console_clear` | unsafe | Clear the console |
| `editor_log_tail` | safe | `Editor.log` from disk (works while the Editor is wedged) |
| `editor_dialog_list` | safe | Title, message and buttons of the modal dialogs the Editor is showing, plus how long the main thread has been stalled (works while the Editor is wedged; Windows only) |
| `editor_dialog_press` | unsafe | Press a button on an open dialog to unblock the main thread. Needs `confirm: true`. Buttons like "Don't Save" discard unsaved work, so read the message with `editor_dialog_list` first |
| `compile_status` | safe | Whether scripts are compiling, and whether the last compile succeeded |
| `compile_request` | unsafe | Ask for a recompile. Runs a full asset refresh first, which imports changed assets and can open a modal dialog |
| `test_run` | unsafe | Start an EditMode or PlayMode test run |
| `test_results` | safe | The current or most recent run (readable while it runs) |
| `scene_browse_hierarchy` | safe | Walk the hierarchy. Emits `path`, which every editing tool takes. A filtered walk also returns the parents leading down to each match. `missing_scripts: true` returns only objects carrying a component whose script cannot be resolved, and `missingScripts` on each object counts them |
| `scene_list` | safe | Open scenes, and the scenes in the build settings |
| `inspect_read` | safe | Read a serialized property. Omit `component_type` to address the GameObject itself |
| `inspect_list` | safe | Discover property paths. Omit `component_type` to address the GameObject itself |
| `asset_find` | safe | Search by type, name, folder or label. The reply is capped at `limit`, with the full match count in `total` |
| `asset_info` | safe | Type, GUID, importer, labels. Dependencies only with `include_dependencies` |
| `play_mode_status` | safe | Playing / paused / compiling |
| `project_assemblies` | safe | Loaded assemblies |
| `project_packages` | safe | UPM packages |
| `capture_screenshot` | safe | Game or Scene view, or an Editor panel. A panel is read off the screen, so anything covering the Editor is in the image. It focuses the window first. `save_path` writes it to disk, creating directories and overwriting |
| `job_status` | safe | State and result of a job id returned by a slow call |
| `animator_inspect` | safe | Read an Animator Controller. Without `layer` it returns the parameters and one line per layer, and no states at all, because a twenty-layer controller has hundreds of them. With `layer` it returns that layer's states with their motion, speed, Write Defaults flag and position. It also returns the transitions out of each state with their conditions, and the layer's Any State and Entry transitions. States inside sub-state machines are addressed as `Machine/State` |
| `animator_audit` | safe | Find the problems in a controller that nothing in the Editor reports. It changes nothing. The report covers unreferenced parameters, states with no motion, states unreachable from the default state, layers with no states, duplicate layer names, transitions with neither a condition nor an exit time, and Write Defaults mixed within a layer |
| `definitions_list` | safe | What defined tools loaded, and why one did not |

## Authoring

One call collapses into a single undo step wherever a tool declares an `UndoGroup`. In authoring those are the eight `gameobject_` tools, `inspect_write`, `prefab_create`, `prefab_instantiate` and the ten `animator_` editing tools, twenty-one in all. In rendering it is `material_set`. In Timeline and Recorder they are the seven `timeline_` tools and `recorder_add_track`.

The rest do not collapse into one. `asset_delete` moves the asset to the OS trash rather than through Undo, so you normally recover it from there. A folder path takes everything under it.

`prefab_apply` rewrites the prefab asset and changes every instance of it, so it asks for `confirm: true`.

`scene_open`, `scene_save` and `scene_create` act on scene files and are outside Undo. Whether `menu_execute` can be undone depends on the menu item it invokes. The five `play_mode_` tools change the Editor's play state, which Undo does not cover.

The `animator_` tools are undoable, but they reach further than the scene in front of you. An Animator Controller is an asset, so a write changes every scene, prefab and character using it.

The `.controller` file is written to disk before the call returns. Undo restores the controller in memory and not the file. Until something saves again, the file holds a change the Editor no longer shows.

| Tool | Idempotency | Purpose |
|---|---|---|
| `gameobject_create` | unsafe | Create an object, optionally a primitive. The new object becomes the selection |
| `gameobject_delete` | unsafe | Delete it (undoable) |
| `gameobject_duplicate` | unsafe | Duplicate it. The copy is a plain GameObject with no prefab link, so `prefab_apply` refuses it. It becomes the selection |
| `gameobject_reparent` | unsafe | Move it under another parent. The world position is kept by default. With `keep_world_position: false` the local position is zeroed and the rotation cleared, so it lands on the parent's origin |
| `gameobject_set_transform` | unsafe | Position, rotation, scale. Only the axes given |
| `gameobject_set_active` | unsafe | Activate or deactivate |
| `gameobject_add_component` | unsafe | Add a component by type name |
| `gameobject_remove_component` | unsafe | Remove one. Base types match, so `Renderer` finds a MeshRenderer. `index` picks between several of a type |
| `inspect_write` | unsafe | Write a serialized property. Omit `component_type` to address the GameObject itself |
| `asset_create_folder` | unsafe | Create a folder and its parents, idempotently |
| `asset_move` | unsafe | Move or rename, keeping the GUID |
| `asset_delete` | unsafe | Delete. Goes to the OS trash, normally recoverable from there. A folder path takes the whole tree |
| `asset_reimport` | unsafe | Reimport |
| `scene_open` | unsafe | Open a scene (refuses over unsaved changes) |
| `scene_save` | unsafe | Save. `path` makes it a Save As. The open scene is retargeted there rather than copied |
| `scene_create` | unsafe | New scene |
| `prefab_create` | unsafe | Save a scene object as a prefab |
| `prefab_instantiate` | unsafe | Place a prefab. The new instance becomes the selection |
| `prefab_apply` | unsafe | Push instance overrides back into the asset. Needs `confirm: true`, because it is not undoable and reaches every instance |
| `menu_execute` | unsafe | Invoke a menu item |
| `play_mode_play` | unsafe | Enter play mode. Unless Enter Play Mode Settings has Reload Domain off, the domain reloads and the connection briefly drops |
| `play_mode_stop` | unsafe | Leave it. The same domain reload applies |
| `play_mode_pause` | unsafe | Pause. Outside play mode it does nothing and answers with an `error` field rather than failing |
| `play_mode_unpause` | unsafe | Resume. Outside play mode it answers with the same `error` field |
| `play_mode_step` | unsafe | Step one frame, pausing first if needed. Outside play mode it answers with the same `error` field |
| `animator_add_layer` | unsafe | Add a layer with an empty state machine. `weight` defaults to 0 in Unity, so pass 1 unless the layer should start switched off |
| `animator_remove_layer` | unsafe | Remove a layer and the states, transitions and state machine inside it. Every later layer is renumbered |
| `animator_add_state` | unsafe | Add a state. The first state in an empty state machine becomes its default. Unity makes the name unique, so the name it ended up with is reported back |
| `animator_remove_state` | unsafe | Remove a state. Unity removes the transitions into it as well, and the count of those is reported |
| `animator_set_state` | unsafe | One state's motion, speed, Write Defaults flag, tag or node position. Only the arguments given are changed |
| `animator_set_write_defaults` | unsafe | Set Write Defaults across a whole layer, or the whole controller. This is the fix for the mixed layer `animator_audit` reports |
| `animator_add_transition` | unsafe | Add a transition, or an Any State transition when `from_state` is left out. Conditions are `{parameter, mode, threshold}` objects. A mode the parameter's type cannot answer is refused rather than silently never firing |
| `animator_remove_transition` | unsafe | Remove one transition by index. Indices shift as they are removed, so read the layer again between removals |
| `animator_add_parameter` | unsafe | Add a parameter. A name that already exists is refused, rather than added again under a made-up name |
| `animator_remove_parameter` | unsafe | Remove a parameter. Conditions naming it are left behind and can never fire again, so they are listed in the reply |

## Rendering and shaders

| Tool | Idempotency | Purpose |
|---|---|---|
| `render_compare` | safe | How two captures differ, in numbers (changed pixels, mean/max delta, bounding box, grid) |
| `render_pipeline_info` | safe | Active pipeline, colour space, graphics API, quality level. Reports the quality-level override too |
| `render_camera_info` | safe | Cameras with view, projection and GPU projection matrices |
| `shader_errors` | safe | Shader compilation errors (a broken shader renders magenta and says nothing). Without a path the check covers Assets only, never packages |
| `shader_info` | safe | Pass count, properties, keyword space, render queue. Passes are counted, never named |
| `material_read` | safe | A material's current values, keywords and render queue. Name a material asset with `path`, or a scene object with `object_path` to read every material slot on its renderer. Reading every slot reports each material's property count rather than its values. One renderer can carry dozens of materials of a few hundred properties each, so name a `slot` to get that slot's values. A shader that is missing or unsupported is named in `shaderProblem`, which is the magenta case. A material that is not an asset comes back with a null path rather than being left out |
| `material_set` | unsafe | Set a property, keyword or render queue. A colour or vector can be an `[x,y,z,w]` array. `object_path` with `slot` reaches a material through a renderer. It writes the shared material rather than instantiating a per-renderer copy, so every renderer using it changes. An asset's .mat file is written to disk, and undo does not restore the file |
| `gpu_readback` | safe | Read a buffer or texture back and report `min`, `max`, `mean`, `zeroCount`, `allZero` and a histogram, plus `samples` raw values. A texture is read as a single 32-bit channel, so the numbers describe red alone |

## Timeline (video / live)

These tools appear only when `com.unity.timeline` is present.

| Tool | Idempotency | Purpose |
|---|---|---|
| `timeline_inspect` | safe | Tracks, clips, bindings and the director's time. Follows Control tracks into the child timelines they drive, for the layered structure a live stage uses. The `track` filter matches inside groups too, so it can return several |
| `timeline_evaluate` | unsafe | Evaluate a director at a time or frame, without Play mode. Pair with `capture_screenshot` to check one frame. The evaluated values are written onto the scene objects and are not undoable |
| `timeline_edit_clip` | unsafe | One clip's start, length, name, ease, blend and speed. Reports the values as they landed, listing anything the clip type discarded in `ignored` |
| `timeline_shift_clips` | unsafe | Ripple edit: move everything at or after a time together. Moves nothing at all if the shift would cross zero |
| `timeline_set_track` | unsafe | Mute, lock, rename, or bind a track. Resolves the component the track's type wants, for example an Animator for an animation track |
| `timeline_delete` | unsafe | Delete a track or a clip. A group takes its children with it. It is undoable, so it does not ask for confirmation |
| `timeline_create` | unsafe | Create a Timeline asset, optionally with a director. It is the only entry point that makes track creation safe. Writing the asset saves every unsaved asset in the project |
| `timeline_create_track` | unsafe | Add a track (activation, animation, audio, control, group, playable, signal), optionally inside a group and bound in the same call |
| `timeline_create_clip` | unsafe | Add a clip. `control_source` configures a Control clip's nesting in one call. `animation_clip` sets the AnimationClip to play |

The editing tools report the value read back after the write, not the requested one. Timeline's setters discard values a clip type does not support, such as the speed of an Activation clip, and they raise no error. Echoing the request back would therefore make the caller believe in a change that never happened.

The creation tools refuse to act if the timeline is not yet an asset. Timeline would otherwise build the track in memory only, and there is no public API to persist it afterwards.

`timeline_evaluate` leaves the animated values on the bound scene objects. That dirties the scene and cannot be undone. Called during Play Mode it leaves the director paused, and no tool here resumes it.

## Recorder (rendering out)

These tools appear only when both `com.unity.recorder` and `com.unity.timeline` are present.

| Tool | Idempotency | Purpose |
|---|---|---|
| `recorder_add_track` | unsafe | Add a Recorder track to a Timeline, so playing the director records it. mp4 / webm / mov and png / jpeg / exr, capturing the game view, a camera or a RenderTexture, at a chosen resolution. Adding the track saves every unsaved asset in the project |
| `recorder_list` | safe | What a Timeline will record, and where it will be written |

Recording runs as a track on the Timeline. The frame rate comes from the Timeline itself, so the recording cannot drift from the animation. The setup also depends less on changes to the Recorder API between versions. Omit `output_path` to write to a `Recording` folder beside `Assets`, named after the Timeline.

## Live state and code execution

| Tool | Idempotency | Purpose |
|---|---|---|
| `reflect_read` | safe | Read live state by type and member path, private members included. A getter such as `Renderer.material` instantiates the shared asset, so prefer `sharedMaterial` |
| `reflect_find_type` | safe | Find a loaded type by name |
| `execute_code` | unsafe | Compile and run a C# snippet (the last resort when no tool reaches it) |

## Input (synthesized into the Editor's own GUI path)

See [Synthesizing, recording and replaying Editor input](input-tools.md) for details.

| Tool | Idempotency | Purpose |
|---|---|---|
| `input_pointer` | unsafe | Synthesize mouse movement, clicks, drags and scrolling |
| `input_key` | unsafe | Synthesize a key press |
| `input_record` | unsafe | Record a human's input to a JSON file |
| `input_replay` | unsafe | Send a recording back, optionally capturing at the end |

## Builds

| Tool | Idempotency | Purpose |
|---|---|---|
| `build_settings` | safe | Active target, the scenes in the build, whether the module is installed |
| `build_player` | unsafe | Build a player. It becomes a job when it outlasts `syncWaitMs`, three seconds by default |
| `build_switch_target` | unsafe | Switch the active target (reimports assets) |

## Availability and limits

- Capture that reads the screen is Windows-only. That is `inspector`, `hierarchy`, `project`, `console`, `window:<title>` and every view whose name ends in `_window`. It is refused with `window_occluded` when another application is in front of the Editor. A floating window of the same Editor covering the target is not detected. `game` and `scene` work everywhere. Capturing a panel focuses its window, which changes what the person at the Editor is looking at.
- `test_run` and `test_results` appear only when `com.unity.test-framework` is present. It is present by default. They live in their own assembly, which is constrained on the presence of the test-framework package. A project without the framework loses those two tools, and the rest of the package keeps working.
- Unity Hub operations (installing Editors or modules) are not provided. The Hub has its own CLI. A build target that is not installed is answered with the Hub command to run.
- Append `?group=diagnostics,authoring` to the MCP URL and `tools/list` returns only those groups. The groups are `diagnostics`, `authoring`, `rendering`, `timeline`, `build`, `code` and `input`. `isuzu-unity-cli tools --group <name>` applies the same filter, and calls themselves are never filtered.

## Things to know before editing

- The `object_path` an editing tool takes is the one `scene_browse_hierarchy` returns. It resolves inactive objects, and it carries an index only where a sibling name repeats, as in `/Canvas/Button[1]/Text`.

  A prefab open for editing is the exception. `scene_browse_hierarchy` still reports the scene behind it. The `gameobject_` and `inspect_` tools address the prefab contents, so they cannot resolve those paths.
- A scene edit made during Play Mode looks like it worked and is reverted when Play Mode stops. Those responses carry a `playModeWarning`. Asset edits made during Play Mode survive, so they carry no warning.
- Deletion is normally recoverable, so it asks for no confirmation. The two tools that do ask are `prefab_apply` and `editor_dialog_press`, because neither can be undone.

  Assets go to the OS trash, and a folder path takes the whole tree with it. GameObjects go through Undo. Opening or replacing a scene over unsaved changes is refused, because Undo cannot restore unsaved changes.
- `gameobject_set_transform` moves a RectTransform too, but what it writes is `localPosition`. The serialized `m_AnchoredPosition` catches up one call later. Reading it back with `inspect_read` immediately after the write therefore reports the old value for an object that has already moved. Use `reflect_read` to verify, or leave one call in between.
- `inspect_write` cannot set a property that holds a reference to another object. A sprite, a material and an event target are all of that shape. Assign those with `execute_code`.
- `execute_code` places the snippet in a method body, so a using directive there is a compile error. `System`, `System.Collections`, `System.Collections.Generic`, `System.Linq`, `System.Threading.Tasks`, `UnityEngine` and `UnityEditor` are already imported. Write any other type in full, as in `UnityEngine.Rendering.Volume`.
- `execute_code` is not on the undo stack. Use the authoring tools for authoring.
