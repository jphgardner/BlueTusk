# Linux x64 GitHub Actions runner for the single BlueTusk measurement host.
# It runs as a container in Docker Desktop's Linux VM on the Windows PC and drives
# sibling fixture containers through the mounted Docker socket.
# See docs/operations/core-performance-evidence.md#runners.
FROM ghcr.io/actions/actions-runner:2.337.0@sha256:e5496277be5d09bc968b3d64911b74e219ac4a3f2edce956a3ecf9271bea1ef4

USER root
# PowerShell and the Docker CLI come from their vendors' signed apt repositories. The base image is
# digest pinned; these tools orchestrate captures and are recorded, not measured.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl gnupg git; \
    . /etc/os-release; \
    curl -fsSL "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb; \
    dpkg -i /tmp/packages-microsoft-prod.deb; \
    install -m 0755 -d /etc/apt/keyrings; \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc; \
    echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends powershell docker-ce-cli; \
    rm -rf /var/lib/apt/lists/* /tmp/packages-microsoft-prod.deb

USER runner
