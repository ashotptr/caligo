import { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import api from '../api/axios';
import Markdown from '../components/Markdown';
import type { PostDetail } from '../types';

const PostEditorPage = () => {
  const { id } = useParams<{ id: string }>();
  const isEditing = !!id;

  const [title, setTitle] = useState('');
  const [tagsInput, setTagsInput] = useState('');
  const [content, setContent] = useState('');
  const [showPreview, setShowPreview] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(isEditing);
  const [saving, setSaving] = useState(false);
  const navigate = useNavigate();

  useEffect(() => {
    if (!isEditing) return;

    const fetchPost = async () => {
      try {
        const response = await api.get<PostDetail>(`/api/blogposts/${id}`);
        setTitle(response.data.title);
        setTagsInput(response.data.tags.join(', '));
        setContent(response.data.content);
      } catch {
        setError('Could not load the post for editing.');
      } finally {
        setLoading(false);
      }
    };
    fetchPost();
  }, [id, isEditing]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSaving(true);

    const tags = tagsInput
      .split(',')
      .map(tag => tag.trim())
      .filter(tag => tag.length > 0);

    try {
      if (isEditing) {
        await api.put(`/api/blogposts/${id}`, { title, content, tags });
        navigate(`/posts/${id}`);
      } else {
        const response = await api.post<PostDetail>('/api/blogposts', { title, content, tags });
        navigate(`/posts/${response.data.id}`);
      }
    } catch {
      setError('Could not save the post. Please try again.');
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return <p className="status-line">Loading…</p>;
  }

  return (
    <div className="editor-page">
      <h1>{isEditing ? 'Edit post' : 'New post'}</h1>

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
          Tags <span className="hint">(comma separated)</span>
          <input
            type="text"
            value={tagsInput}
            onChange={e => setTagsInput(e.target.value)}
            placeholder="dotnet, react, devops"
          />
        </label>

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
          {saving ? 'Saving…' : isEditing ? 'Save changes' : 'Publish'}
        </button>
      </form>
    </div>
  );
};

export default PostEditorPage;
