// Typed RPC client. call() sends a request and resolves with its result; events that carry the request's id go to
// onEvent; aborting the signal sends rpc.cancel. subscribe() sees every event by name (global ones like
// watch.changed, and request events too). Transport: Tauri when window.__TAURI_INTERNALS__ exists, else the mock.
import type {
  ErrorCode,
  EventMessage,
  EventName,
  Events,
  EventsOf,
  MethodName,
  Params,
  RequestId,
  Result,
  RpcIncomingMessage,
} from "./types";
import type { Transport, Unsubscribe } from "./transport";

export class RpcError extends Error {
  readonly code: ErrorCode;
  readonly details: unknown;
  readonly method: MethodName;

  constructor(method: MethodName, code: ErrorCode, message: string, details: unknown = null) {
    super(message);
    this.name = "RpcError";
    this.method = method;
    this.code = code;
    this.details = details;
  }
}

export function isRpcError(error: unknown, code?: ErrorCode): error is RpcError {
  return error instanceof RpcError && (code === undefined || error.code === code);
}

export interface CallOptions<M extends MethodName> {
  /** Events of this request (build progress, git progress, ...). */
  onEvent?: (event: EventMessage<EventsOf<M>>) => void;
  /** Aborting sends rpc.cancel; the call then rejects with code "cancelled". */
  signal?: AbortSignal;
}

type CallArgs<M extends MethodName> = Params<M> extends undefined
  ? [params?: undefined, options?: CallOptions<M>]
  : [params: Params<M>, options?: CallOptions<M>];

interface Pending {
  method: MethodName;
  resolve: (value: unknown) => void;
  reject: (error: RpcError) => void;
  onEvent?: (event: EventMessage) => void;
}

declare global {
  interface Window {
    __TAURI_INTERNALS__?: unknown;
  }
}

export function isTauri(): boolean {
  return typeof window !== "undefined" && window.__TAURI_INTERNALS__ !== undefined;
}

let nextId = 0;
const pending = new Map<RequestId, Pending>();
const subscribers = new Map<EventName, Set<(event: EventMessage) => void>>();
let transportPromise: Promise<Transport> | null = null;
let transportKind: Transport["kind"] | null = null;

function connect(): Promise<Transport> {
  if (!transportPromise) {
    const created: Promise<Transport> = isTauri()
      ? import("./transport-tauri").then((m) => m.createTauriTransport())
      : import("./transport-mock").then((m) => m.createMockTransport());

    transportPromise = created.then(
      (transport) => {
        transportKind = transport.kind;
        transport.onLine(handleLine);
        transport.onClose((reason) => {
          failAll("internal", reason);
          transportPromise = null;
        });
        return transport;
      },
      (error: unknown) => {
        transportPromise = null;
        throw error;
      },
    );
  }
  return transportPromise;
}

function failAll(code: ErrorCode, message: string) {
  const all = [...pending.entries()];
  pending.clear();
  for (const [, request] of all) request.reject(new RpcError(request.method, code, message));
}

function handleLine(line: string) {
  let message: RpcIncomingMessage;
  try {
    message = JSON.parse(line) as RpcIncomingMessage;
  } catch {
    console.warn("rpc: ignored a line that is not JSON", line);
    return;
  }

  if ("event" in message) {
    const event = { event: message.event, data: message.data } as unknown as EventMessage;
    const requestId = message.data?.requestId;
    if (typeof requestId === "number") {
      const request = pending.get(requestId);
      try {
        request?.onEvent?.(event);
      } catch (error) {
        console.error("rpc: onEvent failed", error);
      }
    }
    const listeners = subscribers.get(message.event);
    if (listeners) {
      for (const listener of [...listeners]) {
        try {
          listener(event);
        } catch (error) {
          console.error("rpc: subscriber failed", error);
        }
      }
    }
    return;
  }

  if (typeof message.id !== "number") return;
  const request = pending.get(message.id);
  if (!request) return;
  pending.delete(message.id);

  if (message.error) {
    request.reject(new RpcError(request.method, message.error.code, message.error.message, message.error.details ?? null));
  } else {
    request.resolve(message.result);
  }
}

/** Sends a request. Rejects with RpcError (code from the core, "cancelled" after an abort). */
export function call<M extends MethodName>(method: M, ...args: CallArgs<M>): Promise<Result<M>> {
  const [params, options] = args as [Params<M> | undefined, CallOptions<M> | undefined];
  const signal = options?.signal;

  if (signal?.aborted) return Promise.reject(new RpcError(method, "cancelled", "Cancelled."));

  const id = ++nextId;

  return new Promise<Result<M>>((resolve, reject) => {
    const onAbort = () => {
      void connect()
        .then((transport) => transport.send(JSON.stringify({ id: ++nextId, method: "rpc.cancel", params: { requestId: id } })))
        .catch(() => undefined);
    };

    const cleanup = () => signal?.removeEventListener("abort", onAbort);

    pending.set(id, {
      method,
      resolve: (value) => {
        cleanup();
        resolve(value as Result<M>);
      },
      reject: (error) => {
        cleanup();
        reject(error);
      },
      onEvent: options?.onEvent as ((event: EventMessage) => void) | undefined,
    });

    signal?.addEventListener("abort", onAbort, { once: true });

    connect()
      .then((transport) => transport.send(JSON.stringify(params === undefined ? { id, method } : { id, method, params })))
      .catch((error: unknown) => {
        const request = pending.get(id);
        if (!request) return;
        pending.delete(id);
        request.reject(new RpcError(method, "internal", error instanceof Error ? error.message : String(error)));
      });
  });
}

/** Every event with this name (global and request events). Returns an unsubscribe function. */
export function subscribe<E extends EventName>(event: E, listener: (data: Events[E], message: EventMessage<E>) => void): Unsubscribe {
  let listeners = subscribers.get(event);
  if (!listeners) {
    listeners = new Set();
    subscribers.set(event, listeners);
  }
  const wrapped = (message: EventMessage) => listener((message as EventMessage<E>).data, message as EventMessage<E>);
  listeners.add(wrapped);
  // make sure the core is running so global events can arrive
  void connect().catch(() => undefined);
  return () => {
    listeners?.delete(wrapped);
  };
}

/** "tauri" or "mock" once connected, null before. */
export function currentTransport(): Transport["kind"] | null {
  return transportKind;
}

/** Starts the transport early (optional; call() does it on first use). */
export function connectCore(): Promise<Transport["kind"]> {
  return connect().then((transport) => transport.kind);
}
