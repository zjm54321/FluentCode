{
  description = "Dependency-free OpenCode Windows frontend server plugin";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/55bfb4d7437bd4e09ab7cba58a43bd68eb310044";

  outputs = { nixpkgs, ... }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
    in {
      packages = forAllSystems (system:
        let pkgs = import nixpkgs { inherit system; };
        in {
          default = pkgs.stdenvNoCC.mkDerivation {
            pname = "opencode-windows-frontend-plugin";
            version = "0.1.0";
            src = pkgs.lib.fileset.toSource {
              root = ./.;
              fileset = pkgs.lib.fileset.unions [ ./src ./package.json ./tsconfig.json ];
            };
            nativeBuildInputs = [ pkgs.nodejs pkgs.esbuild pkgs.typescript ];
            buildPhase = ''
              runHook preBuild
              tsc --noEmit
              esbuild src/index.ts --bundle --format=esm --platform=node --target=node22 --outfile=index.js
              runHook postBuild
            '';
            installPhase = ''
              runHook preInstall
              mkdir -p "$out"
              cp package.json index.js "$out/"
              runHook postInstall
            '';
          };
        });
      devShells = forAllSystems (system:
        let pkgs = import nixpkgs { inherit system; };
        in {
          default = pkgs.mkShell {
            packages = [ pkgs.nodejs pkgs.esbuild pkgs.typescript ];
          };
        });
    };
}
