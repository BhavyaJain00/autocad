import { describe, it, expect } from 'vitest';
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
  ServiceUnavailableError,
} from '@anthropic-ai/sdk';
import { describeModelError } from './errors';

const headers = new Headers();
const body = (type: string, message: string) => ({ type: 'error', error: { type, message } });

describe('describeModelError', () => {
  it('invalid API key (401)', () => {
    const error = new AuthenticationError(401, body('authentication_error', 'invalid x-api-key'), undefined, headers);
    expect(describeModelError(error)).toEqual({
      message: 'The Anthropic API key is invalid or has been revoked. Update it in Settings.',
      retryable: false,
    });
  });

  it('no permission (403)', () => {
    const description = describeModelError(new PermissionDeniedError(403, body('permission_error', 'nope'), undefined, headers));
    expect(description.message).toContain('not allowed to use the selected model');
    expect(description.retryable).toBe(false);
  });

  it('model unavailable (404)', () => {
    const error = new NotFoundError(404, body('not_found_error', 'model: claude-x'), undefined, headers);
    expect(describeModelError(error)).toEqual({
      message: 'The selected model is not available for this API key. Choose another model in Settings.',
      retryable: false,
    });
  });

  it('request too large (413)', () => {
    const description = describeModelError(new ContentTooLargeError(413, body('request_too_large', 'too big'), undefined, headers));
    expect(description.message).toContain('too large');
    expect(description.retryable).toBe(false);
  });

  it('rate limited (429)', () => {
    const error = new RateLimitError(429, body('rate_limit_error', 'slow'), undefined, headers);
    expect(describeModelError(error)).toEqual({
      message: 'The Anthropic API is rate limiting this key. Wait a moment, then send again.',
      retryable: true,
    });
  });

  it('overloaded (529) is not mistaken for a generic server error', () => {
    const description = describeModelError(new OverloadedError(529, body('overloaded_error', 'busy'), undefined, headers));
    expect(description.message).toContain('overloaded');
    expect(description.retryable).toBe(true);
    expect(new OverloadedError(529, {}, undefined, headers)).toBeInstanceOf(InternalServerError);
  });

  it('server errors (5xx)', () => {
    const errors = [new InternalServerError(500, {}, undefined, headers), new ServiceUnavailableError(503, {}, undefined, headers)];
    for (const error of errors) {
      expect(describeModelError(error)).toEqual({
        message: 'The Anthropic API had an internal error. Try again in a moment.',
        retryable: true,
      });
    }
  });

  it('offline and timeouts', () => {
    expect(describeModelError(new APIConnectionError({ message: 'fetch failed' }))).toEqual({
      message: 'Could not reach the Anthropic API. Check your internet connection and send again.',
      retryable: true,
    });
    const timeout = describeModelError(new APIConnectionTimeoutError());
    expect(timeout.message).toContain('did not answer in time');
    expect(timeout.retryable).toBe(true);
    expect(describeModelError(new RequestTimeoutError(408, {}, undefined, headers)).message).toContain('did not answer in time');
  });

  it('bad request (400) includes what the API said', () => {
    const error = new BadRequestError(400, body('invalid_request_error', 'messages.3: tool_use ids must be unique'), undefined, headers);
    expect(describeModelError(error)).toEqual({
      message: 'The Anthropic API rejected the request: messages.3: tool_use ids must be unique',
      retryable: false,
    });
    expect(describeModelError(new BadRequestError(400, {}, undefined, headers)).message).toBe('The Anthropic API rejected the request.');
  });

  it('billing problems (402 has no class of its own)', () => {
    const error = APIError.generate(402, body('billing_error', 'credit balance too low'), undefined, headers);
    expect(describeModelError(error)).toEqual({
      message: 'There is a billing problem with this Anthropic account. Check the plan and billing details in the Anthropic Console.',
      retryable: false,
    });
    expect(describeModelError(new APIError(402, {}, undefined, headers)).message).toContain('billing problem');
  });

  it('the SDK builds the typed classes this mapping relies on', () => {
    expect(APIError.generate(401, {}, undefined, headers)).toBeInstanceOf(AuthenticationError);
    expect(APIError.generate(404, {}, undefined, headers)).toBeInstanceOf(NotFoundError);
    expect(APIError.generate(429, {}, undefined, headers)).toBeInstanceOf(RateLimitError);
    expect(APIError.generate(529, {}, undefined, headers)).toBeInstanceOf(OverloadedError);
    expect(APIError.generate(500, {}, undefined, headers)).toBeInstanceOf(InternalServerError);
    expect(APIError.generate(undefined, undefined, 'offline', undefined)).toBeInstanceOf(APIConnectionError);
  });

  it('errors that arrive inside an open stream are classified by their typed error type', () => {
    const streamed = (type: string) => new APIError(undefined, body(type, 'x'), undefined, headers, type as never);
    expect(describeModelError(streamed('overloaded_error')).message).toContain('overloaded');
    expect(describeModelError(streamed('overloaded_error')).retryable).toBe(true);
    expect(describeModelError(streamed('rate_limit_error')).message).toContain('rate limiting');
    expect(describeModelError(streamed('api_error')).message).toContain('internal error');
    expect(describeModelError(streamed('authentication_error')).retryable).toBe(false);
    expect(describeModelError(streamed('permission_error')).retryable).toBe(false);
    expect(describeModelError(streamed('not_found_error')).message).toContain('not available');
    expect(describeModelError(streamed('timeout_error')).retryable).toBe(true);
    expect(describeModelError(streamed('invalid_request_error')).message).toBe('The Anthropic API rejected the request: x');
  });

  it('unknown API errors keep the API message and are retryable only when they may be transient', () => {
    const teapot = describeModelError(new APIError(418, body('teapot_error', 'short and stout'), undefined, headers));
    expect(teapot).toEqual({ message: 'The Anthropic API reported an error: short and stout', retryable: false });
    expect(describeModelError(new APIError(undefined, undefined, undefined, headers)).retryable).toBe(true);
  });

  it('user aborts, unreadable replies and everything else', () => {
    expect(describeModelError(new APIUserAbortError())).toEqual({ message: 'The request was cancelled.', retryable: true });
    expect(describeModelError(new AnthropicError('Unable to parse tool parameter JSON from model.'))).toEqual({
      message: "The model's reply could not be read. Send again.",
      retryable: true,
    });
    const abort = new Error('aborted');
    abort.name = 'AbortError';
    expect(describeModelError(abort).message).toBe('The request was cancelled.');
    expect(describeModelError(new TypeError('x is not a function'))).toEqual({
      message: 'Something went wrong while talking to the model: x is not a function',
      retryable: true,
    });
    expect(describeModelError('plain failure').message).toBe('Something went wrong while talking to the model: plain failure');
    expect(describeModelError(undefined).message).toBe('Something went wrong while talking to the model.');
  });

  it('never puts very long API messages into the UI', () => {
    const error = new BadRequestError(400, body('invalid_request_error', 'y'.repeat(2000)), undefined, headers);
    const description = describeModelError(error);
    expect(description.message.length).toBeLessThan(360);
    expect(description.message.endsWith('...')).toBe(true);
  });
});
