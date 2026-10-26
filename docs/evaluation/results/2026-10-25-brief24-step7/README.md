# Brief 24 step 7: the single Azure run (2026-10-25)

The environment was woken (≈ 09:40 UTC), deployed with the gates, evaluated, and hibernated again at 10:44 UTC. Synthetic data only.

**Order:**
1. **Wake.** Terraform: 18 added, 3 changed, 1 replaced.
2. **Index.** `chat init-index`, then `chat reindex --from-api` (4 uploads).
3. **Database.** `lossrun-db deploy`: 0016 rerun, plus 0020 ×3.
4. **Deploy, each followed by its smoke upload:** Functions, the mock quote system, then the web app. All smoke uploads `ReplayAccepted`.
5. **The four chat evals.**
6. **The live review flow.**
7. **Mocks.** Re-captured, then the web redeployed (mock preview and demo app). Demo pack built and walked offline.
8. **Hibernate.** Terraform: 1 added, 3 changed, 18 destroyed; Defender untouched.

| Eval | Folder | Result | Gate | Turn cost |
|---|---|---|---|---|
| `eval chat` (30 numeric questions + 2 probes) | `chat/` | 28/30 exact, 30/30 citations, 0 leaks, injection ignored | ≥ 90 %, all citations, 0 leaks: **pass** | $0.116 |
| `eval chat-text` (10 rubric turns + 10 large-account turns) | `chat-text/` | 10/10, 0 fabrications, large account p50 2.6 s | ≥ 90 %, 0 fabrications: **pass** | $0.035 (+ ≈ $0.03 for the large-account turns, not itemised) |
| `eval chat-corrections` (10, first run) | `chat-corrections/` | 6/10, 0 unverified | ≥ 9/10, 0 unverified: **fail** | $0.030 |
| `eval chat-multiturn` (5 × 8, first run) | `chat-multiturn/` | 30/40, 0 leaks, 0 invalid citations, 0 unverified | ≥ 90 %, 0 leaks, 0 invalid: **fail** | $0.115 |

**Live review flow** (`review-flow/review-flow.json`).
- **Upload.** A synthetic CSV in a fresh quote (`ReplayAccepted`) gave quote revision 1. Its `quote.payload` was delivered to the mock.
- **Decision.** Reopen, then the override decision "Save changes and approve" sends, gave revision 2. Its `quote.payload` (cause `reopen-decided`) was delivered.
- **Payload.** `ETag "q-2"`, `featuresView: verified-only`; `If-None-Match` returned 304; `?revision=1` returned 409 `revision-superseded`.
- **Chat.** `explain_value` answered with the printed value [S1], the current value [C1], the reviewer, the time and the rationale verbatim. 0 unverified, $0.0033.
- **Readiness** is false (5 blocking fields): the correction moved a value away from the printed subtotal, so R-10 keeps those fields unresolved.

**Why the two first-run evals fail.**
- **`chat-corrections`, 3 of the 4 misses are rubric defects:**
  - `who-when` wanted the current value, which was not asked;
  - `chain-latest` wanted the rationale, which was not asked;
  - `unchanged` looked for "not" where the answer says "No … unchanged … no corrections".
- **`chat-corrections`, one is a capability gap:** `who-approved` ("who corrected values on this quote"). `explain_value` needs a claim number, so there is no quote-wide list of corrections.
- **`chat-multiturn`, real reference-resolution misses:**
  - "those two" was resolved to the two carriers instead of the two claims just discussed (two conversations);
  - "of those" (open claims) lost its filter;
  - "its reserve" became the total reserve.
- **`chat-multiturn`, one convention mismatch:** the `open` thread counts reopened claims as open, and the model filtered `status=open`. Leaks, invalid citations and unverified facts were all 0.

**Metered spend.**
- **Documents:** $0.030 for 19 (eval uploads, the five smoke uploads, the review-flow upload).
- **Chat turns:** $0.30 for the eval turns, plus $0.003 for the live check.
- **Standing cost while awake** (Search Basic, Service Bus Standard, Load Testing, about one hour): ≈ $0.14 from list prices.
- **Total:** ≈ $0.50 of the $8 budget.
