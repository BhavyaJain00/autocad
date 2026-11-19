// The tools offered to the model: the catalog tools the connected CAD supports plus two local
// tools (ask_user, update_plan) that the session answers itself.
import { z } from 'zod';
import {
  TOOL_CATALOG,
  describeToolCall,
  formatValidationIssues,
  getTool,
  toolJsonSchema,
  validateToolArguments,
} from '@cadstudio/cad-protocol';
import type { ToolDefinition } from '@cadstudio/cad-protocol';
import type { ModelToolSpec, PlanTask, Question } from './types';

// ---------------------------------------------------------------------------------------------
// Tool source (injectable catalog)
// ---------------------------------------------------------------------------------------------

/** Result of validating tool arguments: the parsed value (defaults applied) or readable problems. */
export type ToolArgumentCheck = { ok: true; value: unknown } | { ok: false; errors: string[] };

/**
 * Where the session gets CAD tool definitions from. The default is the shared catalog of
 * `@cadstudio/cad-protocol`; tests and other embeddings can inject their own definitions.
 */
export interface ToolSource {
  /** Every tool name in the canonical (deterministic) order used for the model's tool list. */
  readonly names: readonly string[];
  /** Looks a tool up by name. */
  get(name: string): ToolDefinition | undefined;
  /** JSON Schema of the tool's arguments for the model. */
  jsonSchema(tool: ToolDefinition): Record<string, unknown>;
  /** Strictly validates the arguments the model produced. */
  validate(name: string, args: unknown): ToolArgumentCheck;
  /** One short human line describing the call (approval cards, activity log). */
  describe(name: string, args: unknown): string;
}

/** The shared CAD tool catalog (`TOOL_CATALOG`, in `TOOL_NAMES` order). */
export const CATALOG_TOOL_SOURCE: ToolSource = Object.freeze({
  names: Object.freeze(TOOL_CATALOG.map((tool) => tool.name)),
  get: (name: string) => getTool(name),
  jsonSchema: (tool: ToolDefinition) => toolJsonSchema(tool),
  validate: (name: string, args: unknown): ToolArgumentCheck => validateToolArguments(name, args),
  describe: (name: string, args: unknown) => describeToolCall(name, args),
});

/**
 * Builds a tool source from plain tool definitions (their zod `input` schemas are used for the
 * JSON Schema and for validation). The order of `definitions` is the canonical order.
 */
export function createToolSource(definitions: readonly ToolDefinition[]): ToolSource {
  const byName = new Map<string, ToolDefinition>();
  for (const definition of definitions) {
    if (byName.has(definition.name)) throw new Error(`Duplicate tool definition "${definition.name}"`);
    byName.set(definition.name, definition);
  }
  const names = Object.freeze([...byName.keys()]);
  return {
    names,
    get: (name) => byName.get(name),
    jsonSchema: (tool) => {
      const schema = z.toJSONSchema(tool.input, { io: 'input', unrepresentable: 'any' }) as Record<string, unknown>;
      delete schema.$schema;
      if (schema.type !== 'object') {
        throw new Error(`Tool "${tool.name}": the arguments schema must be an object schema`);
      }
      return schema;
    },
    validate: (name, args) => {
      const tool = byName.get(name);
      if (tool === undefined) return { ok: false, errors: [`Unknown tool "${name}"`] };
      const parsed = tool.input.safeParse(args === undefined || args === null ? {} : args);
      if (parsed.success) return { ok: true, value: parsed.data };
      return { ok: false, errors: formatValidationIssues(parsed.error.issues) };
    },
    describe: (name) => byName.get(name)?.title ?? `Run ${name}`,
  };
}

// ---------------------------------------------------------------------------------------------
// Local tools
// ---------------------------------------------------------------------------------------------

/** Name of the clarifying-question tool (answered in the copilot UI, never sent to a CAD). */
export const ASK_USER_TOOL = 'ask_user';
/** Name of the task-plan tool (shown as "Task Progress", never sent to a CAD). */
export const UPDATE_PLAN_TOOL = 'update_plan';
/** The tools the session answers itself, in the order they are offered. */
export const LOCAL_TOOL_NAMES: readonly string[] = [ASK_USER_TOOL, UPDATE_PLAN_TOOL];

/** True for a tool the session answers itself. */
export function isLocalTool(name: string): boolean {
  return name === ASK_USER_TOOL || name === UPDATE_PLAN_TOOL;
}

export const MAX_QUESTIONS = 4;
export const MIN_QUESTION_OPTIONS = 2;
export const MAX_QUESTION_OPTIONS = 6;
export const MAX_PLAN_TASKS = 12;

const questionOptionSchema = z.strictObject({
  label: z.string().trim().min(1).max(120),
  description: z.string().trim().max(300).optional(),
  recommended: z.boolean().optional(),
});

const askUserSchema = z.strictObject({
  questions: z
    .array(
      z.strictObject({
        header: z.string().trim().min(1).max(40),
        question: z.string().trim().min(1).max(600),
        options: z.array(questionOptionSchema).min(MIN_QUESTION_OPTIONS).max(MAX_QUESTION_OPTIONS),
        allowCustom: z.boolean().optional(),
      }),
    )
    .min(1)
    .max(MAX_QUESTIONS),
});

const updatePlanSchema = z.strictObject({
  tasks: z
    .array(
      z.strictObject({
        id: z.string().trim().min(1).max(40),
        title: z.string().trim().min(1).max(200),
        status: z.enum(['pending', 'in_progress', 'done', 'failed']),
      }),
    )
    .min(1)
    .max(MAX_PLAN_TASKS),
});

const ASK_USER_SPEC: ModelToolSpec = {
  name: ASK_USER_TOOL,
  description:
    'Ask the user one to four multiple-choice questions and wait for the answers. Call this when the request is ambiguous in a way that changes the result (a missing dimension, an unclear position, which objects are meant), before doing the work. Do not use it for things you can decide sensibly yourself. Give every question two to six concrete options and mark the one you recommend. The result lists each question with the answer the user gave (an option label or their own text).',
  inputSchema: {
    type: 'object',
    properties: {
      questions: {
        type: 'array',
        description: 'One to four questions, asked together.',
        minItems: 1,
        maxItems: MAX_QUESTIONS,
        items: {
          type: 'object',
          properties: {
            header: {
              type: 'string',
              description: 'Short label for the question card, at most 40 characters, e.g. "Wall thickness".',
            },
            question: { type: 'string', description: 'The full question, in plain language.' },
            options: {
              type: 'array',
              description: 'Two to six concrete answers to choose from.',
              minItems: MIN_QUESTION_OPTIONS,
              maxItems: MAX_QUESTION_OPTIONS,
              items: {
                type: 'object',
                properties: {
                  label: { type: 'string', description: 'The answer as shown on the button, e.g. "230 mm".' },
                  description: { type: 'string', description: 'Optional one-line explanation of this choice.' },
                  recommended: { type: 'boolean', description: 'True for the option you recommend (at most one).' },
                },
                required: ['label'],
                additionalProperties: false,
              },
            },
            allowCustom: {
              type: 'boolean',
              description: 'Whether the user may type their own answer instead (default true).',
            },
          },
          required: ['header', 'question', 'options'],
          additionalProperties: false,
        },
      },
    },
    required: ['questions'],
    additionalProperties: false,
  },
};

const UPDATE_PLAN_SPEC: ModelToolSpec = {
  name: UPDATE_PLAN_TOOL,
  description:
    'Publish or update the task plan the user sees as "Task Progress". Call this at the start of work that has several steps, and again whenever a status changes. Always send the complete list: it replaces the previous plan. Keep the task you are working on in_progress, and mark tasks done or failed as their tool results come in.',
  inputSchema: {
    type: 'object',
    properties: {
      tasks: {
        type: 'array',
        description: 'The complete plan, one to twelve tasks in working order.',
        minItems: 1,
        maxItems: MAX_PLAN_TASKS,
        items: {
          type: 'object',
          properties: {
            id: { type: 'string', description: 'Short stable id, unique in the plan, e.g. "walls".' },
            title: { type: 'string', description: 'What the task achieves, in a few words.' },
            status: { type: 'string', enum: ['pending', 'in_progress', 'done', 'failed'] },
          },
          required: ['id', 'title', 'status'],
          additionalProperties: false,
        },
      },
    },
    required: ['tasks'],
    additionalProperties: false,
  },
};

/** The model-facing definitions of the local tools, in the order they are offered. */
export const LOCAL_TOOL_SPECS: readonly ModelToolSpec[] = [ASK_USER_SPEC, UPDATE_PLAN_SPEC];

/** Result of parsing the arguments of a local tool. */
export type LocalToolParse<T> = { ok: true; value: T } | { ok: false; errors: string[] };

/**
 * Validates `ask_user` arguments and turns them into questions for the UI (ids `q1`..`q4`,
 * upper-case headers, `allowCustom` defaulting to true).
 */
export function parseAskUser(input: unknown): LocalToolParse<Question[]> {
  const parsed = askUserSchema.safeParse(input ?? {});
  if (!parsed.success) return { ok: false, errors: formatValidationIssues(parsed.error.issues) };
  const questions = parsed.data.questions.map((question, index): Question => ({
    id: `q${index + 1}`,
    header: question.header.toUpperCase(),
    question: question.question,
    options: question.options.map((option) => ({
      label: option.label,
      ...(option.description !== undefined && option.description.length > 0
        ? { description: option.description }
        : {}),
      ...(option.recommended === true ? { recommended: true } : {}),
    })),
    allowCustom: question.allowCustom ?? true,
  }));
  return { ok: true, value: questions };
}

/** Validates `update_plan` arguments (task ids must be unique within the plan). */
export function parseUpdatePlan(input: unknown): LocalToolParse<PlanTask[]> {
  const parsed = updatePlanSchema.safeParse(input ?? {});
  if (!parsed.success) return { ok: false, errors: formatValidationIssues(parsed.error.issues) };
  const seen = new Set<string>();
  const duplicates = new Set<string>();
  for (const task of parsed.data.tasks) {
    if (seen.has(task.id)) duplicates.add(task.id);
    seen.add(task.id);
  }
  if (duplicates.size > 0) {
    const ids = [...duplicates].map((id) => `"${id}"`).join(', ');
    return { ok: false, errors: [`tasks: every task needs a unique id (duplicated: ${ids})`] };
  }
  return { ok: true, value: parsed.data.tasks.map((task) => ({ id: task.id, title: task.title, status: task.status })) };
}

// ---------------------------------------------------------------------------------------------
// The model's tool list
// ---------------------------------------------------------------------------------------------

/**
 * Names of the CAD tools to offer: the source's tools the connected CAD supports, in the source's
 * canonical order (never in the order the connector happened to list them), without duplicates.
 * `supported` null means no CAD is connected.
 */
export function offeredCadToolNames(source: ToolSource, supported: readonly string[] | null): string[] {
  if (supported === null) return [];
  const wanted = new Set(supported);
  return source.names.filter((name) => wanted.has(name) && !isLocalTool(name) && source.get(name) !== undefined);
}

/**
 * The tool list for a model request: the supported CAD tools in canonical order, then `ask_user`
 * and `update_plan`. The result depends only on the source and the set of supported names, so the
 * same CAD always yields byte-identical definitions (a requirement for prompt caching).
 */
export function buildModelTools(source: ToolSource, supported: readonly string[] | null): ModelToolSpec[] {
  const tools: ModelToolSpec[] = [];
  for (const name of offeredCadToolNames(source, supported)) {
    const tool = source.get(name);
    if (tool === undefined) continue;
    tools.push({ name: tool.name, description: tool.description, inputSchema: source.jsonSchema(tool) });
  }
  // Copies, so that nothing a caller does to the list can change the shared definitions.
  for (const spec of LOCAL_TOOL_SPECS) tools.push(structuredClone(spec));
  return tools;
}
