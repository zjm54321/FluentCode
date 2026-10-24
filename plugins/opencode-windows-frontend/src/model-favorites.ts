import { mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { randomUUID } from "node:crypto";
import process from "node:process";
import { definition, parseInput, type ModelFavorites } from "./protocol.ts";

export function modelStatePath(): string {
  return join(process.env.XDG_STATE_HOME || join(homedir(), ".local", "state"), "opencode", "model.json");
}

async function readState(path: string): Promise<string> {
  try { return await readFile(path, "utf8"); }
  catch (error) {
    if ((error as { code?: string }).code === "ENOENT") return "{}";
    throw error;
  }
}

function root(text: string): Record<string, unknown> {
  const value: unknown = JSON.parse(text);
  if (value === null || typeof value !== "object" || Array.isArray(value))
    throw new TypeError("Invalid model state root");
  return value as Record<string, unknown>;
}

// JSON.parse validates first. Locate only top-level member boundaries so every
// other value (including large numbers and unknown future fields) stays byte-for-byte intact.
export function replaceFavorite(text: string, favorites: ModelFavorites): string {
  root(text);
  const start = text.indexOf("{");
  const end = text.lastIndexOf("}");
  let depth = 1, quoted = false, escaped = false, member = start + 1;
  const replacements: { start: number; end: number }[] = [];
  for (let i = start + 1; i <= end; i++) {
    const ch = text[i];
    if (quoted) {
      if (escaped) escaped = false;
      else if (ch === "\\") escaped = true;
      else if (ch === '"') quoted = false;
      continue;
    }
    if (ch === '"') { quoted = true; continue; }
    if ((ch === "," && depth === 1) || i === end) {
      const fragment = text.slice(member, i);
      const key = /^\s*("(?:[^"\\]|\\.)*")\s*:\s*/u.exec(fragment);
      if (key && JSON.parse(key[1]!) === "favorite") {
        replacements.push({ start: member + key[0].length, end: i - (fragment.match(/\s*$/u)?.[0].length ?? 0) });
      }
      member = i + 1;
    }
    if (ch === "{" || ch === "[") depth++;
    else if (ch === "}" || ch === "]") depth--;
  }
  const value = JSON.stringify(favorites.favorites);
  if (replacements.length) {
    for (const span of replacements.reverse()) text = text.slice(0, span.start) + value + text.slice(span.end);
    return text;
  }
  return text.slice(0, end) + (Object.keys(root(text)).length ? "," : "") + '"favorite":' + value + text.slice(end);
}

export async function getModelFavorites(path = modelStatePath()): Promise<ModelFavorites> {
  const state = root(await readState(path));
  return parseInput(definition.methods["model.favorites.get"].output, { favorites: state.favorite ?? [] });
}

export async function setModelFavorites(input: unknown, path = modelStatePath()): Promise<ModelFavorites> {
  const favorites = parseInput(definition.methods["model.favorites.set"].input, input);
  const text = replaceFavorite(await readState(path), favorites);
  await mkdir(dirname(path), { recursive: true });
  const temp = `${path}.${process.pid}.${randomUUID()}.tmp`;
  try {
    await writeFile(temp, text, { encoding: "utf8", flag: "wx", mode: 0o600 });
    await rename(temp, path);
  } finally { await rm(temp, { force: true }); }
  return favorites;
}
