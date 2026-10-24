import { billionOrigin } from "./billion-origin.ts";
import { getModelFavorites, setModelFavorites } from "./model-favorites.ts";
import { definition, hello, parseInput, PLUGIN_ID } from "./protocol.ts";

export interface Registration {
  readonly dispose: () => Promise<void>;
}

export const handlers = {
  hello: async (input: unknown) => hello(input),
  "billion.origin": async (input: unknown) => {
    parseInput(definition.methods["billion.origin"].input, input);
    return billionOrigin();
  },
  "model.favorites.get": async (input: unknown) => {
    parseInput(definition.methods["model.favorites.get"].input, input);
    return getModelFavorites();
  },
  "model.favorites.set": async (input: unknown) => setModelFavorites(input),
};

export interface SetupContext {
  readonly rpc: {
    readonly register: (rpcDefinition: typeof definition, rpcHandlers: typeof handlers) => Promise<Registration>;
  };
}

export default {
  id: PLUGIN_ID,
  async setup(ctx: SetupContext): Promise<() => Promise<void>> {
    // The host's acquireRelease waits for setup, including a late registration,
    // before invoking cleanup. The registration also belongs to the host scope.
    const registration = await ctx.rpc.register(definition, handlers);
    let disposal: Promise<void> | undefined;
    return () => disposal ??= registration.dispose();
  },
};
