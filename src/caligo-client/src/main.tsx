import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { Provider } from 'react-redux';
import { BrowserRouter } from 'react-router-dom';
import { GoogleOAuthProvider } from '@react-oauth/google';
import CollectionDialogs from './components/CollectionDialogs';
import Toaster from './components/Toaster';
import { PlayerProvider } from './contexts/PlayerContext';
import { store } from './store/store';
import App from './App';
import './index.css';

const googleClientId = import.meta.env.VITE_GOOGLE_CLIENT_ID as string | undefined;

const app = (
  <Provider store={store}>
    <BrowserRouter>
      <PlayerProvider>
        <App />
        <CollectionDialogs />
      </PlayerProvider>
      <Toaster />
    </BrowserRouter>
  </Provider>
);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {googleClientId ? <GoogleOAuthProvider clientId={googleClientId}>{app}</GoogleOAuthProvider> : app}
  </StrictMode>
);
