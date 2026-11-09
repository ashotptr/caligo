import { useCallback, useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useGetContentQuery } from '../api/catalogApi';
import { apiError, isEditConflict } from '../api/errors';
import { useCreatePostMutation, useLazyGetPostQuery, useUpdatePostMutation } from '../api/postsApi';
import ContentPicker from '../components/ContentPicker';
import type { PickedContent } from '../components/ContentPicker';
import Markdown from '../components/Markdown';
import { formatDateTime, fromLocalInput, parseServerDate, toLocalInput } from '../dates';
import type { PostStatus } from '../types';

const DAY_MS = 24 * 60 * 60 * 1000;

const MAX_SCHEDULE_DAYS = 365;

const PostEditorPage = () => {
  const { id } = useParams<{ id: string }>();
  const [searchParams] = useSearchParams();
  const isEditing = !!id;
  const reviewOf = searchParams.get('contentId');

  const [title, setTitle] = useState('');
  const [tagsInput, setTagsInput] = useState('');
  const [content, setContent] = useState('');
  const [reviewed, setReviewed] = useState<PickedContent | null>(null);
  const [status, setStatus] = useState<PostStatus | null>(null);
  const [scheduledFor, setScheduledFor] = useState<string | null>(null);
  const [when, setWhen] = useState<'now' | 'schedule'>('now');
  const [publishAt, setPublishAt] = useState('');
  const [etag, setEtag] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [showPreview, setShowPreview] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(isEditing);
  const [saving, setSaving] = useState(false);
  const navigate = useNavigate();

  const [fetchPost] = useLazyGetPostQuery();
  const [createPost] = useCreatePostMutation();
  const [updatePost] = useUpdatePostMutation();
  const { data: reviewedContent } = useGetContentQuery(Number(reviewOf), { skip: isEditing || !reviewOf });

  const load = useCallback(async () => {
    const { post, etag: current } = await fetchPost(id ?? '').unwrap();

    setTitle(post.title);
    setTagsInput(post.tags.join(', '));
    setContent(post.content);
    setReviewed(post.contentId === null
      ? null
      : { contentId: post.contentId, title: post.contentTitle ?? `Title ${post.contentId}` });
    setStatus(post.status);
    setScheduledFor(post.scheduledFor);
    setEtag(current);

    if (post.status === 'scheduled' && post.scheduledFor) {
      setWhen('schedule');
      setPublishAt(toLocalInput(parseServerDate(post.scheduledFor)));
    }
  }, [id, fetchPost]);

  useEffect(() => {
    if (!isEditing) return;

    const fetchPost = async () => {
      try {
        await load();
      } catch {
        setError('Could not load the post for editing.');
      } finally {
        setLoading(false);
      }
    };
    fetchPost();
  }, [isEditing, load]);

  useEffect(() => {
    if (reviewedContent) {
      setReviewed({ contentId: reviewedContent.contentId, title: reviewedContent.title });
    }
  }, [reviewedContent]);

  const canSchedule = !isEditing || status === 'scheduled';

  const chooseSchedule = () => {
    setWhen('schedule');
    setPublishAt(current => current || toLocalInput(new Date(Date.now() + 60 * 60 * 1000)));
  };

  const loadTheirVersion = async () => {
    setError(null);
    try {
      await load();
      setConflict(false);
    } catch {
      setError('Could not load the latest version.');
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setConflict(false);

    const tags = tagsInput
      .split(',')
      .map(tag => tag.trim())
      .filter(tag => tag.length > 0);

    let schedule: string | undefined;

    if (canSchedule && when === 'schedule') {
      if (!publishAt || new Date(publishAt) <= new Date()) {
        setError('Pick a time in the future, or publish now.');
        return;
      }

      schedule = fromLocalInput(publishAt);
    } else if (status === 'scheduled') {
      schedule = new Date().toISOString();
    }

    setSaving(true);

    try {
      const body = { title, content, tags, publishAt: schedule, contentId: reviewed?.contentId ?? null };

      if (isEditing && id) {
        await updatePost({ id, body, etag }).unwrap();
        navigate(`/posts/${id}`);
      } else {
        const created = await createPost(body).unwrap();
        navigate(`/posts/${created.id}`);
      }
    } catch (err) {
      if (isEditing && isEditConflict(err)) {
        setConflict(true);
      } else {
        setError(apiError(err, 'Could not save the post. Please try again.'));
      }
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return <p className="status-line">Loading…</p>;
  }

  const now = new Date();
  const submitLabel = saving
    ? 'Saving…'
    : isEditing ? 'Save changes' : when === 'schedule' ? 'Schedule' : 'Publish';

  return (
    <div className="editor-page">
      <h1>{isEditing ? 'Edit post' : 'New post'}</h1>

      {conflict && (
        <div className="notice warning">
          <p>
            Someone else changed this post since you opened it, so your changes were not saved.
            Your text is still in the form: copy anything you want to keep, then load their version.
          </p>
          <button type="button" className="link-button" onClick={loadTheirVersion}>
            Load their version
          </button>
        </div>
      )}

      <form onSubmit={handleSubmit} className="editor-form">
        <label>
          Title
          <input
            type="text"
            value={title}
            onChange={e => setTitle(e.target.value)}
            maxLength={200}
            required
          />
        </label>

        <label>
          <span>Tags <span className="hint">(comma separated)</span></span>
          <input
            type="text"
            value={tagsInput}
            onChange={e => setTagsInput(e.target.value)}
            placeholder="dotnet, react, devops"
          />
        </label>

        <ContentPicker value={reviewed} onChange={setReviewed} />

        {canSchedule && (
          <fieldset className="schedule-field">
            <legend>When</legend>

            <label className="choice">
              <input type="radio" name="when" checked={when === 'now'} onChange={() => setWhen('now')} />
              Publish now
            </label>

            <label className="choice">
              <input type="radio" name="when" checked={when === 'schedule'} onChange={chooseSchedule} />
              Schedule for
              <input
                type="datetime-local"
                value={publishAt}
                min={toLocalInput(now)}
                max={toLocalInput(new Date(now.getTime() + MAX_SCHEDULE_DAYS * DAY_MS))}
                onFocus={chooseSchedule}
                onChange={e => setPublishAt(e.target.value)}
                required={when === 'schedule'}
              />
            </label>

            {status === 'scheduled' && scheduledFor && (
              <p className="hint">
                Currently scheduled for {formatDateTime(scheduledFor)}. Publish now puts it live within a few seconds.
              </p>
            )}
          </fieldset>
        )}

        <div className="editor-toolbar">
          <span className="hint">Markdown with fenced code blocks is supported.</span>
          <button
            type="button"
            className="link-button"
            onClick={() => setShowPreview(prev => !prev)}
          >
            {showPreview ? 'Back to editing' : 'Preview'}
          </button>
        </div>

        {showPreview ? (
          <div className="post-content editor-preview">
            <Markdown>{content || '*Nothing to preview yet.*'}</Markdown>
          </div>
        ) : (
          <textarea
            value={content}
            onChange={e => setContent(e.target.value)}
            rows={18}
            required
          />
        )}

        {error && <p className="form-error">{error}</p>}

        <button type="submit" disabled={saving}>
          {submitLabel}
        </button>
      </form>
    </div>
  );
};

export default PostEditorPage;
