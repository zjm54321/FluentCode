// Only the Node surface used by this dependency-free package.
declare module "node:process" {
  const process: { env: Record<string, string | undefined>; pid: number };
  export default process;
}
declare module "node:fs/promises" {
  export function readFile(path: string, encoding: "utf8"): Promise<string>;
  export function mkdir(path: string, options: { recursive: true }): Promise<unknown>;
  export function writeFile(path: string, text: string, options: { encoding: "utf8"; flag: "wx"; mode: number }): Promise<void>;
  export function rename(from: string, to: string): Promise<void>;
  export function rm(path: string, options: { force: true }): Promise<void>;
}
declare module "node:os" { export function homedir(): string; }
declare module "node:path" {
  export function dirname(path: string): string;
  export function join(...paths: string[]): string;
}
declare module "node:crypto" { export function randomUUID(): string; }
