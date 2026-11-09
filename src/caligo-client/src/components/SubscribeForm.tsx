import { useState } from 'react';
import { useSubscribeMutation } from '../api/accountApi';
import { failureOf } from '../api/errors';

const SubscribeForm = () => {
  const [email, setEmail] = useState('');
  const [subscribe, { isLoading: sending, isSuccess: done }] = useSubscribeMutation();
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    try {
      await subscribe(email).unwrap();
      setEmail('');
    } catch (err) {
      const data = failureOf(err)?.data;

      setError(Array.isArray(data) ? String(data[0]) : 'Could not subscribe. Try again later.');
    }
  };

  if (done) {
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
        <button type="submit" disabled={sending}>
          {sending ? 'Subscribing…' : 'Subscribe'}
        </button>
      </div>
      {error && <p className="form-error">{error}</p>}
    </form>
  );
};

export default SubscribeForm;
