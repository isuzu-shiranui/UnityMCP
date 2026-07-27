/**
 * Tests for picking an Editor from the working directory.
 *
 * This runs whenever several Editors are open and no target was given, so getting it wrong
 * sends a call — possibly a write — to the wrong project while still succeeding.
 */
import { describe, test, expect } from '@jest/globals';
import * as path from 'path';

import {
    isInsideProject,
    matchByWorkingDirectory,
    projectRootOf,
} from '../core/ProjectMatch.js';

const A = { projectName: 'GameA', projectPath: path.join('/projects', 'GameA', 'Assets') };
const B = { projectName: 'GameB', projectPath: path.join('/projects', 'GameB', 'Assets') };

describe('projectRootOf', () => {
    test('strips the Assets folder the Editor publishes', () => {
        expect(projectRootOf(path.join('/projects', 'GameA', 'Assets')))
            .toBe(path.resolve('/projects', 'GameA'));
    });

    test('is case-insensitive about the folder name', () => {
        expect(projectRootOf(path.join('/projects', 'GameA', 'assets')))
            .toBe(path.resolve('/projects', 'GameA'));
    });

    test('leaves a path that does not end in Assets alone', () => {
        expect(projectRootOf(path.join('/projects', 'GameA')))
            .toBe(path.resolve('/projects', 'GameA'));
    });

    test('handles an empty path', () => {
        expect(projectRootOf('')).toBe('');
    });
});

describe('isInsideProject', () => {
    test('the project root itself counts', () => {
        expect(isInsideProject(path.resolve('/projects/GameA'), A.projectPath)).toBe(true);
    });

    test('a nested directory counts', () => {
        expect(isInsideProject(path.resolve('/projects/GameA/Assets/Scripts'), A.projectPath)).toBe(true);
    });

    test('a sibling does not', () => {
        expect(isInsideProject(path.resolve('/projects/GameB'), A.projectPath)).toBe(false);
    });

    test('a name that merely starts the same does not', () => {
        // "/projects/GameA-Sandbox" begins with the root's text but is a different directory;
        // a string prefix check would wrongly accept it.
        expect(isInsideProject(path.resolve('/projects/GameA-Sandbox'), A.projectPath)).toBe(false);
    });

    test('a parent directory does not', () => {
        expect(isInsideProject(path.resolve('/projects'), A.projectPath)).toBe(false);
    });

    test('an empty project path never matches', () => {
        expect(isInsideProject(path.resolve('/anywhere'), '')).toBe(false);
    });
});

describe('matchByWorkingDirectory', () => {
    test('finds the project containing the directory', () => {
        expect(matchByWorkingDirectory([A, B], path.resolve('/projects/GameB/Assets'))?.projectName)
            .toBe('GameB');
    });

    test('returns null when the directory is outside every project', () => {
        expect(matchByWorkingDirectory([A, B], path.resolve('/somewhere/else'))).toBeNull();
    });

    test('returns null when nothing is open', () => {
        expect(matchByWorkingDirectory([], path.resolve('/projects/GameA'))).toBeNull();
    });

    test('the deepest project wins when they nest', () => {
        // A package checked out inside another project — this repository is itself that shape.
        const outer = { projectName: 'Outer', projectPath: path.join('/work', 'Outer', 'Assets') };
        const inner = {
            projectName: 'Inner',
            projectPath: path.join('/work', 'Outer', 'Packages', 'Inner', 'Assets'),
        };

        const found = matchByWorkingDirectory(
            [outer, inner],
            path.resolve('/work/Outer/Packages/Inner/Assets/Scripts')
        );

        expect(found?.projectName).toBe('Inner');
    });

    test('the outer project still wins outside the nested one', () => {
        const outer = { projectName: 'Outer', projectPath: path.join('/work', 'Outer', 'Assets') };
        const inner = {
            projectName: 'Inner',
            projectPath: path.join('/work', 'Outer', 'Packages', 'Inner', 'Assets'),
        };

        expect(matchByWorkingDirectory([outer, inner], path.resolve('/work/Outer/Assets'))?.projectName)
            .toBe('Outer');
    });
});
