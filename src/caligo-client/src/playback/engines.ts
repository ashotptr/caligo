import type { ErrorData, LevelSwitchedData, ManifestParsedData } from 'hls.js';
import type { MediaPlayerErrorEvent, QualityChangeRenderedEvent, TextTracksAddedEvent } from 'dashjs';
import type { PlaybackSession, SubtitleTrackInfo } from './protocol';

export type Delivery = 'hls' | 'hls-native' | 'dash' | 'progressive';

export type DeliveryChoice = 'auto' | Delivery;

export interface Quality {
  index: number;
  height: number;
  bitrate: number;
}

export interface EngineHooks {
  onQualities: (qualities: Quality[]) => void;
  onQualitySwitch: (from: number, to: number) => void;
  onFatal: (status: number | null, detail: string) => void;
}

export interface Engine {
  delivery: Delivery;
  setQuality: (index: number) => void;
  setSubtitle: (track: SubtitleTrackInfo | null) => void;
  destroy: () => void;
}

export const DELIVERY_LABELS: Record<Delivery, string> = {
  hls: 'HLS',
  'hls-native': 'HLS, built in',
  dash: 'DASH',
  progressive: 'MP4'
};

const mediaSourceSupported = (): boolean =>
  typeof window !== 'undefined' && ('MediaSource' in window || 'ManagedMediaSource' in window);

const nativeHlsSupported = (video: HTMLVideoElement): boolean =>
  video.canPlayType('application/vnd.apple.mpegurl') !== '';

export const availableDeliveries = (session: PlaybackSession, video: HTMLVideoElement): Delivery[] => {
  const found: Delivery[] = [];

  if (session.playlist_url && mediaSourceSupported()) {
    found.push('hls');
  }

  if (session.playlist_url && nativeHlsSupported(video)) {
    found.push('hls-native');
  }

  if (session.manifest_url && mediaSourceSupported()) {
    found.push('dash');
  }

  if (session.progressive_url) {
    found.push('progressive');
  }

  return found;
};

const seekWhenReady = (video: HTMLVideoElement, startAt: number) => {
  if (startAt <= 0) {
    return;
  }

  video.addEventListener('loadedmetadata', () => {
    video.currentTime = startAt;
  }, { once: true });
};

const showTextTrack = (video: HTMLVideoElement, track: SubtitleTrackInfo | null, own?: TextTrack[]) => {
  const list = video.textTracks;

  for (let index = 0; index < list.length; index++) {
    const candidate = list[index];
    const matches = track !== null && (!own || own.includes(candidate)) &&
      ((track.language !== null && candidate.language === track.language) || candidate.label === track.label);

    candidate.mode = matches ? 'showing' : 'disabled';
  }
};

const mediaErrorText = (video: HTMLVideoElement): string => {
  const error = video.error;

  return error ? `media error ${error.code}${error.message ? `: ${error.message}` : ''}` : 'media error';
};

const attachHls = async (video: HTMLVideoElement, url: string, startAt: number, hooks: EngineHooks): Promise<Engine> => {
  const { default: Hls } = await import('hls.js');
  const hls = new Hls({
    startPosition: startAt > 0 ? startAt : -1,
    capLevelToPlayerSize: true,
    enableWebVTT: true,
    renderTextTracksNatively: true
  });

  let currentHeight = 0;

  hls.on(Hls.Events.MANIFEST_PARSED, (_event: string, data: ManifestParsedData) => {
    hooks.onQualities(data.levels.map((level, index) => ({ index, height: level.height, bitrate: level.bitrate })));
  });

  hls.on(Hls.Events.LEVEL_SWITCHED, (_event: string, data: LevelSwitchedData) => {
    const height = hls.levels[data.level]?.height ?? 0;

    if (currentHeight && height && height !== currentHeight) {
      hooks.onQualitySwitch(currentHeight, height);
    }

    currentHeight = height;
  });

  hls.on(Hls.Events.ERROR, (_event: string, data: ErrorData) => {
    if (data.fatal) {
      hooks.onFatal(data.response?.code ?? null, `${data.type}: ${data.details}`);
    }
  });

  hls.loadSource(url);
  hls.attachMedia(video);

  return {
    delivery: 'hls',
    setQuality: index => {
      hls.currentLevel = index;
    },
    setSubtitle: track => {
      if (!track) {
        hls.subtitleTrack = -1;

        return;
      }

      hls.subtitleDisplay = true;
      hls.subtitleTrack = hls.subtitleTracks.findIndex(candidate =>
        (track.language !== null && candidate.lang === track.language) || candidate.name === track.label);
    },
    destroy: () => hls.destroy()
  };
};

const attachNativeHls = (video: HTMLVideoElement, url: string, startAt: number, hooks: EngineHooks): Engine => {
  const onError = () => hooks.onFatal(null, mediaErrorText(video));
  let chosen: SubtitleTrackInfo | null = null;
  const apply = () => showTextTrack(video, chosen);

  video.addEventListener('error', onError);
  video.addEventListener('loadedmetadata', apply);
  video.textTracks.addEventListener('addtrack', apply);
  seekWhenReady(video, startAt);
  video.src = url;
  hooks.onQualities([]);

  return {
    delivery: 'hls-native',
    setQuality: () => undefined,
    setSubtitle: track => {
      chosen = track;
      apply();
    },
    destroy: () => {
      video.removeEventListener('error', onError);
      video.removeEventListener('loadedmetadata', apply);
      video.textTracks.removeEventListener('addtrack', apply);
      video.removeAttribute('src');
      video.load();
    }
  };
};

const attachDash = async (video: HTMLVideoElement, url: string, startAt: number, hooks: EngineHooks): Promise<Engine> => {
  const dashjs = await import('dashjs');
  const player = dashjs.MediaPlayer().create();
  const events = dashjs.MediaPlayer.events;
  let textTracks: TextTracksAddedEvent['tracks'] = [];

  player.updateSettings({
    streaming: {
      abr: { autoSwitchBitrate: { video: true } },
      text: { defaultEnabled: false }
    }
  });

  player.on(events.STREAM_INITIALIZED, () => {
    hooks.onQualities(player.getRepresentationsByType('video')
      .map((representation, index) => ({ index, height: representation.height, bitrate: representation.bandwidth })));
  });

  player.on(events.QUALITY_CHANGE_RENDERED, (event: QualityChangeRenderedEvent) => {
    const from = event.oldRepresentation?.height ?? 0;
    const to = event.newRepresentation?.height ?? 0;

    if (event.mediaType === 'video' && from && to && from !== to) {
      hooks.onQualitySwitch(from, to);
    }
  });

  player.on(events.TEXT_TRACKS_ADDED, (event: TextTracksAddedEvent) => {
    textTracks = event.tracks;
  });

  player.on(events.ERROR, (event: MediaPlayerErrorEvent) => {
    hooks.onFatal(null, `dash.js error ${event.error?.code ?? ''}: ${event.error?.message ?? ''}`.trim());
  });

  player.initialize(video, url, false, startAt > 0 ? startAt : 0);

  return {
    delivery: 'dash',
    setQuality: index => {
      player.updateSettings({ streaming: { abr: { autoSwitchBitrate: { video: index < 0 } } } });

      if (index >= 0) {
        player.setRepresentationForTypeByIndex('video', index, true);
      }
    },
    setSubtitle: track => {
      if (!track) {
        player.setTextTrack(-1);

        return;
      }

      const index = textTracks.findIndex(candidate =>
        (track.language !== null && candidate.lang === track.language) ||
        candidate.labels.some(label => label.text === track.label));

      player.enableText(index >= 0);
      player.setTextTrack(index);
    },
    destroy: () => player.reset()
  };
};

const attachProgressive = (video: HTMLVideoElement, session: PlaybackSession, url: string, startAt: number, hooks: EngineHooks): Engine => {
  const onError = () => hooks.onFatal(null, mediaErrorText(video));
  const added: HTMLTrackElement[] = [];
  let chosen: SubtitleTrackInfo | null = null;
  const apply = () => showTextTrack(video, chosen, added.map(element => element.track));

  for (const subtitle of session.subtitles) {
    const element = document.createElement('track');

    element.kind = 'subtitles';
    element.label = subtitle.label;
    element.src = subtitle.url;

    if (subtitle.language) {
      element.srclang = subtitle.language;
    }

    element.addEventListener('load', apply);
    video.appendChild(element);
    added.push(element);
  }

  video.addEventListener('error', onError);
  video.addEventListener('loadedmetadata', apply);
  seekWhenReady(video, startAt);
  video.src = url;
  hooks.onQualities([]);

  return {
    delivery: 'progressive',
    setQuality: () => undefined,
    setSubtitle: track => {
      chosen = track;
      apply();
    },
    destroy: () => {
      video.removeEventListener('error', onError);
      video.removeEventListener('loadedmetadata', apply);

      for (const element of added) {
        element.removeEventListener('load', apply);
        element.remove();
      }

      video.removeAttribute('src');
      video.load();
    }
  };
};

export const attachEngine = async (
  delivery: Delivery,
  video: HTMLVideoElement,
  session: PlaybackSession,
  startAt: number,
  hooks: EngineHooks
): Promise<Engine> => {
  switch (delivery) {
    case 'hls':
      return attachHls(video, session.playlist_url!, startAt, hooks);
    case 'hls-native':
      return attachNativeHls(video, session.playlist_url!, startAt, hooks);
    case 'dash':
      return attachDash(video, session.manifest_url!, startAt, hooks);
    default:
      return attachProgressive(video, session, session.progressive_url!, startAt, hooks);
  }
};
