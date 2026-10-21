import { useCallback, useEffect, useRef, useState } from 'react';
import Modal from '../components/Modal';
import { useAuth } from '../contexts/AuthContext';
import { XmppChat, roomName } from '../chat/xmpp';
import type { XmppStatus } from '../chat/xmpp';

interface Room {
  name: string;
}

type NewLine =
  | { kind: 'system'; text: string }
  | { kind: 'in' | 'out'; sender: string; text: string };

type Line = NewLine & { id: number };

const GUEST_NICK = `User_${Math.floor(Math.random() * 1000)}`;

const STATUS_TEXT: Record<XmppStatus, string> = {
  connecting: '● Connecting...',
  negotiating: '● Negotiating...',
  established: '● Signal Established',
  lost: '● Signal Lost',
  failed: 'Connection Failed'
};

const ChatRoomsPage = () => {
  const { user } = useAuth();
  const nick = user?.name || GUEST_NICK;

  const [rooms, setRooms] = useState<Room[] | null>(null);
  const [roomsFailed, setRoomsFailed] = useState(false);
  const [counts, setCounts] = useState<Record<string, number>>({});
  const [room, setRoom] = useState<string | null>(null);
  const [status, setStatus] = useState<XmppStatus | null>(null);
  const [lines, setLines] = useState<Line[]>([]);
  const [draft, setDraft] = useState('');
  const [creating, setCreating] = useState(false);
  const [newRoom, setNewRoom] = useState('');

  const chat = useRef<XmppChat | null>(null);
  const nextLine = useRef(1);
  const box = useRef<HTMLDivElement>(null);

  const add = useCallback((line: NewLine) => {
    const id = nextLine.current++;

    setLines(previous => [...previous, { ...line, id }]);
  }, []);

  const loadRooms = useCallback(() => {
    setRooms(null);
    setRoomsFailed(false);

    fetch('/cgi_bin/get_chat_rooms')
      .then(response => {
        if (!response.ok) throw new Error('Signal lost');

        return response.json() as Promise<Room[]>;
      })
      .then(found => setRooms(Array.isArray(found) ? found.filter(item => typeof item?.name === 'string') : []))
      .catch(() => setRoomsFailed(true));
  }, []);

  useEffect(() => {
    const client = new XmppChat(nick, {
      status: setStatus,
      message: (sender, text) => add({ kind: 'in', sender, text }),
      rooms: found => setCounts(previous => ({ ...previous, ...found })),
      ready: () => add({ kind: 'system', text: 'Connected to Caligo Network.' })
    });

    chat.current = client;
    client.connect();

    return () => {
      chat.current = null;
      client.disconnect();
    };
  }, [add, nick]);

  useEffect(() => {
    loadRooms();
  }, [loadRooms]);

  useEffect(() => {
    if (box.current) {
      box.current.scrollTop = box.current.scrollHeight;
    }
  }, [lines]);

  const join = (name: string) => {
    setRoom(name);
    setLines([]);
    add({ kind: 'system', text: `Joining frequency ${name}...` });
    chat.current?.join(name);
  };

  const leave = () => {
    setRoom(null);
    chat.current?.join(null);
    loadRooms();
  };

  const create = (event: React.FormEvent) => {
    event.preventDefault();

    const name = roomName(newRoom);

    if (!name) {
      return;
    }

    setCreating(false);
    setNewRoom('');
    join(name);
  };

  const send = () => {
    const text = draft.trim();

    if (!text || !chat.current?.send(text)) {
      return;
    }

    add({ kind: 'out', sender: 'Me', text });
    setDraft('');
  };

  const countText = (name: string) => {
    const count = counts[name];

    return count === undefined ? 'Waiting for signal...' : `${count} online`;
  };

  if (room) {
    const online = counts[room];

    return (
      <div className="chat-interface">
        <div className="chat-header-bar">
          <button type="button" className="chat-leave" onClick={leave} aria-label="Leave the frequency">←</button>
          <div>
            <div className="chat-room-name"># {room}</div>
            <div className="chat-room-stats">{online === undefined ? 'Connecting...' : `● ${online} Online`}</div>
            <div className={`chat-connection ${status ?? 'connecting'}`}>
              {status ? STATUS_TEXT[status] : '● Connected via XMPP'}
            </div>
          </div>
        </div>

        <div className="chat-messages-area" ref={box}>
          {lines.map(line =>
            line.kind === 'system' ? (
              <div key={line.id} className="chat-system">{line.text}</div>
            ) : (
              <div key={line.id} className={line.kind === 'out' ? 'message-bubble msg-out' : 'message-bubble msg-in'}>
                {line.kind === 'in' && <span className="msg-meta">{line.sender}</span>}
                {line.text}
              </div>
            )
          )}
        </div>

        <div className="chat-input-bar">
          <input
            type="text"
            className="chat-input-field"
            placeholder="Broadcast message..."
            autoComplete="off"
            maxLength={1000}
            value={draft}
            aria-label="Message"
            onChange={event => setDraft(event.target.value)}
            onKeyDown={event => {
              if (event.key === 'Enter') send();
            }}
          />
          <button type="button" className="chat-send-btn" onClick={send} aria-label="Send">➤</button>
        </div>
      </div>
    );
  }

  return (
    <div>
      <h1>Chat Rooms</h1>

      <div className="chat-lobby-header">
        <h2>Public Frequencies</h2>
        <button type="button" onClick={() => setCreating(true)}>+ Create Frequency</button>
      </div>

      {rooms === null && !roomsFailed && <div className="status-line">Loading frequencies...</div>}
      {roomsFailed && <div className="form-error">Failed to retrieve frequencies.</div>}
      {rooms?.length === 0 && <p className="status-line">No active frequencies. Create one above.</p>}

      {rooms && rooms.length > 0 && (
        <div className="chat-rooms-grid">
          {rooms.map(item => (
            <button key={item.name} type="button" className="chat-room-card" onClick={() => join(item.name)}>
              <span className="chat-room-title">#{item.name}</span>
              <span className={`chat-room-users${(counts[item.name] ?? 0) > 0 ? ' online' : ''}`}>{countText(item.name)}</span>
            </button>
          ))}
        </div>
      )}

      {creating && (
        <Modal title="New Frequency" onClose={() => setCreating(false)}>
          <p className="modal-note">Create a public channel.</p>
          <form onSubmit={create}>
            <label className="input-group">
              <span>Frequency Name</span>
              <input
                type="text"
                value={newRoom}
                maxLength={64}
                placeholder="e.g., Synthwave Lounge"
                autoFocus
                onChange={event => setNewRoom(event.target.value)}
              />
            </label>
            <div className="modal-actions">
              <button type="button" className="btn-secondary" onClick={() => setCreating(false)}>Cancel</button>
              <button type="submit" disabled={roomName(newRoom).length === 0}>Create</button>
            </div>
          </form>
        </Modal>
      )}
    </div>
  );
};

export default ChatRoomsPage;
