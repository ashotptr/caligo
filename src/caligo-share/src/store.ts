import type { PrismaClient, ShareCard } from './generated/prisma/client.js';

export type Card = ShareCard;

export interface CardStore {
  find(path: string): Promise<Card | null>;
  findByKey(key: string): Promise<Card | null>;
  save(card: Card): Promise<void>;
  markRendered(key: string, version: string): Promise<void>;
  remove(path: string): Promise<void>;
}

export const prismaStore = (prisma: PrismaClient): CardStore => ({
  find: path => prisma.shareCard.findUnique({ where: { path } }),

  findByKey: key => prisma.shareCard.findUnique({ where: { key } }),

  save: async card => {
    await prisma.shareCard.upsert({ where: { path: card.path }, create: card, update: card });
  },

  markRendered: async (key, version) => {
    await prisma.shareCard.updateMany({ where: { key, version }, data: { renderedVersion: version } });
  },

  remove: async path => {
    await prisma.shareCard.deleteMany({ where: { path } });
  }
});
