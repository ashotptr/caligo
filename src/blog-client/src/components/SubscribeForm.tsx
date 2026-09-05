import { useState } from 'react';
import axios from 'axios';
import api from '../api/axios';

type Status = 'idle' | 'sending' | 'done' | 'error';

const SubscribeForm = () => {
  const [email, setEmail] = useState('');
  const [status, setStatus] = useState<Status>('idle');
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setStatus('sending');
    setError(null);

    try {
      await api.post('/api/subscriptions', { email });
      setStatus('done');
      setEmail('');
    } catch (err) {
      setStatus('error');
      setError(
        axios.isAxiosError(err) && Array.isArray(err.response?.data)
          ? String(err.response?.data[0])
          : 'Could not subscribe. Try again later.'
      );
    }
  };

  if (status === 'done') {
    return <p className="subscribe-done">Subscribed. You will get an email when a new post goes up.</p>;
  }

  return (
    <form onSubmit={submit} className="subscribe-form">
      <label htmlFor="subscribe-email">Get new posts by email</label>
      <div className="subscribe-row">
        <input
          id="subscribe-email"
          type="email"
          required
          placeholder="you@example.com"
          value={email}
          onChange={e => setEmail(e.target.value)}
        />
        <button type="submit" disabled={status === 'sending'}>
          {status === 'sending' ? 'Subscribing…' : 'Subscribe'}
        </button>
      </div>
      {error && <p className="form-error">{error}</p>}
    </form>
  );
};

export default SubscribeForm;
