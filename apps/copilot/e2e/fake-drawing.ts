/**
 * A fake CAD application with an in-memory drawing, for CAD Copilot's end-to-end tests. It runs a
 * real connector server (named pipe, advert file, token auth) in the test process and implements
 * the tools the copilot needs for a realistic conversation: get_drawing_info, list_layers,
 * inspect_entities, create_layer, create_entities, delete_entities and zoom_extents. Write tools
 * honour `expectedRevision` the way a real connector must: a stale revision is refused with a
 * failed ToolResult and the drawing stays unchanged.
 */
import {
  BRIDGE_PROTOCOL_VERSION,
  type CadKind,
  type DocumentInfo,
  type HelloResult,
  type ToolCallParams,
  type ToolResult,
  type WindowBounds,
} from '@cadstudio/cad-protocol';
import { startConnectorServer, type ConnectorServer } from '@cadstudio/cad-protocol/node';

/** The tools the fake drawing implements (reported in hello). */
export const FAKE_DRAWING_TOOLS = [
  'get_drawing_info',
  'list_layers',
  'inspect_entities',
  'create_layer',
  'create_entities',
  'delete_entities',
  'zoom_extents',
] as const;

/** The start of the error a stale write gets (the UI shows it on the failed activity row). */
export const STALE_REVISION_MESSAGE = 'The drawing changed since you last looked at it';

interface Point {
  x: number;
  y: number;
}

interface FakeEntity {
  id: string;
  type: string;
  layer: string;
  min: Point;
  max: Point;
}

interface FakeLayer {
  name: string;
  color: string;
}

export interface FakeDrawingOptions {
  /** CADCOPILOT_HOME of the test: the folder the advert is written to. */
  readonly homeDir: string;
  readonly cad?: CadKind;
  readonly displayName?: string;
  readonly documentName?: string;
  /** Window bounds before anything moved the fake window (physical pixels). */
  readonly initialBounds?: WindowBounds;
}

export interface FakeDrawingCad {
  /** Advert id ('<cad>-<pid of the test process>'). */
  readonly id: string;
  readonly cad: CadKind;
  readonly displayName: string;
  /** Every tools.call received, in order (refused ones included). */
  readonly toolCalls: ToolCallParams[];
  /** Every window.setBounds received, in order. */
  readonly setBoundsCalls: WindowBounds[];
  readonly initialBounds: WindowBounds;
  document(): DocumentInfo;
  entityIds(): string[];
  layerNames(): string[];
  /** The fake window's current bounds. */
  bounds(): WindowBounds;
  /** The user edits the drawing in the CAD (adds a line): the revision moves on and clients are notified. */
  externalEdit(): void;
  clientCount(): number;
  /** Announces the shutdown, closes the pipe and removes the advert. */
  close(): Promise<void>;
}

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function asPoint(value: unknown): Point {
  const record = asRecord(value);
  return { x: Number(record.x ?? 0), y: Number(record.y ?? 0) };
}

/** Axis-aligned bounds of one create_entities item (rotation ignored: it is a fake). */
function boundsOf(item: Record<string, unknown>): { min: Point; max: Point } {
  switch (item.type) {
    case 'line': {
      const a = asPoint(item.start);
      const b = asPoint(item.end);
      return { min: { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y) }, max: { x: Math.max(a.x, b.x), y: Math.max(a.y, b.y) } };
    }
    case 'rectangle': {
      const corner = asPoint(item.corner);
      const width = Number(item.width ?? 0);
      const height = Number(item.height ?? 0);
      return { min: corner, max: { x: corner.x + width, y: corner.y + height } };
    }
    case 'circle':
    case 'arc': {
      const center = asPoint(item.center);
      const radius = Number(item.radius ?? 0);
      return { min: { x: center.x - radius, y: center.y - radius }, max: { x: center.x + radius, y: center.y + radius } };
    }
    case 'polyline': {
      const points = Array.isArray(item.points) ? item.points.map(asPoint) : [{ x: 0, y: 0 }];
      return {
        min: { x: Math.min(...points.map((p) => p.x)), y: Math.min(...points.map((p) => p.y)) },
        max: { x: Math.max(...points.map((p) => p.x)), y: Math.max(...points.map((p) => p.y)) },
      };
    }
    default: {
      const position = asPoint(item.position);
      return { min: position, max: position };
    }
  }
}

/** Starts the fake CAD. Only the advert folder and pipe name depend on CADCOPILOT_HOME. */
export async function startFakeDrawingCad(options: FakeDrawingOptions): Promise<FakeDrawingCad> {
  const cad: CadKind = options.cad ?? 'cadstudio';
  const displayName = options.displayName ?? 'Fake CAD';
  const initialBounds: WindowBounds = options.initialBounds ?? { x: 120, y: 90, width: 900, height: 600 };
  let bounds: WindowBounds = { ...initialBounds };
  const toolCalls: ToolCallParams[] = [];
  const setBoundsCalls: WindowBounds[] = [];
  const entities = new Map<string, FakeEntity>();
  const layers: FakeLayer[] = [{ name: '0', color: '#ffffff' }];
  let currentLayer = '0';
  let nextEntity = 1;
  const document: DocumentInfo = {
    id: `doc-${cad}`,
    name: options.documentName ?? 'Ground Floor.cadstudio',
    path: null,
    unit: 'mm',
    revision: 1,
    entityCount: 0,
  };
  let server: ConnectorServer | undefined;

  const result = (overrides: Partial<ToolResult> = {}): ToolResult => ({
    success: true,
    createdIds: [],
    modifiedIds: [],
    deletedIds: [],
    warnings: [],
    errors: [],
    revision: document.revision,
    ...overrides,
  });
  const failed = (message: string): ToolResult => result({ success: false, errors: [message] });

  /** Applies a change: new revision, entity count, and a document.changed notification. */
  const changed = (): void => {
    document.revision += 1;
    document.entityCount = entities.size;
    server?.notify('document.changed', { documentId: document.id, revision: document.revision });
  };

  const summary = (entity: FakeEntity) => ({
    id: entity.id,
    type: entity.type,
    layer: entity.layer,
    color: 'bylayer',
    bbox: { min: entity.min, max: entity.max },
  });

  const addEntity = (type: string, layer: string, box: { min: Point; max: Point }): FakeEntity => {
    const entity: FakeEntity = { id: `e${nextEntity}`, type, layer, ...box };
    nextEntity += 1;
    entities.set(entity.id, entity);
    return entity;
  };

  const extents = (): { min: Point; max: Point } | null => {
    const all = [...entities.values()];
    if (all.length === 0) return null;
    return {
      min: { x: Math.min(...all.map((e) => e.min.x)), y: Math.min(...all.map((e) => e.min.y)) },
      max: { x: Math.max(...all.map((e) => e.max.x)), y: Math.max(...all.map((e) => e.max.y)) },
    };
  };

  const callTool = async (params: ToolCallParams): Promise<ToolResult> => {
    toolCalls.push(structuredClone(params));
    const args = asRecord(params.arguments);
    const writes = params.name === 'create_layer' || params.name === 'create_entities' || params.name === 'delete_entities';
    if (writes && params.expectedRevision !== undefined && params.expectedRevision !== document.revision) {
      return failed(
        `${STALE_REVISION_MESSAGE} (revision ${params.expectedRevision}, now ${document.revision}). ` +
          'Inspect the drawing again before changing it.',
      );
    }
    switch (params.name) {
      case 'get_drawing_info': {
        const counts: Record<string, number> = {};
        for (const entity of entities.values()) counts[entity.type] = (counts[entity.type] ?? 0) + 1;
        return result({
          data: {
            name: document.name,
            path: null,
            unit: document.unit,
            entityCount: entities.size,
            entityCountsByType: counts,
            layerCount: layers.length,
            currentLayer,
            extents: extents(),
            selectionCount: 0,
            revision: document.revision,
          },
        });
      }
      case 'list_layers':
        return result({
          data: {
            layers: layers.map((layer) => ({
              name: layer.name,
              color: layer.color,
              visible: true,
              locked: false,
              current: layer.name === currentLayer,
              entityCount: [...entities.values()].filter((entity) => entity.layer === layer.name).length,
            })),
          },
        });
      case 'inspect_entities': {
        const all = [...entities.values()];
        const offset = Number(args.offset ?? 0);
        const limit = Number(args.limit ?? 50);
        const page = all.slice(offset, offset + limit);
        return result({ data: { total: all.length, entities: page.map(summary), truncated: offset + page.length < all.length } });
      }
      case 'create_layer': {
        const name = String(args.name ?? '');
        if (layers.some((layer) => layer.name.toLowerCase() === name.toLowerCase())) {
          return failed(`A layer named "${name}" already exists.`);
        }
        const layer = { name, color: typeof args.color === 'string' ? args.color : '#ffffff' };
        layers.push(layer);
        if (args.setCurrent === true) currentLayer = name;
        changed();
        return result({ data: { layer: { ...layer, visible: true, locked: false, current: currentLayer === name, entityCount: 0 } } });
      }
      case 'create_entities': {
        const items = Array.isArray(args.entities) ? args.entities.map(asRecord) : [];
        const defaultLayer = typeof args.layer === 'string' ? args.layer : currentLayer;
        const missing = items
          .map((item) => (typeof item.layer === 'string' ? item.layer : defaultLayer))
          .find((name) => !layers.some((layer) => layer.name === name));
        if (missing !== undefined) return failed(`Layer "${missing}" does not exist.`);
        const created = items.map((item) =>
          addEntity(String(item.type), typeof item.layer === 'string' ? item.layer : defaultLayer, boundsOf(item)),
        );
        changed();
        return result({
          createdIds: created.map((entity) => entity.id),
          data: { entities: created.map((entity) => ({ id: entity.id, type: entity.type })) },
        });
      }
      case 'delete_entities': {
        const ids = Array.isArray(args.ids) ? args.ids.map(String) : [];
        const unknown = ids.filter((id) => !entities.has(id));
        if (unknown.length > 0) return failed(`No entity with id ${unknown.join(', ')}.`);
        for (const id of ids) entities.delete(id);
        changed();
        return result({ deletedIds: ids, data: { deletedCount: ids.length } });
      }
      case 'zoom_extents':
        return result({ data: {} });
      default:
        return failed(`The fake CAD does not implement ${params.name}.`);
    }
  };

  const previousHome = process.env.CADCOPILOT_HOME;
  process.env.CADCOPILOT_HOME = options.homeDir;
  try {
    server = await startConnectorServer({
      cad,
      displayName,
      cadVersion: '9.9.9',
      connectorVersion: '1.0.0',
      handlers: {
        hello: async (): Promise<HelloResult> => ({
          cad,
          cadVersion: '9.9.9',
          connectorVersion: '1.0.0',
          protocolVersion: BRIDGE_PROTOCOL_VERSION,
          document: { ...document },
          supportedTools: [...FAKE_DRAWING_TOOLS],
        }),
        callTool,
        getWindowBounds: async () => ({ ...bounds }),
        setWindowBounds: async (next) => {
          setBoundsCalls.push({ ...next });
          bounds = { ...next };
        },
        focusWindow: async () => undefined,
      },
    });
  } finally {
    if (previousHome === undefined) delete process.env.CADCOPILOT_HOME;
    else process.env.CADCOPILOT_HOME = previousHome;
  }

  const running = server;
  let closed = false;
  return {
    id: running.advert.id,
    cad,
    displayName,
    toolCalls,
    setBoundsCalls,
    initialBounds,
    document: () => ({ ...document }),
    entityIds: () => [...entities.keys()],
    layerNames: () => layers.map((layer) => layer.name),
    bounds: () => ({ ...bounds }),
    externalEdit: () => {
      addEntity('line', currentLayer, { min: { x: -500, y: -500 }, max: { x: 0, y: -500 } });
      changed();
    },
    clientCount: () => running.clientCount,
    close: async () => {
      if (closed) return;
      closed = true;
      await running.close();
    },
  };
}
