/** Clarifying questions from the assistant: selectable options, optional free text, one Submit. */
import { Check, Minus } from 'lucide-react';
import { memo, useEffect, useState } from 'react';
import type { Question, QuestionAnswer } from '@cadstudio/copilot-agent';
import {
  answerFor,
  collectAnswers,
  createDrafts,
  selectCustom,
  selectOption,
  setCustomText,
  unansweredCount,
} from '../lib/questions';
import type { QuestionDraft } from '../lib/questions';
import type { QuestionItem } from '../state/transcript';
import { Button, Card, Spinner, cx } from './ui';

/** How long Submit stays disabled while the main process confirms the answer. */
const SUBMIT_ACK_TIMEOUT_MS = 4000;

interface QuestionCardProps {
  item: QuestionItem;
  onSubmit: (requestId: string, answers: QuestionAnswer[]) => void;
}

function QuestionHeading({ question }: { question: Question }) {
  return (
    <>
      {question.header.trim().length > 0 ? (
        <div className="text-[10px] font-semibold uppercase tracking-[0.08em] text-fg-faint">{question.header}</div>
      ) : null}
      <div className="selectable mt-0.5 font-medium text-fg wrap-anywhere">{question.question}</div>
    </>
  );
}

interface OpenQuestionProps {
  question: Question;
  questionIndex: number;
  draft: QuestionDraft | undefined;
  disabled: boolean;
  onSelect: (optionIndex: number) => void;
  onCustomText: (text: string) => void;
  onCustomFocus: () => void;
  onEnter: () => void;
}

function OpenQuestion({
  question,
  questionIndex,
  draft,
  disabled,
  onSelect,
  onCustomText,
  onCustomFocus,
  onEnter,
}: OpenQuestionProps) {
  const groupLabelId = `question-${question.id}-${questionIndex}-label`;
  const customSelected = draft?.choice === 'custom';
  return (
    <div>
      <div id={groupLabelId}>
        <QuestionHeading question={question} />
      </div>
      <div role="radiogroup" aria-labelledby={groupLabelId} className="mt-2 flex flex-col gap-1">
        {question.options.map((option, optionIndex) => {
          const selected = draft?.choice === optionIndex;
          return (
            <button
              key={`${optionIndex}-${option.label}`}
              type="button"
              role="radio"
              aria-checked={selected}
              disabled={disabled}
              data-testid={`question-option-${questionIndex}-${optionIndex}`}
              data-selected={selected ? 'true' : 'false'}
              onClick={() => onSelect(optionIndex)}
              className={cx(
                'flex w-full items-start gap-2 rounded-lg border px-2.5 py-1.5 text-left transition-colors disabled:opacity-60',
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
              <span className="min-w-0 flex-1">
                <span className="text-fg wrap-anywhere">{option.label}</span>
                {option.recommended ? <span className="ml-1.5 text-[11px] text-accent-soft">(Recommended)</span> : null}
                {option.description && option.description.trim().length > 0 ? (
                  <span className="block text-[12px] text-fg-dim wrap-anywhere">{option.description}</span>
                ) : null}
              </span>
            </button>
          );
        })}
        {question.allowCustom ? (
          <input
            type="text"
            value={draft?.customText ?? ''}
            disabled={disabled}
            placeholder="Other..."
            aria-label={`Other answer for: ${question.question}`}
            data-testid={`question-custom-${questionIndex}`}
            data-selected={customSelected ? 'true' : 'false'}
            onChange={(event) => onCustomText(event.target.value)}
            onFocus={onCustomFocus}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.nativeEvent.isComposing) {
                event.preventDefault();
                onEnter();
              }
            }}
            className={cx(
              'h-8 w-full rounded-lg border bg-app px-2.5 text-fg transition-colors disabled:opacity-60',
              customSelected ? 'border-accent' : 'border-line hover:border-line-strong',
            )}
          />
        ) : null}
      </div>
    </div>
  );
}

function SettledQuestion({ question, answer }: { question: Question; answer: string | null }) {
  return (
    <div>
      <QuestionHeading question={question} />
      <div className="mt-1 flex items-start gap-1.5">
        {answer !== null ? (
          <>
            <Check size={14} aria-hidden className="mt-[3px] shrink-0 text-success" />
            <span className="selectable min-w-0 text-fg wrap-anywhere">{answer}</span>
          </>
        ) : (
          <>
            <Minus size={14} aria-hidden className="mt-[3px] shrink-0 text-fg-faint" />
            <span className="text-fg-dim">Not answered</span>
          </>
        )}
      </div>
    </div>
  );
}

/**
 * A card of one or more questions. While open it collects one answer per question; once answered
 * (or once its turn ended) it collapses to header, question and the chosen answer.
 */
export const QuestionCard = memo(function QuestionCard({ item, onSubmit }: QuestionCardProps) {
  const [drafts, setDrafts] = useState(() => createDrafts(item.questions));
  const [submitted, setSubmitted] = useState(false);

  // If the acknowledgement never arrives, let the user try again instead of leaving a dead button.
  useEffect(() => {
    if (!submitted) return undefined;
    const timer = window.setTimeout(() => setSubmitted(false), SUBMIT_ACK_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [submitted]);

  const open = item.status === 'pending';

  if (!open) {
    const state = item.status === 'answered' ? 'answered' : 'cancelled';
    return (
      <Card data-testid="question-card" data-state={state} className="flex flex-col gap-3 px-3 py-2.5">
        {item.questions.map((question, index) => (
          <SettledQuestion
            key={`${question.id}-${index}`}
            question={question}
            answer={item.status === 'answered' ? answerFor(question, item.answers) : null}
          />
        ))}
      </Card>
    );
  }

  const answers = collectAnswers(item.questions, drafts);
  const missing = unansweredCount(item.questions, drafts);
  const submit = (): void => {
    if (answers === null || submitted) return;
    setSubmitted(true);
    onSubmit(item.requestId, answers);
  };

  return (
    <Card data-testid="question-card" data-state="pending" className="flex flex-col gap-3.5 border-line-strong px-3 py-3">
      {item.questions.map((question, index) => (
        <OpenQuestion
          key={`${question.id}-${index}`}
          question={question}
          questionIndex={index}
          draft={drafts[index]}
          disabled={submitted}
          onSelect={(optionIndex) => setDrafts((current) => selectOption(current, index, optionIndex))}
          onCustomText={(text) => setDrafts((current) => setCustomText(current, index, text))}
          onCustomFocus={() => setDrafts((current) => selectCustom(current, index))}
          onEnter={submit}
        />
      ))}
      <div className="flex items-center justify-between gap-2">
        <span className="min-w-0 text-[11px] text-fg-faint" aria-live="polite">
          {submitted
            ? 'Sending your answer...'
            : missing > 0
              ? item.questions.length === 1
                ? 'Choose an answer to continue'
                : `${missing} of ${item.questions.length} still need an answer`
              : ''}
        </span>
        <Button
          variant="primary"
          data-testid="question-submit"
          disabled={answers === null || submitted}
          onClick={submit}
          className="px-3.5"
        >
          {submitted ? <Spinner size={12} /> : null}
          Submit
        </Button>
      </div>
    </Card>
  );
});
