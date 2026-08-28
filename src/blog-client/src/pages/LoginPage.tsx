import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { GoogleLogin } from '@react-oauth/google';
import axios from 'axios';
import api from '../api/axios';
import { useAuth } from '../contexts/AuthContext';

const googleClientId = import.meta.env.VITE_GOOGLE_CLIENT_ID as string | undefined;

const LoginPage = () => {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const { login } = useAuth();
  const navigate = useNavigate();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const response = await api.post('/api/account/login', { username, password });
      login(response.data.accessToken, response.data.refreshToken);
      navigate('/');
    } catch (err) {
      if (axios.isAxiosError(err) && err.response?.status === 401) {
        setError('Invalid username or password.');
      } else if (axios.isAxiosError(err) && err.response?.status === 429) {
        setError('Too many attempts, please wait a moment and try again.');
      } else {
        setError('Could not sign in. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  };

  const handleGoogleSuccess = async (credential?: string) => {
    if (!credential) return;
    setError(null);
    try {
      const response = await api.post('/api/account/google-login', JSON.stringify(credential), {
        headers: { 'Content-Type': 'application/json' }
      });
      login(response.data.accessToken, response.data.refreshToken);
      navigate('/');
    } catch {
      setError('Google sign-in failed. Please try again.');
    }
  };

  return (
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
            onSuccess={cred => handleGoogleSuccess(cred.credential)}
            onError={() => setError('Google sign-in failed. Please try again.')}
          />
        </div>
      )}

      <p className="auth-switch">
        No account? <Link to="/register">Register</Link>
      </p>
    </div>
  );
};

export default LoginPage;
