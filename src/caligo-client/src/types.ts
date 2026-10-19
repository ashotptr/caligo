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
  contentId: number | null;
  contentTitle: string | null;
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
  contentId: number | null;
  contentTitle: string | null;
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

export type ContentKind = 'movie' | 'episode' | 'song' | 'other';

export interface Named {
  id: number;
  name: string;
}

export interface ContentSummary {
  contentId: number;
  title: string;
  kind: ContentKind;
  duration: number;
  releaseDate: string;
  averageRating: number | null;
  genres: string[];
}

export interface SongArtist {
  artistId: number;
  name: string;
  role: string | null;
}

export interface SongRelease {
  id: number;
  title: string;
  trackNumber: number | null;
}

export interface ContentDetail {
  contentId: number;
  title: string;
  description: string | null;
  kind: ContentKind;
  duration: number;
  releaseDate: string;
  averageRating: number | null;
  productionCompany: Named;
  distributingCompanies: Named[];
  genres: Named[];
  languages: Named[];
  subtitles: Named[];
  actors: { actorId: number; name: string }[];
  movie: { movieId: number; budget: number | null; boxOffice: number | null } | null;
  episode: {
    seriesId: number;
    seriesTitle: string;
    seasonNumber: number;
    seasonName: string | null;
    episodeNumber: number;
  } | null;
  song: {
    songId: number;
    audioUri: string | null;
    lyrics: string | null;
    artists: SongArtist[];
    albums: SongRelease[];
    singles: SongRelease[];
  } | null;
  views: number;
  interactions: number;
  comments: number;
}

export interface SimilarContent {
  contentId: number;
  title: string;
  kind: ContentKind;
  similarity: number;
}

export interface ContentComment {
  commentId: number;
  contentId: number;
  author: string;
  comment: string;
}

export interface SeriesSummary {
  seriesId: number;
  title: string;
  releaseDate: string;
  seasons: number;
}

export interface SeriesDetail {
  seriesId: number;
  title: string;
  description: string;
  releaseDate: string;
  seasons: {
    seasonNumber: number;
    name: string | null;
    episodes: { episodeNumber: number; contentId: number; title: string; duration: number; releaseDate: string }[];
  }[];
}

export interface ActorDetail {
  actorId: number;
  name: string;
  dob: string;
  nationality: string;
  biography: string;
  gender: string;
  profilePictureUri: string | null;
  contents: ContentSummary[];
}

export interface Track {
  contentId: number;
  songId: number;
  title: string;
  duration: number;
  discNumber: number | null;
  trackNumber: number | null;
  audioUri: string | null;
  artists: string[];
}

export interface ArtistSummary {
  artistId: number;
  name: string;
  songs: number;
}

export interface AlbumSummary {
  albumId: number;
  title: string;
  releaseDate: string | null;
  coverUri: string | null;
  artists: string[];
  songs: number;
}

export interface ArtistDetail {
  artistId: number;
  name: string;
  country: string | null;
  formedDate: string | null;
  biography: string | null;
  pictureUri: string | null;
  albums: AlbumSummary[];
  singles: Named[];
  songs: Track[];
}

export interface ReleaseDetail {
  title: string;
  description?: string | null;
  releaseDate: string | null;
  coverUri: string | null;
  artists: Named[];
  tracks: Track[];
}

export interface Plan {
  subscriptionPlanId: number;
  paymentInfo: string;
  planInfo: string;
  monthlyPrice: number;
}

export interface StreamingProfile {
  userId: number;
  firstName: string | null;
  lastName: string | null;
  dob: string;
  email: string;
  gender: string;
  subscriptionPlanId: number;
  subscriptionStartDate: string;
  subscriptionEndDate: string;
  active: boolean;
}

export interface Spending {
  allTimeSpending: number;
  payments: { paymentId: number; date: string; amount: number; subscriptionPlanId: number }[];
}

export interface Interaction {
  contentId: number;
  liked: boolean | null;
  rating: number | null;
  averageRating: number | null;
}

export interface WatchEntry {
  watchedContentId: number;
  contentId: number;
  title: string;
  kind: ContentKind;
  timestamp: number | null;
  date: string;
}

export interface Recommendation {
  contentId: number;
  title: string;
  kind: ContentKind;
  reason: string | null;
  date: string | null;
}

export interface StreamingNotification {
  notificationId: number;
  reason: string;
  date: string;
}

export interface PopularContent {
  contentId: number;
  title: string;
  totalViews: number;
}

export interface EngagingContent {
  contentId: number;
  title: string;
  totalViews: number;
  totalInteractions: number;
  commentsLeft: number;
  engagementRatio: number | null;
}

export interface ActiveUser {
  userId: number;
  fullName: string | null;
  totalViews: number;
  totalInteractions: number;
  commentsLeft: number;
}

export interface SavedQuery {
  number: number;
  title: string;
}

export interface QueryResult {
  number: number;
  title: string;
  columns: string[];
  rows: (string | number | boolean | null)[][];
}

export interface MusicImportResult {
  releases: number;
  tracks: number;
  artistsAdded: number;
  albumsAdded: number;
  singlesAdded: number;
  songsAdded: number;
  songsAlreadyThere: number;
  problems: string[];
}

export interface RadioPlay {
  startedAt: string;
  artist: string;
  album: string;
  title: string;
  isRequest: boolean;
  contentId: number | null;
}

export interface RadioHistory {
  enabled: boolean;
  plays: RadioPlay[];
}

export type MediaStatus = 'queued' | 'packaging' | 'ready' | 'failed';

export interface Rendition {
  kind: 'video' | 'audio' | 'progressive';
  codec: string;
  bitrate: number;
  width: number | null;
  height: number | null;
  language: string | null;
}

export interface MediaInfo {
  contentId: number;
  status: MediaStatus;
  playable: boolean;
  durationSeconds: number | null;
  packagedAt: string | null;
  formats: string[];
  renditions: Rendition[];
  subtitles: { label: string; language: string | null }[];
  sourceFile: string | null;
  error: string | null;
}

export interface InboxFile {
  file: string;
  size: number;
  modified: string;
  subtitles: { file: string; language: string; known: boolean }[];
}

export interface PlaybackTitle {
  contentId: number;
  title: string;
  sessions: number;
  startups: number;
  medianStartupMs: number;
  p95StartupMs: number;
  stalls: number;
  stallMs: number;
  qualitySwitches: number;
  errors: number;
  hlsStartups: number;
  nativeHlsStartups: number;
  dashStartups: number;
  progressiveStartups: number;
}

export interface PlaybackQuality {
  enabled: boolean;
  days: number;
  titles: PlaybackTitle[];
}

export interface LabRun {
  runId: string;
  scenario: string;
  profile: string;
  startedAt: string;
  finishedAt: string;
  listeners: number;
  seconds: number;
  repetitions: number;
  contestants: number;
  sourceBiasMs: number | null;
  notes: string;
}

export interface LabRunsResponse {
  enabled: boolean;
  runs: LabRun[];
}

export interface LabContestant {
  contestant: string;
  server: string;
  protocol: string;
  container: string;
  repetitions: number;
  listeners: number;
  connected: number;
  ttfbP50Ms: number | null;
  startupP50Ms: number | null;
  startupP95Ms: number | null;
  startupSpreadMs: number | null;
  stalls: number;
  stallMs: number;
  stallsPerListenerMinute: number | null;
  mediaMs: number;
  latencyP50Ms: number | null;
  latencySpreadMs: number | null;
  dataLatencyP50Ms: number | null;
  cpuMeanPercent: number | null;
  cpuPeakPercent: number | null;
  memoryPeakBytes: number | null;
  impairedPackets: number;
  impairedLost: number;
  impairedOverflowed: number;
  errors: string;
}

export interface LabLatencyPoint {
  contestant: string;
  repetition: number;
  measuredAt: string;
  latencyMs: number;
}

export interface LabRunDetail {
  enabled: boolean;
  run: LabRun;
  contestants: LabContestant[];
  latency: LabLatencyPoint[];
}
