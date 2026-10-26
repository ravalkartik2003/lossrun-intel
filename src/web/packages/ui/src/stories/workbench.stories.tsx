import { useMemo, useState } from "react";
import type { Story } from "@ladle/react";
import { createColumnHelper } from "@tanstack/react-table";
import { DataTable, type CellRef } from "../table.tsx";
import { SourceViewer, type Zoom } from "../source.tsx";
import { LineagePanel, type LineageFacts } from "../lineage.tsx";
import { money } from "../format.ts";
import pageUrl from "../../../../../../docs/ui/mock/capture/documents/doc-9997d9e37c0fcb03-p1-1/pages/1.png?url";

// The table, the source viewer and the lineage panel — the parts of the Document workbench — in their states.

interface Row {
  id: string;
  claim: string;
  status: string;
  indPaid: number;
  indRes: number;
  medPaid: number;
  medRes: number;
}
const helper = createColumnHelper<Row>();
const columns = [
  helper.accessor("claim", { header: "Claim", meta: { rowHeader: true } }),
  helper.accessor("status", { header: "Status" }),
  helper.group({ id: "Indemnity", header: "Indemnity", columns: [helper.accessor("indPaid", { header: "Paid", meta: { numeric: true }, cell: (i) => money(i.getValue()) }), helper.accessor("indRes", { header: "Reserve", meta: { numeric: true }, cell: (i) => money(i.getValue()) })] }),
  helper.group({ id: "Medical", header: "Medical", columns: [helper.accessor("medPaid", { header: "Paid", meta: { numeric: true }, cell: (i) => money(i.getValue()) }), helper.accessor("medRes", { header: "Reserve", meta: { numeric: true }, cell: (i) => money(i.getValue()) })] }),
];
const rows = (n: number): Row[] =>
  Array.from({ length: n }, (_, i) => ({ id: String(i), claim: `WC-${22 + (i % 3)}-${String(i).padStart(4, "0")}`, status: i % 4 === 0 ? "Open" : "Closed", indPaid: (i * 7919) % 60000, indRes: (i * 104729) % 9000, medPaid: (i * 1299709) % 25000, medRes: (i * 15485863) % 4000 }));

export const Table: Story<{ density: "dense" | "comfortable"; count: number }> = ({ density, count }) => {
  const [active, setActive] = useState<CellRef | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const data = useMemo(() => rows(count), [count]);
  return (
    <div data-density={density}>
      <DataTable
        data={data}
        columns={columns}
        caption={`Claims (${count}${count >= 200 ? ", virtualized" : ""})`}
        getRowId={(r) => r.id}
        activeCell={active}
        onCellActivate={(c) => setActive(c)}
        selection={{ selected, onChange: setSelected, label: (r) => `Select claim ${r.claim}` }}
        cellClassName={(c) => (c.rowId === "2" && c.columnId === "medPaid" ? "lr-cell--fail" : undefined)}
        emptyText="This policy period printed no claims."
      />
    </div>
  );
};
Table.args = { density: "dense", count: 12 };
Table.argTypes = { density: { options: ["dense", "comfortable"], control: { type: "radio" } }, count: { options: [0, 12, 1000], control: { type: "radio" } } };
Table.storyName = "DataTable — dense / comfortable / empty / 1,000 rows";

export const Source: Story<{ state: "page" | "loading" | "error" }> = ({ state }) => {
  const [zoom, setZoom] = useState<Zoom>("fit");
  const [active, setActive] = useState<string | null>("m");
  return (
    <div style={{ height: 520, maxWidth: 760 }}>
      <SourceViewer
        title="Source: Synthetic Mutual Insurance Co."
        pages={[{ number: 1, src: state === "page" ? pageUrl : null, width: 11, height: 8.5 }]}
        page={1}
        onPageChange={() => undefined}
        regions={[
          { id: "m", page: 1, polygon: [7.0031, 1.5794, 7.8088, 1.5792, 7.809, 1.8037, 7.0027, 1.8037], label: "Medical paid, WC-22-0001: 8,000.00", short: "Medical paid" },
          { id: "s", page: 1, polygon: [7.0027, 2.0271, 7.8091, 2.0269, 7.8091, 2.2503, 7.0032, 2.2503], label: "Printed subtotal: medical paid 9,200.00", short: "Subtotal", tone: "fail" },
        ]}
        activeRegionIds={new Set(active ? [active] : [])}
        onRegionActivate={setActive}
        zoom={zoom}
        onZoomChange={setZoom}
        loading={state === "loading"}
        error={state === "error" ? "The page image could not be loaded." : null}
      />
    </div>
  );
};
Source.args = { state: "page" };
Source.argTypes = { state: { options: ["page", "loading", "error"], control: { type: "radio" } } };
Source.storyName = "SourceViewer — page / loading / error";

const facts: LineageFacts = {
  field: "Medical paid",
  path: "policyPeriods[0].claims[0].medical.paid",
  context: "Claim WC-22-0001 · policy SYN-WC-1001",
  value: "8,000.00",
  sourceText: "8,000.00",
  confidence: 0.986,
  source: "Page 1 · x 7.00–7.81 in, y 1.58–1.80 in",
  format: "synthetic-mutual-pdf@5",
  extractor: "replay",
  rules: [
    { ruleId: "R-01", status: "verified", message: "Paid does not exceed incurred" },
    { ruleId: "R-10", status: "fail", message: "Printed medical paid subtotal for policy SYN-WC-1001 does not equal the sum of its claims." },
  ],
  overrides: [{ at: "2026-10-21 19:52", user: "m.okafor", oldValue: "8,000.00", newValue: "8,100.00", rationale: "matches claim detail page" }],
};

export const Lineage: Story<{ mode: "cell" | "row" | "derived" }> = ({ mode }) => (
  <div style={{ maxWidth: mode === "row" ? 900 : 360, border: "1px solid var(--line)" }}>
    <LineagePanel
      facts={mode === "derived" ? { ...facts, field: "Indemnity incurred", value: "12,000.00", sourceText: null, source: "Not printed — computed", extractor: "derived", derivedFrom: ["Indemnity paid", "Indemnity reserve"], overrides: [] } : facts}
      onClose={() => undefined}
      autoFocus={false}
      rowSummary={mode === "row" ? "Row WC-22-0001 · 11 values traced · ←/→ to step" : null}
      position={{ index: 3, total: 11, onPrev: () => undefined, onNext: () => undefined }}
    />
  </div>
);
Lineage.args = { mode: "cell" };
Lineage.argTypes = { mode: { options: ["cell", "row", "derived"], control: { type: "radio" } } };
Lineage.storyName = "LineagePanel — cell / row strip / derived value";
