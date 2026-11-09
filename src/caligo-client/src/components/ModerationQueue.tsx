import { Link } from 'react-router-dom';
import { useGetModerationQueueQuery, useLabelCommentMutation } from '../api/mlApi';
import type { CommentVerdict } from '../api/mlApi';
import { apiError } from '../api/errors';
import { useToast } from '../store/hooks';
import { titlePath } from '../thesaurarium';

const VERDICTS: { label: CommentVerdict; text: string }[] = [
  { label: 'spam', text: 'Spam' },
  { label: 'toxic', text: 'Toxic' },
  { label: 'ok', text: 'Fine' }
];

const percent = (value: number | null) => (value === null ? '—' : `${Math.round(value * 100)}%`);

const ModerationQueue = () => {
  const { currentData: queue, error, isFetching } = useGetModerationQueueQuery();
  const [labelComment] = useLabelCommentMutation();
  const toast = useToast();

  const decide = async (commentId: number, label: CommentVerdict) => {
    try {
      await labelComment({ commentId, label }).unwrap();
    } catch (failure) {
      toast(apiError(failure, 'Could not save the decision.'));
    }
  };

  if (error) {
    return <p className="form-error">{apiError(error, 'Could not load the flagged comments.')}</p>;
  }

  if (!queue) {
    return <p className="status-line">{isFetching ? 'Loading…' : ''}</p>;
  }

  if (queue.length === 0) {
    return <p className="status-line">No flagged comments.</p>;
  }

  return (
    <ol className="moderation-queue">
      {queue.map(comment => (
        <li key={comment.commentId}>
          <p className="moderation-text">{comment.text}</p>
          <p className="post-meta">
            <Link to={titlePath(comment.contentId)}>Title {comment.contentId}</Link>
            {` · spam ${percent(comment.spam)} · toxic ${percent(comment.toxicity)}`}
          </p>
          {comment.reasons.length > 0 && (
            <p className="post-tags">
              {comment.reasons.map(reason => <span key={reason} className="tag">{reason}</span>)}
            </p>
          )}
          <div className="album-actions">
            {VERDICTS.map(verdict => (
              <button
                key={verdict.label}
                type="button"
                className={verdict.label === 'ok' ? '' : 'btn-secondary'}
                onClick={() => void decide(comment.commentId, verdict.label)}
              >
                {verdict.text}
              </button>
            ))}
          </div>
        </li>
      ))}
    </ol>
  );
};

export default ModerationQueue;
