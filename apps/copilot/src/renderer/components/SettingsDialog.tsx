/** Settings: API key, model, effort, approvals, connection and window behaviour, about. */
import { Check, X } from 'lucide-react';
import { useEffect, useId, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type { ApprovalMode, Effort, ModelId } from '@cadstudio/copilot-agent';
import { APPROVAL_CHOICES, EFFORT_CHOICES, MODEL_CHOICES } from '../../shared/bridge';
import type { CopilotSettings, SettingsPatch } from '../../shared/bridge';
import { API_KEY_URL } from '../lib/links';
import { ApiKeyForm } from './ApiKeyForm';
import { useBridge } from './BridgeContext';
import { Button, IconButton, Spinner, cx } from './ui';

interface SettingsDialogProps {
  settings: CopilotSettings;
  appVersion: string;
  onUpdateSettings: (patch: SettingsPatch) => void;
  onClose: () => void;
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-2 border-t border-line px-4 py-3.5 first:border-t-0">
      <h3 className="m-0 text-[10px] font-semibold uppercase tracking-[0.08em] text-fg-faint">{title}</h3>
      {children}
    </section>
  );
}

interface ToggleProps {
  label: string;
  hint: string;
  checked: boolean;
  testId: string;
  onChange: (checked: boolean) => void;
}

function Toggle({ label, hint, checked, testId, onChange }: ToggleProps) {
  const labelId = useId();
  const hintId = useId();
  return (
    <div className="flex items-start gap-3">
      <div className="min-w-0 flex-1">
        <div id={labelId} className="text-fg">
          {label}
        </div>
        <div id={hintId} className="text-[12px] text-fg-dim">
          {hint}
        </div>
      </div>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-labelledby={labelId}
        aria-describedby={hintId}
        data-testid={testId}
        onClick={() => onChange(!checked)}
        className={cx(
          'relative mt-0.5 h-[18px] w-8 shrink-0 rounded-full transition-colors',
          checked ? 'bg-accent' : 'bg-line-strong',
        )}
      >
        <span
          aria-hidden
          className={cx(
            'absolute left-0.5 top-0.5 h-3.5 w-3.5 rounded-full bg-white transition-transform',
            checked && 'translate-x-3.5',
          )}
        />
      </button>
    </div>
  );
}

function ApiKeySection({ settings }: { settings: CopilotSettings }) {
  const bridge = useBridge();
  const [removing, setRemoving] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [justSaved, setJustSaved] = useState(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const remove = (): void => {
    if (removing) return;
    setRemoving(true);
    setRemoveError(null);
    setJustSaved(false);
    bridge
      .setApiKey(null)
      .then(
        (result) => {
          if (mounted.current && !result.ok) setRemoveError(result.message);
        },
        (failure: unknown) => {
          if (mounted.current) setRemoveError(failure instanceof Error ? failure.message : String(failure));
        },
      )
      .finally(() => {
        if (mounted.current) setRemoving(false);
      });
  };

  return (
    <Section title="Anthropic API key">
      <div className="flex items-center gap-2">
        <div data-testid="settings-api-key-status" data-has-key={settings.hasApiKey ? 'true' : 'false'} className="min-w-0 flex-1">
          {settings.hasApiKey ? (
            <span className="inline-flex items-center gap-1.5 text-fg">
              <Check size={13} aria-hidden className="text-success" />
              {justSaved ? 'Key saved.' : 'A key is stored on this computer.'}
            </span>
          ) : (
            <span className="text-fg-dim">No key is stored.</span>
          )}
        </div>
        {settings.hasApiKey ? (
          <Button variant="danger" data-testid="settings-api-key-remove" disabled={removing} onClick={remove}>
            {removing ? <Spinner size={12} /> : null}
            Remove
          </Button>
        ) : null}
      </div>
      <ApiKeyForm
        inputTestId="settings-api-key-input"
        saveTestId="settings-api-key-save"
        submitLabel={settings.hasApiKey ? 'Replace' : 'Save'}
        onSaved={() => setJustSaved(true)}
      />
      {removeError !== null ? (
        <p role="alert" className="selectable m-0 text-[12px] text-danger wrap-anywhere">
          {removeError}
        </p>
      ) : null}
      <p className="m-0 text-[12px] text-fg-dim">
        The key is verified, encrypted and kept by the app; it is never shown again.{' '}
        <button
          type="button"
          onClick={() => bridge.openExternal(API_KEY_URL)}
          className="rounded text-accent-soft underline decoration-accent-soft/40 underline-offset-2 hover:decoration-current"
        >
          Get an API key
        </button>
        {settings.testModel ? ' A scripted test model is active, so no key is used right now.' : ''}
      </p>
    </Section>
  );
}

/** Elements the Tab key can reach inside the dialog. */
const FOCUSABLE_SELECTOR =
  'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])';

const FIELD_CLASS =
  'select-pill h-8 w-full rounded-lg border border-line bg-app text-fg transition-colors hover:border-line-strong';

/**
 * The settings dialog. Changes apply immediately (there is no Save button). Escape or the close
 * button dismisses it and focus returns to where it was.
 */
export function SettingsDialog({ settings, appVersion, onUpdateSettings, onClose }: SettingsDialogProps) {
  const dialogRef = useRef<HTMLDivElement | null>(null);
  const titleId = useId();
  const modelId = useId();
  const effortId = useId();

  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialogRef.current?.focus();
    return () => previous?.focus();
  }, []);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onClose();
        return;
      }
      if (event.key !== 'Tab') return;
      // Keep keyboard focus inside the dialog while it is open.
      const dialog = dialogRef.current;
      if (!dialog) return;
      const focusable = Array.from(dialog.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (first === undefined || last === undefined) {
        event.preventDefault();
        return;
      }
      const active = document.activeElement;
      const inside = active instanceof Node && dialog.contains(active) && active !== dialog;
      if (event.shiftKey && (!inside || active === first)) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && (!inside || active === last)) {
        event.preventDefault();
        first.focus();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [onClose]);

  const modelHint = MODEL_CHOICES.find((choice) => choice.id === settings.model)?.hint ?? '';

  return (
    <div
      className="fixed inset-x-0 bottom-0 top-10 z-50 flex items-start justify-center overflow-y-auto bg-black/60 p-3"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) onClose();
      }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        data-testid="settings-dialog"
        className="animate-pop flex w-full max-w-[440px] flex-col rounded-xl border border-line-strong bg-card shadow-[0_16px_40px_rgba(0,0,0,0.6)]"
      >
        <div className="flex h-11 shrink-0 items-center gap-2 border-b border-line pl-4 pr-2">
          <h2 id={titleId} className="m-0 min-w-0 flex-1 text-[14px] font-semibold text-fg">
            Settings
          </h2>
          <IconButton label="Close settings" data-testid="settings-close" onClick={onClose}>
            <X size={15} aria-hidden />
          </IconButton>
        </div>

        <ApiKeySection settings={settings} />

        <Section title="Model">
          <label htmlFor={modelId} className="text-fg">
            Default model
          </label>
          <select
            id={modelId}
            data-testid="settings-model"
            value={settings.model}
            onChange={(event) => onUpdateSettings({ model: event.target.value as ModelId })}
            className={FIELD_CLASS}
          >
            {MODEL_CHOICES.map((choice) => (
              <option key={choice.id} value={choice.id}>
                {choice.label}
              </option>
            ))}
          </select>
          {modelHint ? <div className="-mt-1 text-[12px] text-fg-dim">{modelHint}</div> : null}
          <label htmlFor={effortId} className="mt-1 text-fg">
            Reasoning effort
          </label>
          <select
            id={effortId}
            data-testid="settings-effort"
            value={settings.effort}
            onChange={(event) => onUpdateSettings({ effort: event.target.value as Effort })}
            className={FIELD_CLASS}
          >
            {EFFORT_CHOICES.map((choice) => (
              <option key={choice.id} value={choice.id}>
                {choice.label}
              </option>
            ))}
          </select>
          <div className="-mt-1 text-[12px] text-fg-dim">Higher effort thinks longer before acting and uses more tokens.</div>
        </Section>

        <Section title="Approvals">
          <div role="radiogroup" aria-label="Approval mode" className="flex flex-col gap-1">
            {APPROVAL_CHOICES.map((choice) => {
              const selected = settings.approvalMode === choice.id;
              return (
                <button
                  key={choice.id}
                  type="button"
                  role="radio"
                  aria-checked={selected}
                  data-testid={`settings-approval-${choice.id}`}
                  onClick={() => onUpdateSettings({ approvalMode: choice.id as ApprovalMode })}
                  className={cx(
                    'flex items-start gap-2 rounded-lg border px-2.5 py-1.5 text-left transition-colors',
                    selected ? 'border-accent bg-accent/10' : 'border-line bg-card-2 hover:border-line-strong hover:bg-hover',
                  )}
                >
                  <span
                    aria-hidden
                    className={cx(
                      'mt-[3px] flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded-full border',
                      selected ? 'border-accent' : 'border-fg-faint',
                    )}
                  >
                    {selected ? <span className="h-1.5 w-1.5 rounded-full bg-accent" /> : null}
                  </span>
                  <span className="min-w-0">
                    <span className="block text-fg">{choice.label}</span>
                    <span className="block text-[12px] text-fg-dim">{choice.hint}</span>
                  </span>
                </button>
              );
            })}
          </div>
        </Section>

        <Section title="Connection and window">
          <Toggle
            label="Connect automatically"
            hint="Connect when exactly one CAD application is running."
            checked={settings.autoConnect}
            testId="settings-auto-connect"
            onChange={(autoConnect) => onUpdateSettings({ autoConnect })}
          />
          <Toggle
            label="Dock beside the CAD window"
            hint="After connecting, place this panel at the right of the CAD window."
            checked={settings.autoDock}
            testId="settings-auto-dock"
            onChange={(autoDock) => onUpdateSettings({ autoDock })}
          />
          <Toggle
            label="Always on top"
            hint="Keep this window above other windows."
            checked={settings.alwaysOnTop}
            testId="settings-always-on-top"
            onChange={(alwaysOnTop) => onUpdateSettings({ alwaysOnTop })}
          />
        </Section>

        <Section title="About">
          <div data-testid="settings-about" className="selectable text-fg-dim">
            <span className="text-fg">CAD Copilot</span>
            {appVersion ? ` version ${appVersion}` : ''}
          </div>
          <div className="text-[12px] text-fg-faint">
            The copilot only reports changes the CAD application confirmed. It does not check building-code compliance.
          </div>
        </Section>
      </div>
    </div>
  );
}
