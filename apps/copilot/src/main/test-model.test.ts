import path from 'node:path';
import { describe, expect, it } from 'vitest';
import type { ModelTransport } from '@cadstudio/copilot-agent';
import { loadTestModel, requestedScriptPath } from './test-model';

const SCRIPT_PATH = path.resolve('scripts', 'demo.json');

describe('requestedScriptPath', () => {
  it('needs both CADCOPILOT_E2E=1 and CADCOPILOT_SCRIPT', () => {
    expect(requestedScriptPath({ CADCOPILOT_E2E: '1', CADCOPILOT_SCRIPT: SCRIPT_PATH })).toBe(SCRIPT_PATH);
    expect(requestedScriptPath({})).toBeNull();
    expect(requestedScriptPath({ CADCOPILOT_E2E: '1' })).toBeNull();
    expect(requestedScriptPath({ CADCOPILOT_SCRIPT: SCRIPT_PATH })).toBeNull();
    expect(requestedScriptPath({ CADCOPILOT_E2E: '0', CADCOPILOT_SCRIPT: SCRIPT_PATH })).toBeNull();
    expect(requestedScriptPath({ CADCOPILOT_E2E: 'true', CADCOPILOT_SCRIPT: SCRIPT_PATH })).toBeNull();
    expect(requestedScriptPath({ CADCOPILOT_E2E: '1', CADCOPILOT_SCRIPT: '   ' })).toBeNull();
  });

  it('resolves a relative script path', () => {
    const resolved = requestedScriptPath({ CADCOPILOT_E2E: '1', CADCOPILOT_SCRIPT: 'demo.json' });
    expect(resolved).toBe(path.resolve('demo.json'));
  });
});

describe('loadTestModel', () => {
  const transport: ModelTransport = {
    kind: 'scripted',
    run: () => Promise.reject(new Error('not used in these tests')),
  };
  const env = { CADCOPILOT_E2E: '1', CADCOPILOT_SCRIPT: SCRIPT_PATH };

  it('returns null (the real model) unless the test model was requested', () => {
    let created = 0;
    const result = loadTestModel({
      env: { CADCOPILOT_SCRIPT: SCRIPT_PATH },
      createTransport: () => {
        created += 1;
        return transport;
      },
      readScript: () => '{}',
    });
    expect(result).toBeNull();
    expect(created).toBe(0);
  });

  it('parses the script and builds the scripted transport', () => {
    const seen: unknown[] = [];
    const paths: string[] = [];
    const result = loadTestModel({
      env,
      createTransport: (script) => {
        seen.push(script);
        return transport;
      },
      readScript: (filePath) => {
        paths.push(filePath);
        return '﻿{"turns":[{"text":"Done."}]}';
      },
    });
    expect(result).toBe(transport);
    expect(seen).toEqual([{ turns: [{ text: 'Done.' }] }]);
    expect(paths).toEqual([SCRIPT_PATH]);
  });

  it('throws instead of falling back to the real model when the script cannot be read', () => {
    expect(() =>
      loadTestModel({
        env,
        createTransport: () => transport,
        readScript: () => {
          throw new Error('ENOENT: no such file');
        },
      }),
    ).toThrow(/could not be read: ENOENT/);
  });

  it('throws when the script is not JSON', () => {
    expect(() => loadTestModel({ env, createTransport: () => transport, readScript: () => 'turns: []' })).toThrow(
      /could not be read/,
    );
  });

  it('throws when the script is rejected by the transport', () => {
    expect(() =>
      loadTestModel({
        env,
        createTransport: () => {
          throw new Error('turns must be an array');
        },
        readScript: () => '{}',
      }),
    ).toThrow(/is not valid: turns must be an array/);
  });

  it('reads the script from disk by default and reports a missing file', () => {
    expect(() =>
      loadTestModel({
        env: { CADCOPILOT_E2E: '1', CADCOPILOT_SCRIPT: path.join(SCRIPT_PATH, 'does-not-exist.json') },
        createTransport: () => transport,
      }),
    ).toThrow(/could not be read/);
  });
});
