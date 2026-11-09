import { combineReducers, configureStore } from '@reduxjs/toolkit';
import type { Action, ThunkAction } from '@reduxjs/toolkit';
import { setupListeners } from '@reduxjs/toolkit/query';
import { caligoApi } from './api';
import auth from './authSlice';
import collections from './collectionsSlice';
import omniscient from './omniscientSlice';
import toast from './toastSlice';

const rootReducer = combineReducers({
  auth,
  toast,
  collections,
  omniscient,
  [caligoApi.reducerPath]: caligoApi.reducer
});

export type RootState = ReturnType<typeof rootReducer>;

export const makeStore = (preloadedState?: Partial<RootState>) =>
  configureStore({
    reducer: rootReducer,
    preloadedState,
    middleware: getDefaultMiddleware => getDefaultMiddleware().concat(caligoApi.middleware)
  });

export type AppStore = ReturnType<typeof makeStore>;
export type AppDispatch = AppStore['dispatch'];
export type AppThunk<ReturnType = void> = ThunkAction<ReturnType, RootState, unknown, Action>;

export const store = makeStore();

setupListeners(store.dispatch);
