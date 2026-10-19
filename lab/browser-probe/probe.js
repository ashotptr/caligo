'use strict';

const puppeteer = require('puppeteer-core');
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const USAGE = `usage: node probe.js --base <url> --stream <path> [--stream <path> ...]
  --base <url>            origin that serves the streams, for example http://localhost:8080
  --stream <path>         a stream to measure, for example /live/ or /radio/ (repeat for several)
  --seconds <n>           how long to listen to each stream (default 30)
  --restart <container>   restart this Docker container a third of the way in and watch the recovery
  --map "<host> <ip>"     resolve a host name to an address inside the browser (repeat for several)
  --browser <path>        browser executable (default: the installed Chrome)
  --real-audio            play through the sound card instead of the browser's silent output
  --out <file>            write the full record as JSON`;

const parse = argv => {
  const options = { streams: [], maps: [], seconds: 30 };

  for (let index = 0; index < argv.length; index++) {
    const name = argv[index];
    const value = argv[index + 1];

    if (name === '--help' || name === '-h') {
      options.help = true;
    } else if (name === '--real-audio') {
      options.realAudio = true;
    } else if (name === '--stream') {
      options.streams.push(value);
      index++;
    } else if (name === '--map') {
      options.maps.push(value);
      index++;
    } else if (name === '--seconds') {
      options.seconds = Number(value);
      index++;
    } else if (['--base', '--restart', '--browser', '--out'].includes(name)) {
      options[name.slice(2)] = value;
      index++;
    } else {
      throw new Error(`unknown option ${name}`);
    }
  }

  return options;
};

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

const launch = options => {
  const args = ['--autoplay-policy=no-user-gesture-required'];

  if (!options.realAudio) {
    args.push('--disable-audio-output');
  }

  if (options.maps.length > 0) {
    args.push('--host-resolver-rules=' + options.maps.map(map => `MAP ${map}`).join(', '));
  }

  return puppeteer.launch(options.browser
    ? { executablePath: options.browser, headless: true, args }
    : { channel: 'chrome', headless: true, args });
};

const measure = async (browser, options, stream) => {
  const page = await browser.newPage();
  const record = { stream, requests: [], events: [], samples: [] };
  const started = Date.now();
  const at = () => Date.now() - started;

  page.on('request', request => {
    const url = new URL(request.url());

    if (url.pathname === stream) {
      record.requests.push({ at: at(), range: request.headers().range || null, status: null });
    }
  });

  page.on('response', response => {
    const url = new URL(response.url());
    const open = record.requests.find(request => request.status === null);

    if (url.pathname === stream && open) {
      open.status = response.status();
      open.contentRange = response.headers()['content-range'] || null;
    }
  });

  page.on('requestfailed', request => {
    const url = new URL(request.url());

    if (url.pathname === stream) {
      record.events.push({ at: at(), name: 'request failed: ' + (request.failure() ? request.failure().errorText : 'unknown') });
    }
  });

  await page.exposeFunction('probeEvent', name => record.events.push({ at: at(), name }));
  await page.goto(options.base + '/', { waitUntil: 'domcontentloaded' });

  const clicked = at();

  await page.evaluate(target => {
    const audio = new Audio();

    window.probeAudio = audio;

    for (const name of ['loadedmetadata', 'canplay', 'playing', 'waiting', 'stalled', 'error', 'ended']) {
      audio.addEventListener(name, () => window.probeEvent(name));
    }

    audio.src = target + '?t=' + Date.now();
    audio.play().catch(error => window.probeEvent('play rejected: ' + error));
  }, stream);

  const restartAt = options.restart ? Math.round(options.seconds * 1000 / 3) : null;
  let restarted = false;

  while (at() - clicked < options.seconds * 1000) {
    await sleep(250);

    const sample = await page.evaluate(() => {
      const audio = window.probeAudio;
      const buffered = audio.buffered.length ? audio.buffered.end(audio.buffered.length - 1) : 0;

      return { time: audio.currentTime, buffered, ready: audio.readyState };
    });

    record.samples.push({ at: at() - clicked, ...sample });

    if (restartAt !== null && !restarted && at() - clicked >= restartAt) {
      restarted = true;

      record.events.push({ at: at(), name: `restarting ${options.restart}` });

      execFileSync('docker', ['restart', '--time', '2', options.restart], { stdio: 'ignore' });

      record.events.push({ at: at(), name: `restarted ${options.restart}` });
    }
  }

  await page.close();

  return summarize(record, clicked, restartAt);
};

const summarize = (record, clicked, restartAt) => {
  const first = record.samples.find(sample => sample.time > 0.2);
  const event = name => {
    const found = record.events.find(entry => entry.name === name);

    return found ? found.at - clicked : null;
  };

  let silence = 0;
  let longest = 0;
  let stalls = 0;
  let previous = null;

  for (const sample of record.samples) {
    if (!first || sample.at < first.at) {
      continue;
    }

    if (previous !== null && sample.time <= previous.time + 0.05) {
      if (silence === 0) {
        stalls++;
      }

      silence += sample.at - previous.at;
      longest = Math.max(longest, silence);
    } else {
      silence = 0;
    }

    previous = sample;
  }

  const last = record.samples[record.samples.length - 1];
  const ahead = seconds => {
    const sample = record.samples.find(entry => entry.at >= seconds * 1000);

    return sample ? Number((sample.buffered - sample.time).toFixed(2)) : null;
  };

  return {
    stream: record.stream,
    firstResponseStatus: record.requests[0] ? record.requests[0].status : null,
    metadataMs: event('loadedmetadata'),
    canPlayMs: event('canplay'),
    playingMs: event('playing'),
    startMs: first ? first.at : null,
    bufferedAheadAt5s: ahead(5),
    bufferedAheadAt10s: ahead(10),
    stallsAfterStart: stalls,
    longestSilenceMs: longest,
    playedSeconds: last ? Number(last.time.toFixed(1)) : 0,
    listenedSeconds: last ? Number((last.at / 1000).toFixed(1)) : 0,
    requests: record.requests.map(request => ({
      atMs: request.at - clicked, range: request.range, status: request.status, contentRange: request.contentRange || null
    })),
    restartAtMs: restartAt,
    events: record.events.map(entry => ({ atMs: entry.at - clicked, name: entry.name }))
  };
};

const print = result => {
  const show = value => (value === null ? 'never' : value);

  console.log(`\n${result.stream}`);
  console.log(`  first answer        ${show(result.firstResponseStatus)}`);
  console.log(`  metadata after      ${show(result.metadataMs)} ms`);
  console.log(`  playing after       ${show(result.playingMs)} ms`);
  console.log(`  clock moving after  ${show(result.startMs)} ms`);
  console.log(`  buffered ahead      ${show(result.bufferedAheadAt5s)} s at 5 s, ${show(result.bufferedAheadAt10s)} s at 10 s`);
  console.log(`  stalls after start  ${result.stallsAfterStart}, longest silence ${result.longestSilenceMs} ms`);
  console.log(`  played              ${result.playedSeconds} s of ${result.listenedSeconds} s listened`);

  for (const request of result.requests) {
    console.log(`  request at ${String(request.atMs).padStart(6)} ms  Range ${request.range || 'none'}  ->  ${request.status}${request.contentRange ? '  ' + request.contentRange : ''}`);
  }
};

const main = async () => {
  const options = parse(process.argv.slice(2));

  if (options.help || !options.base || options.streams.length === 0) {
    console.log(USAGE);
    process.exit(options.help ? 0 : 2);
  }

  const browser = await launch(options);
  const results = [];

  try {
    for (const stream of options.streams) {
      const result = await measure(browser, options, stream);

      results.push(result);
      print(result);
    }
  } finally {
    await browser.close();
  }

  if (options.out) {
    fs.mkdirSync(path.dirname(path.resolve(options.out)), { recursive: true });
    fs.writeFileSync(options.out, JSON.stringify({ base: options.base, at: new Date().toISOString(), results }, null, 2));

    console.log(`\nwritten to ${options.out}`);
  }
};

main().catch(error => {
  console.error(error.message || error);
  process.exit(1);
});
