import { PrismaPg } from '@prisma/adapter-pg';
import { createApp } from './app.js';
import { createCards } from './cards.js';
import { loadConfig } from './config.js';
import { startEvents } from './events.js';
import { PrismaClient } from './generated/prisma/client.js';
import { createRenderer } from './renderer.js';
import { createSite } from './site.js';
import { prismaStore } from './store.js';

const config = loadConfig();
const prisma = new PrismaClient({ adapter: new PrismaPg({ connectionString: config.databaseUrl }) });
const site = createSite(config.siteOrigin);

const cards = createCards({
  store: prismaStore(prisma),
  api: site.api,
  image: site.image,
  render: createRenderer(config.chromePath),
  dir: config.cardsDir
});

const server = createApp(cards, config.siteUrl).listen(config.port, () => {
  console.info(`caligo-share is listening on port ${config.port}.`);
});

const events = config.kafkaBrokers.length > 0 ? startEvents(config.kafkaBrokers, cards) : null;

const stop = async (): Promise<void> => {
  server.close();
  await events?.stop();
  await prisma.$disconnect();
  process.exit(0);
};

process.on('SIGTERM', () => void stop());
process.on('SIGINT', () => void stop());
