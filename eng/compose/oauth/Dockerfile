FROM postgres:18-alpine AS builder

RUN apk add --no-cache build-base krb5-dev openssl openssl-dev
COPY validator.c /tmp/bluetusk_oauth_validator.c
RUN cc -O2 -fPIC -shared \
      -I"$(pg_config --includedir-server)" \
      -o /tmp/bluetusk_oauth_validator.so \
      /tmp/bluetusk_oauth_validator.c \
    && openssl req -x509 -newkey rsa:2048 -nodes \
      -keyout /tmp/server.key \
      -out /tmp/server.crt \
      -days 3650 \
      -subj "/CN=localhost" \
      -addext "subjectAltName=DNS:localhost" \
    && chmod 0600 /tmp/server.key

FROM postgres:18-alpine

COPY --from=builder --chown=postgres:postgres \
  /tmp/bluetusk_oauth_validator.so /usr/local/lib/postgresql/bluetusk_oauth_validator.so
COPY --from=builder --chown=postgres:postgres /tmp/server.key /var/lib/postgresql/server.key
COPY --from=builder --chown=postgres:postgres /tmp/server.crt /var/lib/postgresql/server.crt
