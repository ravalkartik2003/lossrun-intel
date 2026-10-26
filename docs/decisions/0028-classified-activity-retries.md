# ADR 0028 — Retry only what is recoverable: classified activity retries, reuse before re-bill

**Status:** accepted (brief 33) · **Date:** 2026-10-26

## Context

Brief 30 item 32 gave every storage activity one Durable retry policy: three attempts at 5 s and 10 s, for **every** exception. That policy had four problems:

- **Permanent errors were retried.** A missing graph, a format exception on a fingerprint hit or a cast error each cost three attempts, 15 s of waits and three error logs before the orchestration saw one defect.
- **Transients were already retried below it, by every lower layer:**
  - the Blob read-modify-write loop;
  - the Azure SDK pipelines;
  - EF's execution strategy on SQL;
  - the embedding client;
  - the learn model.

  So a Durable retry mostly re-ran what had already been given up on.
- **The paid paths were not safe to re-run at all.** ParseFile billed every page again when Durable re-delivered it after a worker loss, and Route re-embedded the fingerprint text on every attempt.
- **Retries could duplicate one event.** A retried persist sent `format.learned` twice.

The capture in brief 33 (`tools/capture-failures`, fixture `src/LossRun.Orchestration.Tests/fixtures/captured-failures.json`) also showed something the brief had assumed otherwise: the failure details of a `SqlException` carry only the server's message, not its error number. A message regex on the number could never match. The number has to travel as a failure-details property.

## Decision

- **Classify, then retry.** `config/retry-policy.json` (embedded, versioned) lists transient and permanent rules: a type, narrowed by a message regex, a property value, or the inner failure. Every regex and property was written from a captured failure, not from memory.
  - The retry handler (`ActivityRetry.Storage`) retries only `Transient`: up to `maxAttempts` (3), at 5 s × 2, within `maxTotalSeconds` (60).
  - `Permanent` and `Unknown` fail on the first attempt, with "permanent: InvalidOperationException — …" in the status and the class in the escalation.
  - The policy reaches the orchestrators in their input (`UploadInput.Retry`, `DocumentWork.Retry`), because an orchestrator replays and never reads a file.
- **Unknown is not transient.** The lower layers already had their chance. An unknown type reaches the Needs-investigation queue keyed by its type (`unhandled-exception`, or `decision-apply-failed`), and the list grows from that evidence. `unknownIsTransient` stays false.
- **The SQL error number is a property.** The Functions host registers `IExceptionPropertiesProvider` (`FailureProperties` → `ExceptionProperties.Of`), which adds `sqlErrorNumber`. The capture shows it arriving in the orchestrator's `TaskFailureDetails.Properties`, on an inner failure too.
- **Reuse before re-bill.**
  - ParseFile reuses a stored CU result when its metadata names the same analyzer and the same bytes (SHA-256). It records the usage rows with quantity 0 and stage `reused`. Only then does ParseFile get the retry handler.
  - Route keeps the fingerprint embedding per document (`fingerprint.json`: vector, model, normalization version, text hash). Only a real call is metered.
- **CU retries per HTTP request, in the client's pipeline** (`ContentUnderstanding__MaxAttempts`, default 5; Retry-After honoured; 60 s cap). That retries a 429 on submit and a 5xx on a poll without ever re-submitting the analyze. A loop around the SDK's `AnalyzeBinaryAsync(WaitUntil.Completed)` would re-submit after a failed poll, so there is none.
- **Learn and Delta are never retried at the Durable layer.** Their own cap and 429 loop are their policy, and a test fails if they are given retry options.
- **Duplicates are keyed.** `format.learned` is keyed on (format, document) and written only when absent. `ApplyDecision` lets a transient failure through to the handler, so a SQL resume during a decision is retried instead of escalated; the decision key makes the retry safe.

## Consequences

- **Attempt counts for a permanent failure:** Route with a missing graph went from 3 to 1, and a format exception on a fingerprint hit from 3 to 1. Each had 15 s of waits; now there are none (brief 33 report).
- **A Durable re-delivery of ParseFile is free.** A retry of Route makes no embedding call.
- **New failure types land in Unknown first.** Somebody has to add them to the policy, with the captured failure as the evidence (runbook §Escalations). Until then they fail fast, which is the intended cost of not guessing.
- **When a package changes,** re-run `tools/capture-failures` whenever a Durable, Functions worker, Azure.Core, SqlClient or EF version changes: the failure-details shape is theirs, not ours.
