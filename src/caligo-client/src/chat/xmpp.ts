export type XmppStatus = 'connecting' | 'negotiating' | 'established' | 'lost' | 'failed';

export interface XmppEvents {
  status: (status: XmppStatus) => void;
  message: (sender: string, text: string) => void;
  rooms: (counts: Record<string, number>) => void;
  ready: () => void;
}

type Phase = 'DISCONNECTED' | 'WAIT_SASL' | 'WAIT_AUTH_RESULT' | 'WAIT_BIND_OFFER' | 'WAIT_BIND_RESULT' | 'READY';

const DOMAIN = 'radio';
const CONFERENCE = 'conference.radio';
const PASSWORD = 'password';
const LOBBY = 'lobby';
const RECONNECT_MS = 3000;
const STREAM = `<stream:stream to='${DOMAIN}' xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams' version='1.0'>`;

const NOT_IN_JID = /[\s@/"&':<>]/g;

const escapeXml = (text: string): string =>
  text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');

export const roomName = (name: string): string => name.trim().replace(/\s+/g, '_').replace(NOT_IN_JID, '');

export const nickName = (name: string): string => name.replace(/[\s/"&'<>]/g, '_');

const plain = (nick: string): string =>
  btoa(String.fromCharCode(...new TextEncoder().encode(`\u0000${nick}\u0000${PASSWORD}`)));

const parse = (frame: string): Document | null => {
  const parser = new DOMParser();
  const direct = parser.parseFromString(frame, 'text/xml');

  if (!direct.getElementsByTagName('parsererror').length) {
    return direct;
  }

  const wrapped = parser.parseFromString(`<frames>${frame}</frames>`, 'text/xml');

  return wrapped.getElementsByTagName('parsererror').length ? null : wrapped;
};

export class XmppChat {
  private socket: WebSocket | null = null;
  private phase: Phase = 'DISCONNECTED';
  private room: string | null = null;
  private wanted = false;
  private timer: number | undefined;
  private readonly nick: string;
  private readonly events: XmppEvents;

  constructor(nick: string, events: XmppEvents) {
    this.nick = nickName(nick);
    this.events = events;
  }

  get name(): string {
    return this.nick;
  }

  connect(): void {
    this.wanted = true;

    if (this.socket && (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING)) {
      return;
    }

    this.events.status('connecting');

    const protocol = window.location.protocol === 'https:' ? 'wss' : 'ws';

    try {
      const socket = new WebSocket(`${protocol}://${window.location.host}/chat/`, 'xmpp');

      socket.onopen = () => {
        if (this.socket !== socket) return;
        this.events.status('negotiating');
        socket.send(STREAM);
        this.phase = 'WAIT_SASL';
      };

      socket.onmessage = event => {
        if (this.socket === socket) this.received(String(event.data));
      };

      socket.onclose = () => {
        if (this.socket !== socket) return;
        this.socket = null;
        this.phase = 'DISCONNECTED';
        this.events.status('lost');

        if (this.wanted) {
          this.timer = window.setTimeout(() => this.connect(), RECONNECT_MS);
        }
      };

      this.socket = socket;
    } catch {
      this.events.status('failed');
    }
  }

  disconnect(): void {
    this.wanted = false;
    window.clearTimeout(this.timer);

    const socket = this.socket;

    this.socket = null;
    this.phase = 'DISCONNECTED';
    socket?.close();
  }

  join(room: string | null): void {
    this.room = room;

    if (this.phase === 'READY') {
      this.presence();
    } else {
      this.connect();
    }
  }

  send(text: string): boolean {
    if (!this.socket || this.phase !== 'READY' || !this.room) {
      return false;
    }

    this.socket.send(
      `<message type='groupchat' to='${escapeXml(this.room)}@${CONFERENCE}' from='${escapeXml(this.nick)}'><body>${escapeXml(text)}</body></message>`
    );

    return true;
  }

  private presence(): void {
    this.socket?.send(`<presence to='${escapeXml(this.room ?? LOBBY)}@${CONFERENCE}/${escapeXml(this.nick)}'/>`);
  }

  private received(frame: string): void {
    const socket = this.socket;

    if (!socket) {
      return;
    }

    if (this.phase === 'WAIT_SASL' && frame.includes('mechanisms')) {
      socket.send(`<auth xmlns='urn:ietf:params:xml:ns:xmpp-sasl' mechanism='PLAIN'>${plain(this.nick)}</auth>`);
      this.phase = 'WAIT_AUTH_RESULT';

      return;
    }

    if (this.phase === 'WAIT_AUTH_RESULT' && frame.includes('success')) {
      socket.send(STREAM);
      this.phase = 'WAIT_BIND_OFFER';

      return;
    }

    if (this.phase === 'WAIT_BIND_OFFER' && frame.includes('bind')) {
      socket.send("<iq type='set' id='bind_1'><bind xmlns='urn:ietf:params:xml:ns:xmpp-bind'><resource>web-client</resource></bind></iq>");
      this.phase = 'WAIT_BIND_RESULT';

      return;
    }

    if (this.phase === 'WAIT_BIND_RESULT' && frame.includes('jid')) {
      this.phase = 'READY';
      socket.send("<iq type='set' id='sess_1'><session xmlns='urn:ietf:params:xml:ns:xmpp-session'/></iq>");
      this.presence();
      this.events.status('established');
      this.events.ready();

      return;
    }

    if (this.phase !== 'READY') {
      return;
    }

    const document = parse(frame);

    if (!document) {
      return;
    }

    const rooms = document.getElementsByTagName('room');

    if (frame.includes('<roomlist>') && rooms.length > 0) {
      const counts: Record<string, number> = {};

      for (const room of Array.from(rooms)) {
        const name = room.getAttribute('name');

        if (name) {
          counts[name] = Number(room.getAttribute('count')) || 0;
        }
      }

      this.events.rooms(counts);

      return;
    }

    for (const message of Array.from(document.getElementsByTagName('message'))) {
      const body = message.getElementsByTagName('body')[0];
      const from = message.getAttribute('from') ?? '';

      if (!body) {
        continue;
      }

      const sender = from.includes('/') ? from.split('/')[1] || from.split('/')[0] : from;

      if (sender !== this.nick) {
        this.events.message(sender, body.textContent ?? '');
      }
    }
  }
}
