FROM alpine:3.22

RUN apk add --no-cache pgbouncer=1.24.0-r0

COPY session.ini /etc/pgbouncer/session.ini
COPY transaction.ini /etc/pgbouncer/transaction.ini
COPY userlist.txt /etc/pgbouncer/userlist.txt

USER nobody
ENTRYPOINT ["pgbouncer"]
CMD ["/etc/pgbouncer/session.ini"]
