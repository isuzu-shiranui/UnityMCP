#!/bin/sh
set -eu
repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test_dir=$(mktemp -d "${TMPDIR:-/tmp}/unity-mcp-native-tests.XXXXXX")
xcrun clang++ -std=c++17 -fobjc-arc -fblocks \
  -framework AppKit -framework CoreFoundation \
  "$repo_dir/native/macos/EditorDialogsTests.mm" \
  -L "$repo_dir/jp.shiranui-isuzu.unity-mcp/Editor/Plugins" -lUnityMcpDialogs \
  -Wl,-rpath,"$repo_dir/jp.shiranui-isuzu.unity-mcp/Editor/Plugins" \
  -o "$test_dir/dialog-tests"
"$test_dir/dialog-tests"
