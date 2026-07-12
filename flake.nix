{
  description = "Harmony Resolver reproducible development and image inputs";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;

      # Publish a .NET project into $out/app/.
      publishProject = pkgs: project:
        pkgs.stdenv.mkDerivation {
          name = "${project}-publish";
          src = pkgs.lib.cleanSourceWith {
            src = ./.;
            filter = name: type:
              let base = builtins.baseNameOf (toString name);
              in !(builtins.elem base [
                ".git" ".github" "plans" "bin" "obj" "node_modules"
              ]);
          };
          nativeBuildInputs = with pkgs; [ dotnet-sdk_10 ];
          dontConfigure = true;
          buildPhase = ''
            dotnet restore src/${project}/${project}.csproj \
              --runtime linux-x64
            dotnet publish src/${project}/${project}.csproj \
              -c Release \
              -o $out/app \
              --runtime linux-x64 \
              --self-contained false \
              --no-restore
          '';
          installPhase = ''
            # publish -o already wrote to $out/app
          '';
        };

      baseImage = pkgs: pkgs.dockerTools.pullImage {
        imageName = "mcr.microsoft.com/dotnet/aspnet";
        imageDigest = "sha256:8c40de4a85aebccf9cb53f53e74e2b19ed951013a75b56f69da2f04778445078";
        finalImageName = "mcr.microsoft.com/dotnet/aspnet";
        finalImageTag = "10.0";
        sha256 = "0d26gw6pn06h3r3qv8alkmgw9jdhbg0vhjr54gyhhs5vlx0gqz1m";
        os = "linux";
        arch = "x86_64";
      };

      # Wrap publish output into a directory that can be added to a layered image.
      appLayer = publish:
        pkgs.runCommand "app-layer" { } ''
          mkdir -p $out/app
          cp -r ${publish}/app/. $out/app/
          chmod -R u+w $out/app
        '';

      apiImage = pkgs: let
        publish = publishProject pkgs "Harmony.Resolver.Api";
      in pkgs.dockerTools.buildLayeredImage {
        name = "ghcr.io/bozmund/harmony-resolver-api";
        tag = "latest";
        fromImage = baseImage pkgs;
        contents = with pkgs; [ ffmpeg python3 python3Packages.pip yt-dlp ]
          ++ [ (appLayer publish) ];
        config = {
          Cmd = [ "dotnet" "/app/Harmony.Resolver.Api.dll" ];
          WorkingDir = "/app";
          User = "65534";
          Env = [
            "ASPNETCORE_URLS=http://+:8080"
            "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false"
          ];
        };
      };

      mcpImage = pkgs: let
        publish = publishProject pkgs "Harmony.Resolver.Mcp";
      in pkgs.dockerTools.buildLayeredImage {
        name = "ghcr.io/bozmund/harmony-resolver-mcp";
        tag = "latest";
        fromImage = baseImage pkgs;
        contents = with pkgs; [ ]
          ++ [ (appLayer publish) ];
        config = {
          Cmd = [ "dotnet" "/app/Harmony.Resolver.Mcp.dll" ];
          WorkingDir = "/app";
          User = "65534";
          Env = [ "ASPNETCORE_URLS=http://+:8080" ];
        };
      };
    in {
      packages = forAllSystems (system:
        let pkgs = import nixpkgs { inherit system; };
        in {
          default = apiImage pkgs;
          api-image = apiImage pkgs;
          mcp-image = mcpImage pkgs;
        });

      devShells = forAllSystems (system:
        let pkgs = import nixpkgs { inherit system; };
        in {
          default = pkgs.mkShell {
            packages = with pkgs; [ dotnet-sdk_10 ffmpeg yt-dlp docker-compose postgresql minio valkey ];
            DOTNET_ROOT = "${pkgs.dotnet-sdk_10}";
          };
        });

      checks = forAllSystems (system:
        let pkgs = import nixpkgs { inherit system; };
        in {
          formatting = pkgs.runCommand "harmony-resolver-flake-check" { } ''
            test -f ${self}/Harmony.Resolver.slnx
            test -f ${self}/Dockerfile
            touch $out
          '';
        });

      nixosModules.harmony-resolver = import ./nix/module.nix;
    };
}