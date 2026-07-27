/**
 * Tests for envelope-error extraction.
 *
 * A 4xx from Unity carries the reason in its body. Reporting only the status line strips out
 * the one thing that tells the caller how to correct the call, so this path matters more
 * than its size suggests.
 */
import { describe, test, expect } from '@jest/globals';
import { parseEnvelopeError } from '../core/retryableFetch.js';

describe('parseEnvelopeError', () => {
    test('extracts code and message from an error envelope', () => {
        const body = JSON.stringify({
            status: 'error',
            error: { code: 'invalid_params', message: "'[unclosed' is not a valid regex" },
        });

        expect(parseEnvelopeError(body)).toEqual({
            code: 'invalid_params',
            message: "'[unclosed' is not a valid regex",
        });
    });

    test('tolerates a missing code', () => {
        const body = JSON.stringify({ status: 'error', error: { message: 'something broke' } });

        expect(parseEnvelopeError(body)).toEqual({ code: undefined, message: 'something broke' });
    });

    test('returns undefined for a success envelope', () => {
        expect(parseEnvelopeError(JSON.stringify({ status: 'success', result: {} }))).toBeUndefined();
    });

    test('returns undefined for a non-JSON body', () => {
        expect(parseEnvelopeError('<h1>Length Required</h1>')).toBeUndefined();
    });

    test('returns undefined for an empty body', () => {
        expect(parseEnvelopeError('')).toBeUndefined();
    });

    test('returns undefined when the message is blank', () => {
        expect(parseEnvelopeError(JSON.stringify({ error: { code: 'x', message: '' } }))).toBeUndefined();
    });
});
