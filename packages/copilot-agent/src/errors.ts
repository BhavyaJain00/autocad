// Plain-English descriptions of model/transport failures, classified with the SDK's typed error
// classes (most specific first) and the typed `type` field, never by matching message text.
import {
  APIConnectionError,
  APIConnectionTimeoutError,
  APIError,
  APIUserAbortError,
  AnthropicError,
  AuthenticationError,
  BadRequestError,
  ContentTooLargeError,
  InternalServerError,
  NotFoundError,
  OverloadedError,
  PermissionDeniedError,
  RateLimitError,
  RequestTimeoutError,
} from '@anthropic-ai/sdk';

/** A failure as shown to the user: what happened, and whether sending again can help. */
export interface ErrorDescription {
  message: string;
  retryable: boolean;
}

const INVALID_KEY = 'The Anthropic API key is invalid or has been revoked. Update it in Settings.';
const NO_PERMISSION =
  'This API key is not allowed to use the selected model. Check the access of your organisation in the Anthropic Console, or choose another model in Settings.';
const MODEL_UNAVAILABLE = 'The selected model is not available for this API key. Choose another model in Settings.';
const TOO_LARGE = 'The request is too large for the model. Remove large attachments or start a new conversation.';
const RATE_LIMITED = 'The Anthropic API is rate limiting this key. Wait a moment, then send again.';
const OVERLOADED = 'Claude is overloaded right now. Try again in a moment, or switch to another model in Settings.';
const TIMED_OUT = 'The Anthropic API did not answer in time. Check your connection and send again.';
const OFFLINE = 'Could not reach the Anthropic API. Check your internet connection and send again.';
const SERVER_ERROR = 'The Anthropic API had an internal error. Try again in a moment.';
const BILLING =
  'There is a billing problem with this Anthropic account. Check the plan and billing details in the Anthropic Console.';
const CANCELLED = 'The request was cancelled.';
const UNREADABLE = "The model's reply could not be read. Send again.";

function withDetail(prefix: string, detail: string | null | undefined): string {
  const text = typeof detail === 'string' ? detail.replace(/\s+/g, ' ').trim() : '';
  if (text.length === 0) return `${prefix}.`;
  const short = text.length > 300 ? `${text.slice(0, 297)}...` : text;
  return `${prefix}: ${short}`;
}

/**
 * Describes a failure of a model call for the user. Recognises invalid API keys, missing
 * permissions, unavailable models, oversized requests, rate limits, overload, timeouts, being
 * offline, billing problems and server errors.
 */
export function describeModelError(error: unknown): ErrorDescription {
  if (error instanceof APIUserAbortError) return { message: CANCELLED, retryable: true };
  if (error instanceof AuthenticationError) return { message: INVALID_KEY, retryable: false };
  if (error instanceof PermissionDeniedError) return { message: NO_PERMISSION, retryable: false };
  if (error instanceof NotFoundError) return { message: MODEL_UNAVAILABLE, retryable: false };
  if (error instanceof ContentTooLargeError) return { message: TOO_LARGE, retryable: false };
  if (error instanceof RateLimitError) return { message: RATE_LIMITED, retryable: true };
  if (error instanceof OverloadedError) return { message: OVERLOADED, retryable: true };
  if (error instanceof RequestTimeoutError) return { message: TIMED_OUT, retryable: true };
  if (error instanceof APIConnectionTimeoutError) return { message: TIMED_OUT, retryable: true };
  if (error instanceof APIConnectionError) return { message: OFFLINE, retryable: true };
  if (error instanceof BadRequestError) {
    return { message: withDetail('The Anthropic API rejected the request', error.apiMessage), retryable: false };
  }
  if (error instanceof InternalServerError) return { message: SERVER_ERROR, retryable: true };
  if (error instanceof APIError) {
    // Errors without a dedicated class (HTTP 402) and errors that arrive inside an already open
    // stream (no HTTP status) are classified by the typed `type` field of the API error.
    switch (error.type) {
      case 'authentication_error':
        return { message: INVALID_KEY, retryable: false };
      case 'permission_error':
        return { message: NO_PERMISSION, retryable: false };
      case 'not_found_error':
        return { message: MODEL_UNAVAILABLE, retryable: false };
      case 'billing_error':
        return { message: BILLING, retryable: false };
      case 'rate_limit_error':
        return { message: RATE_LIMITED, retryable: true };
      case 'overloaded_error':
        return { message: OVERLOADED, retryable: true };
      case 'timeout_error':
        return { message: TIMED_OUT, retryable: true };
      case 'api_error':
        return { message: SERVER_ERROR, retryable: true };
      case 'invalid_request_error':
        return { message: withDetail('The Anthropic API rejected the request', error.apiMessage), retryable: false };
      default:
        break;
    }
    if (error.status === 402) return { message: BILLING, retryable: false };
    const retryable = error.status === undefined || error.status >= 500;
    return { message: withDetail('The Anthropic API reported an error', error.apiMessage), retryable };
  }
  if (error instanceof AnthropicError) return { message: UNREADABLE, retryable: true };
  if (error instanceof Error && error.name === 'AbortError') return { message: CANCELLED, retryable: true };
  const detail = error instanceof Error ? error.message : typeof error === 'string' ? error : null;
  return { message: withDetail('Something went wrong while talking to the model', detail), retryable: true };
}
