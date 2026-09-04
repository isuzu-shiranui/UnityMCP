# Synthesizing, recording and replaying Editor input

This page covers the four input tools: `input_pointer`, `input_key`, `input_record` and `input_replay`. [Back to the README](../../README.en.md)

These tools send events through the same path a person's mouse and keyboard use. A bug that never appears when a value is written directly can be reproduced through this path instead. Rendering that breaks only during a right-drag is one example.

Window addressing is shared with `capture_screenshot`. The accepted values are `scene_view_window` (alias `scene`), `game_view_window` (alias `game`), `inspector`, `hierarchy`, `project`, `console` and `window:<title substring>`.

## input_pointer

`input_pointer(view, action, from, to, ...)` takes `action` as `move`, `down`, `up`, `click`, `drag` or `scroll`.

Coordinates are points measured from the top-left of the window's content area, below the tab bar. The 0..1 fractions `normalized: true` takes, and the bounds a point is checked against, are of the whole window including the tab bar. Normalized 0.5 therefore sits slightly past the content centre. Give points directly when the position has to be exact.

A drag can be spread across Editor frames with `steps` and `frames_per_step`. A time-based effect reacts to that spread itself, so a drag sent in a single frame does not reproduce one.

A right-drag is FPS Look, and an Alt+left-drag is Orbit. The window that had focus before the send gets it back by default, and `restore_focus: false` leaves it alone. `input_key` and `input_replay` take the same argument.

## input_key

`input_key(view, key, action, modifiers, character)` takes `action` as `press`, `down` or `up`. `press` sends KeyDown, then KeyDown with the character, then KeyUp.

## input_record

`input_record(action, view, name, include_moves)` takes `action` as `start`, `stop` or `status`. It records a person's input and writes it to a file on `stop`. That file is `%LOCALAPPDATA%\UnityMCP\recordings\<projectHash>\<name>.json`, or `~/.local/share/UnityMCP/recordings/<projectHash>/<name>.json` on macOS and Linux. `<projectHash>` is derived from the Unity project's path, so the same `name` in another project is a different file.

A Scene View is recorded from its drawing callback, so a whole drag is kept. Other windows are recorded from the UI panel path, which stops seeing a drag once an IMGUI control captures the mouse. A drag inside such a panel therefore keeps only its first frame. Clicks, wheel and keys are unaffected.

Anything in `name` outside letters, digits, `_` and `-` is replaced with `_` rather than refused. Read `path` in the reply to learn the name the file actually got. Only a Windows reserved device name such as `CON`, `NUL` or `COM1` is refused.

The `start` result carries `contentOffset`, `pixelsPerPoint` and `windowSize` alongside `path`. Screenshot pixels can be converted to points before the recording starts.

## input_replay

`input_replay(name, path, view, speed, loop_count, repaint_each_frame, then_capture, capture_path)` sends a recording back. Without `view` it targets the window the recording was made in, and it refuses with `view_mismatch` when the window type differs.

`then_capture` takes a window such as `scene`. It calls `capture_screenshot` at the end of the replay and puts the result under `capture`. By default that is the image itself. With `capture_path` the PNG is written to that path instead, and `capture.path` names the file, which is what `render_compare` takes.

## The typical workflow

The workflow has four steps. Record a human's drag once. Replay the same input with a fix applied. Capture with `then_capture`, then compare before and after with `render_compare`.

Those last three calls can be wrapped under one name as a `sequence` [defined tool](defined-tools.md).

## Limits

Three limits apply. The window must be visible, and a tab hidden behind another one in the same dock area is refused with `window_not_active`. Events go through the Editor's own GUI path, not the operating system's input queue. A long replay returns a job id, and `job_status` fetches the result.
