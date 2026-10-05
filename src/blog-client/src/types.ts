export interface PagedResult<T> {
  items: T[];
  nextCursor: string | null;
  hasMore: boolean;
}

export type PostStatus = 'published' | 'scheduled';

export interface PostSummary {
  id: number;
  title: string;
  excerpt: string;
  publishedDate: string;
  authorName: string;
  tags: string[];
  status: PostStatus;
  scheduledFor: string | null;
}

export interface PostDetail {
  id: number;
  title: string;
  content: string;
  publishedDate: string;
  authorName: string;
  authorId: string | null;
  tags: string[];
  status: PostStatus;
  scheduledFor: string | null;
  version: number;
}

export interface ClapTally {
  total: number;
  clappers: number;
  mine: number;
}

export interface ClapResult {
  applied: number;
  mine: number;
  max: number;
}

export type SeriesBucket = 'minute' | 'hour' | 'day';

export interface SeriesPoint {
  bucketStart: string;
  views: number;
  uniqueVisitors: number;
  claps: number;
}

export interface SeriesResponse {
  postId: number;
  bucket: SeriesBucket;
  from: string;
  to: string;
  enabled: boolean;
  points: SeriesPoint[];
}

export interface FunnelResponse {
  postId: number;
  days: number;
  enabled: boolean;
  views: number;
  uniqueVisitors: number;
  clappers: number;
  claps: number;
  clapperRate: number;
}

export interface WindowPost {
  postId: number;
  title: string;
  views: number;
  uniqueVisitors: number;
  claps: number;
}

export interface TopWindowResponse {
  enabled: boolean;
  minutes: number;
  by: 'views' | 'claps';
  posts: WindowPost[];
}

export interface PostStats {
  postId: number;
  views: number;
  uniqueVisitors: number;
}

export interface PostStatsResponse {
  enabled: boolean;
  posts: PostStats[];
}

export interface TrendingPost {
  postId: number;
  title: string;
  score: number;
}

export interface TrendingSnapshot {
  windowHours: number;
  generatedAt: string;
  posts: TrendingPost[];
}
