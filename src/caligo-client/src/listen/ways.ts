import type { RadioFormat } from '../radio';

export type WayKind = 'stream' | 'objects' | 'audio' | 'moq-lite' | 'hls' | 'dash' | 'whep' | 'external';

export interface Way {
  id: string;
  name: string;
  note: string;
  kind: WayKind;
  url: string;
  lab: boolean;
  format?: RadioFormat;
  events?: string;
  lowLatency?: boolean;
  webTransport?: boolean;
  command?: string;
}

export const DEFAULT_WAY = 'relay-opus';
export const FALLBACK_WAY = 'relay-mp3';

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
      name: 'MoQ',
      note: 'Closest to live',
      kind: 'moq-lite',
      url: `${origin}/live/moq`,
      events: RELAY_EVENTS,
      lab: false
    },
    {
      id: 'relay-objects',
      name: 'Low latency',
      note: 'Near live',
      kind: 'objects',
      url: `${origin}/live/moq`,
      lab: false
    },
    {
      id: 'relay-mp3',
      name: 'MP3',
      note: 'Works everywhere',
      kind: 'stream',
      format: 'mp3',
      url: `${origin}/live/`,
      lab: false
    },
    {
      id: 'relay-opus',
      name: 'Opus',
      note: 'Better sound, less data',
      kind: 'stream',
      format: 'opus',
      url: `${origin}/live/opus`,
      lab: false
    },
    {
      id: 'caligo-radio',
      name: 'Direct',
      note: 'Straight from the radio',
      kind: 'stream',
      format: 'direct',
      url: `${origin}/radio/`,
      lab: false
    },
    {
      id: 'webrtc',
      name: 'WebRTC',
      note: 'Real time',
      kind: 'whep',
      url: `http://${host}:8889/radio/whep`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'rtsp',
      name: 'RTSP',
      note: 'For media players',
      kind: 'external',
      url: `rtsp://${host}:8554/radio`,
      command: `vlc --rtsp-tcp rtsp://${host}:8554/radio`,
      lab: true
    },
    {
      id: 'rtmp',
      name: 'RTMP',
      note: 'For media players',
      kind: 'external',
      url: `rtmp://${host}:1935/radio-aac`,
      command: `vlc rtmp://${host}:1935/radio-aac`,
      lab: true
    },
    {
      id: 'srt',
      name: 'SRT',
      note: 'For media players',
      kind: 'external',
      url: `srt://${host}:8890?streamid=read:radio`,
      command: `vlc "srt://${host}:8890?streamid=read:radio"`,
      lab: true
    },
    {
      id: 'll-hls',
      name: 'LL-HLS',
      note: 'Streaming, low delay',
      kind: 'hls',
      lowLatency: true,
      url: `http://${host}:8888/radio/index.m3u8`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'relay-quic',
      name: 'QUIC',
      note: 'Closest to live',
      kind: 'moq-lite',
      webTransport: true,
      url: `http://${host}:8443/live/moq`,
      events: RELAY_EVENTS,
      lab: true
    },
    {
      id: 'icecast',
      name: 'Icecast',
      note: 'Classic internet radio',
      kind: 'audio',
      url: `http://${host}:8001/stream`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'dash',
      name: 'DASH',
      note: 'Streaming',
      kind: 'dash',
      url: `http://${host}:8090/dash/radio.mpd`,
      events: RADIO_EVENTS,
      lab: true
    },
    {
      id: 'hls',
      name: 'HLS',
      note: 'Streaming',
      kind: 'hls',
      url: `http://${host}:8090/hls/radio.m3u8`,
      events: RADIO_EVENTS,
      lab: true
    }
  ];
};
