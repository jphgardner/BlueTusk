import { ViewportScroller } from '@angular/common';
import {
  ApplicationConfig,
  inject,
  provideBrowserGlobalErrorListeners,
  provideEnvironmentInitializer,
} from '@angular/core';
import { provideRouter, TitleStrategy, withInMemoryScrolling } from '@angular/router';

import { routes } from './app.routes';
import { PageMetadata } from './page-metadata';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      withInMemoryScrolling({ anchorScrolling: 'enabled', scrollPositionRestoration: 'enabled' }),
    ),
    provideEnvironmentInitializer(() => inject(ViewportScroller).setOffset([0, 80])),
    { provide: TitleStrategy, useClass: PageMetadata },
  ],
};
