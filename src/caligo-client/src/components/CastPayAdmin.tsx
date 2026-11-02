import { useState } from 'react';
import api from '../api/axios';
import { catalogError } from '../catalog';
import type { CastMember } from '../types';

const CastPayAdmin = ({ contentId, cast, onChanged }: { contentId: number; cast: CastMember[]; onChanged: () => void }) => {
  const [amounts, setAmounts] = useState<Record<number, string>>(() =>
    Object.fromEntries(cast.map(actor => [actor.actorId, actor.payment == null ? '' : String(actor.payment)])));
  const [saving, setSaving] = useState(false);
  const [note, setNote] = useState<string | null>(null);

  const save = async (actorId: number) => {
    const amount = (amounts[actorId] ?? '').trim();

    setSaving(true);
    setNote(null);
    try {
      await api.put(`/api/content/${contentId}/cast/${actorId}`, { payment: amount === '' ? null : Number(amount) });
      onChanged();
    } catch (err) {
      setNote(catalogError(err, 'Could not save the payment.'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="catalog-section media-admin">
      <h2>Cast pay (admin)</h2>

      {cast.map(actor => (
        <div key={actor.actorId} className="media-admin-form">
          <span>{actor.name}</span>
          <input
            type="number"
            min="0"
            step="any"
            value={amounts[actor.actorId] ?? ''}
            onChange={event => setAmounts(current => ({ ...current, [actor.actorId]: event.target.value }))}
            placeholder="Not recorded"
          />
          <button type="button" onClick={() => void save(actor.actorId)} disabled={saving}>
            Save
          </button>
        </div>
      ))}

      {note && <p className="form-error">{note}</p>}
    </section>
  );
};

export default CastPayAdmin;
