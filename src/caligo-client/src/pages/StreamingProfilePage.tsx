import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatAmount, formatDay, kindLabel, planName, statusOf } from '../catalog';
import { formatDateTime } from '../dates';
import { titlePath } from '../thesaurarium';
import type { Plan, Recommendation, Spending, StreamingNotification, StreamingProfile, WatchEntry } from '../types';

const GENDERS = ['Female', 'Male', 'Other'];

const REASONS: Record<string, string> = {
  'Jaccard index': 'similar to your history'
};

const position = (seconds: number | null): string => {
  if (seconds == null) {
    return '';
  }

  const whole = Math.round(seconds);

  return ` · stopped at ${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
};

const StreamingProfilePage = () => {
  const [profile, setProfile] = useState<StreamingProfile | null>(null);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [spending, setSpending] = useState<Spending | null>(null);
  const [history, setHistory] = useState<WatchEntry[]>([]);
  const [recommendations, setRecommendations] = useState<Recommendation[]>([]);
  const [notifications, setNotifications] = useState<StreamingNotification[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [dateOfBirth, setDateOfBirth] = useState('');
  const [gender, setGender] = useState(GENDERS[0]);

  const loadActivity = useCallback(async () => {
    const [available, spent, watched, recommended, notified] = await Promise.all([
      api.get<Plan[]>('/api/subscription/plans'),
      api.get<Spending>('/api/me/spending'),
      api.get<WatchEntry[]>('/api/me/history'),
      api.get<Recommendation[]>('/api/me/recommendations'),
      api.get<StreamingNotification[]>('/api/me/notifications')
    ]);

    setPlans(available.data);
    setSpending(spent.data);
    setHistory(watched.data);
    setRecommendations(recommended.data);
    setNotifications(notified.data);
  }, []);

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      try {
        const mine = await api.get<StreamingProfile>('/api/me/streaming-profile');

        if (cancelled) return;

        setProfile(mine.data);
        await loadActivity();
      } catch (err) {
        if (!cancelled && statusOf(err) !== 404) {
          setError(catalogError(err, 'Could not load your profile.'));
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    load();

    return () => {
      cancelled = true;
    };
  }, [loadActivity]);

  const create = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const response = await api.post<StreamingProfile>('/api/me/streaming-profile', {
        firstName,
        lastName,
        dateOfBirth,
        gender
      });

      setProfile(response.data);
      await loadActivity();
    } catch (err) {
      setError(catalogError(err, 'Could not save your profile.'));
    } finally {
      setBusy(false);
    }
  };

  if (loading) {
    return <p className="status-line">Loading…</p>;
  }

  if (!profile) {
    return (
      <div className="editor-page">
        <h1>Your profile</h1>
        <p className="lede">
          Set up your profile to rate and comment on titles, keep a history of what you watch and listen to,
          and get recommendations.
        </p>

        {error && <p className="form-error">{error}</p>}

        <form onSubmit={create} className="editor-form">
          <label>
            First name
            <input type="text" value={firstName} onChange={e => setFirstName(e.target.value)} maxLength={50} required />
          </label>

          <label>
            Last name
            <input type="text" value={lastName} onChange={e => setLastName(e.target.value)} maxLength={50} required />
          </label>

          <label>
            Date of birth
            <input type="date" value={dateOfBirth} onChange={e => setDateOfBirth(e.target.value)} required />
          </label>

          <label>
            Gender
            <select value={gender} onChange={e => setGender(e.target.value)}>
              {GENDERS.map(option => (
                <option key={option} value={option}>{option}</option>
              ))}
            </select>
          </label>

          <button type="submit" disabled={busy}>
            {busy ? 'Saving…' : 'Set up profile'}
          </button>
        </form>
      </div>
    );
  }

  const plan = plans.find(candidate => candidate.subscriptionPlanId === profile.subscriptionPlanId);

  return (
    <div className="editor-page">
      <h1>{[profile.firstName, profile.lastName].filter(Boolean).join(' ') || 'Your profile'}</h1>
      <p className="post-meta">
        {profile.email} · born {formatDay(profile.dob)} · {profile.gender}
      </p>

      {error && <p className="form-error">{error}</p>}

      <section className="catalog-section">
        <h2>
          Subscription{' '}
          <span className={profile.active ? 'badge badge-scheduled' : 'badge badge-expired'}>
            {profile.active ? 'active' : 'expired'}
          </span>
        </h2>
        {plan && (
          <p className="status-line">
            {planName(plan)} · {formatAmount(plan.monthlyPrice)} a month ·{' '}
            {profile.active ? 'renews on' : 'ended on'} {formatDay(profile.subscriptionEndDate)}
          </p>
        )}
      </section>

      <section className="catalog-section">
        <h2>Payments</h2>
        <p className="status-line">
          {spending ? <>All time: <strong>{formatAmount(spending.allTimeSpending)}</strong></> : 'Loading…'}
        </p>
        <ul className="dashboard-list">
          {(spending?.payments ?? []).map(payment => {
            const paidFor = plans.find(candidate => candidate.subscriptionPlanId === payment.subscriptionPlanId);

            return (
              <li key={payment.paymentId}>
                <span>{formatDateTime(payment.date)} · {paidFor ? planName(paidFor) : `Plan ${payment.subscriptionPlanId}`}</span>
                <span className="post-meta">{formatAmount(payment.amount)}</span>
              </li>
            );
          })}
        </ul>
      </section>

      <section className="catalog-section">
        <h2>Recommended for you</h2>
        {recommendations.length === 0 && (
          <p className="status-line">Watch or listen to any title and recommendations appear here.</p>
        )}
        <ul className="dashboard-list">
          {recommendations.map(item => (
            <li key={item.contentId}>
              <Link to={titlePath(item.contentId)}>{item.title}</Link>
              <span className="post-meta">
                {[kindLabel(item.kind), item.reason ? REASONS[item.reason] ?? item.reason : undefined, formatDay(item.date)].filter(Boolean).join(' · ')}
              </span>
            </li>
          ))}
        </ul>
      </section>

      {notifications.length > 0 && (
        <section className="catalog-section">
          <h2>Notifications</h2>
          <ul className="dashboard-list">
            {notifications.map(notification => (
              <li key={notification.notificationId}>
                <span>{notification.reason}</span>
                <span className="post-meta">{formatDateTime(notification.date)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="catalog-section">
        <h2>History</h2>
        {history.length === 0 && <p className="status-line">Nothing watched or listened to yet.</p>}
        <ul className="dashboard-list">
          {history.map(entry => (
            <li key={entry.watchedContentId}>
              <Link to={titlePath(entry.contentId)}>{entry.title}</Link>
              <span className="post-meta">
                {kindLabel(entry.kind)} · {formatDateTime(entry.date)}{position(entry.timestamp)}
              </span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
};

export default StreamingProfilePage;
