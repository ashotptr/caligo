import { NavLink, useLocation } from 'react-router-dom';

const TITLES: Record<string, string> = {
  '/catalog': 'Catalog',
  '/series': 'Series',
  '/artists': 'Artists',
  '/albums': 'Albums'
};

const CatalogNav = () => {
  const { pathname } = useLocation();

  return (
    <div className="catalog-head">
      <h1>{TITLES[pathname] ?? 'Catalog'}</h1>

      <nav className="catalog-nav" aria-label="Catalog sections">
        <NavLink to="/catalog" end>Titles</NavLink>
        <NavLink to="/series">Series</NavLink>
        <NavLink to="/artists">Artists</NavLink>
        <NavLink to="/albums">Albums</NavLink>
      </nav>
    </div>
  );
};

export default CatalogNav;
