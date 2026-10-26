Loss-run architecture discussion: gap notes
Recorded: 2026-10-24
Status: discussion notes only; no implementation changes made.

1. Field-level review and downstream delivery
Current behavior:
- Individual field corrections already capture old/new values, reviewer, and rationale.
- Approval/rejection is document-level; no explicit per-field approved, unresolved, or rejected state is exposed downstream.
- The quote payload omits field confidence and includes pending/rejected document claims. Review state is included, but exclusion is not enforced.
- Before review completes, quote-linked uploads publish a Service Bus summary with status and a payload URL. The dispatcher forwards that summary, not full claim data.
- Review decisions publish an Event Grid event. They do not automatically replace/retract the earlier Service Bus summary.
Desired direction:
- Represent field-level validation/review status and expose usable versus unresolved values explicitly.
- Allow accepted fields to populate downstream screens; unresolved required fields prevent quote readiness.
- Exclude rejected documents from usable quote data while preserving audit evidence.
- Define downstream event handling and revision-aware refresh behavior.
- Simplify review actions: accept when unchanged; save changes and approve when edited; reset unsaved changes; reject document.
- Address typed-but-not-staged corrections. Hide redundant future-format checkbox for confirm-format reviews and clearly label activation.

2. Chat context handling and production readiness
Current behavior:
- C# ChatService uses Microsoft Agent Framework and the configured Azure OpenAI model; POC configuration selects gpt-5.4-mini.
- Application loads only four prior question/answer turns; older references can be lost.
- Six tool-loop iterations is a separate framework limit, not the conversation-history window.
- Scoped tools, citation-reference checks, usage metering, and audit recording already exist.
Desired direction:
- Improve context selection and summarization while retaining full audit history; retrieve current business facts through tools.
- Evaluate Agent Framework's existing C# SummarizationCompactionStrategy rather than writing summarization from scratch. Microsoft documentation currently labels compaction experimental; verify package compatibility before adoption.
- Consider Foundry-managed conversations if reduced persistence/runtime maintenance warrants migration. Hosting alone does not guarantee summarization or eliminate application permissions and budgets.
- Validate treatment of approved/pending/rejected data, failure recovery, security configuration, and realistic load before calling chat production-ready.

These are the two main gaps requested for the eventual discussion summary. Additional proposed UI and integration refinements are grouped under gap 1.

