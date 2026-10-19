const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const decoder = new TextDecoder();

const collect = async (track, until, describe) => {
  const groups = [];

  while (Date.now() < until) {
    const group = await Promise.race([track.recvGroup(), sleep(until - Date.now()).then(() => null)]);

    if (!group) {
      break;
    }

    const frames = [];

    for (;;) {
      const frame = await Promise.race([group.readFrame(), sleep(Math.max(until - Date.now(), 0) + 2000).then(() => null)]);

      if (!frame) {
        break;
      }

      frames.push(describe(frame));
    }

    groups.push({ sequence: group.sequence, frames });
  }

  return groups;
};

const audioFrame = frame => {
  const payload = frame.payload;

  return {
    size: payload.byteLength,
    toc: payload[0],
    counter: payload.byteLength >= 5 ? new DataView(payload.buffer, payload.byteOffset + 1, 4).getUint32(0) : -1,
    timestamp: frame.timestamp.value
  };
};

const textFrame = frame => ({ text: decoder.decode(frame.payload), timestamp: frame.timestamp.value });

export const moqLiteReport = async (Moq, { url, seconds, options = {} }) => {
  const report = { url: url.toString() };
  const origin = new Moq.Origin.Producer();
  const started = Date.now();
  const connection = await Moq.Connection.connect({ url, consume: origin, ...options });

  report.version = connection.version;
  report.connectMs = Date.now() - started;

  const announced = origin.announced();
  const seen = [];
  const watch = (async () => {
    for await (const announcement of announced) {
      seen.push({ kind: announcement.kind, path: String(announcement.prefix) });
    }
  })();

  const request = origin.request(Moq.Path.from('caligo/radio/main'));
  const deadline = Date.now() + 10000;
  let active = request.active.peek();

  while (!active && Date.now() < deadline) {
    await Promise.race([request.active.changed(), sleep(250)]);
    active = request.active.peek();
  }

  report.announced = seen;

  if (!active) {
    report.error = 'the broadcast never became active';

    return report;
  }

  const audio = active.track('audio/hi').subscribe({ priority: 0 });
  const meta = active.track('meta').subscribe({ priority: 1 });
  const catalog = active.track('catalog').subscribe({ priority: 2 });
  const info = await audio.info();

  report.audioInfo = { timescale: Number(info.timescale), priority: info.priority, maxAge: Number(info.maxAge) };

  const until = Date.now() + seconds * 1000;
  const [audioGroups, metaGroups, catalogGroups] = await Promise.all([
    collect(audio, until, audioFrame),
    collect(meta, until, textFrame),
    collect(catalog, until, textFrame)
  ]);

  const counters = audioGroups.flatMap(group => group.frames.map(frame => frame.counter));
  const timestamps = audioGroups.flatMap(group => group.frames.map(frame => frame.timestamp));
  const sequences = audioGroups.map(group => group.sequence);

  report.audio = {
    groups: audioGroups.length,
    frames: counters.length,
    sizes: [...new Set(audioGroups.map(group => group.frames.length))],
    firstCounter: counters[0],
    lastCounter: counters[counters.length - 1],
    countersInOrder: counters.every((value, index) => index === 0 || value === counters[index - 1] + 1),
    timestampsStep960: timestamps.every((value, index) => index === 0 || value - timestamps[index - 1] === 960),
    groupsInOrder: sequences.every((value, index) => index === 0 || value === sequences[index - 1] + 1),
    tocs: [...new Set(audioGroups.flatMap(group => group.frames.map(frame => frame.toc)))]
  };
  report.meta = metaGroups.flatMap(group => group.frames.map(frame => frame.text));
  report.catalog = catalogGroups.flatMap(group => group.frames.map(frame => frame.text));

  audio.close();
  meta.close();
  catalog.close();
  request.close();
  announced.close();
  await Promise.race([watch, sleep(500)]);
  await connection.close();
  origin.close();

  return report;
};
