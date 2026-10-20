import process from "node:process";

export type BillionOrigin =
  | { protocolVersion: 1; status: "ready"; origin: string }
  | { protocolVersion: 1; status: "unavailable"; reason: "not-ready" | "disabled" };

export function normalizeOrigin(value: unknown): string | undefined {
  if (typeof value !== "string" || value.length > 256) return undefined;
  const match = /^http:\/\/(localhost|127\.0\.0\.1|\[::1\]):([1-9][0-9]{0,4})\/?$/.exec(value);
  // JavaScript's $ also matches before a final newline; require the whole raw string.
  if (!match || match[0] !== value || Number(match[2]) > 65535) return undefined;
  return `http://${match[1]}:${match[2]}`;
}

export function billionOrigin(): BillionOrigin {
  if (process.env.BILLION_CONTEXT_PLUGIN === "0") {
    return { protocolVersion: 1, status: "unavailable", reason: "disabled" };
  }
  const origin = normalizeOrigin(process.env.BILLION_CONTEXT_PROXY);
  return origin === undefined
    ? { protocolVersion: 1, status: "unavailable", reason: "not-ready" }
    : { protocolVersion: 1, status: "ready", origin };
}
