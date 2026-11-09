import type { BaseQueryFn } from '@reduxjs/toolkit/query';
import type { AxiosError, AxiosRequestConfig } from 'axios';
import api from '../api/axios';

export interface ApiRequest {
  url: string;
  method?: AxiosRequestConfig['method'];
  data?: unknown;
  params?: AxiosRequestConfig['params'];
  headers?: Record<string, string>;
}

export interface ApiError {
  status: number | undefined;
  data: unknown;
}

export interface ApiMeta {
  headers: Record<string, string | undefined>;
}

export const axiosBaseQuery: BaseQueryFn<ApiRequest | string, unknown, ApiError, object, ApiMeta> = async (request, { signal }) => {
  const { url, method = 'GET', data, params, headers } = typeof request === 'string' ? { url: request } : request;

  try {
    const response = await api.request({ url, method, data, params, headers, signal });
    const received: Record<string, string | undefined> = {};

    for (const [name, value] of Object.entries(response.headers ?? {})) {
      if (typeof value === 'string') {
        received[name.toLowerCase()] = value;
      }
    }

    return { data: response.data, meta: { headers: received } };
  } catch (error) {
    const failure = error as AxiosError;

    return { error: { status: failure.response?.status, data: failure.response?.data ?? failure.message } };
  }
};
