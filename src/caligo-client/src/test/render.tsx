import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { Provider } from 'react-redux';
import { MemoryRouter } from 'react-router-dom';
import type { AuthUser } from '../store/authSlice';
import { makeStore } from '../store/store';

export const reader: AuthUser = { id: 'reader-1', name: 'reader', roles: ['Reader'] };

export const renderWithStore = (ui: ReactElement, user: AuthUser | null = null) => {
  const store = makeStore({ auth: { user } });

  return { store, ...render(<Provider store={store}><MemoryRouter>{ui}</MemoryRouter></Provider>) };
};
