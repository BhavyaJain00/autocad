/**
 * Registration of the IPC channels behind the preload bridge. Every message must come from the
 * top-level frame of the copilot window showing the app itself; what each channel then does (and
 * how its arguments are validated) is in ipc-handlers.ts.
 */
import { ipcMain, type BrowserWindow, type IpcMainEvent, type IpcMainInvokeEvent } from 'electron';
import type { CopilotIpcHandlers } from './ipc-handlers';
import { isAppUrl } from './url-policy';

export interface IpcContext {
  /** The copilot window, or null before it exists / after it closed. */
  getWindow(): BrowserWindow | null;
  /** Dev server URL in development (the renderer's origin), null in production. */
  readonly devServerUrl: string | null;
  readonly handlers: CopilotIpcHandlers;
}

/** True when the message was sent by the top-level app frame of the copilot window. */
function isTrustedSender(event: IpcMainEvent | IpcMainInvokeEvent, ctx: IpcContext): boolean {
  const win = ctx.getWindow();
  if (win === null || win.isDestroyed() || event.sender !== win.webContents) return false;
  try {
    const frame = event.senderFrame;
    if (frame === null || frame.parent !== null) return false;
    return isAppUrl(frame.url, ctx.devServerUrl);
  } catch {
    // The frame was disposed (navigated away or destroyed) before the message was handled.
    return false;
  }
}

/** Registers every channel of the bridge with ipcMain. Call once, before the window is created. */
export function registerIpcHandlers(ctx: IpcContext): void {
  for (const [channel, handler] of Object.entries(ctx.handlers.invoke)) {
    ipcMain.handle(channel, async (event, ...args: unknown[]) => {
      if (!isTrustedSender(event, ctx)) {
        console.warn(`[ipc] Rejected ${channel} from an untrusted sender`);
        throw new Error(`${channel} is not available to this sender`);
      }
      return handler(args);
    });
  }

  for (const [channel, handler] of Object.entries(ctx.handlers.send)) {
    ipcMain.on(channel, (event, ...args: unknown[]) => {
      if (!isTrustedSender(event, ctx)) {
        console.warn(`[ipc] Ignored ${channel} from an untrusted sender`);
        return;
      }
      try {
        handler(args);
      } catch (error) {
        console.error(`[ipc] Handling ${channel} failed:`, error);
      }
    });
  }
}
