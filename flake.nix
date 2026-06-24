{
  description = "unity-cli — control a live Unity Editor from the terminal";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
    in {
      packages = forAllSystems (system:
        let pkgs = nixpkgs.legacyPackages.${system}; in {
          default = let version = (builtins.fromJSON (builtins.readFile ./unity-connector/package.json)).version; in pkgs.buildGoModule {
            pname = "unity-cli";
            inherit version;
            src = ./.;
            vendorHash = null;
            # Only the root command — keep dev helpers like tools/release out of
            # the installed output.
            subPackages = [ "." ];
            ldflags = [ "-s" "-w" "-X main.Version=v${version}" ];
          };
        });

      apps = forAllSystems (system: {
        default = {
          type = "app";
          program = "${self.packages.${system}.default}/bin/unity-cli";
        };
      });

      devShells = forAllSystems (system:
        let pkgs = nixpkgs.legacyPackages.${system}; in {
          default = pkgs.mkShell {
            packages = with pkgs; [
              go
              gopls
              golangci-lint
            ];
          };
        });
    };
}
