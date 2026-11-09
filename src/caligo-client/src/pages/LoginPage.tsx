import { useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { GoogleLogin } from '@react-oauth/google';
import { useGoogleLoginMutation, useLoginMutation } from '../api/accountApi';
import { statusOf } from '../api/errors';
import { useAuth, useToast } from '../store/hooks';

const googleClientId = import.meta.env.VITE_GOOGLE_CLIENT_ID as string | undefined;

const LoginPage = () => {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [signIn, { isLoading: submitting }] = useLoginMutation();
  const [googleSignIn] = useGoogleLoginMutation();
  const { login } = useAuth();
  const toast = useToast();
  const navigate = useNavigate();
  const location = useLocation();
  const registered = (location.state as { registered?: boolean } | null)?.registered === true;

  const welcome = (name: string | undefined) => {
    if (name) {
      toast(registered ? `Welcome, ${name}!` : `Welcome back, ${name}`);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const tokens = await signIn({ username, password }).unwrap();
      welcome(login(tokens.accessToken, tokens.refreshToken)?.name);
      navigate('/');
    } catch (err) {
      const status = statusOf(err);

      if (status === 401) {
        setError('Invalid username or password.');
      } else if (status === 429) {
        setError('Too many attempts, please wait a moment and try again.');
      } else {
        setError('Could not sign in. Please try again.');
      }
    }
  };

  const handleGoogleSuccess = async (credential?: string) => {
    if (!credential) return;
    setError(null);
    try {
      const tokens = await googleSignIn(credential).unwrap();
      welcome(login(tokens.accessToken, tokens.refreshToken)?.name);
      navigate('/');
    } catch {
      setError('Google sign-in failed. Please try again.');
    }
  };

  return (
    <div className="fog-stage">
      <div className="auth-page">
        <h1>Sign in</h1>

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
            Password
            <input
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
          </label>

          {error && <p className="form-error">{error}</p>}

          <button type="submit" disabled={submitting}>
            {submitting ? 'Signing in…' : 'Sign in'}
          </button>
        </form>

        {googleClientId && (
          <div className="google-login">
            <div className="divider">or</div>
            <GoogleLogin
              theme="filled_black"
              shape="pill"
              onSuccess={cred => handleGoogleSuccess(cred.credential)}
              onError={() => setError('Google sign-in failed. Please try again.')}
            />
          </div>
        )}

        <p className="auth-switch">
          No account? <Link to="/register">Register</Link>
        </p>
      </div>
    </div>
  );
};

export default LoginPage;
