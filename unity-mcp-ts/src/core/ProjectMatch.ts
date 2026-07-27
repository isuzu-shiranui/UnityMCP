import * as path from 'path';

/**
 * Picking the Editor a command is "obviously" about, from where it was run.
 *
 * With several Editors open, refusing to guess is right when there is nothing to go on. But
 * there usually is: a shell sitting inside a Unity project, or an MCP client launched from
 * one, has already said which project it means. Using that turns the common case back into
 * zero typing without reintroducing a guess, because a working directory inside exactly one
 * open project is not ambiguous.
 */

export interface ProjectLike {
    projectName: string;
    projectPath: string;
}

/**
 * Project root for a descriptor.
 *
 * The Editor publishes `Application.dataPath`, which is `<project>/Assets`, so the root is one
 * level up. Anything else is returned unchanged rather than guessed at.
 */
export function projectRootOf(projectPath: string): string {
    if (!projectPath) {
        return '';
    }

    const normalized = path.resolve(projectPath);

    return path.basename(normalized).toLowerCase() === 'assets'
        ? path.dirname(normalized)
        : normalized;
}

/** True when `directory` is the project root or somewhere inside it. */
export function isInsideProject(directory: string, projectPath: string): boolean {
    const root = projectRootOf(projectPath);

    if (root === '') {
        return false;
    }

    const relative = path.relative(root, path.resolve(directory));

    // Empty means the directory is the root itself. A leading '..' means it is outside, and an
    // absolute result means a different drive.
    return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

/**
 * Finds the open project containing `directory`, or null.
 *
 * When projects nest — a package checkout inside another project, which this repository is
 * itself an example of — the deepest root wins, since that is the one the caller is actually
 * working in.
 */
export function matchByWorkingDirectory<T extends ProjectLike>(
    candidates: T[],
    directory: string
): T | null {
    const containing = candidates.filter(c => isInsideProject(directory, c.projectPath));

    if (containing.length === 0) {
        return null;
    }

    if (containing.length === 1) {
        return containing[0];
    }

    return containing.reduce((deepest, candidate) =>
        projectRootOf(candidate.projectPath).length > projectRootOf(deepest.projectPath).length
            ? candidate
            : deepest);
}
