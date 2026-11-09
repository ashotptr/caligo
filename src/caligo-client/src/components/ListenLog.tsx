import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useLogWatchMutation } from '../api/socialApi';
import { PROFILE, statusOf } from '../catalog';
import { usePlayer } from '../contexts/PlayerContext';
import { useAuth } from '../store/hooks';

type Status = 'idle' | 'saving' | 'profile' | 'failed';

const ListenLog = ({ contentId }: { contentId: number }) => {
  const { isAuthenticated } = useAuth();
  const { loggedId, markLogged } = usePlayer();
  const [logWatch] = useLogWatchMutation();
  const [status, setStatus] = useState<Status>('idle');

  useEffect(() => {
    setStatus('idle');
  }, [contentId]);

  if (!isAuthenticated) {
    return null;
  }

  const log = async () => {
    setStatus('saving');
    try {
      await logWatch({ contentId, timestamp: null }).unwrap();
      setStatus('idle');
      markLogged(contentId);
    } catch (err) {
      setStatus(statusOf(err) === 409 ? 'profile' : 'failed');
    }
  };

  if (loggedId === contentId) {
    return <span className="hint">in your history</span>;
  }

  if (status === 'profile') {
    return <Link to={PROFILE}>set up your profile to keep history</Link>;
  }

  return (
    <button type="button" className="link-button" onClick={log} disabled={status === 'saving'}>
      {status === 'failed' ? 'try again' : 'log this listen'}
    </button>
  );
};

export default ListenLog;
