import { beforeEach, describe, expect, it } from 'vitest';
import {
  COPILOT_IPC,
  PANEL_WIDTH_MAX,
  type ActionResult,
  type CopilotSettings,
  type CopilotSnapshot,
} from '../shared/bridge';
import { createIpcHandlers, PUSH_CHANNELS, type CopilotIpcApi, type CopilotIpcHandlers } from './ipc-handlers';

const SETTINGS: CopilotSettings = {
  model: 'claude-opus-5-5',
  effort: 'high',
  approvalMode: 'ask-destructive',
  panelWidth: 460,
  alwaysOnTop: false,
  autoDock: true,
  autoConnect: true,
  hasApiKey: false,
  testModel: false,
};

const SNAPSHOT: CopilotSnapshot = {
  appVersion: '0.1.0',
  settings: SETTINGS,
  connection: { status: 'disconnected' },
  connectors: [],
  busy: false,
  timeline: [],
};

const OK: ActionResult = { ok: true };
const KEY = 'sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789';

/** Records every call that reaches the application, with its (validated) arguments. */
function createApi(calls: [string, ...unknown[]][]): CopilotIpcApi {
  const record =
    <R>(name: string, result: R) =>
    (...args: unknown[]): R => {
      calls.push([name, ...args]);
      return result;
    };
  return {
    getSnapshot: record('getSnapshot', SNAPSHOT),
    sendMessage: record('sendMessage', OK),
    answerQuestion: record('answerQuestion', undefined),
    resolveApproval: record('resolveApproval', undefined),
    stop: record('stop', undefined),
    resetSession: record('resetSession', undefined),
    refreshConnectors: record('refreshConnectors', Promise.resolve([])),
    connect: record('connect', Promise.resolve(OK)),
    disconnect: record('disconnect', undefined),
    setDocked: record('setDocked', Promise.resolve(OK)),
    currentSettings: record('currentSettings', SETTINGS),
    updateSettings: record('updateSettings', { ...SETTINGS, effort: 'low' as const }),
    setApiKey: record('setApiKey', Promise.resolve(OK)),
    pickAttachments: record('pickAttachments', Promise.resolve([])),
    openExternal: record('openExternal', undefined),
    minimizeWindow: record('minimizeWindow', undefined),
    closeWindow: record('closeWindow', undefined),
  };
}

describe('createIpcHandlers', () => {
  let calls: [string, ...unknown[]][];
  let logs: string[];
  let handlers: CopilotIpcHandlers;

  const invoke = (channel: string, ...args: unknown[]): unknown => {
    const handler = handlers.invoke[channel];
    if (handler === undefined) throw new Error(`no invoke handler for ${channel}`);
    return handler(args);
  };
  const send = (channel: string, ...args: unknown[]): void => {
    const handler = handlers.send[channel];
    if (handler === undefined) throw new Error(`no send handler for ${channel}`);
    handler(args);
  };

  beforeEach(() => {
    calls = [];
    logs = [];
    handlers = createIpcHandlers(createApi(calls), (message) => logs.push(message));
  });

  it('handles every renderer-to-main channel of the contract exactly once', () => {
    const handled = [...Object.keys(handlers.invoke), ...Object.keys(handlers.send)].sort();
    const expected = Object.values(COPILOT_IPC)
      .filter((channel) => !PUSH_CHANNELS.includes(channel))
      .sort();
    expect(handled).toEqual(expected);
    expect(new Set(handled).size).toBe(handled.length);
    // Push channels are never listened on.
    for (const channel of PUSH_CHANNELS) {
      expect(handlers.invoke[channel]).toBeUndefined();
      expect(handlers.send[channel]).toBeUndefined();
    }
  });

  it('getSnapshot returns the snapshot', () => {
    expect(invoke(COPILOT_IPC.getSnapshot)).toBe(SNAPSHOT);
  });

  describe('sendMessage', () => {
    it('passes a valid, trimmed message on', () => {
      const attachment = { name: 'a.txt', mediaType: 'text/plain', dataBase64: 'aGk=', sizeBytes: 999 };
      expect(invoke(COPILOT_IPC.sendMessage, ' hello ', [attachment])).toEqual(OK);
      expect(calls).toEqual([['sendMessage', 'hello', [{ ...attachment, sizeBytes: 2 }]]]);
    });

    it('refuses invalid messages without reaching the application', () => {
      const invalid: unknown[][] = [
        [],
        [''],
        ['   ', []],
        [42, []],
        [{ text: 'hi' }, []],
        ['hi', 'not a list'],
        ['hi', [{ name: 'x.exe', mediaType: 'application/x-msdownload', dataBase64: 'AAAA' }]],
        ['a'.repeat(100_001), []],
      ];
      for (const args of invalid) {
        const result = invoke(COPILOT_IPC.sendMessage, ...args) as ActionResult;
        expect(result.ok, JSON.stringify(args).slice(0, 60)).toBe(false);
      }
      expect(calls).toEqual([]);
    });
  });

  describe('connect', () => {
    it('passes a valid connector id on', async () => {
      expect(await invoke(COPILOT_IPC.connect, 'cadstudio-4242')).toEqual(OK);
      expect(calls).toEqual([['connect', 'cadstudio-4242']]);
    });

    it('refuses anything that is not a connector id', () => {
      for (const id of [undefined, null, 7, '', '..\\..\\pipe', 'a b', { id: 'cadstudio-1' }]) {
        expect((invoke(COPILOT_IPC.connect, id) as ActionResult).ok).toBe(false);
      }
      expect(calls).toEqual([]);
    });
  });

  describe('setDocked', () => {
    it('accepts booleans only', async () => {
      expect(await invoke(COPILOT_IPC.setDocked, true)).toEqual(OK);
      expect(await invoke(COPILOT_IPC.setDocked, false)).toEqual(OK);
      for (const value of [undefined, null, 1, 'true', {}]) {
        expect((invoke(COPILOT_IPC.setDocked, value) as ActionResult).ok).toBe(false);
      }
      expect(calls).toEqual([
        ['setDocked', true],
        ['setDocked', false],
      ]);
    });
  });

  describe('updateSettings', () => {
    it('applies a valid patch, clamped and without foreign fields', () => {
      const result = invoke(COPILOT_IPC.updateSettings, { effort: 'low', panelWidth: 99_999, hasApiKey: true, bogus: 1 });
      expect(result).toEqual({ ...SETTINGS, effort: 'low' });
      expect(calls).toEqual([['updateSettings', { effort: 'low', panelWidth: PANEL_WIDTH_MAX }]]);
    });

    it('applies nothing for an invalid patch and answers with the current settings', () => {
      for (const patch of [undefined, null, 'effort', [], { effort: 'ultra' }, { model: 'x', autoDock: false }, { panelWidth: 'wide' }]) {
        expect(invoke(COPILOT_IPC.updateSettings, patch)).toBe(SETTINGS);
      }
      expect(calls.every(([name]) => name === 'currentSettings')).toBe(true);
      expect(logs).toHaveLength(7);
    });
  });

  describe('setApiKey', () => {
    it('passes a trimmed plausible key on', async () => {
      expect(await invoke(COPILOT_IPC.setApiKey, `  ${KEY} `)).toEqual(OK);
      expect(calls).toEqual([['setApiKey', KEY]]);
    });

    it('passes null on to remove the key', async () => {
      expect(await invoke(COPILOT_IPC.setApiKey, null)).toEqual(OK);
      expect(calls).toEqual([['setApiKey', null]]);
    });

    it('refuses values that cannot be a key, without echoing them', () => {
      for (const value of [undefined, 42, '', 'sk-tiny', 'has spaces in the middle of it', {}, [KEY]]) {
        const result = invoke(COPILOT_IPC.setApiKey, value) as ActionResult;
        expect(result.ok).toBe(false);
        if (!result.ok && typeof value === 'string' && value.length > 0) expect(result.message).not.toContain(value);
      }
      expect(calls).toEqual([]);
    });
  });

  it('refreshConnectors and pickAttachments take no arguments', async () => {
    expect(await invoke(COPILOT_IPC.refreshConnectors, 'ignored')).toEqual([]);
    expect(await invoke(COPILOT_IPC.pickAttachments, { path: 'C:\\secret.txt' })).toEqual([]);
    expect(calls).toEqual([['refreshConnectors'], ['pickAttachments']]);
  });

  describe('answerQuestion', () => {
    it('passes valid answers on, stripped of extra properties', () => {
      send(COPILOT_IPC.answerQuestion, 'q1', [{ questionId: 'wall', answer: '200', x: 1 }]);
      expect(calls).toEqual([['answerQuestion', 'q1', [{ questionId: 'wall', answer: '200' }]]]);
    });

    it('drops invalid answers', () => {
      send(COPILOT_IPC.answerQuestion);
      send(COPILOT_IPC.answerQuestion, '', []);
      send(COPILOT_IPC.answerQuestion, 'q1', 'yes');
      send(COPILOT_IPC.answerQuestion, 'q1', [{ questionId: 'wall' }]);
      send(COPILOT_IPC.answerQuestion, { id: 'q1' }, []);
      expect(calls).toEqual([]);
      expect(logs).toHaveLength(5);
    });
  });

  describe('resolveApproval', () => {
    it('passes valid decisions on', () => {
      send(COPILOT_IPC.resolveApproval, 'p1', 'approve');
      send(COPILOT_IPC.resolveApproval, 'p2', 'reject');
      send(COPILOT_IPC.resolveApproval, 'p3', 'approve-all');
      expect(calls).toEqual([
        ['resolveApproval', 'p1', 'approve'],
        ['resolveApproval', 'p2', 'reject'],
        ['resolveApproval', 'p3', 'approve-all'],
      ]);
    });

    it('drops invalid decisions', () => {
      send(COPILOT_IPC.resolveApproval, 'p1', 'yes');
      send(COPILOT_IPC.resolveApproval, 'p1', true);
      send(COPILOT_IPC.resolveApproval, null, 'approve');
      send(COPILOT_IPC.resolveApproval);
      expect(calls).toEqual([]);
    });
  });

  describe('openExternal', () => {
    it('opens https URLs only', () => {
      send(COPILOT_IPC.openExternal, 'https://console.anthropic.com/settings/keys');
      send(COPILOT_IPC.openExternal, 'http://example.com');
      send(COPILOT_IPC.openExternal, 'file:///C:/Windows/System32/calc.exe');
      send(COPILOT_IPC.openExternal, 'javascript:alert(1)');
      send(COPILOT_IPC.openExternal, 'C:\\Windows\\System32\\cmd.exe');
      send(COPILOT_IPC.openExternal, undefined);
      expect(calls).toEqual([['openExternal', 'https://console.anthropic.com/settings/keys']]);
      expect(logs).toHaveLength(5);
    });
  });

  it('the argument-free one-way channels ignore whatever is sent with them', () => {
    send(COPILOT_IPC.stop, 'x');
    send(COPILOT_IPC.resetSession, { force: true });
    send(COPILOT_IPC.disconnect, 1);
    send(COPILOT_IPC.minimizeWindow, null);
    send(COPILOT_IPC.closeWindow, 'now');
    expect(calls).toEqual([['stop'], ['resetSession'], ['disconnect'], ['minimizeWindow'], ['closeWindow']]);
  });
});
