import { useEffect } from 'react';
import { useAppDispatch, useAppSelector } from '../store/hooks';
import { toastExpired } from '../store/toastSlice';

const TOAST_MS = 3000;

const Toaster = () => {
  const { message, visible, sequence } = useAppSelector(state => state.toast);
  const dispatch = useAppDispatch();

  useEffect(() => {
    if (!visible) {
      return;
    }

    const timer = window.setTimeout(() => dispatch(toastExpired(sequence)), TOAST_MS);

    return () => window.clearTimeout(timer);
  }, [visible, sequence, dispatch]);

  return <div className={visible ? 'toast show' : 'toast'} role="status" aria-live="polite">{message}</div>;
};

export default Toaster;
