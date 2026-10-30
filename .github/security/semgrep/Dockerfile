# Keep the scanner containerized without a Docker Hub pull dependency.
# Python base and every scanner wheel are pinned; this is CI tooling only.
FROM ghcr.io/astral-sh/uv:0.13.0-python3.12-trixie@sha256:6fbe5da29909abd8ae455ebef9c18ae5d008788997bbf2a6d60ace0890f3905a
# The installed Semgrep must be the version requirements.txt pins. The check
# reads that version from the file, so a Dependabot bump of the file alone
# passes it.
COPY requirements.txt /tmp/semgrep-requirements.txt
RUN python -m venv /opt/semgrep \
    && /opt/semgrep/bin/python -m pip install --require-hashes --only-binary=:all: -r /tmp/semgrep-requirements.txt \
    && /opt/semgrep/bin/python -m pip check \
    && git --version \
    && expected="$(sed -n 's/^semgrep==\([^ ]*\) .*/\1/p' /tmp/semgrep-requirements.txt)" \
    && test -n "$expected" \
    && test "$(/opt/semgrep/bin/semgrep --version)" = "$expected"
ENV PATH="/opt/semgrep/bin:${PATH}"
