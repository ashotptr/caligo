import { useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useRegisterMutation } from '../api/accountApi';
import { failureOf } from '../api/errors';
import { useToast } from '../store/hooks';

interface IdentityError {
  code: string;
  description: string;
}

const PASSWORD_CHARS = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*';
const PASSWORD_LENGTH = 14;
const PASSWORD_CLASSES = [/[a-z]/, /[A-Z]/, /[0-9]/, /[!@#$%^&*]/];

const strongPassword = (): string => {
  for (;;) {
    const values = crypto.getRandomValues(new Uint32Array(PASSWORD_LENGTH));
    const password = Array.from(values, value => PASSWORD_CHARS[value % PASSWORD_CHARS.length]).join('');

    if (PASSWORD_CLASSES.every(pattern => pattern.test(password))) {
      return password;
    }
  }
};

const RegisterPage = () => {
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<string[]>([]);
  const [register, { isLoading: submitting }] = useRegisterMutation();
  const [shown, setShown] = useState(false);
  const passwordInput = useRef<HTMLInputElement>(null);
  const toast = useToast();
  const navigate = useNavigate();

  const suggest = () => {
    const suggested = strongPassword();

    setPassword(suggested);
    setShown(true);
    window.setTimeout(() => passwordInput.current?.select(), 0);
    navigator.clipboard?.writeText(suggested).catch(() => undefined);
    toast('Password Copied');
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrors([]);
    try {
      await register({ username, email, password }).unwrap();
      navigate('/login', { state: { registered: true } });
    } catch (err) {
      const failure = failureOf(err);

      if (Array.isArray(failure?.data)) {
        setErrors((failure.data as IdentityError[]).map(x => x.description));
      } else if (failure?.status === 429) {
        setErrors(['Too many attempts, please wait a moment and try again.']);
      } else {
        setErrors(['Registration failed. Please try again.']);
      }
    }
  };

  return (
    <div className="fog-stage">
      <div className="auth-page">
        <h1>Create an account</h1>

        <form onSubmit={handleSubmit} className="auth-form">
          <label>
            Username
            <input
              type="text"
              value={username}
              onChange={e => setUsername(e.target.value)}
              autoComplete="username"
              required
            />
          </label>

          <label>
            Email
            <input
              type="email"
              value={email}
              onChange={e => setEmail(e.target.value)}
              autoComplete="email"
              required
            />
          </label>

          <label>
            <span className="password-label">
              Password
              <button type="button" className="suggest-password" onClick={suggest}>Suggest Strong Password</button>
            </span>
            <input
              ref={passwordInput}
              type={shown ? 'text' : 'password'}
              value={password}
              onChange={e => setPassword(e.target.value)}
              autoComplete="new-password"
              required
            />
          </label>

          {errors.length > 0 && (
            <ul className="form-error">
              {errors.map(err => (
                <li key={err}>{err}</li>
              ))}
            </ul>
          )}

          <button type="submit" disabled={submitting}>
            {submitting ? 'Creating…' : 'Register'}
          </button>
        </form>

        <p className="auth-switch">
          Already have an account? <Link to="/login">Sign in</Link>
        </p>
      </div>
    </div>
  );
};

export default RegisterPage;
