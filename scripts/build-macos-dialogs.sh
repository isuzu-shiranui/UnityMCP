#!/bin/sh
set -eu
repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
output="$repo_dir/jp.shiranui-isuzu.unity-mcp/Editor/Plugins/libUnityMcpDialogs.dylib"
xcrun clang++ -std=c++17 -fobjc-arc -fblocks -dynamiclib -fvisibility=hidden \
  -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 \
  -framework AppKit -framework CoreFoundation \
  -Werror -Wall -Wextra "$repo_dir/native/macos/EditorDialogs.mm" \
  -install_name @rpath/libUnityMcpDialogs.dylib -o "$output"
codesign --force --sign - "$output"
lipo -info "$output"
