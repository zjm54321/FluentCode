// Only the Node surface used by this dependency-free package.
declare module "node:process" {
  const process: { env: Record<string, string | undefined> };
  export default process;
}
