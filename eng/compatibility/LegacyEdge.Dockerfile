ARG LEGACY_IMAGE
ARG MODERN_IMAGE

FROM ${LEGACY_IMAGE} AS legacy
FROM ${MODERN_IMAGE}

COPY --from=legacy /usr/share/dotnet/ /opt/legacy-dotnet/
COPY --from=legacy /usr/lib/x86_64-linux-gnu/libssl.so.1.1 /usr/local/lib/
COPY --from=legacy /usr/lib/x86_64-linux-gnu/libcrypto.so.1.1 /usr/local/lib/
RUN ldconfig

ENV DOTNET_ROOT=/opt/legacy-dotnet
ENV PATH=/opt/legacy-dotnet:${PATH}
