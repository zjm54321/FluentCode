import { normalizeOrigin, type BillionOrigin } from "./billion-origin.ts";

export const PLUGIN_ID = "opencode.windows-frontend";
export type HelloInput = { protocolVersions: number[] };
export type HelloOutput =
  | { protocolVersion: 1; capabilities: ["billion.origin.v1"] }
  | { protocolVersion: null; supportedVersions: [1]; capabilities: [] };

type Validation<T> = { value: T } | { issues: readonly { message: string }[] };
export interface PortableSchema<T> {
  readonly "~standard": {
    readonly version: 1;
    readonly vendor: string;
    readonly validate: (input: unknown) => Validation<T>;
  };
}

function schema<T>(name: string, valid: (value: unknown) => value is T): PortableSchema<T> {
  return {
    "~standard": {
      version: 1,
      vendor: "opencode-windows-frontend-plugin",
      validate: (value) => valid(value) ? { value } : { issues: [{ message: `Invalid ${name}` }] },
    },
  };
}

function exactObject(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    && Object.keys(value).length === keys.length
    && keys.every((key) => Object.hasOwn(value, key));
}

function isHelloInput(value: unknown): value is HelloInput {
  return exactObject(value, ["protocolVersions"])
    && Array.isArray(value.protocolVersions)
    && value.protocolVersions.length <= 8
    && Array.from(value.protocolVersions).every((version) => Number.isInteger(version) && version > 0);
}

function isHelloOutput(value: unknown): value is HelloOutput {
  if (exactObject(value, ["protocolVersion", "capabilities"]) && value.protocolVersion === 1) {
    return Array.isArray(value.capabilities) && value.capabilities.length === 1
      && value.capabilities[0] === "billion.origin.v1";
  }
  return exactObject(value, ["protocolVersion", "supportedVersions", "capabilities"])
    && value.protocolVersion === null
    && Array.isArray(value.supportedVersions) && value.supportedVersions.length === 1
    && value.supportedVersions[0] === 1
    && Array.isArray(value.capabilities) && value.capabilities.length === 0;
}

function isBillionOrigin(value: unknown): value is BillionOrigin {
  if (exactObject(value, ["protocolVersion", "status", "origin"])) {
    return value.protocolVersion === 1 && value.status === "ready"
      && typeof value.origin === "string" && normalizeOrigin(value.origin) === value.origin;
  }
  return exactObject(value, ["protocolVersion", "status", "reason"])
    && value.protocolVersion === 1 && value.status === "unavailable"
    && (value.reason === "not-ready" || value.reason === "disabled");
}

export const definition = {
  id: PLUGIN_ID,
  methods: {
    hello: {
      input: schema("hello input", isHelloInput),
      output: schema("hello output", isHelloOutput),
    },
    "billion.origin": {
      input: schema("billion.origin input", (value): value is Record<string, never> => exactObject(value, [])),
      output: schema("billion.origin output", isBillionOrigin),
    },
  },
  events: {},
} as const;

export function parseInput<T>(schema: PortableSchema<T>, input: unknown): T {
  const result = schema["~standard"].validate(input);
  if ("issues" in result) throw new TypeError(result.issues.map((issue) => issue.message).join("\n"));
  return result.value;
}

export function hello(input: unknown): HelloOutput {
  const { protocolVersions } = parseInput(definition.methods.hello.input, input);
  return protocolVersions.includes(1)
    ? { protocolVersion: 1, capabilities: ["billion.origin.v1"] }
    : { protocolVersion: null, supportedVersions: [1], capabilities: [] };
}
