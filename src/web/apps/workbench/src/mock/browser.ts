import { setupWorker } from "msw/browser";
import { handlers } from "./handlers.ts";

/** Starts the mock API (service worker). Only imported when VITE_MOCK=1, so live builds carry no mock code. */
export async function startMock(): Promise<void> {
  const worker = setupWorker(...handlers);
  await worker.start({ onUnhandledRequest: "bypass", quiet: true, serviceWorker: { url: `${import.meta.env.BASE_URL}mockServiceWorker.js` } });
}
