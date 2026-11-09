import { useState } from 'react';
import { useSetCastPaymentMutation } from '../api/catalogApi';
import { catalogError } from '../catalog';
import type { CastMember } from '../types';

const CastPayAdmin = ({ contentId, cast }: { contentId: number; cast: CastMember[] }) => {
  const [amounts, setAmounts] = useState<Record<number, string>>(() =>
    Object.fromEntries(cast.map(actor => [actor.actorId, actor.payment == null ? '' : String(actor.payment)])));
  const [setCastPayment, { isLoading: saving }] = useSetCastPaymentMutation();
  const [note, setNote] = useState<string | null>(null);

  const save = async (actorId: number) => {
    const amount = (amounts[actorId] ?? '').trim();

    setNote(null);
    try {
      await setCastPayment({ contentId, actorId, payment: amount === '' ? null : Number(amount) }).unwrap();
    } catch (err) {
      setNote(catalogError(err, 'Could not save the payment.'));
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
