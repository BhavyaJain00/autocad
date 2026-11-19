/** "Task Progress (done/total)": the assistant's plan with a status icon per task. */
import { Check, ChevronDown, Circle, CircleDashed, X } from 'lucide-react';
import { memo, useState } from 'react';
import type { PlanTask } from '@cadstudio/copilot-agent';
import { planProgress } from '../state/transcript';
import { Spinner, cx } from './ui';

interface TaskProgressProps {
  tasks: readonly PlanTask[];
  /** True while the plan's turn is running: in-progress tasks spin and the card can be collapsed. */
  live: boolean;
}

const STATUS_WORDS: Record<PlanTask['status'], string> = {
  pending: 'Pending',
  in_progress: 'In progress',
  done: 'Done',
  failed: 'Failed',
};

function TaskIcon({ status, live }: { status: PlanTask['status']; live: boolean }) {
  switch (status) {
    case 'done':
      return (
        <span className="flex h-3.5 w-3.5 items-center justify-center rounded-full bg-success">
          <Check size={10} strokeWidth={3.5} aria-hidden className="text-app" />
        </span>
      );
    case 'failed':
      return <X size={14} strokeWidth={2.5} aria-hidden className="text-danger" />;
    case 'in_progress':
      // A turn that is over cannot be working on a task any more: no spinner in the transcript.
      return live ? (
        <Spinner size={14} className="text-accent-soft" />
      ) : (
        <CircleDashed size={14} aria-hidden className="text-fg-dim" />
      );
    default:
      return <Circle size={14} aria-hidden className="text-fg-faint" />;
  }
}

/**
 * The task list driven by the latest plan update. Pinned above the composer while its turn runs
 * (`live`), part of the transcript afterwards.
 */
export const TaskProgress = memo(function TaskProgress({ tasks, live }: TaskProgressProps) {
  const [collapsed, setCollapsed] = useState(false);
  const { done, total } = planProgress(tasks);
  const title = `Task Progress (${done}/${total})`;
  const showTasks = !(live && collapsed);

  return (
    <section
      data-testid="task-progress"
      data-live={live ? 'true' : 'false'}
      data-done={done}
      data-total={total}
      aria-label={title}
      className="rounded-xl border border-line bg-card"
    >
      <div className="flex h-8 items-center gap-2 pl-3 pr-1.5">
        <h3 className="min-w-0 flex-1 truncate text-[12px] font-semibold text-fg">{title}</h3>
        {total > 0 ? (
          <div
            aria-hidden
            className="h-1 w-14 shrink-0 overflow-hidden rounded-full bg-line"
          >
            <div className="h-full rounded-full bg-success transition-[width]" style={{ width: `${(done / total) * 100}%` }} />
          </div>
        ) : null}
        {live ? (
          <button
            type="button"
            aria-expanded={!collapsed}
            aria-label={collapsed ? 'Show tasks' : 'Hide tasks'}
            title={collapsed ? 'Show tasks' : 'Hide tasks'}
            onClick={() => setCollapsed((value) => !value)}
            className="inline-flex h-6 w-6 shrink-0 items-center justify-center rounded text-fg-dim hover:bg-hover hover:text-fg"
          >
            <ChevronDown size={14} aria-hidden className={cx('transition-transform', collapsed && '-rotate-90')} />
          </button>
        ) : (
          <span className="w-1.5" />
        )}
      </div>
      {showTasks && total > 0 ? (
        <ul className={cx('m-0 flex list-none flex-col gap-0.5 px-3 pb-2.5', live && 'max-h-[28vh] overflow-y-auto')}>
          {tasks.map((task, index) => (
            <li
              key={`${task.id}-${index}`}
              data-testid={`task-item-${task.id}`}
              data-status={task.status}
              className="flex items-start gap-2 py-0.5"
            >
              <span className="mt-[3px] flex h-3.5 w-3.5 shrink-0 items-center justify-center">
                <TaskIcon status={task.status} live={live} />
              </span>
              <span
                className={cx(
                  'min-w-0 wrap-anywhere',
                  task.status === 'done' && 'text-fg-dim',
                  task.status === 'pending' && 'text-fg-dim',
                  task.status === 'in_progress' && 'text-fg',
                  task.status === 'failed' && 'text-danger',
                )}
              >
                <span className="sr-only">{STATUS_WORDS[task.status]}: </span>
                {task.title}
              </span>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
});
