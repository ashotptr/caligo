import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';

export interface ToastState {
  message: string;
  visible: boolean;
  sequence: number;
}

const initialState: ToastState = {
  message: 'Notification',
  visible: false,
  sequence: 0
};

const toastSlice = createSlice({
  name: 'toast',
  initialState,
  reducers: {
    toastShown: (state, action: PayloadAction<string>) => {
      state.message = action.payload;
      state.visible = true;
      state.sequence += 1;
    },
    toastExpired: (state, action: PayloadAction<number>) => {
      if (state.sequence === action.payload) {
        state.visible = false;
      }
    }
  }
});

export const { toastShown, toastExpired } = toastSlice.actions;

export default toastSlice.reducer;
