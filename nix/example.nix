{ ... }: {
  imports = [ ./module.nix ];
  services.harmony-resolver = {
    enable = true;
    source = /srv/harmony-resolver;
    environmentFile = "/run/secrets/harmony-resolver.env";
  };
}
