import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';

type Scope = 'posts' | 'titles';

const CATALOG_PATHS = ['/catalog', '/series', '/actors', '/artists', '/albums', '/singles', '/songs'];

const TopBar = ({ onMenu }: { onMenu: () => void }) => {
  const { isAuthenticated, user, logout } = useAuth();
  const location = useLocation();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const inCatalog = CATALOG_PATHS.some(path => location.pathname.startsWith(path));
  const current = location.pathname === '/'
    ? params.get('q') ?? ''
    : location.pathname === '/catalog' ? params.get('query') ?? '' : '';

  const [scope, setScope] = useState<Scope>(inCatalog ? 'titles' : 'posts');
  const [text, setText] = useState(current);

  useEffect(() => {
    setScope(inCatalog ? 'titles' : 'posts');
  }, [inCatalog]);

  useEffect(() => {
    setText(current);
  }, [current]);

  const search = (event: React.FormEvent) => {
    event.preventDefault();

    const term = text.trim();

    if (scope === 'titles') {
      navigate(term ? `/catalog?query=${encodeURIComponent(term)}` : '/catalog');
    } else {
      navigate(term ? `/?q=${encodeURIComponent(term)}` : '/');
    }
  };

  const signOut = () => {
    logout();
    navigate('/');
  };

  return (
    <header className="top-bar">
      <button type="button" className="menu-button" aria-label="Open the menu" onClick={onMenu}>☰</button>

      <form className="search-container" role="search" onSubmit={search}>
        <select value={scope} onChange={event => setScope(event.target.value as Scope)} aria-label="Search in">
          <option value="posts">Posts</option>
          <option value="titles">Titles</option>
        </select>
        <input
          type="search"
          placeholder="Search..."
          value={text}
          maxLength={100}
          aria-label="Search"
          onChange={event => setText(event.target.value)}
        />
      </form>

      <div className="auth-section">
        {isAuthenticated ? (
          <div className="user-display">
            <span>{user?.name}</span>
            <button type="button" className="logout-icon" title="Sign out" aria-label="Sign out" onClick={signOut}>✕</button>
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
