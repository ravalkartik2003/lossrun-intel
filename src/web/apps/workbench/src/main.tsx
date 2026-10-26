import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "@tanstack/react-router";
import "@fontsource/public-sans/400.css";
import "@fontsource/public-sans/500.css";
import "@fontsource/public-sans/600.css";
import "@lossrun/ui/src/tokens.css";
import "@lossrun/ui/src/base.css";
import "@lossrun/ui/src/components.css";
import "./app.css";
import { AUTH_MODE } from "./env.ts";
import { router } from "./router.tsx";
import { HttpError } from "./api/client.ts";
import { workerSnapshot } from "./analytics/useWhatIf.ts";

(window as unknown as { __lossrun: unknown }).__lossrun = { snapshot: workerSnapshot };

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      retry: (count, error) => !(error instanceof HttpError && error.status >= 400 && error.status < 500) && count < 2,
      refetchOnWindowFocus: false,
    },
  },
});

async function start() {
  // import.meta.env directly (not the MOCK constant) so a live build drops the mock chunk and its captured data.
  if (import.meta.env.VITE_MOCK === "1") {
    const { startMock } = await import("./mock/browser.ts");
    await startMock();
  }
  if (AUTH_MODE === "entra") {
    const { initEntra } = await import("./auth/msal.ts");
    await initEntra();
  }
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </StrictMode>,
  );
}

void start();
