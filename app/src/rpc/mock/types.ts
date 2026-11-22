// What mock handlers (mock/shell.ts, mock/project.ts) are made of.
import type { ErrorCode, EventName, RequestId } from "../types";

export interface MockContext {
  /** Emits an event of this request; requestId is added to data. */
  emit(event: EventName | string, data: unknown): void;
  /** Emits an event that belongs to no request (watch.changed). */
  emitGlobal(event: EventName | string, data: unknown): void;
  requestId: RequestId;
  /** Aborted by rpc.cancel. */
  signal: AbortSignal;
}

export type MockHandler = (params: any, ctx: MockContext) => Promise<unknown>;

/** Throw this from a handler to answer with an RPC error. */
export class MockError extends Error {
  readonly code: ErrorCode;
  readonly details: unknown;

  constructor(code: ErrorCode, message: string, details: unknown = null) {
    super(message);
    this.code = code;
    this.details = details;
  }
}

/** Waits like the real core would, rejecting with "cancelled" when the signal aborts. */
export function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new MockError("cancelled", "Cancelled."));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(new MockError("cancelled", "Cancelled."));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}
