import { useToast } from '../contexts/ToastContext';
import { queueToRadio, useQueueState } from '../radioQueue';
import { mixtapePath } from '../thesaurarium';

interface RadioQueueButtonProps {
  contentId: number;
  audioUri: string | null;
}

const BUSY = ['SYNCING', 'WAITING', 'LIVE', 'ERROR'];

const QueueButton = ({ id, contentId }: { id: string; contentId: number }) => {
  const toast = useToast();
  const state = useQueueState(id);

  const queue = async (event: React.MouseEvent) => {
    event.stopPropagation();

    if (await queueToRadio(id, contentId)) {
      toast('Signal broadcasting to radio');
    }
  };

  return (
    <button
      type="button"
      className={`btn-live-control ${state.toLowerCase()}`}
      title="Queue to Live Radio"
      disabled={BUSY.includes(state)}
      onClick={queue}
    >
      <span className="live-dot" />
      <span className="status-text">{state}</span>
    </button>
  );
};

const RadioQueueButton = ({ contentId, audioUri }: RadioQueueButtonProps) => {
  const id = mixtapePath(audioUri);

  return id ? <QueueButton id={id} contentId={contentId} /> : null;
};

export default RadioQueueButton;
