import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { Toast } from "@heroui/react";
import { App } from "./App";
import { ConfirmHost } from "./components/confirm";
import { notifyError } from "./components/notify";
import "./styles/app.css";

// Errors become toasts with the error code and message. A query or mutation opts out with meta: { silent: true }
// (for example when the view shows the error inline). Cancelled requests never toast.
const queryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (query.meta?.silent === true) return;
      notifyError(error);
    },
  }),
  mutationCache: new MutationCache({
    onError: (error, _variables, _context, mutation) => {
      if (mutation.meta?.silent === true) return;
      notifyError(error);
    },
  }),
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 5_000 },
  },
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <App />
      <ConfirmHost />
      <Toast.Provider placement="bottom end" width={400} maxVisibleToasts={4} />
    </QueryClientProvider>
  </StrictMode>,
);
