import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';

const TOAST_MS = 3000;

type ShowToast = (message: string) => void;

const ToastContext = createContext<ShowToast | undefined>(undefined);

export const ToastProvider = ({ children }: { children: ReactNode }) => {
  const [message, setMessage] = useState('Notification');
  const [visible, setVisible] = useState(false);
  const timer = useRef<number | undefined>(undefined);

  const show = useCallback<ShowToast>(text => {
    window.clearTimeout(timer.current);
    setMessage(text);
    setVisible(true);
    timer.current = window.setTimeout(() => setVisible(false), TOAST_MS);
  }, []);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  return (
    <ToastContext.Provider value={show}>
      {children}
      <div className={visible ? 'toast show' : 'toast'} role="status" aria-live="polite">{message}</div>
    </ToastContext.Provider>
  );
};

export const useToast = (): ShowToast => {
  const context = useContext(ToastContext);

  if (!context) {
    throw new Error('useToast must be used within a ToastProvider');
  }

  return context;
};
