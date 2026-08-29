import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    title: 'BlueTusk — The PostgreSQL platform for .NET',
    loadComponent: () => import('./home/home').then((component) => component.Home),
  },
  {
    path: 'platform',
    title: 'Platform — BlueTusk',
    loadComponent: () =>
      import('./pages/platform.page').then((component) => component.PlatformPage),
  },
  {
    path: 'provider',
    title: 'Provider — BlueTusk',
    loadComponent: () =>
      import('./pages/provider.page').then((component) => component.ProviderPage),
  },
  {
    path: 'ef-core',
    title: 'EF Core — BlueTusk',
    loadComponent: () => import('./pages/ef-core.page').then((component) => component.EfCorePage),
  },
  {
    path: 'real-time',
    title: 'Real Time — BlueTusk',
    loadComponent: () =>
      import('./pages/real-time.page').then((component) => component.RealTimePage),
  },
  {
    path: 'extensions',
    title: 'Extensions — BlueTusk',
    loadComponent: () =>
      import('./pages/extensions.page').then((component) => component.ExtensionsPage),
  },
  {
    path: 'graph',
    title: 'Graph — BlueTusk',
    loadComponent: () => import('./pages/graph.page').then((component) => component.GraphPage),
  },
  {
    path: 'evidence',
    title: 'Engineering Evidence — BlueTusk',
    loadComponent: () =>
      import('./pages/evidence.page').then((component) => component.EvidencePage),
  },
  {
    path: 'documentation',
    title: 'Documentation — BlueTusk',
    loadComponent: () => import('./docs/docs-hub.page').then((component) => component.DocsHubPage),
  },
  {
    path: 'documentation/:category/:slug',
    loadComponent: () => import('./docs/guide.page').then((component) => component.GuidePage),
  },
  {
    path: 'community',
    title: 'Community — BlueTusk',
    loadComponent: () =>
      import('./pages/community.page').then((component) => component.CommunityPage),
  },
  { path: '**', redirectTo: '' },
];
