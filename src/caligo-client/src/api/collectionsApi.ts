import { caligoApi } from '../store/api';
import type { AddTitlesResult, CollectionDetail, CollectionSummary } from '../types';

export const collectionsApi = caligoApi.injectEndpoints({
  endpoints: build => ({
    getCollections: build.query<CollectionSummary[], void>({
      query: () => '/api/collections',
      providesTags: ['Collections']
    }),

    getCollection: build.query<CollectionDetail, string>({
      query: id => `/api/collections/${id}`,
      providesTags: (_result, _error, id) => [{ type: 'Collection', id }]
    }),

    createCollection: build.mutation<CollectionSummary, string>({
      query: name => ({ url: '/api/collections', method: 'POST', data: { name } }),
      invalidatesTags: (_result, error) => (error ? [] : ['Collections'])
    }),

    renameCollection: build.mutation<void, { collectionId: number; name: string }>({
      query: ({ collectionId, name }) => ({ url: `/api/collections/${collectionId}`, method: 'PUT', data: { name } }),
      invalidatesTags: (_result, error, { collectionId }) =>
        (error ? [] : ['Collections', { type: 'Collection', id: String(collectionId) }])
    }),

    deleteCollection: build.mutation<void, number>({
      query: collectionId => ({ url: `/api/collections/${collectionId}`, method: 'DELETE' }),
      invalidatesTags: (_result, error) => (error ? [] : ['Collections'])
    }),

    addTitles: build.mutation<AddTitlesResult, { collectionId: number; contentIds: number[] }>({
      query: ({ collectionId, contentIds }) => ({
        url: `/api/collections/${collectionId}/titles`,
        method: 'POST',
        data: { contentIds }
      }),
      invalidatesTags: (result, error, { collectionId }) =>
        (error || !result || result.added === 0 ? [] : ['Collections', { type: 'Collection', id: String(collectionId) }])
    }),

    removeTitle: build.mutation<void, { collectionId: number; contentId: number }>({
      query: ({ collectionId, contentId }) => ({
        url: `/api/collections/${collectionId}/titles/${contentId}`,
        method: 'DELETE'
      }),
      async onQueryStarted({ collectionId, contentId }, { dispatch, queryFulfilled }) {
        const patch = dispatch(collectionsApi.util.updateQueryData('getCollection', String(collectionId), collection => {
          collection.titles = collection.titles.filter(title => title.contentId !== contentId);
        }));

        try {
          await queryFulfilled;
        } catch {
          patch.undo();
        }
      },
      invalidatesTags: (_result, error) => (error ? [] : ['Collections'])
    })
  })
});

export const {
  useGetCollectionsQuery,
  useGetCollectionQuery,
  useCreateCollectionMutation,
  useRenameCollectionMutation,
  useDeleteCollectionMutation,
  useAddTitlesMutation,
  useRemoveTitleMutation
} = collectionsApi;
