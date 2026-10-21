export interface DownloadTrack {
  title: string;
  audioUri: string;
}

const UNSAFE = /[/\\?%*:|"<>]/g;

const save = (href: string, filename: string): void => {
  const link = document.createElement('a');

  link.href = href;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
};

export const downloadTrack = (audioUri: string, title: string): void => save(audioUri, `${title}.mp3`);

export const downloadAll = async (name: string, tracks: DownloadTrack[], toast: (message: string) => void): Promise<void> => {
  toast('Compressing audio data... Please wait.');

  const { default: JSZip } = await import('jszip');
  const zip = new JSZip();
  const folder = zip.folder(name) ?? zip;

  await Promise.all(tracks.map(async (track, index) => {
    try {
      const response = await fetch(track.audioUri);

      if (!response.ok) {
        throw new Error(`Failed to load ${track.audioUri}`);
      }

      const number = String(index + 1).padStart(2, '0');

      folder.file(`${number} - ${track.title.replace(UNSAFE, '-')}.mp3`, await response.blob());
    } catch (error) {
      console.error('Error zipping track:', track.title, error);
    }
  }));

  toast('Finalizing archive...');

  const archive = await zip.generateAsync({ type: 'blob' });
  const href = URL.createObjectURL(archive);

  save(href, `${name}.zip`);
  URL.revokeObjectURL(href);

  toast('Download started.');
};
