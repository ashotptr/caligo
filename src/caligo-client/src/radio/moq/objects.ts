export const MOQ_PROTOCOL = 'caligo-objects-0';

export const TRACK_CATALOG = 0;
export const TRACK_AUDIO = 1;
export const TRACK_META = 2;

export interface MoqObject {
  track: number;
  group: number;
  index: number;
  payload: Uint8Array;
}

export interface CatalogTrack {
  id: number;
  name: string;
  channels?: number;
  sample_rate?: number;
}

export interface Catalog {
  on_air: boolean;
  tracks: CatalogTrack[];
}

export interface MetaObject {
  at: number;
  artist: string;
  album: string;
  title: string;
  is_request: boolean;
}

const readVarint = (data: Uint8Array, position: number): [number, number] => {
  if (position >= data.length) {
    return [0, data.length + 1];
  }

  const size = 1 << (data[position] >> 6);

  if (position + size > data.length) {
    return [0, data.length + 1];
  }

  let value = data[position] & 0x3f;

  for (let index = 1; index < size; index++) {
    value = value * 256 + data[position + index];
  }

  return [value, position + size];
};

export const parseObject = (buffer: ArrayBuffer): MoqObject | null => {
  const data = new Uint8Array(buffer);

  if (data.length < 4 || data[0] !== 1) {
    return null;
  }

  const [track, afterTrack] = readVarint(data, 1);
  const [group, afterGroup] = readVarint(data, afterTrack);
  const [index, afterIndex] = readVarint(data, afterGroup);

  return afterIndex > data.length ? null : { track, group, index, payload: data.subarray(afterIndex) };
};

export const PROBE = 2;

export const probeMessage = (sent: number): ArrayBuffer => {
  const buffer = new ArrayBuffer(9);
  const view = new DataView(buffer);

  view.setUint8(0, PROBE);
  view.setFloat64(1, sent);

  return buffer;
};

export const parseProbe = (buffer: ArrayBuffer): { sent: number; edge: number } | null => {
  const data = new Uint8Array(buffer);

  if (data.length < 10 || data[0] !== PROBE) {
    return null;
  }

  const [edge, end] = readVarint(data, 9);

  return end > data.length ? null : { sent: new DataView(buffer).getFloat64(1), edge };
};

export const parseAudio =(payload: Uint8Array): { timestamp: number; packet: Uint8Array } | null => {
  if (payload.length < 3 || payload[0] !== 0) {
    return null;
  }

  const [timestamp, start] = readVarint(payload, 1);

  return start < payload.length ? { timestamp, packet: payload.subarray(start) } : null;
};

const decoder = new TextDecoder();

export const parseJson = <T>(payload: Uint8Array): T | null => {
  try {
    return JSON.parse(decoder.decode(payload)) as T;
  } catch {
    return null;
  }
};
