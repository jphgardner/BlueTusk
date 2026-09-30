# The upstream registry image is unavailable. Preserve the same MinIO release
# using its official binary and the SHA-256 published with the GitHub asset.
FROM debian:bookworm-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251
ADD --checksum=sha256:7c5bd8512c6e966455b1d198209358b2d191c77a83ab377c4073281065fb855f --chmod=0755 https://github.com/minio/minio/releases/download/RELEASE.2025-09-07T16-13-09Z/minio.linux-amd64.RELEASE.2025-09-07T16-13-09Z /usr/local/bin/minio
LABEL org.opencontainers.image.source="https://github.com/minio/minio" \
      org.opencontainers.image.version="RELEASE.2025-09-07T16-13-09Z" \
      org.opencontainers.image.licenses="AGPL-3.0-only"
RUN mkdir /data && chown 10001:10001 /data
USER 10001:10001
EXPOSE 9000
ENTRYPOINT ["/usr/local/bin/minio"]
CMD ["server", "/data"]
