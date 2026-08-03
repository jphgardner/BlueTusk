#!/usr/bin/env bash
set -euo pipefail

if [[ ! -f /var/lib/krb5kdc/principal ]]; then
    kdb5_util create -s -P bluetusk-kdc-master
    kadmin.local -q "addprinc -pw bluetusk-gss-password bluetusk_gss_test@BLUETUSK.TEST"
    kadmin.local -q "addprinc -randkey postgres/localhost@BLUETUSK.TEST"
    kadmin.local -q "addprinc -randkey postgres/gss18@BLUETUSK.TEST"
    kadmin.local -q "addprinc -randkey postgres/gss18.bluetusk.test@BLUETUSK.TEST"
    kadmin.local -q "ktadd -k /var/lib/postgresql/postgres.keytab postgres/localhost@BLUETUSK.TEST"
    kadmin.local -q "ktadd -k /var/lib/postgresql/postgres.keytab postgres/gss18@BLUETUSK.TEST"
    kadmin.local -q "ktadd -k /var/lib/postgresql/postgres.keytab postgres/gss18.bluetusk.test@BLUETUSK.TEST"
    chown postgres:postgres /var/lib/postgresql/postgres.keytab
    chmod 0600 /var/lib/postgresql/postgres.keytab
fi

/usr/sbin/krb5kdc
exec /usr/local/bin/docker-entrypoint.sh "$@"
