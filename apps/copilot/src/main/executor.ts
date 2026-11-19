/**
 * The session's view of the connected CAD: a CadToolExecutor over a CadConnection. Names, tools
 * and the current drawing come from the connector's latest `hello`; tool calls go through
 * `tools.call` with `source: 'ai'`. No Electron imports.
 */
import { randomUUID } from 'node:crypto';
import type { ConnectorAdvert, DocumentInfo, HelloResult, ToolCallParams, ToolResult } from '@cadstudio/cad-protocol';
import type { CadToolExecutor } from '@cadstudio/copilot-agent';

/** The part of a CadConnection the executor needs. */
export interface ToolCallConnection {
  readonly advert: Pick<ConnectorAdvert, 'displayName' | 'cad' | 'cadVersion'>;
  callTool(params: ToolCallParams, opts?: { signal?: AbortSignal; timeoutMs?: number }): Promise<ToolResult>;
}

export interface ExecutorOptions {
  /** The connector's most recent `hello` answer (refreshed by the connector manager). */
  readonly latestHello: () => HelloResult;
  /** Creates the `requestId` of each call. Defaults to a random UUID. */
  readonly newRequestId?: () => string;
}

/** Human name of a connected CAD: the advert's display name, or one derived from its kind. */
export function cadDisplayName(advert: Pick<ConnectorAdvert, 'displayName' | 'cad' | 'cadVersion'>): string {
  const displayName = advert.displayName.trim();
  if (displayName.length > 0) return displayName;
  const base = advert.cad === 'autocad' ? 'AutoCAD' : 'CAD Studio';
  const version = advert.cadVersion.trim();
  return version.length > 0 ? `${base} ${version}` : base;
}

/** Builds the executor the CopilotSession uses while `connection` is the connected CAD. */
export function createConnectionExecutor(connection: ToolCallConnection, options: ExecutorOptions): CadToolExecutor {
  const newRequestId = options.newRequestId ?? randomUUID;
  return {
    cadName: () => cadDisplayName(connection.advert),
    supportedTools: () => [...options.latestHello().supportedTools],
    currentDocument: (): DocumentInfo | null => {
      const { document } = options.latestHello();
      return document === null ? null : { ...document };
    },
    execute: (name, args, opts) => {
      const params: ToolCallParams = {
        requestId: newRequestId(),
        operationId: opts.operationId,
        name,
        arguments: args,
        source: 'ai',
        instruction: opts.instruction,
      };
      if (opts.expectedRevision !== undefined) params.expectedRevision = opts.expectedRevision;
      return connection.callTool(params, { signal: opts.signal });
    },
  };
}
