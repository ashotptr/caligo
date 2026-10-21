import { useEffect, useRef, useState } from 'react';

const UNLOCKED_KEY = 'caligo_unlocked';
const ACCESS_CODE = 'caligo';
const FADE_MS = 2000;
const WRONG_MS = 500;

type GateState = 'closed' | 'wrong' | 'opening' | 'open';

const unlocked = (): boolean => {
  try {
    return !!localStorage.getItem(UNLOCKED_KEY);
  } catch {
    return false;
  }
};

const remember = (): void => {
  try {
    localStorage.setItem(UNLOCKED_KEY, 'true');
  } catch {
    return;
  }
};

const FogGate = () => {
  const [state, setState] = useState<GateState>(() => (unlocked() ? 'open' : 'closed'));
  const [code, setCode] = useState('');
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  if (state === 'open') {
    return null;
  }

  const enter = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== 'Enter' || state !== 'closed') {
      return;
    }

    if (code.toLowerCase().trim() === ACCESS_CODE) {
      setState('opening');
      remember();
      timer.current = window.setTimeout(() => setState('open'), FADE_MS);
    } else {
      setState('wrong');
      timer.current = window.setTimeout(() => {
        setState('closed');
        setCode('');
      }, WRONG_MS);
    }
  };

  return (
    <div className={state === 'opening' ? 'fog-gate hidden-gate' : 'fog-gate'}>
      <input
        type="password"
        className={`caligo-gate-input ${state}`}
        placeholder="ACCESS CODE"
        autoComplete="off"
        aria-label="Access code"
        autoFocus
        value={code}
        onChange={event => setCode(event.target.value)}
        onKeyUp={enter}
      />
    </div>
  );
};

export default FogGate;
