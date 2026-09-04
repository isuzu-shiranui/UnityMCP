# 定義ツール

C# をコンパイルせずに JSON ファイル 1 つでツールを追加する仕組みを説明します。[README に戻る](../README.md)

読み取りの集合、C# ファイル、既存ツールの連鎖であれば、JSON ファイル 1 つでツールを追加できます。

短く固定したツール名を使えば、同じリフレクションパスやスニペットを毎ターンのプロンプトに積まずに済みます。プロンプトキャッシュが効いたまま、コンテキストも小さく保てます。

## 置き場所

置き場所は 2 か所です。Windows では `%LOCALAPPDATA%\UnityMCP\tools\<projectHash>\` がプロジェクト固有、`%LOCALAPPDATA%\UnityMCP\tools\shared\` が全プロジェクト共通です。macOS / Linux では `~/.local/share/UnityMCP/tools/<projectHash>/` と `~/.local/share/UnityMCP/tools/shared/` になります。

`<projectHash>` は、プロジェクトの Assets フォルダーのパス（`Application.dataPath`）の SHA-256 から作った 16 桁の 16 進文字列です。descriptor ファイル名と同じ値です。自分で計算する必要はありません。`/health` の `definitionsDir` と `sharedDefinitionsDir` が、それぞれのディレクトリのフルパスを返します。

どちらのディレクトリでもファイル名は問いません。置かれた JSON ファイルはすべて読み込まれます。同じツール名が両方にあるときだけ、プロジェクト固有側が優先されます。ファイル名が同じでもツール名が違えば、両方とも読み込まれます。

1 ファイルが 1 ツールに対応します。

種類は `probe` / `script` / `sequence` の 3 つです。

## probe

リフレクション読み取りの集合です。

```json
{
  "name": "camera_probe",
  "description": "Scene View カメラの transform と、選択中オブジェクトの位置を読む。",
  "kind": "probe",
  "reads": [
    { "id": "camera", "path": "@sceneview:camera/transform/position" },
    { "id": "selected", "path": "@selection/transform/position" }
  ],
  "mode": "changes"
}
```

`path` の先頭セグメントには、通常の型名に加えてルート記法が使えます。使えるのは `@type:Ns.Type`、`@scene:/Canvas/Button[1]`、`@id:<instanceId>`、`@selection`、`@sceneview:camera` です。`@type:` を付けずに裸の型名を書いても、従来どおり通ります。`@scene:` はシーン階層のパスで、次のセグメントがコンポーネント型名なら、そのコンポーネントに解決します。

先頭セグメントより後は、`reflect_read` と同じ規則でメンバーをたどります。

`{input}` は呼び出し時の文字列置換です。置換されるのは `reads[].path` と `steps[].arguments` の文字列値だけです。それ以外のフィールドに書いても置換されず、エラーにもなりません。

`mode` が `changes` のときは、前回呼び出しからの差分だけを返します。初回とドメインリロード直後、そして定義ファイルの変更後は、全件を `baseline: true` 付きで返します。

結果の形は `{reads: {id: {path, type, value}}, mode, changed: [id, …], baseline?: true}` です。読めなかった read は、その id だけ `{path, error}` になります。選択が無い場合やオブジェクトが消えた場合が、これに当たります。他の read は通常どおり返ります。

## script

C# ファイル 1 本です。

```json
{
  "name": "light_bump",
  "description": "シーン内の Light の intensity を factor 倍にする。",
  "kind": "script",
  "file": "light_bump.cs",
  "inputs": {
    "factor": { "type": "number", "description": "intensity に掛ける倍率。", "default": 1.5 }
  }
}
```

`file` は絶対パスか、定義ファイルからの相対パスです。スニペットは引数を `JObject args` として受け取ります。

呼び出しのたびに `.cs` を読んで、ラップ済みソースをハッシュします。そのため `.cs` を直接編集すれば、定義の再読み込みなしで次の呼び出しへ反映されます。

内容の異なるスクリプトは、それぞれ新しいアセンブリとしてコンパイルされます。`execute_code` と同じく、次のドメインリロードまでアンロードされません。編集を繰り返すたびに、アセンブリが増えます。

`.cs` にコンパイルエラーがあると、呼び出しは `script_compile_error`（HTTP 400）で失敗します。そのときエラー内容を返します。

## sequence

既存ツールの連鎖です。

```json
{
  "name": "look_check",
  "description": "記録済みドラッグを再生し、Scene View をキャプチャして基準画像と比較する。",
  "kind": "sequence",
  "steps": [
    { "id": "replay", "tool": "input_replay", "arguments": { "name": "look", "then_capture": "scene", "capture_path": "Temp/look_after.png" } },
    { "id": "compare", "tool": "render_compare", "arguments": { "before": "baseline.png", "after": "{{replay.capture.path}}" } }
  ]
}
```

`steps[].arguments` の文字列では、`{input}` が入力の文字列置換になります。文字列全体が `{{stepId.json.path}}` の形なら、先行ステップの結果からその位置のトークンをそのまま差し込みます。参照できるのは先行ステップだけです。それ以外を参照すると、ロード時にエラーになります。

ステップが失敗すると、そこで停止します。ただし `continue_on_error: true` を付けたステップは続行します。

destructive なステップを 1 つでも含む `sequence` は、それ自体が destructive として扱われます。スキーマには `confirm` / `dry_run` が現れます。呼び出し側の `confirm` は各ステップへ転送されます。そのような `sequence` に `destructive: false` を書くと、ロード時にエラーになります。

`input_replay` や、複数フレームにまたがる `input_pointer` のようなステップも含められます。結果は、それらが終わるまで待ってから返ります。

ステップから別の `sequence` を呼ぶこともできます。ただし、互いを参照し合う 2 つの `sequence` はロード時に拒否されます。

結果は `{steps: [{id, tool, ok, result|error}, …]}` です。記録した入力の再生・キャプチャ・比較を 1 つの名前にまとめるのが、この種類の狙いです。

## 共通フィールド

| フィールド | 既定値 | 意味 |
|---|---|---|
| `name` / `description` / `kind` | 必須 | `kind` は `probe` / `script` / `sequence` |
| `group` | 名前の prefix から推定 | 既知のグループ名以外はエラー。`definitions_` prefix は `diagnostics` に対応します |
| `idempotency` | `probe` は `safe`、`script` / `sequence` は `unsafe` | 属性ツールと同じ意味 |
| `mainThread` | `true` | `false` にできるのは Unity API を触らない場合だけ。`probe` に `false` を指定するとロード時にエラー |
| `destructive` | `false`（destructive なステップを含む `sequence` は `true`） | `true` なら `confirm` / `dry_run` が属性ツールと同じ形で注入される。destructive なステップを含む `sequence` に `false` を書くとロード時にエラー |
| `undoGroup` | なし | `sequence` にだけ指定でき、`mainThread: true` が前提 |
| `alwaysLoad` | `false` | ツール検索の裏に隠さず常にロード済みにする |
| `maxResultSizeChars` | `0`（クライアントの既定のまま） | 結果テキストをファイルに書き出すしきい値を上げる |
| `inputs` | なし | 名前 → `{type, description, required?, default?, enum?}`。`type` は `string` / `integer` / `number` / `boolean` / `object` / `array` |
| `examples` | なし | 引数オブジェクトの実例（JSON オブジェクトか JSON 文字列の配列） |

未知のキーは、トップレベルでも `inputs` / `reads` / `steps` の各要素でもエラーになります。誤字を黙って通さないためです。`script` の `file` が存在しない場合も、ロード時にエラーになります。

`inputs` に宣言した `type` と `enum` は、呼び出し時に検査されます。合わない引数は `invalid_params` で拒否されます。

## 再読み込み

定義ファイルは監視されています。変更するとカタログが再構築されます。

すでに接続済みの MCP クライアントには `tools/list_changed` が送られないので、再接続が必要です。`GET /tools?refresh=1` を使えば、強制的に再構築できます。

`definitions_list` ツールは、何が読み込まれ、何がなぜ読み込まれなかったかを返します。
