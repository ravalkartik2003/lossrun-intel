# 8. Transparency note

*For underwriters, UW managers and reviewers. It follows the shape of Microsoft's transparency notes: what the system does, how well, where it does not work, and how to use it well.*

## What it does

**Reads loss runs.**
- It reads workers' compensation loss runs from carriers into one standard claims record: claim number, dates, status, type, indemnity / medical / expense paid, reserve and incurred, codes, litigation, recoveries.
- **Every number carries its source.** Select it and the screen draws a line to the printed cell on the page, or in the workbook.

**Handles each carrier's layout.**
- The first time it sees a layout, an AI model writes a reading plan for it (a "format"). The system checks the plan against the document's own printed totals before using it.
- Every later report with that layout is read by replaying the plan, **with no AI model involved**: the same input always gives the same output.

**Checks every document.**
- 26 validation rules run on every document: dates in order, paid ≤ incurred, printed totals equal the sum of the rows, and so on.
- A document that fails a hard rule waits for a person in the review queue.

**Analytics, scenarios and chat.**
- It computes 41 account metrics and lets you try scenarios: reserve adjustment, trend, caps, exclusions.
- A chat assistant answers questions about the quote's documents, citing where each fact came from.

## What it does not do

- It does not price, decide eligibility, or make decisions about claimants. You do.
- It does not "fix" a carrier's printed numbers. When a printed subtotal disagrees with the rows, it flags the disagreement. If you correct a value, your correction is recorded with your name and reason.
- Chat is not a source of truth. Its numbers come from the same record you see, with a citation. When the documents do not answer, it says so.

## How well it works (synthetic evidence; blind-set numbers to follow)

| Input | Delivered without review | Accuracy of delivered key fields |
|---|---|---|
| Native PDF from a known carrier | 97 % | 100.0 % |
| First report from a new carrier (native PDF) | 5 of 5 | 100.0 % |
| Excel / CSV export | 99 % | 100.0 % |
| Clean scan (≥ 300 dpi) | 96 % | 100.0 % |
| Rotated or noisy scan (~200 dpi) | 71 % | 100.0 % (pre-fill accuracy for the rest: 99.8 %) |
| Fax (< 200 dpi), narrative claim statements | 0 % (always reviewed) | pre-fill only |

These figures come from 332 generated documents of five fictional carriers (corpus v5, the run the prompt gate holds). Real loss runs are messier. The numbers that count come from the sealed blind test on State Fund's own archive.

## Where it does not work well

- **Faxes and low-resolution scans:** always reviewed; the pre-fill is about 89 % accurate at fax quality.
- **Narrative claim statements** (prose rather than a table): always reviewed.
- **The first report from a new carrier:** it takes about a minute and may need review if the check fails.
- **Values a carrier does not print** (e.g. class codes on these samples): shown as "not printed", never guessed. Metrics that need them say "not measurable" and why.

## Using it well

- When the badge says **Format learned · shadow**, confirm the first few documents of that carrier.
- **Read the red outlines.** They are the rule that failed and the cell it failed on.
- **Give a reason with every correction.** It is part of the permanent record.
- **Check a chat answer through its citation chip** before relying on it.

## Data

- The system keeps your documents and results inside State Fund's Azure environment (US).
- AI models run as Microsoft Azure OpenAI services under Microsoft's data-processing terms. Your documents are not used to train them.
