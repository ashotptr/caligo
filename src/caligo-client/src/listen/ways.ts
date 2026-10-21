import type { RadioFormat } from '../radio';

export type WayKind = 'stream' | 'objects' | 'audio' | 'moq-lite' | 'hls' | 'dash' | 'whep' | 'external';

export interface Way {
  id: string;
  name: string;
  protocol: string;
  server: string;
  kind: WayKind;
  url: string;
  lab: boolean;
  format?: RadioFormat;
  events?: string;
  lowLatency?: boolean;
  webTransport?: boolean;
  command?: string;
}

export const DEFAULT_WAY = 'relay-mp3';

const LAB_HOSTS = ['localhost', '127.0.0.1'];

const RELAY_EVENTS = '/live/events';
const RADIO_EVENTS = '/radio/events';

export const labHere = (): boolean => LAB_HOSTS.includes(window.location.hostname);

export const allWays = (): Way[] => {
  const origin = window.location.origin;
  const host = window.location.hostname;

  return [
    {
      id: 'relay-moq-lite',
      name: 'moq-lite over WebSocket',
      protocol: 'moq-lite-06 over QMux',
      server: 'caligo-relay',
      kind: 'moq-lite',
      url: `${origin}/live/moq`,
      events: RELAY_EVENTS,
      lab: false
    },
    {
      id: 'relay-objects',
      name: 'Relay objects, low latency',
      protocol: 'Opus objects over WebSocket, back to Relay MP3 when the relay sends nothing',
      server: 'caligo-relay',
      kind: 'objects',
      url: `${origin}/live/moq`,
      lab: false
    },
    {
      id: 'relay-mp3',
      name: 'Relay MP3',
      protocol: 'MP3 over HTTP, straight from the radio when the relay is down',
      server: 'caligo-relay',
      kind: 'stream',
      format: 'mp3',
      url: `${origin}/live/`,
      lab: false
    },
    {
      id: 'relay-opus',
      name: 'Relay Opus',
      protocol: 'Ogg Opus over HTTP, MP3 when Opus is off the air',
      server: 'caligo-relay',
      kind: 'stream',
      format: 'opus',
      url: `${origin}/live/opus`,
      lab: false
    },
    {
      id: 'caligo-radio',
      name: 'Caligo\'s radio, direct',
      protocol: 'MP3 over HTTP, no clock in the stream, so the lab measured no delay',
      server: 'radio_server',
      kind: 'stream',
      format: 'direct',
      url: `${origin}/radio/`,
      lab: false
    },
    {
      id: 'webrtc',
      name: 'WebRTC',
      protocol: 'Opus over WebRTC, WHEP',
      server: 'MediaMTX',
      kind: 'whep',
      url: `http://${host}:8889/radio/whep`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'rtsp',
      name: 'RTSP',
      protocol: 'RTSP over TCP, Opus',
      server: 'MediaMTX',
      kind: 'external',
      url: `rtsp://${host}:8554/radio`,
      command: `vlc --rtsp-tcp rtsp://${host}:8554/radio`,
      lab: true
    },
    {
      id: 'rtmp',
      name: 'RTMP',
      protocol: 'RTMP, AAC',
      server: 'MediaMTX',
      kind: 'external',
      url: `rtmp://${host}:1935/radio-aac`,
      command: `vlc rtmp://${host}:1935/radio-aac`,
      lab: true
    },
    {
      id: 'srt',
      name: 'SRT',
      protocol: 'SRT, Opus in MPEG-TS',
      server: 'MediaMTX',
      kind: 'external',
      url: `srt://${host}:8890?streamid=read:radio`,
      command: `vlc "srt://${host}:8890?streamid=read:radio"`,
      lab: true
    },
    {
      id: 'll-hls',
      name: 'LL-HLS',
      protocol: 'low-latency HLS, Opus in fMP4',
      server: 'MediaMTX',
      kind: 'hls',
      lowLatency: true,
      url: `http://${host}:8888/radio/index.m3u8`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'relay-quic',
      name: 'moq-lite over QUIC',
      protocol: 'moq-lite-06 over WebTransport',
      server: 'caligo-relay-quic',
      kind: 'moq-lite',
      webTransport: true,
      url: `http://${host}:8443/live/moq`,
      events: RELAY_EVENTS,
      lab: true
    },
    {
      id: 'icecast',
      name: 'Icecast',
      protocol: 'MP3 over HTTP',
      server: 'Icecast 2',
      kind: 'audio',
      url: `http://${host}:8001/stream`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'dash',
      name: 'DASH',
      protocol: 'live DASH, AAC',
      server: 'ffmpeg and nginx',
      kind: 'dash',
      url: `http://${host}:8090/dash/radio.mpd`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'hls',
      name: 'HLS',
      protocol: 'HLS, AAC in MPEG-TS',
      server: 'ffmpeg and nginx',
      kind: 'hls',
      url: `http://${host}:8090/hls/radio.m3u8`,
      events: RADIO_EVENTS,
      lab: true
    }
  ];
};
