// Per-project state that outlives a tab switch: a build or publish keeps running in the core when the user leaves
// the tab, so its progress lives here (keyed by project path) instead of in component state.
import { useSyncExternalStore } from "react";

export interface KeyedStore<T> {
  get(path: string): T;
  set(path: string, update: (previous: T) => T): void;
  use(path: string): T;
}

export function createKeyedStore<T>(initial: () => T): KeyedStore<T> {
  const values = new Map<string, T>();
  const listeners = new Set<() => void>();
  const keyOf = (path: string) => path.replace(/\\/g, "/").toLowerCase();

  const get = (path: string): T => {
    const key = keyOf(path);
    let value = values.get(key);
    if (value === undefined) {
      value = initial();
      values.set(key, value);
    }
    return value;
  };

  const subscribe = (listener: () => void) => {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  };

  return {
    get,
    set(path, update) {
      values.set(keyOf(path), update(get(path)));
      for (const listener of [...listeners]) listener();
    },
    use(path) {
      return useSyncExternalStore(subscribe, () => get(path));
    },
  };
}
