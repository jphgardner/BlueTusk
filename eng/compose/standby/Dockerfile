FROM postgres:18-alpine

COPY standby-entrypoint.sh /usr/local/bin/bluetusk-standby-entrypoint
RUN chmod 0755 /usr/local/bin/bluetusk-standby-entrypoint

USER postgres
ENTRYPOINT ["/usr/local/bin/bluetusk-standby-entrypoint"]
