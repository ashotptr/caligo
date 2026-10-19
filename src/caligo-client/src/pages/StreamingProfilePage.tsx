import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { catalogError, formatAmount, formatDay, kindLabel, planFacts, planName, statusOf } from '../catalog';
import { formatDateTime } from '../dates';
import type { Plan, Recommendation, Spending, StreamingNotification, StreamingProfile, WatchEntry } from '../types';

const GENDERS = ['Female', 'Male', 'Other'];

const position = (seconds: number | null): string => {
  if (seconds == null) {
    return '';
  }

  const whole = Math.round(seconds);

  return ` · stopped at ${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, '0')}`;
};

const StreamingProfilePage = () => {
  const [plans, setPlans] = useState<Plan[]>([]);
  const [profile, setProfile] = useState<StreamingProfile | null>(null);
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
  const [planId, setPlanId] = useState<number | null>(null);

  const loadActivity = useCallback(async () => {
    const [spent, watched, recommended, notified] = await Promise.all([
      api.get<Spending>('/api/me/spending'),
      api.get<WatchEntry[]>('/api/me/history'),
      api.get<Recommendation[]>('/api/me/recommendations'),
      api.get<StreamingNotification[]>('/api/me/notifications')
    ]);

    setSpending(spent.data);
    setHistory(watched.data);
    setRecommendations(recommended.data);
    setNotifications(notified.data);
  }, []);

  useEffect(() => {
    let cancelled = false;

    const load = async () => {
      try {
        const available = await api.get<Plan[]>('/api/subscription/plans');

        if (cancelled) return;

        setPlans(available.data);
        setPlanId(current => current ?? available.data[0]?.subscriptionPlanId ?? null);

        try {
          const mine = await api.get<StreamingProfile>('/api/me/streaming-profile');

          if (cancelled) return;

          setProfile(mine.data);
          setPlanId(mine.data.subscriptionPlanId);
          await loadActivity();
        } catch (err) {
          if (statusOf(err) !== 404) {
            throw err;
          }
        }
      } catch (err) {
        if (!cancelled) setError(catalogError(err, 'Could not load your streaming profile.'));
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
        gender,
        subscriptionPlanId: planId
      });

      setProfile(response.data);
      await loadActivity();
    } catch (err) {
      setError(catalogError(err, 'Could not create the profile.'));
    } finally {
      setBusy(false);
    }
  };

  const renew = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const response = await api.post<StreamingProfile>('/api/me/subscription', { subscriptionPlanId: planId });

      setProfile(response.data);
      await loadActivity();
    } catch (err) {
      setError(catalogError(err, 'Could not renew the subscription.'));
    } finally {
      setBusy(false);
    }
  };

  if (loading) {
    return <p className="status-line">Loading…</p>;
  }

  const planPicker = (
    <fieldset className="schedule-field">
      <legend>Plan</legend>
      {plans.map(plan => (
        <label key={plan.subscriptionPlanId} className="choice plan-choice">
          <input
            type="radio"
            name="plan"
            checked={planId === plan.subscriptionPlanId}
            onChange={() => setPlanId(plan.subscriptionPlanId)}
          />
          <span>
            <strong>{planName(plan)}</strong> · {formatAmount(plan.monthlyPrice)} a month
            <span className="hint plan-facts">{planFacts(plan).join(' · ')}</span>
          </span>
        </label>
      ))}
    </fieldset>
  );

  if (!profile) {
    return (
      <div className="editor-page">
        <h1>Streaming profile</h1>
        <p className="lede">
          Rating, commenting, history and recommendations live in the streaming catalog, which keeps its own
          list of users. This creates yours and links it to your Caligo account. Nothing is charged: the first
          month is only written down as a payment.
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

          {planPicker}

          <button type="submit" disabled={busy || planId === null}>
            {busy ? 'Creating…' : 'Create profile'}
          </button>
        </form>
      </div>
    );
  }

  const currentPlan = plans.find(plan => plan.subscriptionPlanId === profile.subscriptionPlanId);

  return (
    <div className="editor-page">
      <h1>{[profile.firstName, profile.lastName].filter(Boolean).join(' ') || 'Streaming profile'}</h1>
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
        <p className="status-line">
          {currentPlan ? planName(currentPlan) : `Plan ${profile.subscriptionPlanId}`} · {formatDay(profile.subscriptionStartDate)} to {formatDay(profile.subscriptionEndDate)}
        </p>

        <form onSubmit={renew} className="editor-form">
          {planPicker}
          <button type="submit" disabled={busy || planId === null}>
            {busy ? 'Renewing…' : 'Renew for 30 days'}
          </button>
        </form>
      </section>

      <section className="catalog-section">
        <h2>Spending</h2>
        <p className="status-line">
          {spending ? <>All time: <strong>{formatAmount(spending.allTimeSpending)}</strong></> : 'Loading…'}
        </p>
        <ul className="dashboard-list">
          {(spending?.payments ?? []).map(payment => {
            const plan = plans.find(candidate => candidate.subscriptionPlanId === payment.subscriptionPlanId);

            return (
              <li key={payment.paymentId}>
                <span>{formatDateTime(payment.date)} · {plan ? planName(plan) : `plan ${payment.subscriptionPlanId}`}</span>
                <span className="post-meta">{formatAmount(payment.amount)}</span>
              </li>
            );
          })}
        </ul>
      </section>

      <section className="catalog-section">
        <h2>Recommended for you</h2>
        {recommendations.length === 0 && (
          <p className="status-line">Log a watch or a listen on any title and recommendations appear here.</p>
        )}
        <ul className="dashboard-list">
          {recommendations.map(item => (
            <li key={item.contentId}>
              <Link to={`/catalog/${item.contentId}`}>{item.title}</Link>
              <span className="post-meta">
                {[kindLabel(item.kind), item.reason, formatDay(item.date)].filter(Boolean).join(' · ')}
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
              <Link to={`/catalog/${entry.contentId}`}>{entry.title}</Link>
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
