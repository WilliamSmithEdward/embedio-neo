# Keep the scanner containerized without a Docker Hub pull dependency.
# Python base and every scanner wheel are pinned; this is CI tooling only.
FROM ghcr.io/astral-sh/uv:0.13.0-python3.12-trixie@sha256:6fbe5da29909abd8ae455ebef9c18ae5d008788997bbf2a6d60ace0890f3905a
COPY requirements.txt /tmp/semgrep-requirements.txt
RUN python -m venv /opt/semgrep \
    && /opt/semgrep/bin/python -m pip install --require-hashes --only-binary=:all: -r /tmp/semgrep-requirements.txt \
    && /opt/semgrep/bin/python -m pip check \
    && git --version \
    && test "$(/opt/semgrep/bin/semgrep --version)" = "1.178.0"
ENV PATH="/opt/semgrep/bin:${PATH}"
