// The real transport: talks to the .NET core embedded in the app exe. The Rust side (src-tauri/src/core.rs) unpacks
// it on first start, runs `addonstudio-core rpc`, and bridges stdin/stdout: core_write sends a line, every stdout line
// comes back as a "core://stdout" event, stderr as "core://stderr", the end of the process as "core://exit". Each
// event carries the id core_spawn returned, so lines from a replaced core are ignored.
// If the core exits on its own, requests it was handling fail with a clear error and it is restarted once; if it
// exits again the transport closes (client.ts then fails everything and starts a fresh transport on the next call).
import { createLineBuffer, createListeners, type Transport } from "./transport";

/** A running core process, as the transport sees it. */
export interface CoreProcess {
  write(line: string): Promise<void>;
  kill(): Promise<void>;
}

export interface CoreHandlers {
  stdout(chunk: string): void;
  stderr(chunk: string): void;
  /** The process is gone (exit code, signal, or a spawn/IO error). */
  exit(reason: string): void;
}

export type SpawnCore = (handlers: CoreHandlers) => Promise<CoreProcess>;

interface CoreLine {
  id: number;
  line: string;
}

interface CoreExit {
  id: number;
  reason: string;
}

/** Starts the embedded core through the app's Rust commands. */
export const spawnEmbedded: SpawnCore = async (handlers) => {
  const { invoke } = await import("@tauri-apps/api/core");
  const { listen } = await import("@tauri-apps/api/event");

  let id: number | null = null;
  // events can arrive before core_spawn has returned the id: keep them and replay once it is known
  const early: Array<() => void> = [];
  const forThisCore =
    <T extends { id: number }>(handle: (payload: T) => void) =>
    (event: { payload: T }) => {
      const payload = event.payload;
      if (id === null) {
        early.push(() => {
          if (payload.id === id) handle(payload);
        });
        return;
      }
      if (payload.id === id) handle(payload);
    };

  const unlisteners: Array<() => void> = [];
  const stopListening = () => {
    for (const unlisten of unlisteners.splice(0)) unlisten();
  };

  unlisteners.push(await listen<CoreLine>("core://stdout", forThisCore((payload) => handlers.stdout(`${payload.line}\n`))));
  unlisteners.push(await listen<CoreLine>("core://stderr", forThisCore((payload) => handlers.stderr(`${payload.line}\n`))));
  unlisteners.push(
    await listen<CoreExit>(
      "core://exit",
      forThisCore((payload) => {
        stopListening();
        handlers.exit(payload.reason);
      }),
    ),
  );

  try {
    id = await invoke<number>("core_spawn");
  } catch (error) {
    stopListening();
    throw error instanceof Error ? error : new Error(String(error));
  }

  for (const replay of early.splice(0)) replay();

  const mine = id;
  return {
    write: (line) => invoke("core_write", { id: mine, line }),
    kill: async () => {
      stopListening();
      await invoke("core_kill", { id: mine });
    },
  };
};

interface InFlight {
  method: string;
}

export interface TauriTransportOptions {
  spawn?: SpawnCore;
  /** Automatic restarts after the core exits on its own (default 1). */
  restarts?: number;
  /** Where the core's stderr goes (default console.debug). */
  log?: (line: string) => void;
}

export async function createTauriTransport(options: TauriTransportOptions = {}): Promise<Transport> {
  const spawn = options.spawn ?? spawnEmbedded;
  const log = options.log ?? ((line: string) => console.debug("[core]", line));
  let restartsLeft = options.restarts ?? 1;

  const lines = createListeners<[string]>();
  const closes = createListeners<[string]>();
  const inFlight = new Map<number, InFlight>();
  let closed = false;
  let stopping = false;
  /** Bumps on every spawn so events from a dead process are ignored. */
  let generation = 0;
  let current: Promise<CoreProcess>;

  const finish = (reason: string) => {
    if (closed) return;
    closed = true;
    closes.emit(reason);
  };

  /** Answers every request the dead core was handling with an error, so callers do not hang. */
  const failInFlight = (message: string) => {
    const ids = [...inFlight.entries()];
    inFlight.clear();
    for (const [id, request] of ids) {
      lines.emit(JSON.stringify({ id, error: { code: "internal", message: `${message} (while running ${request.method})`, details: null } }));
    }
  };

  const start = (): Promise<CoreProcess> => {
    const mine = ++generation;
    const stdoutLines = createLineBuffer((line) => {
      if (mine !== generation) return;
      trackResponse(line);
      lines.emit(line);
    });

    return spawn({
      stdout: (chunk) => {
        if (mine === generation) stdoutLines(chunk);
      },
      // the core logs to stderr only; it arrives a line at a time
      stderr: (chunk) => {
        for (const line of chunk.replace(/\r?\n$/, "").split(/\r?\n/)) if (line.length > 0) log(line);
      },
      exit: (reason) => {
        if (mine !== generation || closed) return;
        generation += 1;
        if (stopping) {
          finish("closed");
          return;
        }
        if (restartsLeft > 0) {
          restartsLeft -= 1;
          console.warn(`The core stopped (${reason}). Restarting it.`);
          failInFlight(`The core stopped (${reason}) and was restarted. Try again`);
          current = start();
          current.catch((error: unknown) => {
            failInFlight(`The core could not restart: ${String(error)}`);
            finish(`The core could not restart: ${String(error)}`);
          });
          return;
        }
        failInFlight(`The core stopped again (${reason}). Restart Addon Studio`);
        finish(`The core stopped again (${reason}). Restart Addon Studio.`);
      },
    });
  };

  /** Remembers a request until its response arrives; returns its id. */
  const trackRequest = (line: string): number | null => {
    try {
      const message = JSON.parse(line) as { id?: unknown; method?: unknown };
      if (typeof message.id === "number" && typeof message.method === "string") {
        inFlight.set(message.id, { method: message.method });
        return message.id;
      }
    } catch {
      // not JSON: the core answers with an error line of its own
    }
    return null;
  };

  function trackResponse(line: string) {
    if (inFlight.size === 0) return;
    try {
      const message = JSON.parse(line) as { id?: unknown; event?: unknown };
      if (typeof message.id === "number" && message.event === undefined) inFlight.delete(message.id);
    } catch {
      // ignored, client.ts warns about lines that are not JSON
    }
  }

  current = start();
  try {
    await current;
  } catch (error) {
    throw new Error(`Could not start the core: ${error instanceof Error ? error.message : String(error)}`);
  }

  return {
    kind: "tauri",
    async send(line: string) {
      if (closed) throw new Error("The core is not running.");
      const process = await current;
      const text = line.endsWith("\n") ? line : `${line}\n`;
      const id = trackRequest(text.trimEnd());
      try {
        await process.write(text);
      } catch (error) {
        if (id !== null) inFlight.delete(id);
        throw error;
      }
    },
    onLine: lines.add,
    onClose: closes.add,
    async close() {
      if (closed) return;
      stopping = true;
      try {
        await (await current).kill();
      } finally {
        finish("closed");
      }
    },
  };
}
