import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from '@/app/App';
import { applyBranding } from '@/shared/lib/branding';
import '@/shared/i18n';
import '@/styles/global.css';

applyBranding();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
