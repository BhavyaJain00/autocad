/** Password field + Save for the Anthropic API key. The stored key is never shown. */
import { useEffect, useId, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { useBridge } from './BridgeContext';
import { Button, Spinner } from './ui';

interface ApiKeyFormProps {
  inputTestId: string;
  saveTestId: string;
  /** Label of the submit button when idle ('Save', 'Replace key', ...). */
  submitLabel: string;
  /** Called after the main process verified and stored the key. */
  onSaved?: () => void;
  autoFocus?: boolean;
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message.length > 0 ? error.message : String(error);
}

/**
 * Sends the typed key to the main process, which verifies and stores it. Shows 'Checking...'
 * while that runs and the returned message when the key is refused. The field is cleared as soon
 * as the key is stored; the renderer never keeps it.
 */
export function ApiKeyForm({ inputTestId, saveTestId, submitLabel, onSaved, autoFocus }: ApiKeyFormProps) {
  const bridge = useBridge();
  const [value, setValue] = useState('');
  const [checking, setChecking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  const errorId = useId();

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const key = value.trim();
  const submit = (event: FormEvent): void => {
    event.preventDefault();
    if (key.length === 0 || checking) return;
    setChecking(true);
    setError(null);
    bridge
      .setApiKey(key)
      .then(
        (result) => {
          if (!mounted.current) return;
          if (result.ok) {
            setValue('');
            onSaved?.();
          } else {
            setError(result.message);
          }
        },
        (failure: unknown) => {
          if (mounted.current) setError(errorMessage(failure));
        },
      )
      .finally(() => {
        if (mounted.current) setChecking(false);
      });
  };

  return (
    <form onSubmit={submit} className="flex flex-col gap-1.5" noValidate>
      <div className="flex items-center gap-1.5">
        <input
          type="password"
          value={value}
          onChange={(event) => {
            setValue(event.target.value);
            if (error !== null) setError(null);
          }}
          disabled={checking}
          autoFocus={autoFocus}
          autoComplete="off"
          spellCheck={false}
          placeholder="sk-ant-..."
          aria-label="Anthropic API key"
          aria-invalid={error !== null}
          aria-describedby={error !== null ? errorId : undefined}
          data-testid={inputTestId}
          className="h-8 min-w-0 flex-1 rounded-lg border border-line bg-app px-2.5 font-mono text-[12px] text-fg hover:border-line-strong disabled:opacity-60"
        />
        <Button
          type="submit"
          variant="primary"
          data-testid={saveTestId}
          disabled={key.length === 0 || checking}
          className="h-8 px-3"
        >
          {checking ? <Spinner size={12} /> : null}
          {checking ? 'Checking...' : submitLabel}
        </Button>
      </div>
      {error !== null ? (
        <p id={errorId} role="alert" data-testid={`${inputTestId}-error`} className="selectable m-0 text-[12px] text-danger wrap-anywhere">
          {error}
        </p>
      ) : null}
    </form>
  );
}
