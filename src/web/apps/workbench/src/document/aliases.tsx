import { StatusBadge, type Status } from "@lossrun/ui";
import type { AliasDecision, LearnedAlias } from "../api/types.ts";
import { labelOf } from "./model.ts";

/** ADR 0019 lifecycle → the design system's status vocabulary. */
export const ALIAS_STATUS: Record<string, { status: Status; label: string }> = {
  active: { status: "verified", label: "Active" },
  candidate: { status: "learned", label: "Candidate" },
  retired: { status: "kept", label: "Retired" },
};

export function AliasStatusBadge({ status }: { status: string }) {
  const s = ALIAS_STATUS[status] ?? { status: "kept" as Status, label: status };
  return <StatusBadge status={s.status} label={s.label} />;
}

/** Canonical targets a reviewer may edit an alias to (docs/evaluation/canonical-schema.json: the fields a printed column maps to). */
export const ALIAS_TARGETS = [
  "claim.claimNumber", "claim.claimantName", "claim.dateOfInjury", "claim.dateReported", "claim.dateClosed", "claim.status", "claim.claimType",
  "claim.jurisdictionState", "claim.indemnity.paid", "claim.indemnity.reserve", "claim.indemnity.incurred", "claim.medical.paid", "claim.medical.reserve",
  "claim.medical.incurred", "claim.expense.paid", "claim.expense.reserve", "claim.expense.incurred", "claim.total.paid", "claim.total.reserve",
  "claim.total.incurred", "claim.recoverySubrogation", "claim.natureOfInjury.code", "claim.causeOfInjury.code", "claim.partOfBody.code",
  "period.policyNumber", "period.effectiveDate", "period.expirationDate",
] as const;

/** "claim.indemnity.paid" → "Indemnity paid" (the ledger's labels). */
export function fieldLabel(field: string): string {
  return field.startsWith("claim.") ? labelOf(`policyPeriods[0].claims[0].${field.slice(6)}`) : labelOf(field.replace(/^(period|document)\./, ""));
}

export function evidenceText(a: LearnedAlias): string {
  const docs = new Set(a.evidence.map((e) => e.documentId)).size;
  const formats = new Set(a.evidence.map((e) => e.formatRef.split("@")[0])).size;
  return `${docs} document${docs === 1 ? "" : "s"} · ${formats} format${formats === 1 ? "" : "s"}${a.totalsTie ? " · totals tie" : ""}`;
}

export type AliasChoice = { action: AliasDecision["action"]; newField?: string };

/**
 * The confirm-format review's learned aliases (ADR 0019): each printed header the format mapped that the glossary did not
 * know, with accept / edit / reject. Undecided aliases are accepted with the format.
 */
export function AliasReview({ aliases, choices, onChoose }: { aliases: LearnedAlias[]; choices: Record<string, AliasChoice>; onChoose: (key: string, c: AliasChoice) => void }) {
  if (aliases.length === 0) return <p className="lr-meta">This format introduces no new glossary aliases: every column it maps is a glossary alias already.</p>;
  return (
    <div className="aliases" role="group" aria-labelledby="aliases-title">
      <h3 id="aliases-title">Glossary aliases this format introduces</h3>
      <p className="lr-hint" id="aliases-hint">Confirming the format accepts these mappings for every carrier. Edit or reject one that is wrong; corrections later retire it.</p>
      <ul className="aliases__list">
        {aliases.map((a) => {
          const key = `${a.header}|${a.canonicalField}`;
          const choice = choices[key] ?? { action: "accept" };
          const id = `alias-${a.id ?? key}`.replace(/[^A-Za-z0-9_-]/g, "-");
          return (
            <li key={key} className="aliases__item">
              <fieldset className="aliases__fieldset" aria-describedby="aliases-hint">
                <legend>
                  <span className="lr-num">“{a.header}”</span> → {fieldLabel(a.canonicalField)} <span className="lr-meta">({a.canonicalField}; {evidenceText(a)})</span>
                </legend>
                <div className="aliases__choices">
                  {(["accept", "edit", "reject"] as const).map((action) => (
                    <label key={action} className="lr-check-row">
                      <input type="radio" name={id} value={action} checked={choice.action === action} onChange={() => onChoose(key, { action, newField: action === "edit" ? choice.newField ?? a.canonicalField : undefined })} />
                      {action === "accept" ? "Accept" : action === "edit" ? "Map to another field" : "Reject"}
                    </label>
                  ))}
                  {choice.action === "edit" ? (
                    <span className="lr-field aliases__edit">
                      <label className="lr-label" htmlFor={`${id}-field`}>Canonical field for “{a.header}”</label>
                      <select id={`${id}-field`} className="lr-select" value={choice.newField ?? a.canonicalField} onChange={(e) => onChoose(key, { action: "edit", newField: e.target.value })}>
                        {ALIAS_TARGETS.map((f) => <option key={f} value={f}>{fieldLabel(f)} ({f})</option>)}
                      </select>
                    </span>
                  ) : null}
                </div>
              </fieldset>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
