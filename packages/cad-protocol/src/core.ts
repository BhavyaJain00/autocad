// Core shared types of the CAD Copilot bridge (docs/architecture/copilot.md). Browser-safe.
import type { z } from 'zod';

// ---------------------------------------------------------------------------------------------
// Basics
// ---------------------------------------------------------------------------------------------

/** Length unit accepted at the tool boundary. */
export type Unit = 'mm' | 'cm' | 'm' | 'in' | 'ft';
/** Every supported unit, in a stable order (smallest metric first, then imperial). */
export const UNITS: readonly Unit[] = ['mm', 'cm', 'm', 'in', 'ft'];
/** CAD applications a connector exists (or is planned) for. */
export type CadKind = 'autocad' | 'cadstudio';
/** Every connector kind. */
export const CAD_KINDS: readonly CadKind[] = ['autocad', 'cadstudio'];
/** A 2D point in the call's unit (world coordinates, Y up). */
export interface Point2 {
  x: number;
  y: number;
}

/** Version of the pipe protocol; connectors advertise it and report it in `hello`. */
export const BRIDGE_PROTOCOL_VERSION = 1;

/** JSON-RPC error codes used by connectors (see copilot.md). */
export const BridgeErrorCode = {
  Unauthorized: -32001,
  UnknownTool: -32002,
  InvalidArguments: -32003,
  StaleRevision: -32004,
  NoActiveDocument: -32005,
  Busy: -32006,
  CadOperationFailed: -32007,
  NotSupported: -32008,
  ParseError: -32700,
  InvalidRequest: -32600,
  MethodNotFound: -32601,
  InvalidParams: -32602,
  InternalError: -32603,
} as const;
export type BridgeErrorCode = (typeof BridgeErrorCode)[keyof typeof BridgeErrorCode];

/**
 * Protocol-level failure (a JSON-RPC error): unauthorised, malformed request, unknown method,
 * connector busy / timed out, connection lost, internal error. Tool-level failures (invalid
 * arguments, stale revision, unsupported tool, no drawing open, CAD operation failed) are NOT
 * errors: `tools.call` resolves with a ToolResult whose `success` is false and `errors` explain why.
 */
export class BridgeError extends Error {
  readonly code: number;
  readonly data: unknown;
  constructor(code: number, message: string, data?: unknown) {
    super(message);
    this.name = 'BridgeError';
    this.code = code;
    this.data = data;
  }
}

/** True for a BridgeError, including one created by another copy of this module (checks the shape). */
export function isBridgeError(value: unknown): value is BridgeError {
  if (value instanceof BridgeError) return true;
  return (
    value instanceof Error &&
    value.name === 'BridgeError' &&
    typeof (value as { code?: unknown }).code === 'number'
  );
}

// ---------------------------------------------------------------------------------------------
// Discovery & handshake
// ---------------------------------------------------------------------------------------------

/** Written by each running connector to %APPDATA%/CadCopilot/connectors/<cad>-<pid>.json. */
export interface ConnectorAdvert {
  id: string; // '<cad>-<pid>'
  cad: CadKind;
  displayName: string; // e.g. 'AutoCAD 2026' or 'CAD Studio'
  cadVersion: string;
  connectorVersion: string;
  protocolVersion: number;
  pipeName: string; // full path, e.g. '\\\\.\\pipe\\cadcopilot-autocad-1234'
  token: string; // 64 hex chars
  pid: number;
  startedAt: string; // ISO-8601
}

/** The drawing a connector currently has active. */
export interface DocumentInfo {
  id: string;
  name: string;
  path: string | null;
  unit: Unit;
  revision: number;
  entityCount: number;
}

/** Result of the `hello` method. */
export interface HelloResult {
  cad: CadKind;
  cadVersion: string;
  connectorVersion: string;
  protocolVersion: number;
  document: DocumentInfo | null;
  /** Catalog tool names this connector implements. */
  supportedTools: string[];
}

/**
 * Outer bounds of the CAD application's main window in PHYSICAL screen pixels (what Win32
 * GetWindowRect reports to a per-monitor-DPI-aware process). Electron code converts with
 * screen.screenToDipRect / screen.dipToScreenRect.
 */
export interface WindowBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

// ---------------------------------------------------------------------------------------------
// Tools
// ---------------------------------------------------------------------------------------------

export type ToolCategory =
  | 'inspection'
  | 'geometry'
  | 'architecture'
  | 'modification'
  | 'annotation'
  | 'layers'
  | 'validation'
  | 'view'
  | 'document';

/** Every tool category, in catalog order. */
export const TOOL_CATEGORIES: readonly ToolCategory[] = [
  'inspection',
  'geometry',
  'architecture',
  'modification',
  'annotation',
  'layers',
  'validation',
  'view',
  'document',
];

/** read: runs without approval; write: approval per user setting; destructive: always approval. */
export type ToolRisk = 'read' | 'write' | 'destructive';

export interface ToolDefinition {
  /** snake_case catalog name, e.g. 'create_room'. */
  readonly name: string;
  /** Short human title, e.g. 'Create room'. */
  readonly title: string;
  /** Description written for the model: what it does, units, when to use it. */
  readonly description: string;
  readonly category: ToolCategory;
  readonly risk: ToolRisk;
  /** Strict zod schema of the arguments. */
  readonly input: z.ZodType;
  /** zod schema of `ToolResult.data`. */
  readonly output: z.ZodType;
}

/** Envelope returned by every tools.call. */
export interface ToolResult {
  success: boolean;
  data?: unknown;
  createdIds: string[];
  modifiedIds: string[];
  deletedIds: string[];
  warnings: string[];
  errors: string[];
  /** Drawing revision after the call. */
  revision: number;
}

/** Params of the `tools.call` method. */
export interface ToolCallParams {
  requestId: string;
  operationId: string;
  name: string;
  arguments: unknown;
  expectedRevision?: number;
  source: 'ai' | 'user';
  /** The user instruction behind the call (stored in the CAD's operation history). */
  instruction?: string;
}

/** Method names of the bridge (client to connector). */
export const BRIDGE_METHODS = {
  auth: 'auth',
  hello: 'hello',
  callTool: 'tools.call',
  getWindowBounds: 'window.getBounds',
  setWindowBounds: 'window.setBounds',
  focusWindow: 'window.focus',
} as const;

/** Notification names of the bridge (connector to client). */
export const BRIDGE_NOTIFICATIONS = {
  documentChanged: 'document.changed',
  documentActivated: 'document.activated',
  selectionChanged: 'selection.changed',
  connectorClosing: 'connector.closing',
} as const;

/** Params of each connector notification. */
export interface BridgeNotificationParams {
  'document.changed': { documentId: string; revision: number };
  'document.activated': { document: DocumentInfo | null };
  'selection.changed': { ids: string[] };
  'connector.closing': Record<string, never>;
}

/** Builds a ToolResult that reports a tool-level failure (never a JSON-RPC error). */
export function failedToolResult(errors: string | readonly string[], revision: number): ToolResult {
  return {
    success: false,
    createdIds: [],
    modifiedIds: [],
    deletedIds: [],
    warnings: [],
    errors: typeof errors === 'string' ? [errors] : [...errors],
    revision,
  };
}
