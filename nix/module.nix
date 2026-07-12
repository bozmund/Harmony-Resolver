{ config, lib, pkgs, ... }:
let cfg = config.services.harmony-resolver;
in {
  options.services.harmony-resolver = {
    enable = lib.mkEnableOption "Harmony Resolver distributed stack";
    source = lib.mkOption { type = lib.types.path; description = "Checkout containing compose.yaml."; };
    environmentFile = lib.mkOption { type = lib.types.path; description = "Root-readable runtime environment file outside the Nix store."; };
  };

  config = lib.mkIf cfg.enable {
    virtualisation.docker.enable = true;
    systemd.services.harmony-resolver = {
      description = "Harmony Resolver stack";
      wantedBy = [ "multi-user.target" ];
      after = [ "docker.service" "network-online.target" ];
      requires = [ "docker.service" ];
      serviceConfig = {
        Type = "oneshot";
        RemainAfterExit = true;
        WorkingDirectory = cfg.source;
        EnvironmentFile = cfg.environmentFile;
        ExecStart = "${pkgs.docker-compose}/bin/docker-compose up --build -d";
        ExecStop = "${pkgs.docker-compose}/bin/docker-compose down";
      };
    };
  };
}
