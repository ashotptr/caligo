export interface Config {
  port: number;
  databaseUrl: string;
  siteUrl: string;
  siteOrigin: string;
  kafkaBrokers: string[];
  cardsDir: string;
  chromePath: string;
}

const required = (name: string): string => {
  const value = process.env[name]?.trim();

  if (!value) {
    throw new Error(`${name} is not set.`);
  }

  return value;
};

const withoutTrailingSlash = (url: string): string => url.replace(/\/+$/, '');

export const loadConfig = (): Config => ({
  port: Number(process.env.PORT ?? 8080),
  databaseUrl: required('DATABASE_URL'),
  siteUrl: withoutTrailingSlash(required('SITE_URL')),
  siteOrigin: withoutTrailingSlash(required('SITE_ORIGIN')),
  kafkaBrokers: (process.env.KAFKA_BROKERS ?? '')
    .split(',')
    .map(broker => broker.trim())
    .filter(Boolean),
  cardsDir: required('CARDS_DIR'),
  chromePath: process.env.CHROME_PATH?.trim() || '/usr/bin/chromium'
});
