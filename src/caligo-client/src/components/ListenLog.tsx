import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import api from '../api/axios';
import { statusOf } from '../catalog';
import { useAuth } from '../contexts/AuthContext';
import { usePlayer } from '../contexts/PlayerContext';

type Status = 'idle' | 'saving' | 'profile' | 'failed';

const ListenLog = ({ contentId }: { contentId: number }) => {
  const { isAuthenticated } = useAuth();
  const { loggedId, markLogged } = usePlayer();
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
      await api.post(`/api/content/${contentId}/watch`, { timestamp: null });
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
    return <Link to="/streaming">create a streaming profile to keep history</Link>;
  }

  return (
    <button type="button" className="link-button" onClick={log} disabled={status === 'saving'}>
      {status === 'failed' ? 'try again' : 'log this listen'}
    </button>
  );
};

export default ListenLog;
