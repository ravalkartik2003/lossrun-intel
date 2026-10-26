import { describe, expect, it } from "vitest";
import { QueryClient } from "@tanstack/react-query";
import { invalidateAfterDecision } from "./queries.ts";

// Brief 30 item 23: a decision / override / reopen must invalidate the review, the quote payload and the analytics
// reads, with the keys the hooks in queries.ts actually use.

describe("invalidateAfterDecision", () => {
  it("invalidates the review, document, audit, quote payload and analytics queries, and nothing unrelated", async () => {
    const client = new QueryClient();
    const keys: unknown[][] = [
      ["reviews", "pending"],
      ["reviews", "decided"],
      ["document", "doc-1"],
      ["audit", "document", "doc-1"],
      ["audit", "quote", "Q-1"],
      ["aliases", "all"],
      ["quote-payload", "Q-1"],
      ["claims", "Q-1", "adjusted"],
      ["claims", "Q-1", "reported"],
      ["features", "Q-1", "adjusted"],
      ["features", "Q-1", "reported"],
      ["quotes"],
      ["upload", "up-1"],
    ];
    const untouched: unknown[][] = [["document", "doc-2"], ["formats"], ["costs"], ["me"], ["catalog"]];
    for (const k of [...keys, ...untouched]) client.setQueryData(k, { seeded: true });

    await invalidateAfterDecision(client, "doc-1");

    for (const k of keys) expect(client.getQueryState(k)?.isInvalidated, JSON.stringify(k)).toBe(true);
    for (const k of untouched) expect(client.getQueryState(k)?.isInvalidated, JSON.stringify(k)).toBe(false);
  });
});
