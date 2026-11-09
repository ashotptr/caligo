import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import { omniscientApi } from '../api/omniscientApi';
import type { AskAnswer, AskSong } from '../types';
import type { AppThunk } from './store';

export type OmniscientEntry =
  | { id: number; from: 'you'; text: string }
  | { id: number; from: 'ai'; state: 'thinking' }
  | { id: number; from: 'ai'; state: 'failed' }
  | { id: number; from: 'ai'; state: 'answered'; text: string | null; songs: AskSong[] | null };

export interface OmniscientState {
  entries: OmniscientEntry[];
  nextId: number;
}

const SESSION_KEY = 'music_agent_session';

const newSessionId = () => `sess_${Date.now()}_${Math.random().toString(36).slice(2, 11)}`;

export const sessionId = (): string => {
  try {
    const stored = sessionStorage.getItem(SESSION_KEY);

    if (stored) {
      return stored;
    }
  } catch {
    return newSessionId();
  }

  const created = newSessionId();

  try {
    sessionStorage.setItem(SESSION_KEY, created);
  } catch {
    return created;
  }

  return created;
};

const initialState: OmniscientState = {
  entries: [],
  nextId: 1
};

const omniscientSlice = createSlice({
  name: 'omniscient',
  initialState,
  reducers: {
    asked: (state, action: PayloadAction<string>) => {
      const id = state.nextId;

      state.entries.push({ id, from: 'you', text: action.payload }, { id: id + 1, from: 'ai', state: 'thinking' });
      state.nextId += 2;
    },
    answered: (state, action: PayloadAction<{ id: number; answer: AskAnswer | null }>) => {
      const { id, answer } = action.payload;
      const index = state.entries.findIndex(entry => entry.id === id);

      if (index >= 0) {
        state.entries[index] = answer
          ? { id, from: 'ai', state: 'answered', text: answer.text, songs: answer.songs }
          : { id, from: 'ai', state: 'failed' };
      }
    }
  }
});

const { asked, answered } = omniscientSlice.actions;

export default omniscientSlice.reducer;

export const askOmniscient = (query: string): AppThunk<Promise<void>> => async (dispatch, getState) => {
  const pending = getState().omniscient.nextId + 1;

  dispatch(asked(query));

  const request = dispatch(omniscientApi.endpoints.askOmniscient.initiate({ query, sessionId: sessionId() }));

  try {
    dispatch(answered({ id: pending, answer: await request.unwrap() }));
  } catch {
    dispatch(answered({ id: pending, answer: null }));
  } finally {
    request.reset();
  }
};
