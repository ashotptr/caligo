import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import axios from 'axios';
import api from '../api/axios';

interface IdentityError {
  code: string;
  description: string;
}

const RegisterPage = () => {
  const [username, setUsername] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<string[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const navigate = useNavigate();

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErrors([]);
    setSubmitting(true);
    try {
      await api.post('/api/account/register', { username, email, password });
      navigate('/login', { state: { registered: true } });
    } catch (err) {
      if (axios.isAxiosError(err) && Array.isArray(err.response?.data)) {
        setErrors((err.response.data as IdentityError[]).map(x => x.description));
      } else if (axios.isAxiosError(err) && err.response?.status === 429) {
        setErrors(['Too many attempts, please wait a moment and try again.']);
      } else {
        setErrors(['Registration failed. Please try again.']);
      }
    } finally {
      setSubmitting(false);
    }
  };

  return (
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
          Password
          <input
            type="password"
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
  );
};

export default RegisterPage;
