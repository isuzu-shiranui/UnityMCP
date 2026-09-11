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

Values are typed automatically. `--limit 20` sends a number and `--active_only true` sends a boolean,
and a value that parses as JSON is sent as JSON, which is how a list or an object gets in.
Errors print to stderr and set a non-zero exit code, so the commands can be used in scripts.

A list can also be typed by naming the option once per value, which no shell can mangle:

```bash
isuzu-unity-cli call reflect_read --paths "@scene:/A/Transform/position" --paths "@scene:/B/Transform/position"
```

That is the way to do it on Windows PowerShell, which strips the double quotes out of an argument
on its way to a program: `--paths '["a","b"]'` arrives as `[a,b]` and is refused rather than read
as one long path.

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

## The rest of it

Two files sit beside this one. Read the one the task calls for rather than both:

- `reference/tools.md` - what each tool is for, by area: authoring, animator controllers,
  rendering and shaders, timeline and recorder.
- `reference/workflows.md` - the sequences that come up: chasing an error to the object that
  raised it, editing a script and confirming it built, running the tests, proving a rendering
  change did something, reproducing an interaction, recovering a connection that stopped
  answering.

## Ask for more in one call

Reading and writing one thing at a time is the largest cost here, and every one of these numbers
came from watching a real task:

```bash
# Several paths in one read: three objects' bounds took fourteen calls without this
isuzu-unity-cli call reflect_read --json '{"paths":[
  "@scene:/A/MeshRenderer/bounds","@scene:/A/BoxCollider/bounds",
  "@scene:/B/MeshRenderer/bounds"],"depth":2}'

# Every component on an object, with its properties, in one call rather than one call each
isuzu-unity-cli call inspect_list --object_path /Player --detail full

# Several properties on one component: one ConfigurableJoint took twenty-one calls without this
isuzu-unity-cli call inspect_write --json '{"object_path":"/Hair","component_type":"ConfigurableJoint",
  "values":{"m_XMotion":0,"m_YMotion":0,"m_ZMotion":0}}'

# The same edit across many objects, the way the Inspector edits a multi-selection:
# swapping a material across three hundred objects took two hundred and ninety-nine calls
isuzu-unity-cli call inspect_write --json '{"object_paths":["/Brick_0","/Brick_1","/Brick_2"],
  "component_type":"MeshRenderer","property_path":"m_ReceiveShadows","value":false}'

# Frames, not one frame: watching an animation a frame at a time cost 1,255 calls.
# 'paths' reads while the frame is still that one, so a step and the look that always
# follows it are one call: twenty-one steps once came with forty-six reads behind them.
isuzu-unity-cli call play_mode_step --json '{"count":120,"paths":[
  "@scene:/Turnstile/Transform/localEulerAngles"]}'
```

Both `values` and `object_paths` write nothing at all if any path fails to resolve, so a refusal
costs a round trip rather than leaving something half configured.

`paths` is also how to ask what a set of objects has in common. Reading each renderer's
`sharedMaterial` tells the materials apart by the `instanceId` every reference carries, so two
materials with the same name still count as two:

```bash
# 360 renderers in batches of 50: 8 calls and 104 KB, and it found 301 distinct materials.
# Asking a material tool once per object took 361 calls and 886 KB for the same answer.
isuzu-unity-cli call reflect_read --json '{"paths":[
  "@scene:/HeavyScene/Brick_0/MeshRenderer/sharedMaterial",
  "@scene:/HeavyScene/Brick_1/MeshRenderer/sharedMaterial"],"depth":1}'
```

`search_query` has a `ref:` token that goes the other way - `h: ref:Assets/Art/Stone.mat` names
the scene objects using that material. It works on assets only, so a material created at run time
and never saved has no path to search by; `sharedMaterial` through `paths` reaches those too.

A picture is the other end of the scale: one screenshot at the default size is around 40,000
tokens, so four of them outweigh every other call of a session. Read the numbers with
`inspect_read` or `reflect_read` when a number would answer the question.

## Finding what a reply cannot show

```bash
# Fields whose target was deleted: the id is still there and the object is gone. A field nobody
# filled in has neither, so an empty slot is not reported as damage.
isuzu-unity-cli call asset_broken_references --scope scene
isuzu-unity-cli call asset_broken_references --scope assets --folder Assets/Prefabs --max_seconds 30

# The Editor's own search, for conditions the hierarchy walk cannot express
isuzu-unity-cli call search_query --json '{"query":"h: t:meshrenderer p(castshadows)!=\"Off\""}'
isuzu-unity-cli call search_query --json '{"query":"p: t:Material"}'
```

`search_query` is Unity's query language, not one this package defines: a term Unity does not
understand narrows nothing rather than failing, so check the count against what you expected. A
project query can also come back empty the first time it is asked in a session - ask again before
concluding it found nothing.

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
| The call fails outright right after `play_mode_play`, `play_mode_stop` or a script edit | The Editor is reloading its domain and the server is gone for those few seconds; it comes back on its own. A read can simply be called again. A change may already have arrived, so read the state back before sending it again. `reference/workflows.md` has the rest |
