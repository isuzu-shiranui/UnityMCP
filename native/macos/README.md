# macOS blocked-editor dialogs

The Editor-only `libUnityMcpDialogs.dylib` implements native dialog inspection and
response-button actions for the same Unity process. It uses AppKit on the main
Cocoa run loop, including `NSModalPanelRunLoopMode`, rather than queueing work on
Unity's blocked update dispatcher. No Accessibility grant, external automation,
managed callback from native code, or scene changes are involved.

Supported: native modal windows, attached sheets, static labels, and enabled
bordered response buttons. Checkboxes, editable fields, custom Unity UI, and other
applications are not exposed. Handles are opaque and checked again before a press;
changed dialog text/buttons invalidate the handle. Duplicate button labels fail
closed. Windows retains its existing implementation; Linux remains unsupported.

Inspection waits at most 750 ms, and a press at most 3 seconds. Single-flight
scheduling bounds queued work if Cocoa itself freezes. An unstarted timed-out
request is cancelled before the loop resumes. A started but unfinished action is
reported as uncertain (`dialog_action_pending`); callers must not retry blindly.
View traversal is bounded and fails closed if its limits are exceeded.

## Build and test

Requires macOS and Xcode Command Line Tools. From the repository root:

```sh
sh scripts/build-macos-dialogs.sh
sh scripts/test-macos-dialogs.sh
```

The build produces an ad-hoc-signed universal arm64/x86_64 dylib, targeting macOS
11 or newer. Its checked-in Unity importer metadata enables it only in the macOS
Editor, never in players. Tests require a logged-in graphical desktop and briefly
open harmless alerts. They test worker-thread access while the main thread is in
a modal loop, Unicode, response-button filtering, exact matching, stale handles,
disabled and ambiguous buttons, content changes, bounded pending requests, and
cancellation of a queued press during a deliberate main-loop stall.

After rebuilding an already loaded native plugin, restart only the disposable
test Editor before integration testing; native libraries cannot be hot-reloaded.
In an isolated interactive Unity project, invoke `EditorUtility.DisplayDialog`
through `execute_code`, read it using `editor_dialog_list`, then press `Cancel`
with its handle and `confirm: true`. Verify the waiting call returns false,
the dialog disappears, and a subsequent press with the old handle is rejected.
Also run the package's EditMode tests. Do not create test modals in a user's dirty
production project.

Apple references: [modalWindow](https://developer.apple.com/documentation/appkit/nsapplication/modalwindow),
[attachedSheet](https://developer.apple.com/documentation/appkit/nswindow/attachedsheet),
[runModal](https://developer.apple.com/documentation/appkit/nsapplication/runmodal(for:)).
