import { describe, it, expect } from 'vitest';
import type { Question } from '@cadstudio/copilot-agent';
import {
  answerFor,
  collectAnswers,
  createDrafts,
  draftAnswer,
  selectCustom,
  selectOption,
  setCustomText,
  unansweredCount,
} from './questions';

const THICKNESS: Question = {
  id: 'thickness',
  header: 'WALL THICKNESS',
  question: 'How thick should the walls be?',
  options: [{ label: '9 in', recommended: true }, { label: '4.5 in' }],
  allowCustom: true,
};

const DOOR: Question = {
  id: 'door',
  header: 'DOOR',
  question: 'Where does the door go?',
  options: [{ label: 'South wall' }, { label: 'East wall' }],
  allowCustom: false,
};

const QUESTIONS = [THICKNESS, DOOR];

describe('question drafts', () => {
  it('start unanswered', () => {
    const drafts = createDrafts(QUESTIONS);
    expect(drafts).toHaveLength(2);
    expect(collectAnswers(QUESTIONS, drafts)).toBeNull();
    expect(unansweredCount(QUESTIONS, drafts)).toBe(2);
  });

  it('collect the chosen option labels once every question is answered', () => {
    let drafts = createDrafts(QUESTIONS);
    drafts = selectOption(drafts, 0, 1);
    expect(collectAnswers(QUESTIONS, drafts)).toBeNull();
    expect(unansweredCount(QUESTIONS, drafts)).toBe(1);
    drafts = selectOption(drafts, 1, 0);
    expect(collectAnswers(QUESTIONS, drafts)).toEqual([
      { questionId: 'thickness', answer: '4.5 in' },
      { questionId: 'door', answer: 'South wall' },
    ]);
    expect(unansweredCount(QUESTIONS, drafts)).toBe(0);
  });

  it('let a later choice replace an earlier one', () => {
    let drafts = selectOption(createDrafts(QUESTIONS), 0, 0);
    drafts = selectOption(drafts, 0, 1);
    expect(draftAnswer(THICKNESS, drafts[0])).toBe('4.5 in');
  });

  it('use typed custom text, trimmed, as the answer', () => {
    let drafts = selectOption(createDrafts(QUESTIONS), 0, 0);
    drafts = setCustomText(drafts, 0, '  230 mm ');
    expect(drafts[0]?.choice).toBe('custom');
    expect(draftAnswer(THICKNESS, drafts[0])).toBe('230 mm');
  });

  it('go back to unanswered when the custom text is cleared', () => {
    let drafts = setCustomText(createDrafts(QUESTIONS), 0, 'x');
    drafts = setCustomText(drafts, 0, '   ');
    expect(drafts[0]?.choice).toBeNull();
    expect(draftAnswer(THICKNESS, drafts[0])).toBeNull();
  });

  it('keep the typed text when an option is chosen, and restore it on focus', () => {
    let drafts = setCustomText(createDrafts(QUESTIONS), 0, '230 mm');
    drafts = selectOption(drafts, 0, 0);
    expect(draftAnswer(THICKNESS, drafts[0])).toBe('9 in');
    expect(drafts[0]?.customText).toBe('230 mm');
    drafts = selectCustom(drafts, 0);
    expect(draftAnswer(THICKNESS, drafts[0])).toBe('230 mm');
  });

  it('do not switch to an empty custom field on focus', () => {
    const drafts = selectOption(createDrafts(QUESTIONS), 0, 0);
    expect(selectCustom(drafts, 0)).toBe(drafts);
  });

  it('refuse custom text for a question that does not allow it', () => {
    const drafts = setCustomText(createDrafts(QUESTIONS), 1, 'North wall');
    expect(draftAnswer(DOOR, drafts[1])).toBeNull();
    expect(collectAnswers([DOOR], [drafts[1] ?? { choice: null, customText: '' }])).toBeNull();
  });

  it('ignore out-of-range questions and options', () => {
    const drafts = createDrafts(QUESTIONS);
    expect(selectOption(drafts, 7, 0)).toBe(drafts);
    expect(setCustomText(drafts, -1, 'x')).toBe(drafts);
    expect(draftAnswer(THICKNESS, selectOption(drafts, 0, 9)[0])).toBeNull();
    expect(draftAnswer(THICKNESS, undefined)).toBeNull();
  });

  it('never mutate the previous drafts', () => {
    const drafts = createDrafts(QUESTIONS);
    const next = selectOption(drafts, 0, 1);
    expect(next).not.toBe(drafts);
    expect(drafts[0]?.choice).toBeNull();
  });

  it('cannot submit a card without questions', () => {
    expect(collectAnswers([], [])).toBeNull();
  });
});

describe('answerFor', () => {
  it('finds the submitted answer by question id', () => {
    const answers = [
      { questionId: 'door', answer: 'East wall' },
      { questionId: 'thickness', answer: '9 in' },
    ];
    expect(answerFor(THICKNESS, answers)).toBe('9 in');
    expect(answerFor(DOOR, answers)).toBe('East wall');
  });

  it('returns null when a question has no (or a blank) answer', () => {
    expect(answerFor(DOOR, [])).toBeNull();
    expect(answerFor(DOOR, [{ questionId: 'door', answer: '  ' }])).toBeNull();
  });
});
