FROM debian:bookworm-slim AS build

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        bison \
        build-essential \
        ca-certificates \
        curl \
        flex \
        libicu-dev \
        libreadline-dev \
        libxml2-dev \
        perl \
        pkg-config \
        zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build
RUN curl --fail --show-error --silent --location \
        --output postgresql-19-snapshot.tar.bz2 \
        https://ftp.postgresql.org/pub/snapshot/19/postgresql-19-snapshot.tar.bz2 \
    && curl --fail --show-error --silent --location \
        --output postgresql-19-snapshot.tar.bz2.sha256 \
        https://ftp.postgresql.org/pub/snapshot/19/postgresql-19-snapshot.tar.bz2.sha256 \
    && sha256sum --check postgresql-19-snapshot.tar.bz2.sha256 \
    && mkdir source \
    && tar --extract --bzip2 --file postgresql-19-snapshot.tar.bz2 \
        --directory source --strip-components=1

WORKDIR /build/source
RUN ./configure \
        --prefix=/usr/local \
        --with-libxml \
    && make --silent --jobs=2 world-bin \
    && make --silent install-world-bin

FROM postgres:18-bookworm

COPY --from=build /usr/local /usr/local
RUN sed --regexp-extended --in-place \
    "s!^#?listen_addresses[[:space:]]*=.*!listen_addresses = '*'!" \
    /usr/local/share/postgresql/postgresql.conf.sample
ENV PGDATA=/var/lib/postgresql/19/docker
