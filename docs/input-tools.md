# Editor 入力の合成・記録・再生

`input_pointer` / `input_key` / `input_record` / `input_replay` の 4 つの入力ツールを説明します。[README に戻る](../README.md)

これらのツールは、人がマウスやキーボードで操作したときと同じ経路で Editor にイベントを送ります。値を直接書き込む経路では再現しない不具合を、この経路で再現し直せます。右ドラッグの最中だけ起きる描画崩れが、その例です。

ウィンドウの指定は `capture_screenshot` と共通です。`scene_view_window`（別名 `scene`）、`game_view_window`（別名 `game`）、`inspector`、`hierarchy`、`project`、`console`、`window:<タイトルの部分一致>` が使えます。

## input_pointer

`input_pointer(view, action, from, to, ...)` の `action` は `move` / `down` / `up` / `click` / `drag` / `scroll` です。

座標は、ウィンドウのコンテンツ領域の左上を原点とする point です。`normalized: true` の 0〜1 と、座標が範囲内かの判定は、タブバーを含むウィンドウ全体を基準にします。そのため正規化の 0.5 は、コンテンツの中心よりわずかに下にずれます。位置を厳密に決めたいときは point で渡してください。

ドラッグは `steps` と `frames_per_step` で、Editor の複数フレームに分けて送れます。時間経過に反応する処理は、複数フレームに分けて送られること自体を検知して動きます。1 フレームにまとめて送ると再現しません。

右ドラッグは FPS Look、Alt+左ドラッグは Orbit です。送信前にフォーカスを持っていたウィンドウは、既定で戻します。`restore_focus: false` で戻しません。この引数は `input_key` と `input_replay` にもあります。

## input_key

`input_key(view, key, action, modifiers, character)` の `action` は `press` / `down` / `up` です。`press` は KeyDown、文字付き KeyDown、KeyUp の順に送ります。

## input_record

`input_record(action, view, name, include_moves)` の `action` は `start` / `stop` / `status` です。人が操作した内容を記録し、`stop` でファイルに書き出します。書き出し先は `%LOCALAPPDATA%\UnityMCP\recordings\<projectHash>\<name>.json` です。macOS と Linux では `~/.local/share/UnityMCP/recordings/<projectHash>/<name>.json` になります。`<projectHash>` は Unity プロジェクトのパスから作るので、同じ `name` でもプロジェクトが違えば別のファイルになります。

Scene View は描画コールバックから記録するので、ドラッグ全体が残ります。他のウィンドウは UI パネルの経路から記録します。そのため IMGUI のコントロールがマウスを捕捉した後のドラッグは、最初のフレームしか記録できません。クリック、ホイール、キー入力には影響しません。

`name` に含まれる英数字と `_` / `-` 以外の文字は、拒否されずに `_` へ置き換えられます。実際に付いた名前は応答の `path` で確認してください。拒否されるのは `CON` や `NUL`、`COM1` のような Windows の予約デバイス名だけです。

`start` の結果には、`path` に加えて `contentOffset`、`pixelsPerPoint`、`windowSize` が入ります。スクリーンショットの画素座標を、記録前に point へ換算できます。

## input_replay

`input_replay(name, path, view, speed, loop_count, repaint_each_frame, then_capture, capture_path)` は、記録した入力を送り直します。`view` を省くと記録時のウィンドウに送り、ウィンドウの型が違えば `view_mismatch` で拒否します。

`then_capture` に `scene` などを渡すと、再生の最後に `capture_screenshot` を呼び、結果の `capture` に入れます。既定では画像そのものが入ります。`capture_path` を渡すと PNG をそのパスに書き出し、`capture.path` がそのファイルを指します。`render_compare` にはこのパスを渡します。

## 典型的な使い方

4 段で使います。人のドラッグを 1 回だけ記録します。修正を当てた状態で同じ入力を再生します。`then_capture` でキャプチャします。`render_compare` で修正前後を比較します。

後半の 3 呼び出しは、`sequence` [定義ツール](defined-tools.md)として 1 つの名前にまとめられます。

## 制約

制約は 3 つあります。ウィンドウは表示されている必要があり、同じ DockArea でタブの裏に隠れている場合は `window_not_active` で拒否されます。イベントは OS の入力ではなく、Editor 自身の GUI 経路を通ります。長い再生は job id を返すので、`job_status` で結果を取ります。
