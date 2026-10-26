# Glossary learning: results (2026-10-24, brief 21, ADR 0019)

ADR 0019 makes a learned header mapping a **learned alias**:
- a **candidate**, never used, when a verified learn maps a header the glossary does not know;
- **active** when a reviewer confirms its format, or when it has verified on 3 documents across 2 formats;
- **retired** when a correction of a value read through it, or a rejection, removes it.

Two experiments test whether vocabulary grows through that loop without code changes. Both remove hand aliases from the seed glossary (`LOSSRUN_GLOSSARY_ABLATE`, `scoring-protocol.md` §8).

## Files

| Path | What it is |
|---|---|
| `ablate-synthetic.txt` | The five hand aliases removed for the synthetic run |
| `deck-table.md`, `report.md`, `documents.csv`, `run.json` | The corpus v4 run with those aliases removed. `run.json` records `glossaryAblation` and `learnedAliasesAtEnd` |
| `ablate-real.txt` | The 22 brief 17 aliases removed for the real-sample run |
| `glossary.learned.json` | `lossrun glossary export --run …`: every learned alias at the end of that run, for review |

## 1. Synthetic: five hand aliases removed, corpus v4 (326 documents)

The five aliases are printed by learned formats and by the hand-spec carriers. With them removed, the seed glossary no longer resolves those headers. They come back only if a verified learn maps them, and they are used only after activation.

| Removed hand alias | Came back as | Introduced by | Evidence at the end | Activated |
|---|---|---|---|---|
| Claimant → claim.claimantName | **active**, same field | the c5 CSV learn | 129 documents, 4 formats | verified on 3 documents across 3 formats |
| Reported → claim.dateReported | **active**, same field | the c4 XLSX learn | 122 documents, 3 formats | 3 documents across 3 formats |
| Loss Date → claim.dateOfInjury | **active**, same field | the c4 XLSX learn | 85 documents, 3 formats | 3 documents across 3 formats |
| Claim Number → claim.claimNumber | **active**, same field | the c5 CSV learn | 46 documents, 2 formats | 3 documents across 2 formats |
| Claim Status → claim.status | **active**, same field | the c5 CSV learn | 46 documents, 2 formats | 3 documents across 2 formats |

**Result:**
- All five came back through the loop, each to the field the hand glossary had, and each became active through evidence alone, with no reviewer.
- Besides them the run learned 34 more aliases:
  - **7 are active, all correct:**
    - "Closed" → close date;
    - "State" → claim state;
    - "Ind. Res" and "Med. Res" → reserves;
    - "Exp Res" → expense reserve;
    - "Pol Eff" and "Pol Exp" → period dates.
  - **27 stay candidates,** printed by one format only and so never used. They include the stacked composites ("Claim Number Claim Type"), which a single format introduces with both parts.
- **No active alias maps to a wrong field.**

**The deck table reproduces** (§8), against `../2026-10-23-corpus-v3/deck-table.md` and brief 20's run:
- every v3 row with n ≥ 5 is unchanged;
- the only difference from the committed table is fax pre-fill at 89.0 % against 89.1 %, within CI, the same as brief 20's run;
- the n = 1 first-seen document was delivered (2 calls);
- construct rows: c6 6/6, c7 6/6, c8 5/6, c9 6/6 delivered, all at 100.0 % key-field accuracy;
- all 9 source-inconsistent (planted) documents were kept out of straight-through; classification was 100 / 100;
- no delivered value is wrong.

The table being unchanged is the point: removing five common hand aliases cost nothing that the learn loop did not recover.

Spend: $0.57 in model calls, plus Content Understanding for the 10 password-locked PDFs (their encryption salt changes on every generation).

## 2. Real samples: the 22 brief 17 aliases removed (17 files, local runner)

Run 7 is the local runner on all 17 redacted real samples, with Content Understanding from the cache, against run 4 (brief 19). It removes all 22 aliases brief 17 added from the real samples (`ablate-real.txt`; the headers are already in the committed glossary and are not document values). The table gives counts only:

| Outcome for the 22 removed aliases | Count |
|---|---:|
| Re-learned as a **candidate**, to the right field, by a verified learn (Starr: period effective date, close date, accident description, recovery) | 4 |
| Read correctly without an alias: the composite grammar resolves it, and Starr's published spec maps it | 1 |
| Mapped to the right field in an unverified learn's best attempt. No candidate, because only a verified learn records one | 5 |
| Mapped to a neighbouring field (St Charles: open/closed counts to period subtotals rather than report totals) | 2 |
| Not mapped. Mostly alvin, whose scanned segments are review-only and never learned | 10 |

**Routes, run 4 → run 7:**
- **Starr:** review → **learned in 2 calls** (4 calls in brief 19 with the hand aliases).
- **Three files lost a segment:** collin, cccta, and one alvin segment. They now go "no loss table found" because their header resolution fell below the segment threshold without the removed aliases. None of the three can learn (150 dpi or form-like), so no verified learn brings the aliases back.
- **Every other file** routes as before.
- **Nothing was delivered wrong.**

**What this shows:**
- **On the real samples, vocabulary grows only as fast as learns verify.** 5 of the 22 aliases came back without a hand entry (4 as candidates, 1 by grammar). The aliases tied to documents that never verify (scans, other lines of business, unstable learns) cannot come back through ADR 0019's verified-learn rule.
- **The rule works as designed.** An unverified best attempt mapped 5 more correctly, but an unverified mapping is exactly what ADR 0019 refuses to record.
- **Dependence on hand aliases.** The three lost segments show that segment detection on scans depends on hand aliases. For classification, a candidate alias would not help either: it is never used.
- **No real alias became active:** every real format is its carrier's only one, so nothing reaches 2 formats. On the tuning set, the confirm-format review is the path to active.

Spend: $0.72 in model calls (Content Understanding fully cached).
