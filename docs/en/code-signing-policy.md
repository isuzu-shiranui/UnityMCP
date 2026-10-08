# Code signing policy

The Windows build of the command-line tool, `isuzu-unity-cli-win-x64.exe`, is distributed with an Authenticode signature. [Back to the README](../../README.en.md)

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## What is signed

Only the Windows build of `isuzu-unity-cli` that GitHub Actions builds from the sources in this repository is signed. It is built on a GitHub-hosted runner from the commit the release tag names. Files built on a workstation and files from other projects are never signed.

The Windows binary inside `isuzu-unity-cli.mcpb`, the Claude Desktop bundle, is the same signed file. The macOS and Linux binaries and the Unity package are not signed.

Windows binaries in releases made before signing began are unsigned.

## Team roles

| Role | Members |
|---|---|
| Committers (may change the sources without review) | [isuzu-shiranui](https://github.com/isuzu-shiranui) |
| Reviewers (review every pull request from outside the team) | [isuzu-shiranui](https://github.com/isuzu-shiranui) |
| Approvers (approve each signing request) | [isuzu-shiranui](https://github.com/isuzu-shiranui) |

Every member uses multi-factor authentication on GitHub and on SignPath. Each release is signed only after an approver approves its signing request on SignPath by hand.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

These are the systems it talks to, and when:

- The Unity Editor: it connects only to an Editor on `127.0.0.1`, and nothing leaves the machine.
- GitHub: `isuzu-unity-cli doctor` and `isuzu-unity-cli update` ask `api.github.com` whether a newer release exists, and `update` and `upgrade` download the newer version from GitHub. Each happens only when the user runs that command.

The [GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement) applies to those requests.
