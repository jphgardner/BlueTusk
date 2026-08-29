FROM nginxinc/nginx-unprivileged:1.30.3-alpine3.23@sha256:b3f2436575bd5be7386518084d842dac414ab4962712afa31e99e0942a56e3b2

ARG BUILD_VERSION=development
ARG BUILD_REVISION=unknown

LABEL org.opencontainers.image.title="BlueTusk documentation" \
      org.opencontainers.image.description="Production Angular documentation portal for the BlueTusk platform" \
      org.opencontainers.image.version="${BUILD_VERSION}" \
      org.opencontainers.image.revision="${BUILD_REVISION}" \
      org.opencontainers.image.source="https://github.com/jphgardner/BlueTusk"

COPY --chown=101:101 nginx.conf /etc/nginx/nginx.conf
COPY --chown=101:101 dist/website/browser/ /usr/share/nginx/html/

USER 101:101
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/health/ready || exit 1

