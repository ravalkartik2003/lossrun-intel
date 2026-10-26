// Canonical JSON writer and integer arithmetic — the twin of src/LossRun.Analytics/Canonical.cs (brief 10 parity).
// Compact output, keys in the order written, strings escaped exactly as the C# writer escapes them, and every number
// printed from an integer at a fixed scale (never from a float), so both engines emit byte-identical JSON.

export class CanonicalWriter {
  private out: string[] = [];
  private needComma: boolean[] = [];
  private afterName = false;

  toString(): string {
    return this.out.join("");
  }

  startObject(): this {
    this.beforeValue();
    this.out.push("{");
    this.needComma.push(false);
    return this;
  }

  endObject(): this {
    this.needComma.pop();
    this.out.push("}");
    return this;
  }

  startArray(): this {
    this.beforeValue();
    this.out.push("[");
    this.needComma.push(false);
    return this;
  }

  endArray(): this {
    this.needComma.pop();
    this.out.push("]");
    return this;
  }

  name(name: string): this {
    this.beforeValue();
    this.writeString(name);
    this.out.push(":");
    this.afterName = true;
    return this;
  }

  string(value: string | null | undefined): this {
    this.beforeValue();
    if (value === null || value === undefined) {
      this.out.push("null");
    } else {
      this.writeString(value);
    }
    return this;
  }

  bool(value: boolean | null | undefined): this {
    this.beforeValue();
    this.out.push(value === null || value === undefined ? "null" : value ? "true" : "false");
    return this;
  }

  null(): this {
    this.beforeValue();
    this.out.push("null");
    return this;
  }

  /** An integer scaled by 10^scale, printed with trailing zeros trimmed (12345, 2 → 123.45). */
  scaled(value: number | null | undefined, scale: number): this {
    this.beforeValue();
    this.out.push(value === null || value === undefined ? "null" : formatScaled(value, scale));
    return this;
  }

  private beforeValue(): void {
    if (this.afterName) {
      this.afterName = false;
      return;
    }
    const n = this.needComma.length;
    if (n > 0) {
      if (this.needComma[n - 1]) {
        this.out.push(",");
      } else {
        this.needComma[n - 1] = true;
      }
    }
  }

  private writeString(value: string): void {
    let s = '"';
    for (let i = 0; i < value.length; i++) {
      const ch = value[i];
      const code = value.charCodeAt(i);
      switch (ch) {
        case '"': s += '\\"'; break;
        case "\\": s += "\\\\"; break;
        case "\b": s += "\\b"; break;
        case "\f": s += "\\f"; break;
        case "\n": s += "\\n"; break;
        case "\r": s += "\\r"; break;
        case "\t": s += "\\t"; break;
        default:
          s += code < 0x20 ? "\\u" + code.toString(16).padStart(4, "0") : ch;
      }
    }
    this.out.push(s + '"');
  }
}

export function formatScaled(value: number, scale: number): string {
  if (!Number.isSafeInteger(value)) {
    throw new RangeError(`not a safe integer: ${value}`);
  }
  if (scale === 0) {
    return String(value);
  }
  const negative = value < 0;
  const magnitude = BigInt(negative ? -value : value);
  const divisor = 10n ** BigInt(scale);
  const whole = magnitude / divisor;
  const fraction = (magnitude % divisor).toString().padStart(scale, "0").replace(/0+$/, "");
  const text = whole.toString() + (fraction.length > 0 ? "." + fraction : "");
  return negative ? "-" + text : text;
}

/** n / d rounded half away from zero; null when d is 0 (exact: BigInt). */
export function roundDiv(n: number | bigint, d: number | bigint): number | null {
  const bn = BigInt(n);
  const bd = BigInt(d);
  if (bd === 0n) {
    return null;
  }
  const negative = (bn < 0n) !== (bd < 0n);
  const an = bn < 0n ? -bn : bn;
  const ad = bd < 0n ? -bd : bd;
  const q = (an * 2n + ad) / (ad * 2n);
  return Number(negative ? -q : q);
}

/** x × bps / 10,000, rounded half away from zero. */
export function mulBps(x: number, bps: number): number {
  return roundDiv(BigInt(x) * BigInt(bps), 10000n) as number;
}

/** Days since 1970-01-01 of an ISO yyyy-MM-dd date; null when absent or not a valid date. */
export function day(iso: string | null | undefined): number | null {
  if (typeof iso !== "string" || iso.length !== 10 || !/^\d{4}-\d{2}-\d{2}$/.test(iso)) {
    return null;
  }
  const y = Number(iso.slice(0, 4));
  const m = Number(iso.slice(5, 7));
  const d = Number(iso.slice(8, 10));
  const t = Date.UTC(y, m - 1, d);
  const back = new Date(t);
  if (back.getUTCFullYear() !== y || back.getUTCMonth() !== m - 1 || back.getUTCDate() !== d || y < 1) {
    return null;
  }
  return t / 86400000;
}

/** Median of whole days in tenths (mean of the two middle values for an even count). */
export function medianTenths(values: number[]): number | null {
  if (values.length === 0) {
    return null;
  }
  values.sort((a, b) => a - b);
  const n = values.length;
  return n % 2 === 1 ? values[(n - 1) / 2] * 10 : (values[n / 2 - 1] + values[n / 2]) * 5;
}

/** Nearest-rank percentile of whole days, in tenths. */
export function percentileTenths(values: number[], percentile: number): number | null {
  if (values.length === 0) {
    return null;
  }
  values.sort((a, b) => a - b);
  const rank = Math.floor((percentile * values.length + 99) / 100);
  return values[Math.max(1, rank) - 1] * 10;
}

/** Ordinal (UTF-16 code unit) string comparison — the same order as C# StringComparer.Ordinal. */
export function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
