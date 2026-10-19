import { useEffect, useState } from 'react';
import { Link, NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './contexts/AuthContext';
import ProtectedRoute from './components/ProtectedRoute';
import RequireRole from './components/RequireRole';
import PostsListPage from './pages/PostsListPage';
import PostDetailPage from './pages/PostDetailPage';
import PostEditorPage from './pages/PostEditorPage';
import PostStatsPage from './pages/PostStatsPage';
import DashboardPage from './pages/DashboardPage';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import CatalogPage from './pages/CatalogPage';
import ContentDetailPage from './pages/ContentDetailPage';
import SeriesListPage from './pages/SeriesListPage';
import SeriesDetailPage from './pages/SeriesDetailPage';
import ActorPage from './pages/ActorPage';
import ArtistsPage from './pages/ArtistsPage';
import ArtistDetailPage from './pages/ArtistDetailPage';
import AlbumsPage from './pages/AlbumsPage';
import ReleasePage from './pages/ReleasePage';
import StreamingProfilePage from './pages/StreamingProfilePage';
import InsightsPage from './pages/InsightsPage';
import LabRunPage from './pages/LabRunPage';
import ListenPage from './pages/ListenPage';
import RadioBar from './components/RadioBar';
import TopBar from './components/TopBar';
import './App.css';

const App = () => {
  const { isAuthenticated, hasRole } = useAuth();
  const location = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);

  useEffect(() => {
    setMenuOpen(false);
    window.scrollTo(0, 0);
  }, [location.pathname]);

  return (
    <div className="shell">
      <aside className={`sidebar${menuOpen ? ' open' : ''}`}>
        <Link to="/" className="brand">./caligo</Link>

        <nav className="sidebar-nav" aria-label="Main">
          <NavLink to="/" end className="nav-btn">Posts</NavLink>
          <NavLink to="/catalog" className="nav-btn">Catalog</NavLink>
          <NavLink to="/listen" className="nav-btn">Radio</NavLink>

          {isAuthenticated && <div className="nav-divider" />}
          {isAuthenticated && <NavLink to="/streaming" className="nav-btn">Streaming</NavLink>}
          {isAuthenticated && <NavLink to="/dashboard" className="nav-btn">Dashboard</NavLink>}
          {hasRole('Admin') && <NavLink to="/insights" className="nav-btn">Insights</NavLink>}
          {hasRole('Admin') && <NavLink to="/lab" className="nav-btn">Lab</NavLink>}
          {hasRole('Admin', 'Writer') && <NavLink to="/posts/new" className="nav-btn">Write</NavLink>}
        </nav>
      </aside>

      {menuOpen && <div className="sidebar-scrim" onClick={() => setMenuOpen(false)} />}

      <div className="main-view">
        <TopBar onMenu={() => setMenuOpen(open => !open)} />

        <main className="content-area">
          <Routes>
            <Route path="/" element={<PostsListPage />} />
            <Route path="/posts/:id" element={<PostDetailPage />} />
            <Route path="/login" element={<LoginPage />} />
            <Route path="/register" element={<RegisterPage />} />

            <Route path="/catalog" element={<CatalogPage />} />
            <Route path="/catalog/:id" element={<ContentDetailPage />} />
            <Route path="/songs" element={<Navigate to="/catalog?kind=song" replace />} />
            <Route path="/series" element={<SeriesListPage />} />
            <Route path="/series/:id" element={<SeriesDetailPage />} />
            <Route path="/actors/:id" element={<ActorPage />} />
            <Route path="/artists" element={<ArtistsPage />} />
            <Route path="/artists/:id" element={<ArtistDetailPage />} />
            <Route path="/albums" element={<AlbumsPage />} />
            <Route path="/albums/:id" element={<ReleasePage kind="album" />} />
            <Route path="/singles/:id" element={<ReleasePage kind="single" />} />
            <Route path="/listen" element={<ListenPage />} />

            <Route element={<ProtectedRoute />}>
              <Route path="/dashboard" element={<DashboardPage />} />
              <Route path="/streaming" element={<StreamingProfilePage />} />
            </Route>

            <Route element={<RequireRole roles={['Admin']} />}>
              <Route path="/insights" element={<InsightsPage />} />
              <Route path="/lab" element={<LabRunPage />} />
              <Route path="/lab/:runId" element={<LabRunPage />} />
            </Route>

            <Route element={<RequireRole roles={['Admin', 'Writer']} />}>
              <Route path="/posts/new" element={<PostEditorPage />} />
              <Route path="/posts/:id/edit" element={<PostEditorPage />} />
              <Route path="/posts/:id/stats" element={<PostStatsPage />} />
            </Route>

            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </main>
      </div>

      <footer className="player-footer">
        <RadioBar />
      </footer>
    </div>
  );
};

export default App;
