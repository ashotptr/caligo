import { Link, useNavigate } from 'react-router-dom';

const BackLink = ({ fallback, label = '← Back' }: { fallback: string; label?: string }) => {
  const navigate = useNavigate();

  const back = (event: React.MouseEvent) => {
    const depth = (window.history.state as { idx?: number } | null)?.idx ?? 0;

    if (depth > 0) {
      event.preventDefault();
      navigate(-1);
    }
  };

  return (
    <Link to={fallback} className="back-link" onClick={back}>
      {label}
    </Link>
  );
};

export default BackLink;
