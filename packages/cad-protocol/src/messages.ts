// zod schemas of the bridge's wire messages (method params and results, the advert file). Used by
// the Node transport on both ends and usable by any other implementation of the protocol.
// Browser-safe.
import { z } from 'zod';
import type { ConnectorAdvert, DocumentInfo, HelloResult, ToolCallParams, ToolResult, WindowBounds } from './core';
import { unitSchema } from './tools/shared';

const cadKindSchema = z.enum(['autocad', 'cadstudio']);

/** Params of `auth`. */
export const authParamsSchema = z.object({
  token: z.string(),
  client: z
    .object({
      name: z.string(),
      version: z.string(),
    })
    .optional(),
});
export type AuthParams = z.infer<typeof authParamsSchema>;

/** `WindowBounds`: finite position, positive size (physical screen pixels). */
export const windowBoundsSchema: z.ZodType<WindowBounds> = z.object({
  x: z.number(),
  y: z.number(),
  width: z.number().positive(),
  height: z.number().positive(),
});

/** Params of `window.setBounds`. */
export const setWindowBoundsParamsSchema = z.object({ bounds: windowBoundsSchema });

/** Params of `tools.call`. */
export const toolCallParamsSchema: z.ZodType<ToolCallParams> = z.object({
  requestId: z.string().min(1),
  operationId: z.string().min(1),
  name: z.string().min(1),
  arguments: z.unknown(),
  expectedRevision: z.number().int().nonnegative().optional(),
  source: z.enum(['ai', 'user']),
  instruction: z.string().optional(),
});

/** The `ToolResult` envelope. */
export const toolResultSchema: z.ZodType<ToolResult> = z.object({
  success: z.boolean(),
  data: z.unknown().optional(),
  createdIds: z.array(z.string()),
  modifiedIds: z.array(z.string()),
  deletedIds: z.array(z.string()),
  warnings: z.array(z.string()),
  errors: z.array(z.string()),
  revision: z.number(),
});

/** `DocumentInfo`. */
export const documentInfoSchema: z.ZodType<DocumentInfo> = z.object({
  id: z.string(),
  name: z.string(),
  path: z.string().nullable(),
  unit: unitSchema,
  revision: z.number(),
  entityCount: z.number(),
});

/** `HelloResult`. */
export const helloResultSchema: z.ZodType<HelloResult> = z.object({
  cad: cadKindSchema,
  cadVersion: z.string(),
  connectorVersion: z.string(),
  protocolVersion: z.number(),
  document: documentInfoSchema.nullable(),
  supportedTools: z.array(z.string()),
});

/** The advert file a connector writes for discovery. */
export const connectorAdvertSchema: z.ZodType<ConnectorAdvert> = z.object({
  id: z.string().min(1),
  cad: cadKindSchema,
  displayName: z.string(),
  cadVersion: z.string(),
  connectorVersion: z.string(),
  protocolVersion: z.number().int(),
  pipeName: z.string().min(1),
  token: z.string().regex(/^[0-9a-f]{64}$/i),
  pid: z.number().int().positive(),
  startedAt: z.string().refine((value) => !Number.isNaN(Date.parse(value)), 'must be an ISO-8601 date'),
});
