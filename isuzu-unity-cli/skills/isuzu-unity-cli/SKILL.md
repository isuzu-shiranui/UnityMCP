---
name: isuzu-unity-cli
description: >
  Drive the open Unity Editor from the shell with the isuzu-unity-cli command: console errors,
  compiling and tests, scenes, GameObjects and components, prefabs, play mode, uGUI clicks, and C#
  run inside the Editor. SKILL.md gives a command for each common task, so no tool list is needed.
---

# isuzu-unity-cli

Each command talks to the Unity Editor that has this project open and prints compact JSON. A call
returns when its work is done: `play_mode_play` returns once play mode has started, and a call that
takes long waits for its result.

## Rules

- Do not list tools. The table below covers common work; for anything else run
  `isuzu-unity-cli tools --search <words>`, then `isuzu-unity-cli tools <name>`.
- Put every call you already know into one shell command with the template below. Do not read
  again to confirm a change: each reply already shows the result, and you can answer from it.
- Windows PowerShell deletes double quotes inside arguments, so never pass JSON text. Use
  `--name value`; repeat an option for a list (`--paths a --paths b`); `--values.m_Mass 2` sets one
  field of an object argument; a vector is `--position '1,0,2'`; anything else goes in a file:
  `--args-file args.json`.
- Single-quote values with spaces, commas or `@`: `'/Main Camera'`, `'error,warning'`,
  `'@scene:/Boss/Enemy/health'`. Unquoted, PowerShell splits `1,0,2` into three words.
- Scene paths start with `/`. Siblings that share a name take a 0-based index: `/Audio/Speaker[1]`.
- Property paths are serialized names (`m_Mass`, `m_LocalPosition.x`, a script's field name); the C#
  name (`mass`) also works when it is unambiguous, and a miss lists the nearest names.

## Template

```powershell
function u { isuzu-unity-cli @args; if ($LASTEXITCODE) { throw "isuzu-unity-cli $args failed ($LASTEXITCODE)" } }
try {
  u call play_mode_play
  u call ui_click --text Start
} finally { isuzu-unity-cli call play_mode_stop }
```

A failed call throws, so the calls after it do not run on a broken state, and `finally` leaves play
mode. Without play mode, the `try`/`finally` is not needed.

## Tasks

| Task | Calls |
|---|---|
| Fix compile errors | `u verify` prints each error with the source lines around it; after the edit, `u verify --test --filter <regex>` compiles and runs the tests in one call |
| Run tests | `u verify --no-compile --test --filter <regex>` (EditMode; `tests: none matched` when nothing matched) |
| Console errors and warnings | `u call console_read_logs --type 'error,warning' --limit 30` |
| Find objects | `u call scene_browse_hierarchy --name <text>` or `--component <Type>` |
| Objects matching a condition | `execute_code` with `McpSnippet.All<T>(true)` and `McpSnippet.PathOf` (below) |
| Every property of an object | `u call inspect_list --object_path <path> --detail full` |
| Read or write a property | `u call inspect_read --object_path <path> --property_path <prop>`; `inspect_write` also takes `--value <v>`, or `--values.<prop> <v>` for several. The component is found from the property; add `--component_type <Type>` when several have it |
| Change a prefab asset | `u call inspect_write --asset_path Assets/X.prefab --property_path <prop> --value <v>` saves it; `overriddenBy` lists scene instances that keep their own value; `--object_path Child/Name` for a child |
| Create an object | `u call gameobject_create --name <n> --parent_path <path> --position '1,0,2'` |
| Add a component | `u call gameobject_add_component --object_path <path> --component_type <Type> --values.<field> <v>` |
| Many objects by a formula | `foreach ($i in 0..9) { u call gameobject_create --name "Item$i" --parent_path /Root --position "$($i * 2),0,0"; u call gameobject_add_component --object_path "/Root/Item$i" --component_type Rigidbody --values.m_Mass ($i + 1) }`, then `u call scene_save` |
| Add a script, then use it | one command: write the file, `u verify`, `u call gameobject_add_component --object_path /<name> --component_type <Class> --values.<field> <v>`, `u call scene_save`. A name without a leading `/` is looked up anywhere in the scene |
| Why a camera does not draw an object | the second snippet below gives every usual cause in one call; fix the one it shows with `inspect_write`, then `u call scene_save` |
| Exception in play mode | `u call play_mode_play`, `u call play_mode_step --count 150`, `u call console_read_logs --type error --stack_trace true --limit 5` |
| A value while playing, or its value after each call that changes it | `u call play_mode_play --paused`, then `u call play_mode_step --seconds 3 --changes --paths '@scene:/<object>/<Component>/<field>'`: `changes` lists `[frame, time, value]` each time the value changed, which is its value right after the code that changed it. No breakpoints or source reading needed |
| Press a uGUI button | `u call ui_click --text <label>` (or `--object_path`): `textChanges` shows the text it changed |
| Save the scene | `u call scene_save` |

## execute_code

```powershell
@'
return McpSnippet.All<Light>(true)
    .Where(l => l.enabled && l.gameObject.activeInHierarchy)
    .Select(l => McpSnippet.PathOf(l.gameObject));
'@ | Set-Content -Encoding UTF8 $env:TEMP\snippet.cs
u call execute_code --file $env:TEMP\snippet.cs
```

Why a camera does not draw an object (replace the two paths):

```powershell
@'
var cam = McpSnippet.Find("/Main Camera").GetComponent<Camera>();
var r = McpSnippet.Find("/Path/To/Object").GetComponentInChildren<Renderer>(true);
return new { cameraObjectActive = cam.gameObject.activeInHierarchy, cameraEnabled = cam.enabled, cam.targetTexture, cam.cullingMask, r.gameObject.layer, layerInMask = (cam.cullingMask & (1 << r.gameObject.layer)) != 0,
    objectActive = r.gameObject.activeInHierarchy, rendererEnabled = r.enabled, material = r.sharedMaterial ? r.sharedMaterial.name : null,
    cam.nearClipPlane, cam.farClipPlane, depth = cam.transform.InverseTransformPoint(r.bounds.center).z, halfDepth = r.bounds.extents.z,
    inFrustum = GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(cam), r.bounds) };
'@ | Set-Content -Encoding UTF8 $env:TEMP\visible.cs
u call execute_code --file $env:TEMP\visible.cs
```

- Statements with `return <value>`. `System`, `System.Linq`, `UnityEngine`, `UnityEditor` and both
  `SceneManagement` namespaces are imported. `McpSnippet` has `PathOf(go)`, `Find(path)`,
  `IdOf(obj)` and `All<T>(includeInactive)`.
- Record scene edits with `Undo.RegisterCreatedObjectUndo` or `Undo.RecordObject`, then save with
  `u call scene_save` rather than inside the snippet.
- A compile error names the line and column in your snippet.

## When a call fails

| Output | Meaning |
|---|---|
| `error [not_found]` | The path or name does not exist; the message lists what does |
| `error [invalid_params]` | A wrong argument; `isuzu-unity-cli tools <name>` shows the right ones |
| `error [conflict]` | Several matches, or something covers the element you tried to click; the message names them |
| `error [play_refused]` | Play mode did not start; `u verify` shows the compile errors |
| exit code 4 | The wait ran out while the work continues; the message says how to pick it up |
| `No Editor is running.` | Open the project in Unity first |

More: `reference/tools.md` (tools by area), `reference/workflows.md` (dialogs, a stuck Editor,
input recording).
