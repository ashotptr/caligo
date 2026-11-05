import { Kafka, logLevel, type Consumer } from 'kafkajs';
import type { Cards } from './cards.js';

export const POST_LIFECYCLE = 'caligo.post.lifecycle.v1';
export const GROUP_ID = 'caligo-share';

const RETRY_MS = 10_000;

export const handlePostEvent = async (cards: Cards, eventType: string | undefined, value: string | null): Promise<void> => {
  if (!value) {
    return;
  }

  const { postId } = JSON.parse(value) as { postId?: unknown };

  if (typeof postId !== 'number') {
    return;
  }

  switch (eventType) {
    case 'post.published':
    case 'post.updated':
      await cards.refresh(`/posts/${postId}`);
      break;
    case 'post.deleted':
      await cards.remove(`/posts/${postId}`);
      break;
  }
};

export const startEvents = (brokers: string[], cards: Cards): { stop: () => Promise<void> } => {
  const kafka = new Kafka({ clientId: GROUP_ID, brokers, logLevel: logLevel.WARN });
  let consumer: Consumer | null = null;
  let stopped = false;

  const connect = async (): Promise<void> => {
    while (!stopped) {
      const attempt = kafka.consumer({ groupId: GROUP_ID });

      try {
        await attempt.connect();
        await attempt.subscribe({ topic: POST_LIFECYCLE, fromBeginning: false });
        await attempt.run({
          eachMessage: async ({ message }) => {
            try {
              await handlePostEvent(cards, message.headers?.['event-type']?.toString(), message.value?.toString() ?? null);
            } catch (error) {
              console.warn('Could not handle a post event:', error);
            }
          }
        });

        consumer = attempt;
        console.info(`Listening to ${POST_LIFECYCLE}.`);

        return;
      } catch (error) {
        console.warn('Kafka is not ready yet; trying again:', error instanceof Error ? error.message : error);
        await attempt.disconnect().catch(() => undefined);
        await new Promise(resolve => setTimeout(resolve, RETRY_MS));
      }
    }
  };

  void connect();

  return {
    stop: async () => {
      stopped = true;
      await consumer?.disconnect();
    }
  };
};
