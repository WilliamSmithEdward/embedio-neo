# Test-only conformance runner. Every input is pinned: images by digest, h2spec by
# commit (its go.sum verifies dependencies), MsQuic by SHA-256 and Python packages by hash.
# Build from the repository root:
#   docker build -f test/EmbedIO.Conformance/docker/Dockerfile -t embedio-conformance test/EmbedIO.Conformance

# golang:1.25-bookworm (go1.25.14), digest recorded 2026-10-09.
FROM golang@sha256:3b4a11519ad929d1e1d261a12cff056f0c85b735253d7d861346b9c6f8b36437 AS h2spec
# h2spec v2.6.0 tag commit. Move by changing the tag and commit together.
ARG H2SPEC_COMMIT=70ac2294010887f48b18e2d64f5cccd48421fad1
RUN git clone --quiet https://github.com/summerwind/h2spec.git /src \
 && git -C /src checkout --quiet "$H2SPEC_COMMIT" \
 && test "$(git -C /src rev-parse HEAD)" = "$H2SPEC_COMMIT" \
 && cd /src && CGO_ENABLED=0 GOFLAGS=-mod=readonly go build -trimpath -o /h2spec ./cmd/h2spec

# mcr.microsoft.com/dotnet/sdk:10.0.401 (runtime 10.0.12, Ubuntu 24.04).
FROM mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
# Same MsQuic package and digest as the desktop CI workflow.
RUN curl --fail --silent --show-error --location --output /tmp/libmsquic.deb \
      https://packages.microsoft.com/ubuntu/24.04/prod/pool/main/libm/libmsquic/libmsquic_2.6.2_amd64.deb \
 && printf '%s  %s\n' 9ac90841ad45d99c1ff85b427d6dc7f0849291d596f0049d13db779a5a66ba4e /tmp/libmsquic.deb | sha256sum --check \
 && apt-get update \
 && apt-get install --yes --no-install-recommends /tmp/libmsquic.deb python3 python3-venv \
 && rm -rf /var/lib/apt/lists/* /tmp/libmsquic.deb
COPY drivers/requirements.txt /opt/drivers/requirements.txt
RUN python3 -m venv /opt/venv \
 && /opt/venv/bin/pip install --no-cache-dir --require-hashes --only-binary=:all: -r /opt/drivers/requirements.txt
COPY --from=h2spec /h2spec /usr/local/bin/h2spec
ENV PATH=/opt/venv/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
