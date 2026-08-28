import { Link, NavLink, Navigate, Route, Routes, useNavigate } from 'react-router-dom';
import { useAuth } from './contexts/AuthContext';
import ProtectedRoute from './components/ProtectedRoute';
import RequireRole from './components/RequireRole';
import PostsListPage from './pages/PostsListPage';
import PostDetailPage from './pages/PostDetailPage';
import PostEditorPage from './pages/PostEditorPage';
import DashboardPage from './pages/DashboardPage';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import './App.css';

const App = () => {
  const { isAuthenticated, user, hasRole, logout } = useAuth();
  const navigate = useNavigate();

  const handleLogout = () => {
    logout();
    navigate('/');
  };

  return (
    <div className="site">
      <header className="site-header">
        <Link to="/" className="site-title">./blog</Link>

        <nav>
          <NavLink to="/" end>Posts</NavLink>
          {isAuthenticated && <NavLink to="/dashboard">Dashboard</NavLink>}
          {hasRole('Admin', 'Writer') && <NavLink to="/posts/new">Write</NavLink>}
          {!isAuthenticated ? (
            <NavLink to="/login">Sign in</NavLink>
          ) : (
            <button type="button" className="link-button" onClick={handleLogout}>
              Sign out{user ? ` (${user.name})` : ''}
            </button>
          )}
        </nav>
      </header>

      <main className="site-main">
        <Routes>
          <Route path="/" element={<PostsListPage />} />
          <Route path="/posts/:id" element={<PostDetailPage />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/register" element={<RegisterPage />} />

          <Route element={<ProtectedRoute />}>
            <Route path="/dashboard" element={<DashboardPage />} />
          </Route>

          <Route element={<RequireRole roles={['Admin', 'Writer']} />}>
            <Route path="/posts/new" element={<PostEditorPage />} />
            <Route path="/posts/:id/edit" element={<PostEditorPage />} />
          </Route>

          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>

      <footer className="site-footer">
        <p>Blog</p>
      </footer>
    </div>
  );
};

export default App;
