import { caligoApi } from '../store/api';

export interface Tokens {
  accessToken: string;
  refreshToken: string;
}

export const accountApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    login: build.mutation<Tokens, { username: string; password: string }>({
      query: body => ({ url: '/api/account/login', method: 'POST', data: body })
    }),

    googleLogin: build.mutation<Tokens, string>({
      query: credential => ({
        url: '/api/account/google-login',
        method: 'POST',
        data: JSON.stringify(credential),
        headers: { 'Content-Type': 'application/json' }
      })
    }),

    register: build.mutation<void, { username: string; email: string; password: string }>({
      query: body => ({ url: '/api/account/register', method: 'POST', data: body })
    }),

    subscribe: build.mutation<void, string>({
      query: email => ({ url: '/api/subscriptions', method: 'POST', data: { email } })
    })
  })
});

export const {
  useLoginMutation,
  useGoogleLoginMutation,
  useRegisterMutation,
  useSubscribeMutation
} = accountApi;
