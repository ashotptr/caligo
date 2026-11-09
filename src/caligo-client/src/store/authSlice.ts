import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import { jwtDecode } from 'jwt-decode';
import api, { LOGOUT } from '../api/axios';
import { caligoApi } from './api';
import type { AppThunk } from './store';

interface JwtClaims {
  sub?: string;
  jti?: string;
  exp?: number;

  unique_name?: string;
  nameid?: string;
  name?: string;
  role?: string | string[];

  'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name'?: string;
  'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier'?: string;
  'http://schemas.microsoft.com/ws/2008/06/identity/claims/role'?: string | string[];
}

export interface AuthUser {
  id: string;
  name: string;
  roles: string[];
}

export interface AuthState {
  user: AuthUser | null;
}

const REFRESH_WINDOW_MS = 7 * 24 * 60 * 60 * 1000;

const CLAIM_NAME = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name';
const CLAIM_NAMEID = 'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier';
const CLAIM_ROLE = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

const toRoles = (value: string | string[] | undefined): string[] => {
  if (value == null) {
    return [];
  }

  return Array.isArray(value) ? value : [value];
};

export const parseUser = (token: string): AuthUser | null => {
  try {
    const claims = jwtDecode<JwtClaims>(token);

    if (claims.exp && claims.exp * 1000 + REFRESH_WINDOW_MS < Date.now()) {
      return null;
    }

    const name = claims[CLAIM_NAME] ?? claims.unique_name ?? claims.name ?? '';
    const id = claims[CLAIM_NAMEID] ?? claims.nameid ?? claims.sub ?? '';

    const roles = [
      ...toRoles(claims[CLAIM_ROLE]),
      ...toRoles(claims.role)
    ];

    return { id, name, roles: [...new Set(roles)] };
  }
  catch {
    return null;
  }
};

const storedUser = (): AuthState => {
  const token = localStorage.getItem('accessToken');

  if (!token) {
    return { user: null };
  }

  const user = parseUser(token);

  if (!user) {
    localStorage.removeItem('accessToken');
    localStorage.removeItem('refreshToken');
  }

  return { user };
};

const authSlice = createSlice({
  name: 'auth',
  initialState: storedUser,
  reducers: {
    userChanged: (state, action: PayloadAction<AuthUser | null>) => {
      state.user = action.payload;
    }
  }
});

export const { userChanged } = authSlice.actions;

export default authSlice.reducer;

export const signIn = (accessToken: string, refreshToken: string): AppThunk<AuthUser | null> => dispatch => {
  localStorage.setItem('accessToken', accessToken);
  localStorage.setItem('refreshToken', refreshToken);

  const user = parseUser(accessToken);

  dispatch(userChanged(user));
  dispatch(caligoApi.util.resetApiState());

  return user;
};

export const signOut = (): AppThunk => dispatch => {
  api.post(LOGOUT, { refreshToken: localStorage.getItem('refreshToken') }).catch(() => undefined);

  localStorage.removeItem('accessToken');
  localStorage.removeItem('refreshToken');

  dispatch(userChanged(null));
  dispatch(caligoApi.util.resetApiState());
};
