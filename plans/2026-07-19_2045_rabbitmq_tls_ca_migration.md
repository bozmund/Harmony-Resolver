# RabbitMQ TLS CA migration

## Context

The production RabbitMQ container rejects the original single self-signed certificate when it is used
as both its CA bundle and server certificate. This prevents the AMQPS listener from starting and blocks
the whole Compose stack.

## Accepted plan

1. Generate a dedicated local CA certificate and a separate RabbitMQ server certificate signed by that
   CA in `deploy/bootstrap-oracle-vps.sh`.
2. Make the bootstrap migration replace the old one-certificate layout when `ca.crt` is absent, while
   preserving an audit-friendly backup of the old certificate pair.
3. Configure RabbitMQ with the CA bundle (`/certs/ca.crt`) separately from its server certificate and
   private key.
4. Validate certificate generation and an actual RabbitMQ TLS startup locally, then run the repository
   validation suite.

## Operational impact

The current VPS needs the bootstrap script run once as root after this change is deployed so it can
generate the CA/server certificate pair. Existing downloader certificate pins must be refreshed only if
any downloader has already been configured; production is currently empty.
