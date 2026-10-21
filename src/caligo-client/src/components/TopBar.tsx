import { useEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import api from '../api/axios';
import { useAuth } from '../contexts/AuthContext';
import { usePlayer } from '../contexts/PlayerContext';
import { useToast } from '../contexts/ToastContext';
import { THESAURARIUM, inThesaurarium } from '../thesaurarium';
import type { Named } from '../types';

type Scope = 'posts' | 'thesaurarium';

const LIVE_SEARCH_MS = 250;
const SEARCHABLE = ['', '/titles', '/series', '/artists', '/collections'].map(section => `${THESAURARIUM}${section}`);

const TopBar = ({ onMenu }: { onMenu: () => void }) => {
  const { isAuthenticated, user, logout } = useAuth();
  const { stop } = usePlayer();
  const toast = useToast();
  const location = useLocation();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();

  const inside = inThesaurarium(location.pathname);
  const searchable = SEARCHABLE.includes(location.pathname);
  const onGrid = location.pathname === THESAURARIUM;
  const current = location.pathname === '/' ? params.get('q') ?? '' : searchable ? params.get('query') ?? '' : '';
  const artistId = onGrid ? params.get('artistId') ?? '' : '';

  const [scope, setScope] = useState<Scope>(inside ? 'thesaurarium' : 'posts');
  const [text, setText] = useState(current);
  const [artists, setArtists] = useState<Named[]>([]);
  const typing = useRef<number | undefined>(undefined);

  useEffect(() => {
    setScope(inside ? 'thesaurarium' : 'posts');
  }, [inside]);

  useEffect(() => {
    setText(current);
  }, [current]);

  useEffect(() => () => window.clearTimeout(typing.current), []);

  useEffect(() => {
    if (!onGrid) {
      return;
    }

    let cancelled = false;

    api
      .get<Named[]>('/api/releases/artists')
      .then(response => {
        if (!cancelled) setArtists(response.data);
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
    };
  }, [onGrid, isAuthenticated]);

  const filter = (changes: Record<string, string>) => {
    setParams(previous => {
      const next = new URLSearchParams(previous);

      for (const [key, value] of Object.entries(changes)) {
        if (value) {
          next.set(key, value);
        } else {
          next.delete(key);
        }
      }

      return next;
    }, { replace: true });
  };

  const typed = (value: string) => {
    setText(value);

    if (scope !== 'thesaurarium' || !searchable) {
      return;
    }

    window.clearTimeout(typing.current);
    typing.current = window.setTimeout(() => filter({ query: value }), LIVE_SEARCH_MS);
  };

  const search = (event: React.FormEvent) => {
    event.preventDefault();
    window.clearTimeout(typing.current);

    const term = text.trim();

    if (scope === 'thesaurarium') {
      if (searchable) {
        filter({ query: term });
      } else {
        navigate(term ? `${THESAURARIUM}?query=${encodeURIComponent(term)}` : THESAURARIUM);
      }
    } else {
      navigate(term ? `/?q=${encodeURIComponent(term)}` : '/');
    }
  };

  const signOut = () => {
    logout();
    stop();
    toast('Logged out');
    navigate('/');
  };

  return (
    <header className="top-bar">
      <button type="button" className="menu-button" aria-label="Open the menu" onClick={onMenu}>☰</button>

      <form className="search-container" role="search" onSubmit={search}>
        <select
          className="scope-select"
          value={scope}
          onChange={event => setScope(event.target.value as Scope)}
          aria-label="Search in"
        >
          <option value="posts">Posts</option>
          <option value="thesaurarium">Thesaurarium</option>
        </select>
        {onGrid && scope === 'thesaurarium' && (
          <select
            className="artist-filter"
            value={artistId}
            onChange={event => filter({ artistId: event.target.value })}
            aria-label="Artist"
          >
            <option value="">All Artists</option>
            {artists.map(artist => (
              <option key={artist.id} value={artist.id}>{artist.name}</option>
            ))}
          </select>
        )}
        <input
          type="search"
          placeholder="Search..."
          value={text}
          maxLength={100}
          aria-label="Search"
          onChange={event => typed(event.target.value)}
        />
      </form>

      <div className="auth-section">
        {isAuthenticated ? (
          <div className="user-display">
            <span>{user?.name}</span>
            <button type="button" className="logout-icon" title="Logout" aria-label="Logout" onClick={signOut}>✕</button>
          </div>
        ) : (
          <>
            <Link to="/register" className="btn-secondary">Sign up</Link>
            <Link to="/login" className="btn-primary">Log in</Link>
          </>
        )}
      </div>
    </header>
  );
};

export default TopBar;
