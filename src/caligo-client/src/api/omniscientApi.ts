import { caligoApi } from '../store/api';
import type { AskAnswer } from '../types';

export const omniscientApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    askOmniscient: build.mutation<AskAnswer, { query: string; sessionId: string }>({
      query: body => ({ url: '/api/caligo/ask', method: 'POST', data: body })
    })
  })
});
