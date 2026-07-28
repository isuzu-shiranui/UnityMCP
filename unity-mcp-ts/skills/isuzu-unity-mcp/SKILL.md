---
name: isuzu-unity-mcp
description: >
  Control Unity Editor from the CLI with the isuzu-unity-mcp command. Execute C# code, browse scene
  hierarchy, inspect/modify GameObjects, capture screenshots, read console logs, check compile
  status, control play mode, and execute menu items. Use when: user wants to interact with Unity
  Editor programmatically, run C# code in Unity, debug Unity scenes, capture Unity screenshots,
  check Unity logs, or automate Unity Editor operations via command line.
---

# Unity MCP - CLI Control for Unity Editor

Drive a running Unity Editor with the `isuzu-unity-mcp` command. It reads the descriptor file the
Editor publishes, so it needs no port scan, no token handling, and no MCP client running.

```bash
isuzu-unity-mcp projects   # which Editors are running
isuzu-unity-mcp tools      # what this Editor publishes, with argument names
isuzu-unity-mcp health     # server state, queue depth, running jobs
```

> Requires UnityMCP v3. On v2 there is no `isuzu-unity-mcp` binary and endpoints are unauthenticated
> on `127.0.0.1:27182`; see the v2 section at the bottom.

## デバッグの鉄則: まずコンソールを読む (必須・最優先)

**何かが動かない/表示されない時、自作の計装(リフレクションでのバッファ読み出し・デバッグカウンタ・合成テスト等)を組む前に、必ず先にコンソールのエラー/警告を読む。**

```bash
isuzu-unity-mcp call console_read_logs --type error --limit 30
```

理由: Unityは原因を**既に文章で教えている**ことが多い。`Property (_HZB) at kernel index (5) is not set` の1行が、2時間分のGPUバッファ readback デバッグより速く真因(未bindリソースでdispatchがドロップ)を名指しした(2026-06-23 実証)。**システムが出している事実を先に読む** > 自分で観測を組む。コストの安い診断から順に: console → 既存のデバッグ表示 → 自作計装。

- 症状が出たら**即** `--type error` と `--type warning` の両方を見る(warning側に本質が出ることがある)。
- **コンソールが空でも信じない。** 取りこぼしが疑われるときは `editor_log_tail` でログファイルを直接読む。Editor がビジーでコンソールに届いていないだけのことがある。
- compile/reimport/recompile の後は必ず `compile_status` で `succeeded` を確認する。**コンパイルに失敗すると Editor は直前のアセンブリのまま `isCompiling` を false に戻すので、「静か」＝「成功」ではない。**
- 「エラーが消えるまで直す」= それ自体が客観的な完了判定になる。

## Calling tools

```bash
isuzu-unity-mcp call <tool>                                # no arguments
isuzu-unity-mcp call <tool> --name value --other 3         # individual arguments
isuzu-unity-mcp call <tool> --json '{"key":"value"}'       # one JSON object
isuzu-unity-mcp call <tool> --project MyGame               # when several Editors are open
isuzu-unity-mcp call <tool> --raw                          # whole envelope, not just the result
```

Values are typed automatically: `--limit 20` sends a number, `--active_only true` a boolean.
Errors print to stderr and set a non-zero exit code, so these compose in scripts.

Run `isuzu-unity-mcp tools` for the authoritative list — it comes from the Editor, so it is never
out of date with the version you are talking to.

## Execute C# code

**Always pass snippets with `--file`.**

```bash
cat > /tmp/snippet.cs <<'EOF'
var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
foreach (var l in lights) l.intensity = 2f;
Debug.Log($"adjusted {lights.Length} lights");
return lights.Length;
EOF
isuzu-unity-mcp call execute_code --file /tmp/snippet.cs
```

`--file` sends the snippet base64-encoded. Passing C# through a shell **and** a JSON encoder
loses the backslashes in string literals, and the failure surfaces as a compile error naming
generated source you never see ("Unrecognized escape sequence", "Newline in constant"). Using
`--file` removes both layers.

Namespaces already imported: `System`, `System.Collections`, `System.Collections.Generic`,
`System.Linq`, `System.Threading.Tasks`, `UnityEngine`, `UnityEditor`. Write statements only —
no class or method wrapper. `return <expr>;` surfaces a value; `Debug.Log` output is captured
separately.

Return values are serialized structurally, so returning a list gives an array. An `await`ing
snippet returns no value: the Editor will not block its main thread on an incomplete Task.

Identical snippets are compiled once and reused. Distinct ones each load an assembly that
cannot be unloaded, so a long session of one-off snippets grows the domain until a reload.

## Common tools

| Tool | Purpose |
|---|---|
| `console_read_logs --type error --limit 30` | Console entries; types: `all`, `error`, `warning`, `log` |
| `console_get_count` | Error / warning / log counts, cheap |
| `console_clear` | Clear before an action so later entries are known to come from it |
| `editor_log_tail --pattern "Shader" --lines 50` | `Editor.log` from disk; **works while the Editor is wedged** |
| `compile_status` | `isCompiling`, `succeeded`, and the error messages |
| `compile_request` | Trigger a recompile (`AssetDatabase.Refresh` alone does not) |
| `test_run --mode edit --assembly MyGame.Tests` | Start a test run; returns immediately |
| `test_results` | Counts and failures; **answers while the run holds the main thread** |

### Authoring — every one of these is a single undo step

| Tool | Purpose |
|---|---|
| `gameobject_create --primitive Cube --name Enemy --parent_path /Root` | Create; returns the `path` to address it by |
| `gameobject_delete` / `gameobject_duplicate` / `gameobject_reparent` | Undoable, so no confirmation is asked for |
| `gameobject_set_transform --object_path /Root/Enemy --json '{"position":{"y":2}}'` | Only the axes given are changed |
| `gameobject_add_component --component_type Rigidbody` / `gameobject_remove_component` | Returns the component list |
| `asset_find --type Material --folder Assets/Art --limit 20` | Then `asset_info`, `asset_move`, `asset_delete` (to the OS trash) |
| `asset_create_folder --path Assets/Art/Materials` | Creates parents too; calling it twice is not an error |
| `scene_list` / `scene_open` / `scene_save` / `scene_create` | `scene_open` refuses over unsaved changes |
| `prefab_create` / `prefab_instantiate` / `prefab_apply` | `prefab_apply` is not undoable and changes every instance |
| `build_settings` then `build_player --output_path C:/out/Game.exe` | A cold build returns a job id; poll `jobs <id>` |

### Rendering and shader debugging

| Tool | Purpose |
|---|---|
| `shader_errors` | Compilation errors. **A broken shader renders magenta and never says so** — ask after every shader edit |
| `shader_info` / `material_read` / `material_set` | What a frame is drawn from, as opposed to the shader's defaults |
| `render_pipeline_info` | The pipeline actually in force. **The quality level overrides graphics settings** |
| `render_camera_info` | View, projection and GPU projection matrices, for checking a value against a CPU replica |
| `render_compare --before a.png --after b.png` | Differences as numbers |
| `reflect_read --path "MyPipeline.Manager/ByCamera[0]/levels[2]"` | Live private state without writing a snippet |
| `gpu_readback --path "MyPipeline.Manager/pool" --format uint` | **`allZero` answers "did the pass write anything"** in one line |

### Two things that will cost you an afternoon otherwise

- **The `path` these tools take is the one `scene_browse_hierarchy` returns.** It resolves
  inactive objects, and carries an index only when a sibling name repeats: `/Canvas/Button[1]/Text`.
- **A scene edit during Play Mode succeeds and is reverted when Play Mode stops.** The response
  carries `playModeWarning` when that applies. Asset edits made at the same moment do survive.
| `scene_browse_hierarchy --name Player --limit 20` | Hierarchy; also filters `component`, `tag`, `active_only`, `max_depth` |
| `inspect_list --game_object_path Player --component_type Transform` | Discover property paths |
| `inspect_read --game_object_path Player --component_type Transform --property_path m_LocalPosition` | Read one property |
| `inspect_write ... --json '{"property_path":"m_LocalScale","value":{"x":2,"y":2,"z":2}}'` | Write one property; collapses into a single Undo step |
| `capture_screenshot --view scene --max_size 512` | base64 PNG |
| `play_mode_status` / `play_mode_play` / `play_mode_stop` | Play mode |
| `menu_execute --menu_item "File/Save"` | Invoke a menu item |
| `project_packages` / `project_assemblies` | Project metadata |

## Common workflows

### Debug: find errors, then the object they name

```bash
isuzu-unity-mcp call console_read_logs --type error --limit 10
isuzu-unity-mcp call scene_browse_hierarchy --name ObjectName
isuzu-unity-mcp call inspect_list --game_object_path ObjectName --component_type Transform
```

### Edit a script and confirm it built

```bash
isuzu-unity-mcp call compile_request
sleep 3
isuzu-unity-mcp call compile_status          # check succeeded, not just isCompiling
```

### Run the tests

```bash
isuzu-unity-mcp call test_run --mode edit --assembly MyGame.Tests
isuzu-unity-mcp call test_results            # poll; status goes running -> completed
```

`test_run` does not wait for the outcome, because the run occupies the main thread for its
whole duration. During that window `test_results` is the only tool that answers — poll it
rather than retrying `test_run`. A `status` of `interrupted` means a domain reload happened
mid-run and the outcome was lost; start the run again.

### Prove a rendering change did something

```bash
isuzu-unity-mcp call capture_screenshot --view game --save_path /tmp/before.png
# toggle the thing under test
isuzu-unity-mcp call render_compare --before /tmp/before.png --after /tmp/after.png
```

Compare rather than look. Screenshot colours are post-tonemap, so absolute values do not settle
an argument; changed-pixel counts and where they are do. Passing `save_path` keeps both images
out of the conversation entirely.

### Save a screenshot to a file

```bash
isuzu-unity-mcp call capture_screenshot --view scene --max_size 512 \
  | python -c "import sys,json,base64; d=json.load(sys.stdin); open('scene.png','wb').write(base64.b64decode(d['image']))"
```

### The Editor stopped responding

```bash
isuzu-unity-mcp health                       # queueDepth climbing with reqCount flat = wedged main thread
isuzu-unity-mcp call editor_log_tail --lines 50
isuzu-unity-mcp jobs                         # what is queued or running
```

`health`, `jobs` and `editor_log_tail` are answered off the main thread, so they keep working
when nothing else does.

## Jobs

Work slower than about three seconds returns a job id instead of a result:

```json
{"state":"running","jobId":"execute_code-3","poll":"/jobs/execute_code-3"}
```

```bash
isuzu-unity-mcp jobs execute_code-3
```

**Do not repeat the call.** The work is still running; repeating it runs it twice.

## Errors

| Message | Meaning |
|---|---|
| `No running Unity Editor found` | No Editor has a project open with the package installed |
| `Several Editors are running` | Pass `--project <name>` |
| `error [invalid_params]` | Argument missing or the value was rejected; the text says which |
| `error [tool_not_found]` | Run `isuzu-unity-mcp tools` |
| `error [unauthorized]` | The descriptor is stale; restart the Editor |

## Talking to a v2 Editor

v2 has no CLI and no authentication. Endpoints live on `127.0.0.1:27182` (scanning to 27199),
take different names, and `/read_logs` uses `count` rather than `limit`:

```bash
curl -s http://127.0.0.1:27182/health
curl -s -X POST http://127.0.0.1:27182/read_logs \
  -H "Content-Type: application/json" -d '{"count":30,"type":"error"}'
```

Note that v2 returns HTTP 504 after 10 seconds while **continuing to run the work**, so a retry
on timeout executes it twice. v3 returns a job id instead.
