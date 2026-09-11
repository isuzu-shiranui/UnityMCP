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

Without a recording to replay, `input_pointer` and `input_key` send the events directly:

```bash
isuzu-unity-cli call input_pointer --view scene_view_window --action drag --json '{"from":[200,200],"to":[400,200],"button":1}'
isuzu-unity-cli call input_key --view inspector --key A --character a
```

A right-drag is FPS Look and Alt+left-drag is Orbit. A drag is spread over `steps` frames by
default, which matters: anything that reacts to time passing does not reproduce when the whole
drag arrives in one frame. Coordinates are points, not pixels; divide a screenshot pixel by the
`pixelsPerPoint` in the reply.

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
isuzu-unity-cli call capture_screenshot --view scene --max_size 512 --save_path scene.png
```

### The connection was refused

A call that comes back "unable to connect", `ECONNREFUSED`, or with the MCP server reported as
disconnected is almost always the Editor rebuilding its domain. Changing a `.cs` file starts
that, and the server is gone for the few seconds it takes.

Wait and call again. Do not fall back to reading the code statically, and do not re-run setup:
the registration is fine and the Editor is coming back.

```bash
isuzu-unity-cli verify                       # edits, waits out the reload, returns the errors
isuzu-unity-cli call compile_status          # or just call again after a few seconds
```

It is worth knowing that this reaches every client at once. Two agents on one project both lose
the connection when either of them edits a script, so the one that did nothing sees it too.

If calls still fail after half a minute, the Editor is closed, or the server was stopped on the
Preferences page.

### The Editor stopped responding

```bash
isuzu-unity-cli health                       # queueDepth climbing with reqCount flat = wedged main thread
isuzu-unity-cli call editor_state             # the same, as a tool an MCP client can reach
isuzu-unity-cli call editor_log_tail --lines 50
isuzu-unity-cli jobs                         # what is queued or running
```

`health`, `editor_state`, `jobs` and `editor_log_tail` are answered off the main thread, so they
keep working when nothing else does. Ask `editor_state` first when a call has not come back:
importing a project of ten thousand assets put twenty-five calls in the queue and answered none,
which from the caller's side is indistinguishable from tools that hang.

Most often the Editor is not wedged but waiting: a modal dialog holds the main thread inside its
own message loop until someone answers it. `health` reports it under `mainThread` as `stalledMs`
with the dialog's title, message and buttons, and a job that is waiting for one says so.

```bash
isuzu-unity-cli call editor_dialog_list      # title, message, buttons
isuzu-unity-cli call editor_dialog_press --button "Cancel" --confirm true
```

Read the dialog before pressing anything. `Don't Save` and `Discard` throw away unsaved work;
`Cancel` is the safe answer, after which the cause can be fixed and the original call retried.
Windows only - elsewhere `editor_dialog_list` answers `supported: false` and a person has to
answer the dialog at the Editor.

Some of these windows are not questions at all. Unity's progress window - `Hold on (busy for ...)`,
`Running managed callbacks`, anything offering `Skip Transcoding` - clears when the work behind it
finishes, and answering it abandons that work rather than letting the call through. When its
message reads `Waiting for Unity's code to finish executing`, the work is the call you made:
pressing a button leaves it running and puts the window straight back, and a call that holds the
main thread cannot be interrupted from outside, so one that never ends means restarting the
Editor. The running-job notice says which of the three kinds it is.
