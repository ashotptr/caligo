export interface PagedResult<T> {
  items: T[];
  /** Opaque cursor for the next page; null once the last page has been reached. */
  nextCursor: string | null;
  hasMore: boolean;
}

export interface PostSummary {
  id: number;
  title: string;
  excerpt: string;
  publishedDate: string;
  authorName: string;
  tags: string[];
}

export interface PostDetail {
  id: number;
  title: string;
  content: string;
  publishedDate: string;
  authorName: string;
  authorId: string | null;
  tags: string[];
}

export interface PostStats {
  postId: number;
  views: number;
  uniqueVisitors: number;
}

export interface PostStatsResponse {
  /** False when the analytics service could not be reached; the page renders without counts. */
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
