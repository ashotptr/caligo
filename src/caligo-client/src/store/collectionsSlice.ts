import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';

export interface PendingAdd {
  contentIds: number[];
  title: string;
}

export interface CollectionsUiState {
  pending: PendingAdd | null;
  creating: boolean;
}

const initialState: CollectionsUiState = {
  pending: null,
  creating: false
};

const collectionsSlice = createSlice({
  name: 'collections',
  initialState,
  reducers: {
    addRequested: (state, action: PayloadAction<PendingAdd>) => {
      if (action.payload.contentIds.length > 0) {
        state.pending = action.payload;
      }
    },
    addClosed: state => {
      state.pending = null;
    },
    createRequested: state => {
      state.creating = true;
    },
    createClosed: state => {
      state.creating = false;
    }
  }
});

export const { addRequested, addClosed, createRequested, createClosed } = collectionsSlice.actions;

export default collectionsSlice.reducer;
