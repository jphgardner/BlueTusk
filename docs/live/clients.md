# Framework guides: Angular, React, Vue and Svelte

This page shows how to bind a live query to a component in Angular, React, Vue
or Svelte. Each adapter starts the query with the component and stops it when
the component goes away. The examples use the `my-todos` query from the
[quick start](quickstart.md).

The adapters only handle the framework's lifecycle and change notification.
Reconnects, resume tokens and result updates come from the core client,
`@bluetusk/live`, which each adapter installs as a dependency. Each adapter
also combines rapid updates into one notification per microtask.

| Package | Install | Status |
| --- | --- | --- |
| `@bluetusk/live-angular` | `npm install @bluetusk/live-angular` | Angular 20 to 22 |
| `@bluetusk/live-react` | `npm install @bluetusk/live-react` | React 18 or 19 |
| `@bluetusk/live-vue` | `npm install @bluetusk/live-vue` | New in 1.1.0. Vue 3.4 or later |
| `@bluetusk/live-svelte` | `npm install @bluetusk/live-svelte` | New in 1.1.0. Svelte 5 |

## Create one client

Every example shares one client. Create it once per application, in its own
module:

```typescript
import { BlueTuskLiveClient } from "@bluetusk/live";

export const client = new BlueTuskLiveClient({
  endpoint: "/bluetusk/live/sse"
});
```

All client options are listed in
[configuration](configuration.md#browser-client-options). With the published
1.0.0 or 1.1.0-rc.1 client, also pass `fetch: (input, init) => fetch(input, init)`;
see [troubleshooting](troubleshooting.md#the-page-says-illegal-invocation).

## Angular

`provideBlueTuskLive(client)` registers the client. Inject
`BlueTuskLiveAngular` and call `createQuery`. The returned
`AngularLiveQuery` exposes read-only signals: `state`, `rows`, `phase` and
`error`.

```typescript
import type { ApplicationConfig } from "@angular/core";
import { provideBlueTuskLive } from "@bluetusk/live-angular";
import { client } from "./live-client";

export const appConfig: ApplicationConfig = {
  providers: [provideBlueTuskLive(client)]
};
```

```typescript
import { Component, DestroyRef, inject } from "@angular/core";
import { BlueTuskLiveAngular } from "@bluetusk/live-angular";

interface Todo {
  Id: number;
  Title: string;
}

@Component({
  selector: "app-todo-list",
  template: `
    @if (todos.error(); as error) {
      <p>{{ todos.phase() }}: {{ error.message }}</p>
    } @else {
      <ul>
        @for (todo of todos.rows(); track todo.Id) {
          <li>{{ todo.Title }}</li>
        }
      </ul>
    }
  `
})
export class TodoListComponent {
  readonly todos = inject(BlueTuskLiveAngular).createQuery<Todo, number, object>({
    query: "my-todos",
    parameters: {}
  });

  constructor() {
    this.todos.start();
    inject(DestroyRef).onDestroy(() => this.todos.destroy());
  }
}
```

The Angular query does not start by itself: call `start()`. Call `destroy()`
when the owner is destroyed; a destroyed query cannot be restarted. Use
`stop()` and `start()` for a temporary pause.

## React

`useBlueTuskLiveQuery(client, request)` returns the current `LiveQueryState`:
`{ phase, rows, lastSequence, error }`. It starts the query after mount and
stops it on unmount.

```tsx
import { useMemo } from "react";
import { useBlueTuskLiveQuery } from "@bluetusk/live-react";
import { client } from "./live-client";

interface Todo {
  Id: number;
  Title: string;
}

export function TodoList() {
  const request = useMemo(() => ({ query: "my-todos", parameters: {} }), []);
  const state = useBlueTuskLiveQuery<Todo, number, object>(client, request);

  if (state.error) {
    return <p>{state.phase}: {state.error.message}</p>;
  }

  return (
    <ul>
      {state.rows.map((todo) => <li key={todo.Id}>{todo.Title}</li>)}
    </ul>
  );
}
```

Keep the `request` object and the `client` stable. The hook creates a new
query, and a new connection, whenever either object changes. Wrap the request
in `useMemo` with its real inputs as dependencies, as shown. Server rendering
does not open a connection.

## Vue

`useBlueTuskLiveQuery(client, request, options?)` returns a `VueLiveQuery`
with read-only refs `state`, `rows`, `phase` and `error`. It starts
immediately and stops when the component's setup scope is disposed.

```html
<script setup lang="ts">
import { useBlueTuskLiveQuery } from "@bluetusk/live-vue";
import { client } from "./live-client";

interface Todo {
  Id: number;
  Title: string;
}

const { rows, phase, error } = useBlueTuskLiveQuery<Todo, number, object>(client, {
  query: "my-todos",
  parameters: {}
});
</script>

<template>
  <p v-if="error">{{ phase }}: {{ error.message }}</p>
  <ul v-else>
    <li v-for="todo in rows" :key="todo.Id">{{ todo.Title }}</li>
  </ul>
</template>
```

Destructure the refs as shown so the template unwraps them. Outside a
component, for example in a store or a test, call
`createBlueTuskLiveQuery(client, request)` instead and call `destroy()`
yourself. Pass `{ autoStart: false }` to either function to start it later
with `start()`.

## Svelte

`useBlueTuskLiveQuery(client, request, options?)` returns a `SvelteLiveQuery`
with readable stores `state`, `rows`, `phase` and `error`. It starts
immediately and stops when the component is destroyed.

```html
<script lang="ts">
  import { useBlueTuskLiveQuery } from "@bluetusk/live-svelte";
  import { client } from "./live-client";

  interface Todo {
    Id: number;
    Title: string;
  }

  const { rows, phase, error } = useBlueTuskLiveQuery<Todo, number, object>(client, {
    query: "my-todos",
    parameters: {}
  });
</script>

{#if $error}
  <p>{$phase}: {$error.message}</p>
{:else}
  <ul>
    {#each $rows as todo (todo.Id)}
      <li>{todo.Title}</li>
    {/each}
  </ul>
{/if}
```

Destructure the stores so you can read them with `$rows`, `$phase` and
`$error`. Outside component initialization, call
`createBlueTuskLiveQuery(client, request)`, subscribe to its stores, and call
`destroy()` yourself.

## Plain TypeScript

Without a framework, use the core client directly, as in the
[quick start](quickstart.md#4-write-the-page): `createQuery`, then
`subscribe(listener)`, `start()`, and `stop()` when the view goes away.

## Next steps

- [Configuration](configuration.md#browser-client-options): retries,
  credentials and batching.
- [Troubleshooting](troubleshooting.md): reconnect loops and errors.
