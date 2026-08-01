FROM postgres:18

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        krb5-admin-server \
        krb5-kdc \
    && rm -rf /var/lib/apt/lists/*

COPY krb5-server.conf /etc/krb5.conf
COPY kdc.conf /etc/krb5kdc/kdc.conf
COPY entrypoint.sh /usr/local/bin/bluetusk-gss-entrypoint

RUN chmod 0755 /usr/local/bin/bluetusk-gss-entrypoint

ENTRYPOINT ["/usr/local/bin/bluetusk-gss-entrypoint"]
CMD ["postgres"]
