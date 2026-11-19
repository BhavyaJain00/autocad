/** Small shared building blocks of the copilot UI. */
import { LoaderCircle } from 'lucide-react';
import type { ButtonHTMLAttributes, HTMLAttributes, ReactNode } from 'react';

/** Joins class names, skipping falsy entries. */
export function cx(...parts: (string | false | null | undefined)[]): string {
  return parts.filter(Boolean).join(' ');
}

/** A spinning ring; decorative unless `label` is given. */
export function Spinner({ size = 14, className, label }: { size?: number; className?: string; label?: string }) {
  return (
    <LoaderCircle
      size={size}
      className={cx('animate-spin-slow shrink-0', className)}
      role={label ? 'img' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    />
  );
}

interface IconButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  /** Accessible name; also the tooltip. */
  label: string;
  children: ReactNode;
}

/** A square, borderless icon button with an accessible name. */
export function IconButton({ label, children, className, type, ...rest }: IconButtonProps) {
  return (
    <button
      type={type ?? 'button'}
      aria-label={label}
      title={label}
      className={cx(
        'inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md text-fg-dim transition-colors',
        'hover:bg-hover hover:text-fg disabled:opacity-40 disabled:hover:bg-transparent disabled:hover:text-fg-dim',
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  children: ReactNode;
}

const BUTTON_VARIANTS: Record<ButtonVariant, string> = {
  primary: 'bg-accent text-on-accent hover:bg-accent-hover disabled:bg-card-2 disabled:text-fg-faint',
  secondary: 'border border-line-strong bg-card-2 text-fg hover:bg-hover disabled:text-fg-faint disabled:hover:bg-card-2',
  danger: 'border border-danger/50 text-danger hover:bg-danger/10 disabled:opacity-50',
  ghost: 'text-fg-dim hover:bg-hover hover:text-fg disabled:opacity-50',
};

/** A text button in one of the app's four styles. */
export function Button({ variant = 'secondary', className, type, children, ...rest }: ButtonProps) {
  return (
    <button
      type={type ?? 'button'}
      className={cx(
        'inline-flex h-7 shrink-0 items-center justify-center gap-1.5 rounded-md px-2.5 text-[12px] font-medium transition-colors',
        BUTTON_VARIANTS[variant],
        className,
      )}
      {...rest}
    >
      {children}
    </button>
  );
}

/** A bordered card on the slightly lighter surface. */
export function Card({ className, children, ...rest }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div className={cx('rounded-xl border border-line bg-card', className)} {...rest}>
      {children}
    </div>
  );
}
