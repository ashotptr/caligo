'use strict';

const puppeteer = require('puppeteer-core');
const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const EXPERIMENTS = {
  limits: { port: 18700, takesBrowserNames: true },
  endings: { port: 18720, takesBrowserNames: false },
  resume: { port: 18740, takesBrowserNames: false }
};

const USAGE = `usage: node experiments/run.js <limits|endings|resume> [--browser <path>] [--out <file>]
  limits    how an audio element treats live streams, very large lengths and ranges
  endings   what it does when a stream ends cleanly, abruptly or with a wrong length
  resume    how it retries after a drop, answered with 200 or with 206`;

const main = async () => {
  const [name, ...rest] = process.argv.slice(2);
  const experiment = EXPERIMENTS[name];

  if (!experiment) {
    console.log(USAGE);
    process.exit(2);
  }

  const option = flag => (rest.includes(flag) ? rest[rest.indexOf(flag) + 1] : undefined);
  const out = path.resolve(option('--out') || path.join(os.tmpdir(), `browser-probe-${name}-${Date.now()}.json`));
  const serverArguments = [path.join(__dirname, `${name}.js`), out];

  if (experiment.takesBrowserNames) {
    serverArguments.push('chrome');
  }

  const server = spawn(process.execPath, serverArguments, { stdio: ['ignore', 'pipe', 'inherit'] });

  await new Promise((resolve, reject) => {
    server.stdout.once('data', resolve);
    server.once('exit', code => reject(new Error(`the experiment server stopped early with code ${code}`)));
  });

  const finished = new Promise(resolve => server.once('exit', resolve));
  const executable = option('--browser');
  const args = ['--autoplay-policy=no-user-gesture-required'];
  const browser = await puppeteer.launch(executable
    ? { executablePath: executable, headless: true, args }
    : { channel: 'chrome', headless: true, args });

  try {
    const page = await browser.newPage();

    await page.goto(`http://127.0.0.1:${experiment.port}/t.html?b=chrome`);
    await finished;
  } finally {
    await browser.close();
  }

  if (!fs.existsSync(out)) {
    throw new Error('the experiment wrote no record');
  }

  spawnSync(process.execPath, [path.join(__dirname, `summarize-${name}.js`), out], { stdio: 'inherit' });

  console.log(`\nfull record: ${out}`);
};

main().catch(error => {
  console.error(error.message || error);
  process.exit(1);
});
