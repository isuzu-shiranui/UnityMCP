# scripts

## `run-editmode-tests.ps1`

Runs the package's EditMode tests in a real Unity Editor and records the result.

```powershell
pwsh scripts/run-editmode-tests.ps1
```

It creates a scratch project under `%TEMP%` on first use — a manifest and a junction back to
this repository, nothing else — reuses it afterwards, and writes `editmode-attestation.json`
naming the sources it ran against.

**Commit that file with the change it covers.** The release refuses to publish Editor sources
that no recorded run covers.

### Which Unity it runs

Pinned to 6000.0.x, not "whichever Editor is newest here": a gate whose meaning depends on what
happens to be installed on one machine is not a gate. The version actually used is recorded in
the attestation.

That pin currently also hides something worth knowing. On Unity 6000.5 the package **does not
compile** — `Object.GetInstanceID()`, `EditorUtility.InstanceIDToObject` and
`SerializedProperty.objectReferenceInstanceIDValue` became obsolete-as-error there, and the
package has not been migrated to `EntityId`. Ten call sites across six files. The README claims
2022.3 through Unity 6, so that claim is currently too broad.

Options: `-Unity <path>` to pick an Editor, `-ProjectPath <dir>` to put the scratch project
elsewhere, `-KeepProject` to keep it after a first run.

## `source-hash.js`

Hashes every `.cs` and `.asmdef` under the Unity package. Both the script above and the release
workflow call this one implementation: two answers to "which sources are these" would disagree
the first time either changed, and the check would then either block a good release or wave a
bad one through.

Line endings are stripped before hashing, because the repository stores LF, a Windows checkout
has CRLF, and a Linux runner has LF.

## Why an attestation rather than CI

No runner in this repository has a Unity licence, so nothing automated compiles a line of C#.
Adding one is possible — `game-ci` plus a `UNITY_LICENSE` secret — but for a project with one
maintainer it buys less than it costs: there are no pull requests from strangers to guard.

What it does not fix is release-time. The workflow verifies TypeScript and publishes, so a C#
regression could reach npm without a single test having run. This closes that specific hole for
the price of one command, and leaves the day-to-day loop where it already was: run the tests on
the machine that has Unity on it.

The attestation names the *sources*, not the commit, so a documentation change releases without
re-running anything — while any edit to a `.cs` or `.asmdef` file requires a fresh run.
