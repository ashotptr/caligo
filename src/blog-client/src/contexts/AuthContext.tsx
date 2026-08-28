import { createContext, useContext, useState } from 'react';
import type { ReactNode } from 'react';
import { jwtDecode } from 'jwt-decode';
import api from '../api/axios';

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

interface AuthContextType {
  user: AuthUser | null;
  isAuthenticated: boolean;
  hasRole: (...roles: string[]) => boolean;
  login: (accessToken: string, refreshToken: string) => void;
  logout: () => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

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

const parseUser = (token: string): AuthUser | null => {
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

export const AuthProvider = ({ children }: { children: ReactNode }) => {
  const [user, setUser] = useState<AuthUser | null>(() => {
    const token = localStorage.getItem('accessToken');

    if (!token) {
      return null;
    }

    const parsed = parseUser(token);

    if (!parsed) {
      localStorage.removeItem('accessToken');
      localStorage.removeItem('refreshToken');
    }

    return parsed;
  });

  const login = (accessToken: string, refreshToken: string) => {
    localStorage.setItem('accessToken', accessToken);
    localStorage.setItem('refreshToken', refreshToken);

    setUser(parseUser(accessToken));
  };

  const logout = () => {
    // Best effort: tell the server to revoke the refresh sessions, but never block signing
    // out on the network. Clearing local storage is what the user actually asked for.
    api.post('/api/account/logout').catch(() => undefined);

    localStorage.removeItem('accessToken');
    localStorage.removeItem('refreshToken');

    setUser(null);
  };

  const hasRole = (...roles: string[]) => !!user && roles.some(role => user.roles.includes(role));

  return (
    <AuthContext.Provider value={{ user, isAuthenticated: !!user, hasRole, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
};

export const useAuth = (): AuthContextType => {
  const context = useContext(AuthContext);

  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider');
  }

  return context;
};