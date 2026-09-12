namespace IsuzuUnityCli.Cli;

public static class Usage
{
    public const string Text = """
        isuzu-unity-cli - drive a running Unity Editor from the terminal

        USAGE
          isuzu-unity-cli <command> [options]

        COMMANDS
          projects                       List Editors that are currently running
          tools [exact-name]             List tools, or print one tool's full schema as JSON
                                         With a name the output is the same with or without --raw
          call <tool> [args]             Invoke a tool
          verify                         Recompile, optionally run the tests, read the console,
                                         and answer with one exit code
          health                         Show the Editor's server status
          jobs [id]                      List background jobs, show one, or wait for one to end
          mcp-stdio                      Bridge stdio to the Editor's MCP endpoint (for MCP clients)

          setup                          Register with an MCP client and install the skill
          doctor                         Show what is installed, where, and what is stale
          uninstall                      Remove everything this tool put on the machine
          update                         Bring the CLI and every project's package to the newest
                                         release. The two ship as one version
          upgrade                        Replace this executable with the latest release

        CALL ARGUMENTS
          --json '<object>'              Arguments as one JSON object
          --<name> <value>               Individual argument. Naming one twice sends a list, which
                                         is how an array is typed where quotes do not survive:
                                         --paths one --paths two
          --file <path>                  For execute_code: read the snippet from a file and
                                         send it base64-encoded, so nothing can mangle it

        OPTIONS
          --project <name>               Which Editor to use; needed when several are running.
                                         Either the product name or the project folder the
                                         window title shows; a unique part of either will do
          --raw                          Print the response envelope instead of just the result
          --compact                      Print JSON without indentation. Already the default when
                                         output is piped or redirected, which is how an agent
                                         reads it; this forces it in a terminal too
          --version                      Show the version
          -h, --help                     Show this help

        TOOLS AND MCP-STDIO OPTIONS
          --group <g>[,<g>]              Only these groups: diagnostics, authoring, rendering,
                                         timeline, build, code, input. On mcp-stdio it narrows
                                         what the client is offered, which it otherwise pays for
                                         on every request: every tool together is about 43,000 tokens
                                         and diagnostics alone is about 11,000

        VERIFY OPTIONS
          --no-compile                   Skip the compile step
          --test                         Also run a test suite
          --test-mode edit|play          Which suite; defaults to edit
          --assembly <name>              Restrict the run to one test assembly
          --filter <regex>               Restrict the run to matching test names
          --category <name>              Restrict the run to one NUnit category
          --timeout <seconds>            Give up after this long; defaults to 300
          --logs <n>                     How many console errors to report; defaults to 20
          --raw                          Print the JSON summary instead of the short report

          Exit codes: 0 verified, 1 compile or tests failed, 2 an option is wrong, 3 no
          Editor or an ambiguous one, 4 timed out. Console errors are reported but never
          fail the run, because entries from earlier in the session linger.

        JOBS OPTIONS
          --wait                         Poll until the job ends and print only its last answer.
                                         Needs an id. What holds the Editor up is reported on
                                         stderr while the wait lasts
          --timeout <seconds>            Give up waiting after this long; defaults to 300. The job
                                         itself keeps running in the Editor

          Exit codes: 0 the job completed, 1 it failed or was cancelled, 2 an option is wrong,
          3 no Editor or an ambiguous one, 4 the wait timed out.

        ENVIRONMENT
          UNITY_MCP_STATE_DIR            Where the Editor writes its descriptors, when that is
                                         not a path this process would find on its own. Several
                                         may be given, separated the way PATH is. From WSL2 the
                                         Windows Editor writes to
                                         /mnt/c/Users/<you>/AppData/Local/UnityMCP
          UNITY_MCP_HOST                 Replaces 127.0.0.1 in the descriptor's endpoint. The
                                         Editor binds loopback only, so this reaches it only
                                         with a port proxy or mirrored networking in front of
                                         it; it is an escape hatch, not the usual setup.
          UNITY_MCP_TRACE                Any non-empty value prints stage timings on stderr,
                                         measured from the process start time the OS recorded.
          CLAUDE_CONFIG_DIR              Claude Code's configuration directory, in place of
                                         ~/.claude. setup, doctor and uninstall take both the
                                         skills directory and .claude.json from it.
          CODEX_HOME                     Codex's configuration directory, in place of ~/.codex,
                                         holding config.toml and the skills directory.

        SETUP OPTIONS
          --agent <name>[,<name>]        Which agents to set up: claude-code, claude-desktop,
                                         codex, cursor, gemini, vscode. Defaults to every one
                                         found installed.
          --client <name>[,<name>]       Another spelling of --agent. If both are given, --agent
                                         is the one that counts.
          --mcp                          Also register the MCP server. Claude Code and Codex get
                                         the skill by default and the server only on request;
                                         the other agents have no skill mechanism and imply this.
          --scope user|project           Where the Claude Code entry goes. "project" writes
                                         .mcp.json in the Unity project and reads the token from
                                         $UNITY_MCP_TOKEN, so no token is committed.
          --no-skill                     Do not install the skill

        DOCTOR OPTIONS
          --fix                          Reinstall stale skills and rewrite stale MCP entries

        UPDATE OPTIONS
          --project <name>               Only this project's package; otherwise every Editor that
                                         is running
          --release <tag>                Go to this release instead of the newest
          --dry-run                      Say what would change and change nothing

          Four of the six ways the package can be installed cannot be updated from here - a
          working copy, a folder under Packages/, and anything VCC or ALCOM manages. Those are
          named with the step that does move them.

        UPGRADE OPTIONS
          --release <tag>                Install this release instead of the newest, e.g. v4.1.1.
                                         The way back when a new one turns out to be broken.

        UNINSTALL OPTIONS
          --yes                          Actually remove, rather than listing what would be removed
          --no-skill                     Leave installed skills alone

        EXAMPLES
          isuzu-unity-cli setup
          isuzu-unity-cli setup --agent claude-code --mcp
          isuzu-unity-cli projects
          isuzu-unity-cli tools
          isuzu-unity-cli tools --group timeline,rendering
          isuzu-unity-cli tools compile_status
          isuzu-unity-cli verify
          isuzu-unity-cli verify --test --assembly UnityMCP.Editor.Tests
          isuzu-unity-cli call play_mode_status
          isuzu-unity-cli call console_read_logs --type error --limit 20
          isuzu-unity-cli call scene_browse_hierarchy --json '{"name":"Player","limit":5}'
          isuzu-unity-cli call execute_code --file snippet.cs
          isuzu-unity-cli jobs execute_code-3f2a-1 --wait
          isuzu-unity-cli update --dry-run
          isuzu-unity-cli update
          isuzu-unity-cli doctor --fix
          isuzu-unity-cli uninstall --yes
        """;
}
