import { useCallback, useMemo } from 'react';
import { useDispatch, useSelector } from 'react-redux';
import { signIn, signOut } from './authSlice';
import type { AuthUser } from './authSlice';
import { addRequested, createRequested } from './collectionsSlice';
import { toastShown } from './toastSlice';
import type { AppDispatch, RootState } from './store';

export const useAppDispatch = useDispatch.withTypes<AppDispatch>();
export const useAppSelector = useSelector.withTypes<RootState>();

export interface Auth {
  user: AuthUser | null;
  isAuthenticated: boolean;
  hasRole: (...roles: string[]) => boolean;
  login: (accessToken: string, refreshToken: string) => AuthUser | null;
  logout: () => void;
}

export const useAuth = (): Auth => {
  const user = useAppSelector(state => state.auth.user);
  const dispatch = useAppDispatch();

  return useMemo(() => ({
    user,
    isAuthenticated: !!user,
    hasRole: (...roles: string[]) => !!user && roles.some(role => user.roles.includes(role)),
    login: (accessToken: string, refreshToken: string) => dispatch(signIn(accessToken, refreshToken)),
    logout: () => dispatch(signOut())
  }), [user, dispatch]);
};

export const useToast = (): ((message: string) => void) => {
  const dispatch = useAppDispatch();

  return useCallback((message: string) => {
    dispatch(toastShown(message));
  }, [dispatch]);
};

export const useCollections = () => {
  const dispatch = useAppDispatch();

  return useMemo(() => ({
    addToCollection: (contentIds: number[], title: string) => {
      dispatch(addRequested({ contentIds, title }));
    },
    createCollection: () => {
      dispatch(createRequested());
    }
  }), [dispatch]);
};
