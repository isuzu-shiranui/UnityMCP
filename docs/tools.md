# ツール一覧

Editor が公開するツールを、グループごとの表で説明します。[README に戻る](../README.md)

利用できるツールは接続先のパッケージ構成で変わります。Timeline と Recorder のツールは、それぞれ`com.unity.timeline`と`com.unity.recorder`があるときだけ現れます。`test_run`と`test_results`には`com.unity.test-framework`が必要です。

冪等性の列は、接続失敗時に自動リトライしてよいかを示します。組込みツールはread-onlyかつ再試行可能なものだけを`safe`としています。任意getterを呼ぶ`reflect_read`/`gpu_readback`と、ファイル出力を行える`capture_screenshot`は`unsafe`です。

Unity 6.5 以降では `instanceId` が JSON の数値ではなく文字列で返ります。64 ビットの EntityId は JSON の数値では正確に表せないためです。引数の `instance_id` は数値と文字列のどちらでも受け付けます。

実際に公開されているツールは `isuzu-unity-cli tools` で確認できます。一覧は Editor から取得するので、接続先のバージョンと必ず一致します。

## 診断（見る）

| ツール | 冪等性 | 用途 |
|---|---|---|
| `console_read_logs` | safe | コンソールのエントリを読む。`total` は絞り込みに一致した件数、`inConsole` はコンソール全体の件数、`errors` / `warnings` は絞り込みに関係なく重大度ごとの件数 |
| `console_get_count` | safe | エラー / 警告 / ログの件数 |
| `console_clear` | unsafe | コンソールをクリア |
| `editor_log_tail` | safe | `Editor.log` を直接読む（Editor が固まっていても動く） |
| `editor_dialog_list` | safe | Editor が表示中のモーダルダイアログの題名・本文・ボタンと、メインスレッドの停止時間（Editor が固まっていても動く。Windows 限定） |
| `editor_dialog_press` | unsafe | 表示中のダイアログのボタンを押してメインスレッドを再開する。`confirm: true` が必要。「Don't Save」系は未保存の作業を捨てるので、先に `editor_dialog_list` で本文を読む |
| `compile_status` | safe | コンパイル中か、直前のコンパイルが成功したか |
| `compile_request` | unsafe | 再コンパイルを要求。先にアセットの完全なリフレッシュが実行されるので、変更されたアセットのインポートが起きます。モーダルダイアログが開くこともあります |
| `test_run` | unsafe | EditMode / PlayMode テストの実行を開始 |
| `test_results` | safe | 実行中・直近のテスト結果（実行中でも読める） |
| `scene_browse_hierarchy` | safe | シーン階層の走査。`path` を返すので編集系にそのまま渡せます。絞り込んでも、一致したオブジェクトへ至る親は結果に含まれます。**一致したものの子は、その子自身が一致しない限り出ません** — 親の名前で絞ると親1つだけが返るので、その下に何個あるかは `childrenNotShown` で分かります。`missing_scripts: true` は、スクリプトが解決できないコンポーネントを持つオブジェクトだけを返します。各オブジェクトの `missingScripts` が、その件数です。ノードに無いキーは既定値です（active は true、tag は Untagged、layer は Default、欠けたスクリプトなし）。応答は必ず `snapshotId` を返し、それを `since` に渡すと木の代わりにその状態との差分が返ります。snapshot はスクリプトの再読み込みを越えないので、失効していれば `sinceExpired` を付けて木が返ります。`limit` や `offset` で一部しか返らなかった応答の snapshot は、差分の基準にできません |
| `scene_list` | safe | 開いているシーンとビルド設定のシーン |
| `inspect_read` | safe | シリアライズプロパティの読み取り。`component_type` を省くと GameObject 自身が対象です。配列は長さと要素の指定方法に加えて、中身も `elements` として返します（先頭20件まで。それ以上あるときは `elementsShown` が付きます）。要素が構造体のときは中身を返しません |
| `inspect_list` | safe | シリアライズプロパティの一覧。`component_type` を省くと、その GameObject が持つコンポーネントの一覧になります。`detail: "full"` を付ければ全コンポーネントのプロパティが1回で返るので、コンポーネントごとに呼ぶ必要はありません |
| `asset_find` | safe | 型 / 名前 / フォルダー / ラベルでアセット検索。応答は `limit` で打ち切ります。一致件数は `total` に入ります |
| `asset_info` | safe | 型・GUID・importer・ラベル。依存は `include_dependencies` を渡したときだけ返します |
| `play_mode_status` | safe | 再生中 / 一時停止中 / コンパイル中 |
| `project_assemblies` | safe | ロード済みアセンブリ一覧 |
| `project_packages` | safe | UPM パッケージ一覧 |
| `capture_screenshot` | unsafe | Game / Scene ビューや Editor パネルの画像。パネルは画面から読み取るので、Editor に重なっているものが一緒に写ります。ウィンドウを手前に出してから撮ります。`save_path` を渡すとファイルに出力します。途中のディレクトリは作成し、既存のファイルは上書きします。`focus` に写したいオブジェクトを渡すと、カメラの視野に入っていないときは撮らずに断ります |
| `job_status` | safe | 長い呼び出しが返した job id の状態と結果。記録は完了から10分で消え、**ドメインリロードを跨ぐと残りません**。そのため「終わった job」と「リロードで中断された job」は区別できず、どちらも見つからないと答えます。自分の仕事がリロードを起こす job（アセットを保存する設定変更・再コンパイル・Play Mode 開始）は必ずこうなるので、job を追わずに、変えたはずのものを読み直して確かめます |
| `animator_inspect` | safe | Animator Controller の読み取り。`layer` を省くと、パラメーターと各レイヤーの 1 行ずつの要約だけを返します。ステートは返しません。レイヤーが 20 あるコントローラーでは、ステートが数百になるためです。`layer` を渡すと、そのレイヤーのステート（モーション、速度、Write Defaults、ノード位置）と、各ステートから出る遷移とその条件、レイヤーの Any State と Entry の遷移を返します。サブステートマシンの中のステートは `マシン名/ステート名` で指します |
| `animator_audit` | safe | Editor が何も知らせてくれない問題を Animator Controller から洗い出す。何も変更しません。報告するのは、どこからも参照されていないパラメーター、モーションが空のステート、既定ステートから到達できないステート、ステートが 1 つも無いレイヤー、名前が重複したレイヤー、条件も Exit Time も無い遷移、そして 1 つのレイヤーの中で Write Defaults が食い違っているステートです |
| `definitions_list` | safe | 定義ツールの一覧と読み込みエラー |

## オーサリング（作る・変える）

1 回の呼び出しが Undo 1 操作にまとまるのは、`UndoGroup` が付いたツールです。オーサリングでは `gameobject_` で始まる 8 つ、`inspect_write`、`prefab_create`、`prefab_instantiate`、そして `animator_` で始まる編集用の 10 個の、合わせて 21 個です。描画では `material_set` です。Timeline と Recorder では `timeline_` で始まる 7 つと `recorder_add_track` です。

それ以外は Undo でまとめて戻せません。`asset_delete` は Undo ではなく OS のゴミ箱へ移動するので、通常はゴミ箱から取り出して戻します。フォルダーを渡すと、配下すべてが移動します。

`prefab_apply` は Prefab アセットを書き換えます。その Prefab の全インスタンスに影響します。戻せないので `confirm: true` を求めます。

`scene_open` / `scene_save` / `scene_create` はシーンファイルの操作で、Undo の対象外です。`menu_execute` が Undo の対象になるかどうかは、実行するメニュー項目の実装しだいです。`play_mode_` で始まる 5 つは Editor の再生状態を変えるもので、Undo とは無関係です。

`animator_` で始まる編集用のツールは Undo で戻せます。ただし影響は、目の前のシーンより広い範囲に及びます。Animator Controller はアセットです。書き込みは、そのコントローラーを使っているすべてのシーン・Prefab・キャラクターに及びます。

`.controller` ファイルは、呼び出しが返る前にディスクへ書かれます。Undo が戻すのはメモリ上のコントローラーだけで、ファイルは戻しません。次に何かが保存するまで、ファイルには Editor がもう表示しない変更が残ります。

| ツール | 冪等性 | 用途 |
|---|---|---|
| `gameobject_create` | unsafe | GameObject / プリミティブの生成。生成したオブジェクトが選択状態になります。プリミティブには Unity と同じく Collider が付きますが、`collider: false` で外した状態で作れます（見せるだけの形に、あとから外す呼び出しを足さずに済みます） |
| `gameobject_delete` | unsafe | 削除（Undo で戻せる） |
| `gameobject_duplicate` | unsafe | 複製。コピーは Prefab とのリンクを持たないただの GameObject なので `prefab_apply` は受け付けません。そのコピーが選択状態になります |
| `gameobject_reparent` | unsafe | 親子付け。ワールド位置は既定で維持します。`keep_world_position: false` ではローカル位置とローカル回転をリセットするので、親の原点に移動します |
| `gameobject_set_transform` | unsafe | 位置 / 回転 / スケール。指定した軸だけ変える |
| `gameobject_set_active` | unsafe | 有効・無効の切り替え |
| `gameobject_add_component` | unsafe | コンポーネント追加 |
| `gameobject_remove_component` | unsafe | コンポーネント削除。基底型でも一致するので、`Renderer` が MeshRenderer に当たります。同じ型が複数あるときは `index` で選びます |
| `inspect_write` | unsafe | シリアライズプロパティの書き込み。`component_type` を省くと GameObject 自身が対象です |
| `asset_create_folder` | unsafe | フォルダー作成（親も作る、冪等） |
| `asset_move` | unsafe | 移動・リネーム（GUID を維持） |
| `asset_delete` | unsafe | 削除。OS のゴミ箱へ移動するので、通常はそこから戻せます。フォルダーを渡すと配下すべてが対象です |
| `asset_reimport` | unsafe | 再インポート |
| `asset_export_package` | unsafe | 指定したアセットを `.unitypackage` に書き出します。`.meta` ごと入るので、読み込み直すと GUID が戻り、そのアセットを指していた参照も残ります。マテリアルの一括変換やスプライトの再スライスのように参照を壊しうる操作の前に、退避を取る用途で使えます。書き出し先はプロジェクトの外なので `destination` は必須で、既にファイルがあるときは `overwrite` を渡さない限り残します。書き出しは一時ファイルに行ってから置き換えるので、失敗したときは前のファイルがそのまま残ります。`include_dependencies` は参照グラフをすべて辿るため、キャラクター1体でプロジェクトの大半に届くことがあります。応答は実際に書けたバイト数を返します |
| `scene_open` | unsafe | シーンを開く（未保存があれば拒否） |
| `scene_save` | unsafe | 保存。`path` を渡すと Save As になります。コピーを書き出すのではなく、開いているシーンがそのパスに切り替わります |
| `scene_create` | unsafe | 新規シーン |
| `prefab_create` | unsafe | シーンオブジェクトを Prefab 化 |
| `prefab_instantiate` | unsafe | Prefab をシーンに配置。生成したインスタンスが選択状態になります |
| `prefab_apply` | unsafe | インスタンスのオーバーライドを Prefab へ適用。`confirm: true` が必要。Undo で戻せず、その Prefab の全インスタンスに及びます |
| `menu_execute` | unsafe | メニュー項目の実行 |
| `play_mode_play` | unsafe | 再生開始。Enter Play Mode Settings で Reload Domain を無効にしていなければ、ドメインリロードで接続が一瞬切れます |
| `play_mode_stop` | unsafe | 停止。こちらも Reload Domain が有効なら接続が一瞬切れます |
| `play_mode_pause` | unsafe | 一時停止。Play Mode の外では、その旨のエラーで失敗します |
| `play_mode_unpause` | unsafe | 一時停止解除。Play Mode の外では同じくエラーで失敗します |
| `play_mode_step` | unsafe | フレームを進める。必要なら先に一時停止します。`count` で 1〜1000 フレームをまとめて進められ、応答には到達フレームと経過秒数が入ります。`paths` を渡すと、進めた直後のそのフレームのまま `reflect_read` と同じ形で読めます。`animators` にオブジェクトのパスを渡すと、そのフレームでの Animator の状態（各レイヤーの状態名・進行度・遷移中か・パラメータの値）が同じ応答に入ります。状態の進行度はプロパティではないので `paths` では読めません。Play Mode の外では同じくエラーで失敗し、測定値は付きません |
| `animator_create` | unsafe | Animator Controller のアセットを作り、`object_path` を渡せばその GameObject に付けます（Animator が無ければ足します）。編集する `animator_*` は11本あるのに作る手段が無く、空のプロジェクトからは11本すべてに手が届きませんでした。作られるのは Base Layer が1つだけの空の Controller で、中身は `animator_add_state` と `animator_add_parameter` で埋めます |
| `animation_clip_create` | unsafe | 空の AnimationClip アセットを作ります。状態にはモーションが要り、他に作る手段がありませんでした。カーブは持ちません。クリップの長さはカーブから決まるので、ここで設定する長さはありません。カーブを入れるのは `animation_clip_write_curves` です。ループはカーブではなく設定なので、ここにあります |
| `animation_clip_write_curves` | unsafe | AnimationClip に float カーブを書きます。1本ずつではなく複数まとめて渡します（揺れ・回り込み・うなずきで3本になるため）。各カーブは対象の子パス・コンポーネント型・シリアライズされたプロパティ名・キー列で指定します。接線は Animation ウィンドウの Auto と同じ形に均されます。1本でも解決できなければ何も書きません。長さはキーから決まるので応答で返します |
| `animator_add_layer` | unsafe | 空のステートマシンを持つレイヤーを追加。Unity では `weight` の既定が 0 なので、無効にした状態で始めるのでなければ 1 を渡します |
| `animator_remove_layer` | unsafe | レイヤーと、その中のステート・遷移・ステートマシンを削除。後ろのレイヤーは番号が 1 つずつ繰り上がります |
| `animator_add_state` | unsafe | ステートを追加。空のステートマシンに最初に入れたステートが既定ステートになります。名前は Unity が一意にするので、実際に付いた名前を返します |
| `animator_remove_state` | unsafe | ステートを削除。そのステートへ入る遷移も Unity が一緒に消すので、その本数を返します |
| `animator_set_state` | unsafe | 1 つのステートのモーション・速度・Write Defaults・タグ・ノード位置を変更。渡した引数だけが変わります |
| `animator_set_write_defaults` | unsafe | レイヤー全体、またはコントローラー全体の Write Defaults をまとめて設定。`animator_audit` が報告する食い違いは、これで直します |
| `animator_add_transition` | unsafe | 遷移を追加。`from_state` を省くと Any State からの遷移になります。条件は `{parameter, mode, threshold}` のオブジェクトの配列で渡します。パラメーターの型が答えられないモードは拒否します。黙って一度も成立しない遷移を作ることはありません |
| `animator_remove_transition` | unsafe | 遷移を番号で削除。削除すると後ろの番号がずれるので、続けて消すときは毎回読み直します |
| `animator_add_parameter` | unsafe | パラメーターを追加。すでにある名前は、勝手な別名で 2 つ目を作らずに拒否します |
| `animator_remove_parameter` | unsafe | パラメーターを削除。その名前を指したままの条件を Unity は消しません。その条件は二度と成立しなくなるので、残った条件を返します |

## 描画・シェーダーのデバッグ

| ツール | 冪等性 | 用途 |
|---|---|---|
| `render_compare` | safe | 2枚のキャプチャの差を数値で返す（差分画素数・平均/最大デルタ・矩形・グリッド） |
| `render_pipeline_info` | safe | 実効 RP、色空間、Graphics API、品質レベル。Quality 側の RP 上書きも併記 |
| `render_camera_info` | safe | カメラと view / projection / GPU projection 行列 |
| `shader_errors` | safe | シェーダーのコンパイルエラー。シェーダーはエラーを出さずに magenta 表示になるので、明示的に確認する必要があります。パスを省いた一括チェックの対象は Assets 配下だけです。パッケージ内のシェーダーは含みません |
| `shader_info` | safe | パス数、プロパティ、キーワード空間、render queue |
| `material_read` | safe | マテリアルの現在値・有効キーワード・render queue。`path` でマテリアルアセットを指定できます。`object_path` でシーン上の GameObject を指定すると、その Renderer が描画に使っているマテリアルをスロットごとに返します。全スロットを読むときは、値ではなくプロパティ数だけを返します。Renderer 1 つに数十のマテリアルが設定され、その 1 つずつが数百のプロパティを持つことがあるためです。値が必要なときは `slot` で 1 つに絞ります。`property` に文字列を渡すと、名前にそれを含むプロパティだけを返します。lilToon のような数百のプロパティを持つシェーダーでは、1 つ確認するのに応答が 32,743 バイトから 653 バイトになります。シェーダーが見つからない・サポートされていない場合は、その理由を `shaderProblem` に入れます（magenta になる原因はこれです）。アセットではないマテリアルも、`path` が null の項目として省かずに返します。複数のオブジェクトをまとめて読むときは `object_paths` に最大50件渡します。`group` を付けると、同じ内容のマテリアルを1つにまとめ、それを使っているオブジェクトのパスを並べて返します（「この300個は同じマテリアルか」を訊くのに300個分の全文は要らないため。実測 104,142バイト → 3,213バイト）。まとめる基準は内容で、個体でも名前でもありません（同じ設定なら別名でも1つにまとめ、名前は `names` に並べて返します）。読めなかったものはその項目だけが `error` になり、他は返ります。300個を1件ずつ読むと300回・744KBかかりました |
| `material_create` | unsafe | マテリアルアセットを作ります。`shader` を省くと、そのプロジェクトのレンダーパイプラインが描くシェーダーを使います。URP のプロジェクトに `Standard` を当てて紫にしないためです。`Assets/` 配下の足りないフォルダは作り、`.mat` は省いても付きます。同じ場所に既にあるときは拒否し、`overwrite` で置き換えます。値の設定は `material_set`、Renderer への割り当ては `inspect_write` です |
| `material_set` | unsafe | プロパティ / キーワード / render queue を変更。色やベクトルは `[x,y,z,w]` の配列でも渡せます。`object_path` と `slot` で Renderer 経由でも指定できます。その場合は共有マテリアルを書き換えます。Renderer ごとのコピーは作らないので、そのマテリアルを使っている他の Renderer も変わります。アセットの .mat ファイルはその場でディスクに書かれます。Undo してもファイルの内容は戻りません |
| `scene_settings` | unsafe | シーンが GameObject の外に持つ照明と環境。skybox、環境光、フォグ、反射の 20 項目を `property` / `value` で読み書きします。これらは `RenderSettings` にあり、シーン全体で 1 つでパスを持たないので `inspect_write` からは届きません。`sun` にはシーン上の Light をパスで、`skybox` と `customReflection` にはアセットパスを渡します。空文字を渡すと参照を外します。`customReflection` は `defaultReflectionMode` を `Custom` にしないと反射に使われません。変更は Undo でき、シーンを dirty にするので `scene_save` で保存します |
| `render_stats` | safe | 最後に描かれた1フレームの費用。draw call、SetPass、三角形、頂点、影を落とす数と、バッチングが何をどれだけまとめたか。安くする変更の前後で取ると、主張が形からの推測でなく実測になります。開いている全シーンを含む Game ビュー全体の数字で、特定のオブジェクトのものではありません。Game ビューが閉じている・隠れていると値は古いままなので、変更後は開いてから読み直します。**複数フレーム進めた直後の値は1フレーム分とは限りません** — 5フレーム進めて読むと draw call が 80、1フレームなら 16 でした。比べる2つの値は、同じフレーム数だけ進めてから取ること。1フレーム分の数字が要るなら1フレームだけ進めて読みます。frameTime と renderTime は Unity 側が非推奨としていて Editor では異常値を返すため、載せません |
| `gpu_readback` | unsafe | バッファ / テクスチャを読み戻し、統計（min / max / mean / zeroCount / allZero / histogram）と `samples` 件の生の値を返す。テクスチャは 32 bit の 1 チャンネルとして読むので、数値は赤成分だけを表します |

## Timeline（動画制作・ライブ）

`com.unity.timeline` がある時だけ現れます。

| ツール | 冪等性 | 用途 |
|---|---|---|
| `timeline_inspect` | safe | トラック / クリップ / バインディングと director の時刻。ControlTrack を辿って子 Timeline を再帰展開する（ライブの多層構造向け）。`track` はグループの中のトラックにも当たるので、複数返ることがあります |
| `timeline_evaluate` | unsafe | director を時刻 / フレームに評価（Play mode 不要）。`capture_screenshot` と組んで1コマ検証。評価した値はシーンオブジェクトに書き込まれ、Undo では戻せません |
| `timeline_edit_clip` | unsafe | 1クリップの start / duration / 表示名 / ease / blend / 速度。要求値でなく実効値を返します。効かなかった引数は、理由付きで `ignored` に入れます |
| `timeline_shift_clips` | unsafe | リップル編集。指定時刻以降をまとめてずらす。0秒を割る場合は1つも動かさず拒否 |
| `timeline_set_track` | unsafe | mute / lock / リネーム / バインディング。トラックの型に必要なコンポーネントを自前で解決（Animation なら Animator）。バインディングは PlayableDirector に入るシーン側の情報なので `.playable` は書き換わらず、`scene_save` が要る。mute / lock / リネームは資産側なので `.playable` が保存される |
| `timeline_delete` | unsafe | トラック / クリップの削除。グループは配下ごと。Undo 可能なので確認を求めない |
| `timeline_create` | unsafe | Timeline アセットの新規作成。`object_path` か `instance_id` を渡したときだけ、その GameObject に PlayableDirector を付けます。トラック追加の前提となる唯一の入口。アセットを書き込む際に、プロジェクト内の未保存アセットをすべて保存します |
| `timeline_create_track` | unsafe | トラック追加（activation / animation / audio / control / group / playable / signal）。グループへのネストとバインド同時指定可 |
| `timeline_create_clip` | unsafe | クリップ追加。`control_source` で ControlTrack のネストを一発で構成、`animation_clip` で AnimationClip を指定 |

編集系は、書き込んだ後に読み直した実効値を返します。Timeline の setter は、クリップ型が対応していない値をエラーを出さずに無視します。Activation クリップの速度が、その例です。要求値をそのまま返すと、変更されたように見えてしまうためです。

作成系は、対象の Timeline がまだアセットでなければ着手前に拒否します。Timeline はその状態だとトラックをメモリ上にしか作らず、後から永続化する公開 API が無いためです。

`timeline_evaluate` は、評価した値をバインド先のシーンオブジェクトに書き込んだまま残します。そのためシーンが変更済みになり、Undo でも戻せません。再生中に呼ぶと、director は一時停止したままになります。Timeline のツールに、再開する手段はありません。

## Recorder（書き出し）

`com.unity.recorder` と `com.unity.timeline` が揃っている時だけ現れます。

| ツール | 冪等性 | 用途 |
|---|---|---|
| `recorder_add_track` | unsafe | Timeline に Recorder トラックを追加し、director を再生するだけで録画にする。mp4 / webm / mov と png / jpeg / exr、入力は game view / カメラ / RenderTexture、解像度指定可。トラックを追加する際に、プロジェクト内の未保存アセットをすべて保存します |
| `recorder_list` | safe | その Timeline が何をどこへ書き出すか（形式・出力先・有効/無効） |

Recorder は Timeline のトラックとして扱います。フレームレートが Timeline 側から決まるので、録画と Timeline の時刻が一致します。Recorder API のバージョン差の影響も受けにくくなります。`output_path` を省くと、`Assets` と同階層の `Recording` フォルダーに Timeline 名で書き出します。

## 内部状態とコード実行

| ツール | 冪等性 | 用途 |
|---|---|---|
| `search_query` | safe | Editor の検索を1回の問い合わせで使います。`h:` はシーン、`p:` はプロジェクト、`t:` は型、`ref:` は参照、`p(name)` は serialize されたプロパティの比較です。`h: t:meshrenderer p(castshadows)!="Off"` のような複合条件が1回で書けます。シーンの結果には他のツールが取る `objectPath` が付きます。**Unity のクエリ言語なので、理解されない語は失敗せず絞り込みが効かないだけ**です。件数を期待値と突き合わせてください |
| `asset_broken_references` | safe | 参照先を失ったフィールドを探します。**id は残っていて中身が null** のものだけを挙げるので、もともと未設定のフィールドとは区別されます。スクリプトが解決できないコンポーネントも一緒に報告します。`scope` は `scene` / `assets` / `both`。アセット走査は `max_assets` と `max_seconds` で打ち切り、どちらで止まったかを返します |
| `editor_state` | safe | Editor がいま応答できるかを、他のツールが要るメインスレッドを使わずに読みます。取り込み・コンパイル・モーダルダイアログがメインスレッドを掴んでいる間は、**それを報告するはずのツールも含めて全部が列に積まれます**。`queueDepth` が伸びて `reqCount` が動かないのが詰まりの形で、ダイアログが掴んでいればその名前も出ます。呼び出しが返ってこないときに最初に聞くもの |
| `animator_set_parameter` | unsafe | 実行中の Animator のパラメータを設定します（Bool / Trigger / Int / Float）。**Play mode 限定**で、停止中は「既定値は controller 側にある」と案内して拒否します。無い名前を渡すと、現存するパラメータの一覧を添えて拒否します |
| `reflect_read` | unsafe | 型とメンバーパスで private を含む live な状態を読む。`Renderer.material` のような getter は読むだけで共有アセットをインスタンス化するので、`sharedMaterial` を読んでください。複数まとめて読むなら `paths`（最大50本）。結果は要求したパスをキーにした辞書で返り、読めなかった1本はその要素だけが `error` になります。Collider の `bounds` を読むときは物理へ同期してから返します |
| `reflect_find_type` | safe | ロード済み型の検索 |
| `execute_code` | unsafe | C# スニペットのコンパイル・実行（専用ツールで届かないときの最後の手段） |

## 入力（Editor の GUI 経路に合成する）

詳細は [Editor 入力の合成・記録・再生](input-tools.md) を参照してください。

| ツール | 冪等性 | 用途 |
|---|---|---|
| `input_pointer` | unsafe | マウスの移動・クリック・ドラッグ・スクロールを合成 |
| `input_key` | unsafe | キー入力を合成 |
| `input_record` | unsafe | 人間の操作を記録して JSON に書き出す |
| `input_replay` | unsafe | 記録した入力を送り直す。`then_capture` でキャプチャまで一続きにできる |

## ビルド

| ツール | 冪等性 | 用途 |
|---|---|---|
| `build_settings` | safe | 実効ビルドターゲット、ビルドに入るシーン、モジュールの有無 |
| `build_player` | unsafe | プレイヤービルド。`syncWaitMs`（既定 3 秒）を超えれば job になります |
| `build_switch_target` | unsafe | ビルドターゲット切替（再インポートを伴う） |
| `project_settings` | unsafe | `ProjectSettings/` 配下の設定を読み書き。`section` は player / quality / graphics / tags / physics / physics2d / time / audio / input / editor / build / memory / navigation / presets の 14 種です。`property` にはシリアライズ名（`m_Gravity`）を渡します。丸ごとの名前に当たらなければ部分一致で検索し、`m_QualitySettings.Array.data[0].shadowDistance` のように配列や構造体の中まで降りて探します。一覧は最上位だけで、200 件で打ち切ると `truncated` が付きます。書き込みは Undo でき、ディスクへは `AssetDatabase.SaveAssets` で届きます。**この保存はプロジェクト内の未保存アセットをすべて書き出します**。設定ファイル1つだけを書く手段は Unity 側にありません。応答にもその旨が入ります。`properties` に配列を渡すとまとめて読み、`values` にオブジェクトを渡すとまとめて書きます。**まとめ書きは1つでもパスが見つからなければ何も書きません** — レイヤーの衝突行列のように、片方だけ書くと行列が自分と矛盾するためです |

## 公開条件と制限

- 画面から読み取るキャプチャは Windows 限定です。対象は `inspector` / `hierarchy` / `project` / `console` / `window:<title>` と、名前が `_window` で終わるものすべてです。手前に別のアプリケーションがあると `window_occluded` で拒否します。同じ Editor の浮いたウィンドウが重なっている場合は検出できません。`game` と `scene` は全プラットフォームで動きます。パネルをキャプチャするとそのウィンドウが手前に出るので、Editor を見ている人の画面が切り替わります。
- `test_run` / `test_results` は `com.unity.test-framework` が入っているときだけ現れます。Unity の既定パッケージなので、通常は入っています。専用のアセンブリに分けて、test-framework パッケージの有無で制約しています。そのため無い環境では、この 2 つが一覧に出ないだけです。パッケージ全体は変わらず動きます。
- Unity Hub の操作（Editor やモジュールのインストール）は提供しません。Hub 自身に CLI があります。未インストールのビルドターゲットを指定したときは、実行すべき Hub のコマンドを返します。
- MCP の URL に `?group=diagnostics,authoring` のようにグループを付けると、`tools/list` がそのグループだけを返します。グループは `diagnostics` / `authoring` / `rendering` / `timeline` / `build` / `code` / `input` の 7 つです。CLI では `isuzu-unity-cli tools --group <name>` で同じ絞り込みができます。呼び出し自体は、絞り込みの影響を受けません。

## 注意点

- 編集系ツールが受け取る `object_path` は、`scene_browse_hierarchy` が返すものです。非アクティブなオブジェクトも解決できます。兄弟に同名がいるときだけ、`/Canvas/Button[1]/Text` と添字が付きます。

  Prefab を開いている間は例外です。`scene_browse_hierarchy` が返すのは、背後のシーンのパスのままです。`gameobject_` と `inspect_` のツールは Prefab の中身を見るので、そのパスを解決できません。
- Play Mode 中のシーン編集は、成功したように見えて終了時に破棄されます。その状況では応答に `playModeWarning` が付きます。アセットの変更は残るので、そちらには付きません。
- 削除は確認を求めません。その代わり、通常は元に戻せます。確認を求めるのは `prefab_apply` と `editor_dialog_press` の 2 つです。どちらも元に戻せないためです。

  アセットは OS のゴミ箱へ移動し、フォルダーを渡した場合は配下すべてが移動します。GameObject は Undo で戻せます。ただし未保存シーンへの上書きは拒否します。これだけは Undo でも戻せないためです。
- `gameobject_set_transform` は RectTransform も動かしますが、書き込むのは `localPosition` です。そのため `m_AnchoredPosition` の値は 1 回分遅れて追いつきます。書き込んだ直後に `inspect_read` で読み直すと、実際には動いているのに古い値が返ります。読み直して確かめるなら `reflect_read` を使うか、1 呼び出し置いてください。
- `execute_code` のコードはメソッドの本体に置かれるので、using ディレクティブを書くとコンパイルエラーになります。`System`、`System.Collections`、`System.Collections.Generic`、`System.Linq`、`System.Threading.Tasks`、`UnityEngine`、`UnityEditor` は最初から取り込まれています。それ以外の型は `UnityEngine.Rendering.Volume` のように完全な形で書いてください。
- `execute_code` は Undo の対象になりません。オーサリングは専用ツールを使ってください。
