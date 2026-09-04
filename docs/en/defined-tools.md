# Defined tools

This page explains how a single JSON file adds a tool without compiling C#. [Back to the README](../../README.en.md)

A JSON file adds a tool for anything that is a set of reads, a C# file, or a chain of existing tools.

A short, stable tool name keeps repeated reflection paths and snippets out of every turn's prompt. Prompt caching keeps working, and context stays small.

## Where the files live

Definition files live in two places. On Windows they are `%LOCALAPPDATA%\UnityMCP\tools\<projectHash>\` for one project and `%LOCALAPPDATA%\UnityMCP\tools\shared\` for every project. On macOS and Linux the paths are `~/.local/share/UnityMCP/tools/<projectHash>/` and `~/.local/share/UnityMCP/tools/shared/`.

`<projectHash>` is a 16-character hex string derived from the SHA-256 of the project's Assets path (`Application.dataPath`). It is the same value as the descriptor file name. There is no need to compute it, because `/health` reports the full path of each directory as `definitionsDir` and `sharedDefinitionsDir`.

In both directories every JSON file is loaded regardless of its file name. Only when the same tool name appears in both does the project-specific side win. Two files with the same file name but different tool names both load.

One file defines one tool.

The three kinds are `probe`, `script` and `sequence`.

## probe

A set of reflection reads.

```json
{
  "name": "camera_probe",
  "description": "Read the Scene View camera's transform and the selected object's position.",
  "kind": "probe",
  "reads": [
    { "id": "camera", "path": "@sceneview:camera/transform/position" },
    { "id": "selected", "path": "@selection/transform/position" }
  ],
  "mode": "changes"
}
```

The first segment of `path` accepts a root notation in addition to a bare type name. The forms are `@type:Ns.Type`, `@scene:/Canvas/Button[1]`, `@id:<instanceId>`, `@selection` and `@sceneview:camera`. A bare type name without `@type:` still works. `@scene:` takes a scene hierarchy path, and a component type name in the next segment resolves to that component.

Every segment after the first follows the same rules as `reflect_read`.

`{input}` is a string substitution applied at call time. It reaches only `reads[].path` and the string values inside `steps[].arguments`. A placeholder in any other field is neither substituted nor rejected.

With `mode: "changes"`, the result carries only what changed since the previous call. The first call, the call right after a domain reload, and the call right after the definition file changes each return every read with `baseline: true`.

The result shape is `{reads: {id: {path, type, value}}, mode, changed: [id, …], baseline?: true}`. A read that cannot be resolved yields `{path, error}` for that id. An empty selection or an object that has been deleted is such a case. The other reads return as usual.

## script

One C# file.

```json
{
  "name": "light_bump",
  "description": "Multiply every Light's intensity by a factor.",
  "kind": "script",
  "file": "light_bump.cs",
  "inputs": {
    "factor": { "type": "number", "description": "Multiplier applied to intensity.", "default": 1.5 }
  }
}
```

`file` is absolute, or relative to the definition file. The snippet receives its arguments as a `JObject args`.

Each call reads the `.cs` file and hashes the wrapped source. An edit to the file therefore takes effect on the next call, with nothing else to reload.

Each distinct script content compiles a new assembly. That assembly stays loaded until the next domain reload, exactly like `execute_code`, so every edit adds one more assembly.

A compile error in the `.cs` file fails the call with `script_compile_error` (HTTP 400). The compiler messages come back with it.

## sequence

A chain of existing tools.

```json
{
  "name": "look_check",
  "description": "Replay a recorded drag, capture the Scene View, and compare it against a baseline.",
  "kind": "sequence",
  "steps": [
    { "id": "replay", "tool": "input_replay", "arguments": { "name": "look", "then_capture": "scene", "capture_path": "Temp/look_after.png" } },
    { "id": "compare", "tool": "render_compare", "arguments": { "before": "baseline.png", "after": "{{replay.capture.path}}" } }
  ]
}
```

Inside a step's `arguments`, `{input}` is a string substitution. A value that is exactly `{{stepId.json.path}}` is replaced with that token from an earlier step's result, unchanged. A step can only reference an earlier step, and anything else is rejected when the definitions load.

A failed step stops the sequence, unless it carries `continue_on_error: true`.

A `sequence` that contains at least one destructive step is treated as destructive itself. `confirm` and `dry_run` appear in its schema, and the `confirm` passed to the sequence call is forwarded to each destructive step. Writing `destructive: false` on such a sequence is a load error.

A step may be a multi-frame tool such as `input_replay` or a spread-out `input_pointer` drag. The sequence result waits until those frames have run.

A step may call another `sequence`. Two sequences that reference each other are refused at load time.

The result is `{steps: [{id, tool, ok, result|error}, …]}`. This kind exists to give a replay, a capture and a comparison a single name.

## Common fields

| Field | Default | Meaning |
|---|---|---|
| `name` / `description` / `kind` | required | `kind` is `probe`, `script` or `sequence` |
| `group` | derived from the name prefix | Any other value is rejected. The `definitions_` prefix maps to `diagnostics` |
| `idempotency` | `safe` for `probe`, `unsafe` for `script`/`sequence` | same meaning as on an attribute-based tool |
| `mainThread` | `true` | `false` is safe only for work that touches no Unity API. `false` on a `probe` is a load error |
| `destructive` | `false` (`true` for a `sequence` that contains a destructive step) | `true` injects `confirm`/`dry_run` the same way an attribute-based tool does. `false` on a `sequence` that contains a destructive step is a load error |
| `undoGroup` | none | allowed only on `sequence`, and only with `mainThread: true` |
| `alwaysLoad` | `false` | keeps the tool loaded rather than deferred behind tool search |
| `maxResultSizeChars` | `0` (client default) | raises the size at which the result spills to a file |
| `inputs` | none | name → `{type, description, required?, default?, enum?}`. `type` is `string`, `integer`, `number`, `boolean`, `object` or `array` |
| `examples` | none | worked argument objects, as JSON objects or JSON strings |

An unknown key is rejected at the top level and inside each `inputs`, `reads` and `steps` entry. A typo therefore cannot pass silently. A `script` whose `file` does not exist is rejected at load time as well.

The `type` and `enum` declared under `inputs` are checked at call time. An argument that violates them is refused with `invalid_params`.

## Reloading

Files are watched, and a change rebuilds the catalogue.

A client already connected gets no `tools/list_changed` and has to reconnect to see the change. `GET /tools?refresh=1` forces the rebuild instead.

The `definitions_list` tool reports what loaded and, for anything that did not, why.
