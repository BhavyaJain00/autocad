/** Pure draft state of a question card: which option (or custom text) answers each question. */
import type { Question, QuestionAnswer } from '@cadstudio/copilot-agent';

export interface QuestionDraft {
  /** Index of the chosen option, 'custom' for the free-text answer, null when nothing is chosen. */
  choice: number | 'custom' | null;
  /** What the user typed in the "Other..." field (kept when they switch back to an option). */
  customText: string;
}

export type QuestionDrafts = readonly QuestionDraft[];

const EMPTY_DRAFT: QuestionDraft = { choice: null, customText: '' };

/** One empty draft per question. */
export function createDrafts(questions: readonly Question[]): QuestionDrafts {
  return questions.map(() => EMPTY_DRAFT);
}

function withDraft(drafts: QuestionDrafts, index: number, draft: QuestionDraft): QuestionDrafts {
  if (!Number.isInteger(index) || index < 0 || index >= drafts.length) return drafts;
  const next = drafts.slice();
  next[index] = draft;
  return next;
}

/** Chooses option `optionIndex` of question `questionIndex`. */
export function selectOption(drafts: QuestionDrafts, questionIndex: number, optionIndex: number): QuestionDrafts {
  const current = drafts[questionIndex];
  if (current === undefined) return drafts;
  return withDraft(drafts, questionIndex, { ...current, choice: optionIndex });
}

/**
 * Updates the free-text answer. Typing makes the custom text the answer; clearing the field while
 * it was the answer leaves the question unanswered.
 */
export function setCustomText(drafts: QuestionDrafts, questionIndex: number, text: string): QuestionDrafts {
  const current = drafts[questionIndex];
  if (current === undefined) return drafts;
  const hasText = text.trim().length > 0;
  const choice = hasText ? 'custom' : current.choice === 'custom' ? null : current.choice;
  return withDraft(drafts, questionIndex, { choice, customText: text });
}

/** Makes the typed custom text the answer again (when the field is focused and not empty). */
export function selectCustom(drafts: QuestionDrafts, questionIndex: number): QuestionDrafts {
  const current = drafts[questionIndex];
  if (current === undefined || current.customText.trim().length === 0) return drafts;
  return withDraft(drafts, questionIndex, { ...current, choice: 'custom' });
}

/** The answer a draft stands for, or null when the question is not answered yet. */
export function draftAnswer(question: Question, draft: QuestionDraft | undefined): string | null {
  if (draft === undefined || draft.choice === null) return null;
  if (draft.choice === 'custom') {
    if (!question.allowCustom) return null;
    const text = draft.customText.trim();
    return text.length > 0 ? text : null;
  }
  const option = question.options[draft.choice];
  return option === undefined ? null : option.label;
}

/** The answers to submit, or null while at least one question has no answer. */
export function collectAnswers(questions: readonly Question[], drafts: QuestionDrafts): QuestionAnswer[] | null {
  if (questions.length === 0) return null;
  const answers: QuestionAnswer[] = [];
  for (let i = 0; i < questions.length; i += 1) {
    const question = questions[i];
    if (question === undefined) return null;
    const answer = draftAnswer(question, drafts[i]);
    if (answer === null) return null;
    answers.push({ questionId: question.id, answer });
  }
  return answers;
}

/** How many questions still need an answer (for the Submit button's hint). */
export function unansweredCount(questions: readonly Question[], drafts: QuestionDrafts): number {
  let missing = 0;
  questions.forEach((question, index) => {
    if (draftAnswer(question, drafts[index]) === null) missing += 1;
  });
  return missing;
}

/** The submitted answer to a question of an answered card, or null when it has none. */
export function answerFor(question: Question, answers: readonly QuestionAnswer[]): string | null {
  const match = answers.find((answer) => answer.questionId === question.id);
  return match && match.answer.trim().length > 0 ? match.answer : null;
}
