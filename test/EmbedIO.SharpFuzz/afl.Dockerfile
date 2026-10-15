FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:3a48280a9a7c6276c3eeaba1efa6a08820f18c84fb9e6274c187719ecfaf9fbc
RUN apt-get update && apt-get install --yes --no-install-recommends build-essential curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN curl --fail --location --retry 3 https://github.com/AFLplusplus/AFLplusplus/archive/dbaf11913c1b2702dee5b4d3dcfffd52f1defe50.tar.gz -o /tmp/afl.tar.gz \
    && echo 'd5f4f72c830526654bebbc637f6164a93c22d7309ce8ba73979abadc42ce5974  /tmp/afl.tar.gz' | sha256sum --check \
    && mkdir /tmp/afl && tar -xzf /tmp/afl.tar.gz --strip-components=1 -C /tmp/afl \
    && make -C /tmp/afl -j1 afl-fuzz afl-showmap \
    && cp /tmp/afl/afl-fuzz /tmp/afl/afl-showmap /usr/local/bin/
RUN dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path /opt/sharpfuzz
