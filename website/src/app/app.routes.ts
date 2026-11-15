import { ActivatedRouteSnapshot, Routes } from '@angular/router';
import { loadGuide } from '../generated/guide-loader.generated';
import { PRODUCTS } from './content/catalog';
import { RESOURCES } from './content/resources';
import { REAL_TIME_RESOURCES } from './content/real-time-resources';
import { USE_CASES } from './content/use-cases';

const productPage = () => import('./pages/product.page').then((module) => module.ProductPage);

const useCasesPage = () => import('./pages/use-cases.page').then((module) => module.UseCasesPage);
const resourcePage = () => import('./pages/resource.page').then((module) => module.ResourcePage);

export const routes: Routes = [
  {
    path: '',
    title: 'BlueTusk · PostgreSQL for .NET',
    data: {
      description:
        'PostgreSQL for .NET. Connect with ADO.NET or EF Core, stream committed changes, and work with graphs and data services.',
    },
    loadComponent: () => import('./home/home').then((module) => module.Home),
  },
  {
    path: 'platform',
    title: 'Platform · BlueTusk',
    data: {
      description:
        'Explore BlueTusk data access, real-time delivery, graphs, PostgreSQL extensions, and data services.',
    },
    loadComponent: productPage,
  },
  ...PRODUCTS.map((product) => ({
    path: product.path,
    title: product.name + ' · BlueTusk',
    data: { product, description: product.description },
    loadComponent: productPage,
  })),
  {
    path: 'documentation',
    title: 'Documentation · BlueTusk',
    data: { description: 'Learn how to install, build with, and operate BlueTusk on PostgreSQL.' },
    loadComponent: () => import('./docs/docs-hub.page').then((module) => module.DocsHubPage),
  },
  {
    path: 'documentation/:category/:slug',
    resolve: {
      guide: (route: ActivatedRouteSnapshot) =>
        loadGuide(route.paramMap.get('category') ?? '', route.paramMap.get('slug') ?? ''),
    },
    loadComponent: () => import('./docs/guide.page').then((module) => module.GuidePage),
  },
  {
    path: 'packages',
    title: 'Packages · BlueTusk',
    data: { description: 'Find and install matching BlueTusk 1.2 packages.' },
    loadComponent: () => import('./pages/packages.page').then((module) => module.PackagesPage),
  },
  {
    path: 'architecture',
    title: 'Architecture · BlueTusk',
    data: {
      description: 'Trace data between PostgreSQL, a .NET application, and its subscribers.',
    },
    loadComponent: () =>
      import('./pages/architecture.page').then((module) => module.ArchitecturePage),
  },
  {
    path: 'use-cases',
    title: 'Use cases · BlueTusk',
    data: { description: 'Explore reference applications and their PostgreSQL workflows.' },
    loadComponent: useCasesPage,
  },
  ...USE_CASES.map((useCase) => ({
    path: 'use-cases/' + useCase.slug,
    title: useCase.name + ' · BlueTusk',
    data: { description: useCase.summary, useCase },
    loadComponent: useCasesPage,
  })),
  ...[...RESOURCES, ...REAL_TIME_RESOURCES].map((resource) => ({
    path: resource.path,
    title: resource.title + ' · BlueTusk',
    data: { description: resource.description, resource },
    loadComponent: resourcePage,
  })),
  {
    path: 'community',
    title: 'Community · BlueTusk',
    data: { description: 'Find support, view the source, or contribute to BlueTusk.' },
    loadComponent: () => import('./pages/community.page').then((module) => module.CommunityPage),
  },
  {
    path: '**',
    title: 'Page not found · BlueTusk',
    loadComponent: () => import('./pages/not-found.page').then((module) => module.NotFoundPage),
  },
];
