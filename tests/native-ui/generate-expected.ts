// Exact lang-core 1.1.0 oracle; run with Bun from the FluentCode lane, passing the QoL lane.
import { createHash } from "node:crypto";
import path from "node:path";
import { pathToFileURL } from "node:url";

const qol = path.resolve(process.argv[2] ?? process.env.NUI_QOL_ROOT ?? "/home/ming/opencode-qol-lanes/native-ui");
const core = await import(pathToFileURL(path.join(qol, "kernel/node_modules/@openuidev/lang-core/dist/index.mjs")).href);
const { library, librarySchema } = await import(pathToFileURL(path.join(qol, "kernel/plugins/native-ui/src/library.ts")).href);
const folder = path.join(import.meta.dir, "fixtures");
const names = [...new Bun.Glob("*.oui").scanSync(folder)].sort();
for (const name of names) {
  const source = await Bun.file(path.join(folder, name)).text();
  const parser = core.createParser(librarySchema, library.root);
  const parsed = parser.parse(source);
  const ctx = {
    getState: (key: string) => parsed.stateDeclarations[key] ?? null,
    resolveRef: (key: string): unknown => {
      const query = parsed.queryStatements.find((item: any) => item.statementId === key);
      if (query) return query.defaultsAST ? core.evaluate(query.defaultsAST, ctx) : null;
      return parsed.mutationStatements.some((item: any) => item.statementId === key) ? { status: "idle" } : null;
    },
  };
  // Query cycles are parse errors; do not attempt to evaluate their cyclic defaults.
  const evaluated = parsed.meta.errors.some((error: any) => error.code === "query-cycle") ? null
    : core.evaluateRoot(parsed, { ctx, library, store: null });
  const stream = core.createStreamingParser(librarySchema, library.root);
  const lengths = [...new Set([0, 1, Math.floor(source.length / 3), Math.floor(source.length * 2 / 3), source.length])].sort((a, b) => a - b);
  const streaming = lengths.map(length => ({ length, result: stream.set(source.slice(0, length), length === source.length) }));
  const expected = { parserVersion: "1.1.0", sha256: createHash("sha256").update(source).digest("hex"), parse: parsed, evaluated, streaming };
  await Bun.write(path.join(folder, name.replace(/\.oui$/, ".expected.json")), JSON.stringify(expected, null, 2) + "\n");
}
console.log(`lang-core 1.1.0: generated ${names.length} parse/runtime/streaming fixtures`);
