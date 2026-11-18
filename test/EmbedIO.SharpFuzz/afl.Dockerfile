# mcr.microsoft.com/dotnet/sdk:10.0.404-noble (runtime 10.0.15, Ubuntu 24.04).
FROM mcr.microsoft.com/dotnet/sdk@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb
RUN apt-get update && apt-get install --yes --no-install-recommends build-essential curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN curl --fail --location --retry 3 https://github.com/AFLplusplus/AFLplusplus/archive/dbaf11913c1b2702dee5b4d3dcfffd52f1defe50.tar.gz -o /tmp/afl.tar.gz \
    && echo 'd5f4f72c830526654bebbc637f6164a93c22d7309ce8ba73979abadc42ce5974  /tmp/afl.tar.gz' | sha256sum --check \
    && mkdir /tmp/afl && tar -xzf /tmp/afl.tar.gz --strip-components=1 -C /tmp/afl \
    && make -C /tmp/afl -j1 afl-fuzz afl-showmap \
    && cp /tmp/afl/afl-fuzz /tmp/afl/afl-showmap /usr/local/bin/
RUN dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path /opt/sharpfuzz
