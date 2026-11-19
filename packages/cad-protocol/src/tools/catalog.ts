// The tool catalog: every tool keyed by name, the ordered list, lookup, JSON Schema export and
// argument validation. Browser-safe.
import { z } from 'zod';
import type { ToolDefinition } from '../core';
import { architectureTools } from './architecture';
import { geometryTools } from './geometry';
import { inspectionTools } from './inspection';
import { annotationTools, documentTools, layerTools, validationTools, viewTools } from './misc';
import { modificationTools } from './modification';
import { TOOL_NAMES, isToolName } from './names';
import type { ToolName } from './names';
import type { CatalogTool } from './shared';

/** Every catalog tool keyed by its name, with exact schema types (see ToolInput / ToolOutput). */
export const TOOLS = {
  ...inspectionTools,
  ...geometryTools,
  ...architectureTools,
  ...modificationTools,
  ...annotationTools,
  ...layerTools,
  ...validationTools,
  ...viewTools,
  ...documentTools,
} as const satisfies { readonly [N in ToolName]: CatalogTool<N> };

/** Validated arguments of tool `N` (after defaults were applied). */
export type ToolInput<N extends ToolName> = z.output<(typeof TOOLS)[N]['input']>;
/** Arguments of tool `N` as a caller may write them (fields with defaults are optional). */
export type ToolArguments<N extends ToolName> = z.input<(typeof TOOLS)[N]['input']>;
/** Shape of `ToolResult.data` of tool `N`. */
export type ToolOutput<N extends ToolName> = z.output<(typeof TOOLS)[N]['output']>;

/** All catalog tools with strict zod schemas, in TOOL_NAMES order. */
export const TOOL_CATALOG: readonly ToolDefinition[] = Object.freeze(TOOL_NAMES.map((name) => TOOLS[name]));

/** Looks a tool up by name; undefined for names that are not in the catalog. */
export function getTool(name: string): ToolDefinition | undefined {
  return isToolName(name) ? TOOLS[name] : undefined;
}

// ---------------------------------------------------------------------------------------------
// JSON Schema
// ---------------------------------------------------------------------------------------------

/** Keywords kept in exported schemas: the subset every LLM tool API and validator understands. */
const JSON_SCHEMA_KEYWORDS: ReadonlySet<string> = new Set([
  'type',
  'enum',
  'const',
  'properties',
  'required',
  'items',
  'anyOf',
  'minimum',
  'maximum',
  'exclusiveMinimum',
  'minItems',
  'maxItems',
  'minLength',
  'maxLength',
  'description',
  'default',
  'additionalProperties',
]);

type JsonObject = Record<string, unknown>;

function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function cloneJson<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

/** Reduces a zod-generated schema node to the supported keywords and closes every object. */
function simplifySchema(node: unknown): JsonObject {
  if (!isJsonObject(node)) return {};
  const out: JsonObject = {};
  for (const [key, value] of Object.entries(node)) {
    if (key === 'oneOf' || key === 'anyOf') {
      // Discriminated unions are emitted as oneOf; anyOf is equivalent here and more widely supported.
      if (Array.isArray(value)) out.anyOf = value.map(simplifySchema);
      continue;
    }
    if (!JSON_SCHEMA_KEYWORDS.has(key)) continue;
    if (key === 'properties') {
      const properties: JsonObject = {};
      if (isJsonObject(value)) {
        for (const [name, child] of Object.entries(value)) properties[name] = simplifySchema(child);
      }
      out.properties = properties;
    } else if (key === 'items') {
      out.items = simplifySchema(value);
    } else if (key === 'additionalProperties') {
      // Normalised below for every object node.
    } else if (key === 'default' || key === 'const' || key === 'enum' || key === 'required') {
      out[key] = cloneJson(value);
    } else {
      out[key] = value;
    }
  }
  if (out.type === 'object') {
    if (!isJsonObject(out.properties)) out.properties = {};
    if (!Array.isArray(out.required)) out.required = [];
    out.additionalProperties = false;
  }
  return out;
}

const jsonSchemaCache = new WeakMap<ToolDefinition, JsonObject>();

/**
 * JSON Schema of the tool's arguments, suitable for an LLM tool definition and for validators in
 * other languages: `type: "object"`, described properties, a `required` list and
 * `additionalProperties: false` at every object level, restricted to widely supported keywords
 * (type, enum, const, properties, required, items, anyOf, minimum, maximum, exclusiveMinimum,
 * minItems, maxItems, minLength, maxLength, description, default). Cross-field rules (for example
 * "at least one of layer and color") cannot be expressed in this subset; they are stated in the
 * descriptions and enforced by {@link validateToolArguments}. Returns a fresh object every call.
 */
export function toolJsonSchema(def: ToolDefinition): Record<string, unknown> {
  let schema = jsonSchemaCache.get(def);
  if (schema === undefined) {
    schema = simplifySchema(z.toJSONSchema(def.input, { io: 'input', unrepresentable: 'any' }));
    if (schema.type !== 'object') {
      throw new Error(`Tool "${def.name}": the arguments schema must be an object schema`);
    }
    jsonSchemaCache.set(def, schema);
  }
  return cloneJson(schema);
}

// ---------------------------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------------------------

/** Most error strings validateToolArguments returns. */
export const MAX_VALIDATION_ERRORS = 20;

/** Result of validateToolArguments. */
export type ToolValidation<T = unknown> = { ok: true; value: T } | { ok: false; errors: string[] };

function formatPath(path: readonly PropertyKey[]): string {
  let text = '';
  for (const segment of path) {
    if (typeof segment === 'number') text += `[${segment}]`;
    else text += text.length === 0 ? String(segment) : `.${String(segment)}`;
  }
  return text;
}

const MISSING_SUFFIX = ', received undefined';

function formatIssue(issue: z.core.$ZodIssue): string {
  const path = formatPath(issue.path);
  let message = issue.message;
  if (issue.code === 'invalid_type' && message.endsWith(MISSING_SUFFIX)) {
    message = `is required (expected ${issue.expected})`;
  } else if (issue.code === 'unrecognized_keys') {
    const keys = issue.keys.map((key) => `"${key}"`).join(', ');
    message = `${issue.keys.length === 1 ? 'Unknown argument' : 'Unknown arguments'} ${keys}`;
  }
  return path.length === 0 ? message : `${path}: ${message}`;
}

/** Turns zod issues into at most MAX_VALIDATION_ERRORS readable `path: message` strings. */
export function formatValidationIssues(issues: readonly z.core.$ZodIssue[]): string[] {
  const lines: string[] = [];
  const seen = new Set<string>();
  for (const issue of issues) {
    const line = formatIssue(issue);
    if (seen.has(line)) continue;
    seen.add(line);
    lines.push(line);
  }
  if (lines.length <= MAX_VALIDATION_ERRORS) return lines;
  const kept = lines.slice(0, MAX_VALIDATION_ERRORS - 1);
  kept.push(`... and ${lines.length - kept.length} more problems`);
  return kept;
}

/**
 * Validates tool arguments with the tool's strict zod schema (including the cross-field rules).
 * Returns the parsed value with defaults applied, or readable `path: message` strings (at most
 * 20). An unknown tool yields `Unknown tool "<name>"`. Missing arguments (`undefined` / `null`)
 * are treated as `{}` so tools without arguments accept them and the others report what is missing.
 */
export function validateToolArguments<N extends ToolName>(name: N, args: unknown): ToolValidation<ToolInput<N>>;
export function validateToolArguments(name: string, args: unknown): ToolValidation;
export function validateToolArguments(name: string, args: unknown): ToolValidation {
  const tool = getTool(name);
  if (tool === undefined) return { ok: false, errors: [`Unknown tool "${name}"`] };
  const result = tool.input.safeParse(args === undefined || args === null ? {} : args);
  if (result.success) return { ok: true, value: result.data };
  return { ok: false, errors: formatValidationIssues(result.error.issues) };
}
