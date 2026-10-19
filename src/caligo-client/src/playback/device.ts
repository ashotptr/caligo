const DEVICE_KEY = 'caligo.device';
const DEVICE_PATTERN = /^[A-Za-z0-9_-]{8,64}$/;

let memoryDevice: string | null = null;
let eventCounter = 0;

const randomBytes = (length: number): Uint8Array => {
  const bytes = new Uint8Array(length);

  crypto.getRandomValues(bytes);

  return bytes;
};

const hex = (bytes: Uint8Array): string => Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('');

export const uuid = (): string => {
  const bytes = randomBytes(16);

  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;

  const text = hex(bytes);

  return `${text.slice(0, 8)}-${text.slice(8, 12)}-${text.slice(12, 16)}-${text.slice(16, 20)}-${text.slice(20)}`;
};

export const deviceId = (): string => {
  try {
    const stored = localStorage.getItem(DEVICE_KEY);

    if (stored && DEVICE_PATTERN.test(stored)) {
      return stored;
    }

    const fresh = `web-${hex(randomBytes(12))}`;

    localStorage.setItem(DEVICE_KEY, fresh);

    return fresh;
  } catch {
    memoryDevice ??= `web-${hex(randomBytes(12))}`;

    return memoryDevice;
  }
};

export const nextEventSeq = (): number => {
  eventCounter = (eventCounter + 1) % 1000;

  return Date.now() * 1000 + eventCounter;
};
