import { Navigate, Outlet } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';

interface RequireRoleProps {
  roles: string[];
}

const RequireRole = ({ roles }: RequireRoleProps) => {
  const { isAuthenticated, hasRole } = useAuth();

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }
  
  return hasRole(...roles) ? <Outlet /> : <Navigate to="/" replace />;
};

export default RequireRole;
