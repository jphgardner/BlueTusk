ARG POSTGRES_IMAGE=postgres:18-alpine
FROM ${POSTGRES_IMAGE} AS build
RUN apk add --no-cache build-base
ARG GRAPH_UBSAN=0
WORKDIR /build
COPY Makefile bluetusk_graph.control bluetusk_graph--*.sql bluetusk_graph.c ./
RUN if [ "$GRAPH_UBSAN" = "1" ]; then \
        make with_llvm=no PG_CFLAGS="-fsanitize=undefined -fno-sanitize-recover=undefined" SHLIB_LINK="-lubsan" \
        && install -D "$(gcc -print-file-name=libubsan.so.1)" /install/usr/local/lib/libubsan.so.1; \
    else \
        make with_llvm=no; \
    fi && make with_llvm=no DESTDIR=/install install

FROM ${POSTGRES_IMAGE}
COPY --from=build /install/ /
