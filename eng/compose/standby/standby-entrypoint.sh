#!/bin/sh
set -eu

attempt=0
until pg_isready -h topology-primary18 -U postgres -d bluetusk_tests >/dev/null 2>&1; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 60 ]; then
        echo "Primary did not become ready for the standby base backup." >&2
        exit 1
    fi

    sleep 1
done

mkdir -p "$PGDATA"
if [ ! -s "$PGDATA/PG_VERSION" ]; then
    pg_basebackup \
        --dbname="host=topology-primary18 port=5432 user=postgres password=postgres dbname=bluetusk_tests" \
        --pgdata="$PGDATA" \
        --format=plain \
        --wal-method=stream \
        --checkpoint=fast \
        --write-recovery-conf
fi

exec docker-entrypoint.sh postgres \
    -c hot_standby=on \
    -c max_prepared_transactions=10 \
    -c hba_file=/etc/postgresql/bluetusk-pg_hba.conf
