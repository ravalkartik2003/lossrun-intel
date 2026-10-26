# All-fields study

Every field the scorer compares on **delivered** documents (every field the layout prints, by the truth's `printed` list), not only the
key fields of the deck table (`docs/evaluation/scoring-protocol.md` §8). On a synthetic corpus the expected result is **0 wrong**.

**Delivered values compared: 179,409 · wrong: 0** (caught by a hard rule 0, silent 0) · missing 42,627

| Field | Compared | OK | Missing | Wrong (caught) | Wrong (silent) |
|---|---:|---:|---:|---:|---:|
| `claim.accidentDescription` | 1,364 | 1,364 | 0 | 0 | 0 |
| `claim.causeOfInjury` | 2,519 | 2,463 | 56 | 0 | 0 |
| `claim.claimNumber` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.claimType` | 5,159 | 3,945 | 1,214 | 0 | 0 |
| `claim.claimantName` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.dateClosed` | 6,896 | 5,002 | 1,894 | 0 | 0 |
| `claim.dateOfInjury` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.dateReported` | 10,757 | 7,738 | 3,019 | 0 | 0 |
| `claim.expense.incurred` | 1,374 | 1,318 | 56 | 0 | 0 |
| `claim.expense.paid` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.expense.reserve` | 2,921 | 2,865 | 56 | 0 | 0 |
| `claim.indemnity.incurred` | 1,374 | 1,318 | 56 | 0 | 0 |
| `claim.indemnity.paid` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.indemnity.reserve` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.jurisdictionState` | 2,732 | 2,676 | 56 | 0 | 0 |
| `claim.lastDayWorked` | 2,519 | 2,463 | 56 | 0 | 0 |
| `claim.lineOfBusiness` | 1,364 | 1,364 | 0 | 0 | 0 |
| `claim.litigationFlag` | 2,519 | 2,463 | 56 | 0 | 0 |
| `claim.medical.incurred` | 1,374 | 1,318 | 56 | 0 | 0 |
| `claim.medical.paid` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.medical.reserve` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.natureOfInjury` | 2,519 | 2,463 | 56 | 0 | 0 |
| `claim.partOfBody` | 1,155 | 1,099 | 56 | 0 | 0 |
| `claim.recoverySubrogation` | 2,519 | 2,463 | 56 | 0 | 0 |
| `claim.status` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.total.incurred` | 11,302 | 8,250 | 3,052 | 0 | 0 |
| `claim.total.paid` | 402 | 402 | 0 | 0 | 0 |
| `claim.total.reserve` | 4,234 | 2,728 | 1,506 | 0 | 0 |
| `document.carrierName` | 326 | 239 | 87 | 0 | 0 |
| `document.insuredName` | 286 | 197 | 89 | 0 | 0 |
| `document.reportRunDate` | 222 | 143 | 79 | 0 | 0 |
| `document.valuationDate` | 332 | 245 | 87 | 0 | 0 |
| `grandTotal.claimCount` | 240 | 147 | 93 | 0 | 0 |
| `grandTotal.closedCount` | 110 | 77 | 33 | 0 | 0 |
| `grandTotal.expense.incurred` | 6 | 6 | 0 | 0 | 0 |
| `grandTotal.expense.paid` | 234 | 157 | 77 | 0 | 0 |
| `grandTotal.expense.reserve` | 6 | 6 | 0 | 0 | 0 |
| `grandTotal.indemnity.incurred` | 6 | 6 | 0 | 0 | 0 |
| `grandTotal.indemnity.paid` | 234 | 157 | 77 | 0 | 0 |
| `grandTotal.indemnity.reserve` | 234 | 157 | 77 | 0 | 0 |
| `grandTotal.medical.incurred` | 6 | 6 | 0 | 0 | 0 |
| `grandTotal.medical.paid` | 234 | 157 | 77 | 0 | 0 |
| `grandTotal.medical.reserve` | 234 | 157 | 77 | 0 | 0 |
| `grandTotal.openCount` | 110 | 77 | 33 | 0 | 0 |
| `grandTotal.total.incurred` | 246 | 157 | 89 | 0 | 0 |
| `grandTotal.total.paid` | 18 | 6 | 12 | 0 | 0 |
| `grandTotal.total.reserve` | 118 | 70 | 48 | 0 | 0 |
| `period.effectiveDate` | 995 | 735 | 260 | 0 | 0 |
| `period.expirationDate` | 995 | 735 | 260 | 0 | 0 |
| `period.policyNumber` | 995 | 735 | 260 | 0 | 0 |
| `subtotal.claimCount` | 839 | 566 | 273 | 0 | 0 |
| `subtotal.closedCount` | 330 | 240 | 90 | 0 | 0 |
| `subtotal.expense.incurred` | 18 | 18 | 0 | 0 | 0 |
| `subtotal.expense.paid` | 702 | 480 | 222 | 0 | 0 |
| `subtotal.expense.reserve` | 18 | 18 | 0 | 0 | 0 |
| `subtotal.indemnity.incurred` | 18 | 18 | 0 | 0 | 0 |
| `subtotal.indemnity.paid` | 702 | 480 | 222 | 0 | 0 |
| `subtotal.indemnity.reserve` | 702 | 480 | 222 | 0 | 0 |
| `subtotal.medical.incurred` | 18 | 18 | 0 | 0 | 0 |
| `subtotal.medical.paid` | 702 | 480 | 222 | 0 | 0 |
| `subtotal.medical.reserve` | 702 | 480 | 222 | 0 | 0 |
| `subtotal.openCount` | 485 | 356 | 129 | 0 | 0 |
| `subtotal.total.incurred` | 857 | 596 | 261 | 0 | 0 |
| `subtotal.total.paid` | 54 | 18 | 36 | 0 | 0 |
| `subtotal.total.reserve` | 354 | 210 | 144 | 0 | 0 |

## Wrong delivered values

None.
