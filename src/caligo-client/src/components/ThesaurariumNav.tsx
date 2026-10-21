import { NavLink } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import { ARTISTS, COLLECTIONS, SERIES, THESAURARIUM, TITLES } from '../thesaurarium';

const ThesaurariumNav = ({ title = 'Thesaurarium' }: { title?: string }) => {
  const { isAuthenticated } = useAuth();

  return (
    <div className="catalog-head">
      <h1>{title}</h1>

      <nav className="catalog-nav" aria-label="Thesaurarium sections">
        <NavLink to={THESAURARIUM} end>Albums</NavLink>
        <NavLink to={TITLES} end>Titles</NavLink>
        <NavLink to={SERIES} end>Series</NavLink>
        <NavLink to={ARTISTS} end>Artists</NavLink>
        {isAuthenticated && <NavLink to={COLLECTIONS} end>My Collections</NavLink>}
      </nav>
    </div>
  );
};

export default ThesaurariumNav;
