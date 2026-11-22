// A line transport to the core: one JSON message per line each way.

export type Unsubscribe = () => void;

export interface Transport {
  /** Which transport this is, for the UI (e.g. a "demo data" badge). */
  readonly kind: "tauri" | "mock";
  /** Sends one protocol line (no trailing newline needed). */
  send(line: string): Promise<void>;
  /** Called for every complete line the core writes to stdout. */
  onLine(listener: (line: string) => void): Unsubscribe;
  /** Called once when the core goes away (exited, crashed, could not start). */
  onClose(listener: (reason: string) => void): Unsubscribe;
  /** Stops the core. */
  close(): Promise<void>;
}

/** Joins chunks into lines. Copes with chunks that are whole lines without "\n" and with lines split across chunks. */
export function createLineBuffer(emit: (line: string) => void): (chunk: string) => void {
  let buffer = "";

  return (chunk: string) => {
    buffer += chunk;

    let newline = buffer.indexOf("\n");
    while (newline >= 0) {
      const line = buffer.slice(0, newline).replace(/\r$/, "");
      buffer = buffer.slice(newline + 1);
      if (line.trim().length > 0) emit(line);
      newline = buffer.indexOf("\n");
    }

    // a transport that strips newlines hands over whole messages: a complete JSON object is a line already
    const rest = buffer.trim();
    if (rest.startsWith("{") && rest.endsWith("}")) {
      try {
        JSON.parse(rest);
        buffer = "";
        emit(rest);
      } catch {
        // not complete yet, wait for more
      }
    }
  };
}

/** A tiny listener set. */
export function createListeners<T extends unknown[]>() {
  const listeners = new Set<(...args: T) => void>();
  return {
    add(listener: (...args: T) => void): Unsubscribe {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    emit(...args: T) {
      for (const listener of [...listeners]) {
        try {
          listener(...args);
        } catch (error) {
          console.error("rpc listener failed", error);
        }
      }
    },
    clear() {
      listeners.clear();
    },
  };
}
