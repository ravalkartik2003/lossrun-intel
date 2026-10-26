/** Rule id → name, generated at build time from docs/evaluation/rule-catalog.json (vite.config.ts). */
declare module "virtual:rule-names" {
  const names: Record<string, string>;
  export default names;
}

/** Canonical field path (indices as `[]`) → override value type, generated from docs/evaluation/canonical-schema.json (vite.config.ts). */
declare module "virtual:field-types" {
  const types: Record<string, { kind: "money" | "count" | "string" | "boolean" | "date" | "enum" | "code"; enum?: string[]; codeSystem?: string }>;
  export default types;
}
