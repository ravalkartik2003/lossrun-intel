import * as Tooltip from "@radix-ui/react-tooltip";
import { usd } from "./format.ts";

export interface CostMeterLine {
  service: string;
  meter: string;
  quantity: number;
  unitPrice?: number;
  costUsd: number;
}

/**
 * "$0.034 · 6 pages · 0 model calls" (design-brief §4 CostLine). With meter lines, the line is a button that shows the
 * breakdown on hover or focus; the text alone carries the information for screen readers.
 */
export function CostLine({ usd: amount, pages, modelCalls, lines, label }: { usd: number | null | undefined; pages?: number | null; modelCalls?: number | null; lines?: CostMeterLine[]; label?: string }) {
  const parts = [amount === null || amount === undefined ? "cost —" : usd(amount, amount !== 0 && amount < 0.1 ? 3 : 2)];
  if (pages !== null && pages !== undefined) parts.push(`${pages} page${pages === 1 ? "" : "s"}`);
  if (modelCalls !== null && modelCalls !== undefined) parts.push(`${modelCalls} model call${modelCalls === 1 ? "" : "s"}`);
  const text = parts.join(" · ");
  if (!lines || lines.length === 0) {
    return (
      <span className="lr-costline">
        {label ? <span className="lr-sr-only">{label}: </span> : null}
        {text}
      </span>
    );
  }
  return (
    <Tooltip.Provider delayDuration={200}>
      <Tooltip.Root>
        <Tooltip.Trigger asChild>
          <button type="button" className="lr-costline lr-costline--button" aria-label={`${label ? label + ": " : ""}${text}. Meter breakdown`}>
            {text}
          </button>
        </Tooltip.Trigger>
        <Tooltip.Portal>
          <Tooltip.Content className="lr-tooltip" sideOffset={6}>
            <table className="lr-costline__table">
              <caption className="lr-sr-only">Meter breakdown</caption>
              <thead>
                <tr><th scope="col">Meter</th><th scope="col" className="num">Qty</th><th scope="col" className="num">Cost</th></tr>
              </thead>
              <tbody>
                {lines.map((l, i) => (
                  <tr key={i}>
                    <td>{l.service} · {l.meter}</td>
                    <td className="num">{l.quantity}</td>
                    <td className="num">{usd(l.costUsd, 4)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Tooltip.Content>
        </Tooltip.Portal>
      </Tooltip.Root>
    </Tooltip.Provider>
  );
}
