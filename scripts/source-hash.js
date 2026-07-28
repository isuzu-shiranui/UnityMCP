#!/usr/bin/env node
// Identifies the Editor sources, so a recorded test run can be matched to the code it ran
// against. Used by both scripts/run-editmode-tests.ps1 and the release workflow: two
// implementations of "which sources are these" would disagree the first time one of them was
// changed, and the check would either block a good release or wave a bad one through.
//
// Line endings are stripped before hashing. The repository stores LF, a Windows checkout has
// CRLF, and a Linux runner has LF, so hashing the bytes as they sit on disk would make the
// attestation fail on every platform but the one that wrote it.

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const PACKAGE = 'jp.shiranui-isuzu.unity-mcp';

/** Everything that decides what the Editor assemblies compile to. */
function sourceFiles(root) {
    const out = [];

    const walk = (dir) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
            const full = path.join(dir, entry.name);

            if (entry.isDirectory()) {
                walk(full);
            } else if (entry.name.endsWith('.cs') || entry.name.endsWith('.asmdef')) {
                out.push(full);
            }
        }
    };

    walk(path.join(root, PACKAGE));

    return out.sort();
}

function sourceHash(root) {
    const hash = crypto.createHash('sha256');

    for (const file of sourceFiles(root)) {
        const relative = path.relative(root, file).split(path.sep).join('/');
        const content = fs.readFileSync(file, 'utf8').replace(/\r/g, '');

        // The path goes in too: moving a file changes what compiles even when no line does.
        hash.update(relative);
        hash.update('\0');
        hash.update(content);
        hash.update('\0');
    }

    return hash.digest('hex');
}

module.exports = { sourceHash, sourceFiles, PACKAGE };

if (require.main === module) {
    const root = process.argv[2] || path.join(__dirname, '..');
    const files = sourceFiles(root);

    if (process.argv.includes('--verbose')) {
        console.error(`${files.length} source files under ${PACKAGE}`);
    }

    console.log(sourceHash(root));
}
