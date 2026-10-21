import { useEffect, useState } from 'react';
import { Link, NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { useAuth } from './contexts/AuthContext';
import { useCollections } from './contexts/CollectionsContext';
import ProtectedRoute from './components/ProtectedRoute';
import RequireRole from './components/RequireRole';
import PostsListPage from './pages/PostsListPage';
import PostDetailPage from './pages/PostDetailPage';
import PostEditorPage from './pages/PostEditorPage';
import PostStatsPage from './pages/PostStatsPage';
import DashboardPage from './pages/DashboardPage';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import ThesaurariumPage from './pages/ThesaurariumPage';
import CatalogPage from './pages/CatalogPage';
import ContentDetailPage from './pages/ContentDetailPage';
import SeriesListPage from './pages/SeriesListPage';
import SeriesDetailPage from './pages/SeriesDetailPage';
import ActorPage from './pages/ActorPage';
import ArtistsPage from './pages/ArtistsPage';
import ArtistDetailPage from './pages/ArtistDetailPage';
import ReleasePage from './pages/ReleasePage';
import CollectionsPage from './pages/CollectionsPage';
import CollectionPage from './pages/CollectionPage';
import StreamingProfilePage from './pages/StreamingProfilePage';
import InsightsPage from './pages/InsightsPage';
import LabRunPage from './pages/LabRunPage';
import RadioPage from './pages/RadioPage';
import ChatRoomsPage from './pages/ChatRoomsPage';
import OmniscientPage from './pages/OmniscientPage';
import FogGate from './components/FogGate';
import IngestPanel from './components/IngestPanel';
import PlayerBar from './components/PlayerBar';
import TopBar from './components/TopBar';
import { ARTISTS, COLLECTIONS, SERIES, THESAURARIUM, TITLES, inThesaurarium } from './thesaurarium';
import './App.css';

const App = () => {
  const { isAuthenticated, hasRole } = useAuth();
  const { createCollection } = useCollections();
  const location = useLocation();
  const [menuOpen, setMenuOpen] = useState(false);

  const inCollections = location.pathname.startsWith(COLLECTIONS);
  const thesaurariumActive = inThesaurarium(location.pathname) && !inCollections;

  useEffect(() => {
    setMenuOpen(false);
    window.scrollTo(0, 0);
  }, [location.pathname]);

  return (
    <div className="shell">
      <FogGate />

      <aside className={`sidebar${menuOpen ? ' open' : ''}`}>
        <Link to="/" className="brand">./caligo</Link>

        <nav className="sidebar-nav" aria-label="Main">
          <NavLink to="/" end className="nav-btn">Posts</NavLink>
          <Link to={THESAURARIUM} className={`nav-btn${thesaurariumActive ? ' active' : ''}`}>Thesaurarium</Link>
          <NavLink to="/listen" className="nav-btn">Radio</NavLink>

          <div className="nav-divider" />
          {isAuthenticated && <NavLink to={COLLECTIONS} className="nav-btn">My Collections</NavLink>}
          {isAuthenticated && (
            <button
              type="button"
              className="nav-btn"
              onClick={() => {
                setMenuOpen(false);
                createCollection();
              }}
            >
              <span>＋</span> Create Playlist
            </button>
          )}
          <NavLink to="/chat-rooms" className="nav-btn">Chat Rooms</NavLink>
          <NavLink to="/omniscient" className="nav-btn">Omniscient AI</NavLink>

          {isAuthenticated && <div className="nav-divider" />}
          {isAuthenticated && <NavLink to="/streaming" className="nav-btn">Streaming</NavLink>}
          {isAuthenticated && <NavLink to="/dashboard" className="nav-btn">Dashboard</NavLink>}
          {hasRole('Admin') && <NavLink to="/insights" className="nav-btn">Insights</NavLink>}
          {hasRole('Admin') && <NavLink to="/lab" className="nav-btn">Lab</NavLink>}
          {hasRole('Admin', 'Writer') && <NavLink to="/posts/new" className="nav-btn">Write</NavLink>}
        </nav>

        {isAuthenticated && <IngestPanel />}
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

            <Route path={THESAURARIUM} element={<ThesaurariumPage />} />
            <Route path={TITLES} element={<CatalogPage />} />
            <Route path={`${TITLES}/:id`} element={<ContentDetailPage />} />
            <Route path={SERIES} element={<SeriesListPage />} />
            <Route path={`${SERIES}/:id`} element={<SeriesDetailPage />} />
            <Route path={ARTISTS} element={<ArtistsPage />} />
            <Route path={`${ARTISTS}/:id`} element={<ArtistDetailPage />} />
            <Route path={`${THESAURARIUM}/albums/:id`} element={<ReleasePage kind="album" />} />
            <Route path={`${THESAURARIUM}/singles/:id`} element={<ReleasePage kind="single" />} />
            <Route path={`${THESAURARIUM}/actors/:id`} element={<ActorPage />} />

            <Route path="/listen" element={<RadioPage />} />
            <Route path="/chat-rooms" element={<ChatRoomsPage />} />
            <Route path="/omniscient" element={<OmniscientPage />} />

            <Route element={<ProtectedRoute />}>
              <Route path={COLLECTIONS} element={<CollectionsPage />} />
              <Route path={`${COLLECTIONS}/:id`} element={<CollectionPage />} />
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
        <PlayerBar />
      </footer>
    </div>
  );
};

export default App;
