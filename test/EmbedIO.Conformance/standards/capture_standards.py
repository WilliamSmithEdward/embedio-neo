"""Capture the HTTP standards baseline from primary sources (development-only).

Fetches RFC Editor metadata for a seed list of RFCs, follows updated_by and
obsoleted_by recursively, records the datatracker state of tracked drafts, extracts the errata recorded for every captured RFC
from the RFC Editor errata API, and downloads the IANA registries that govern
HTTP/1.1, HTTP/2, HTTP/3, QPACK, QUIC and WebSocket. Every response is stored
with its URL, UTC retrieval time and SHA-256 so the audit can cite exact bytes.

Usage: python -I capture_standards.py <output-directory>
"""

import datetime
import hashlib
import json
import pathlib
import sys
import urllib.request

SEED_RFCS = [
    # Core HTTP
    9110, 9111, 9112, 9113, 7541, 9114, 9204,
    # QUIC and TLS
    9000, 9001, 9002, 9369, 9846, 8446, 7301,
    # Extensions and companion specifications
    9218, 9297, 8441, 9220, 6455, 7692, 7838, 9460, 8297, 8470, 9651,
    9412, 8336, 9298, 9484, 10008, 9931, 9530, 10036,
    # Forwarding and intermediary signals referenced by RFC 10036
    7239, 9209, 6585,
    # Content codings
    1950, 1951, 1952, 7932, 8878, 9659, 9841, 9842,
]

REGISTRIES = [
    "http-parameters", "http-fields", "http-status-codes", "http-methods",
    "http2-parameters", "http3-parameters", "quic", "websocket",
    "tls-extensiontype-values", "http-upgrade-tokens", "http-alt-svc-parameters",
    "http-priority", "masque", "http-cache-directives",
]

# Internet-Drafts whose status bounds the audit's scope. Their datatracker
# records give the current revision and IESG state; a draft is never a
# requirement, but an approved one can become one within the audit horizon.
DRAFTS = [
    "draft-ietf-webtrans-overview", "draft-ietf-webtrans-http3",
    "draft-ietf-webtrans-http2", "draft-ietf-httpbis-connect-tcp",
    "draft-ietf-masque-connect-udp-listen", "draft-ietf-httpbis-resumable-upload",
    "draft-ietf-quic-reliable-stream-reset", "draft-ietf-httpbis-unencoded-digest",
]

RECENT_GROUPS = {"httpbis", "quic", "masque", "tls", "webtrans", "httpapi", "moq"}

USER_AGENT = "embedio-neo-standards-capture/1 (development audit)"


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=120) as response:
        body = response.read()
        return response.geturl(), response.status, body


def record(out, name, url):
    final_url, status, body = fetch(url)
    path = out / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(body)
    return {
        "file": name, "url": url, "final_url": final_url, "status": status,
        "bytes": len(body), "sha256": hashlib.sha256(body).hexdigest(),
        "retrieved_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    }


def rfc_number(doc_id):
    return int(doc_id.upper().removeprefix("RFC"))


def main():
    out = pathlib.Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    manifest = {"rfcs": {}, "registries": {}, "errata": None}
    pending = list(SEED_RFCS)
    seen = set()
    while pending:
        number = pending.pop(0)
        if number in seen:
            continue
        seen.add(number)
        entry = record(out, f"rfc/rfc{number}.json", f"https://www.rfc-editor.org/rfc/rfc{number}.json")
        metadata = json.loads((out / entry["file"]).read_bytes())
        entry["title"] = metadata.get("title")
        entry["status"] = metadata.get("status")
        entry["pub_date"] = metadata.get("pub_date")
        for key in ("obsoletes", "obsoleted_by", "updates", "updated_by"):
            entry[key] = metadata.get(key, [])
        manifest["rfcs"][number] = entry
        for related in entry["obsoleted_by"] + entry["updated_by"]:
            if related.upper().startswith("RFC"):
                pending.append(rfc_number(related))

    errata_entry = record(out, "errata/errata.json", "https://www.rfc-editor.org/errata.json")
    manifest["errata"] = errata_entry
    all_errata = json.loads((out / errata_entry["file"]).read_bytes())
    selected = {}
    for item in all_errata:
        doc = str(item.get("doc-id", "")).upper()
        if doc.startswith("RFC") and rfc_number(doc) in seen:
            selected.setdefault(doc, []).append({
                "errata_id": item.get("errata_id"),
                "status": item.get("errata_status_code"),
                "type": item.get("errata_type_code"),
                "section": item.get("section"),
                "submit_date": item.get("submit_date"),
                "orig_text": item.get("orig_text"),
                "correct_text": item.get("correct_text"),
                "notes": item.get("notes"),
            })
    (out / "errata" / "selected.json").write_text(
        json.dumps(selected, indent=1, sort_keys=True, ensure_ascii=True), encoding="utf-8", newline="\n")

    # Sweep the full RFC index for recent output of the relevant working groups so
    # the seed list cannot silently miss a newly published specification.
    index_entry = record(out, "rfc-index.xml", "https://www.rfc-editor.org/rfc-index.xml")
    manifest["rfc_index"] = index_entry
    import xml.etree.ElementTree as ElementTree
    namespace = {"r": "https://www.rfc-editor.org/rfc-index"}
    recent = []
    for entry in ElementTree.parse(out / "rfc-index.xml").getroot().findall("r:rfc-entry", namespace):
        group = entry.findtext("r:wg_acronym", default="", namespaces=namespace)
        year = int(entry.findtext("r:date/r:year", default="0", namespaces=namespace))
        if group in RECENT_GROUPS and year >= 2023:
            number = rfc_number(entry.findtext("r:doc-id", namespaces=namespace))
            recent.append({"rfc": number, "group": group, "year": year,
                           "month": entry.findtext("r:date/r:month", namespaces=namespace),
                           "title": entry.findtext("r:title", namespaces=namespace),
                           "status": entry.findtext("r:current-status", namespaces=namespace),
                           "in_seed_chain": number in seen})
    manifest["recent_working_group_rfcs"] = sorted(recent, key=lambda item: item["rfc"])

    for registry in REGISTRIES:
        url = f"https://www.iana.org/assignments/{registry}/{registry}.xml"
        try:
            manifest["registries"][registry] = record(out, f"iana/{registry}.xml", url)
        except Exception as error:  # Record the failure; never fabricate a registry.
            manifest["registries"][registry] = {"url": url, "error": repr(error)}

    manifest["drafts"] = {}
    for draft in DRAFTS:
        url = f"https://datatracker.ietf.org/doc/{draft}/doc.json"
        try:
            entry = record(out, f"drafts/{draft}.json", url)
        except Exception as error:  # Record the failure; never fabricate a status.
            manifest["drafts"][draft] = {"url": url, "error": repr(error)}
            continue
        metadata = json.loads((out / entry["file"]).read_bytes())
        for key in ("rev", "time", "state", "iesg_state", "intended_std_level"):
            entry[key] = metadata.get(key)
        manifest["drafts"][draft] = entry

    (out / "manifest.json").write_text(
        json.dumps(manifest, indent=1, sort_keys=True, ensure_ascii=True), encoding="utf-8", newline="\n")
    print(f"rfcs={len(manifest['rfcs'])} errata_rfcs={len(selected)} "
          f"registries_ok={sum('sha256' in r for r in manifest['registries'].values())}/{len(REGISTRIES)} "
          f"drafts_ok={sum('sha256' in d for d in manifest['drafts'].values())}/{len(DRAFTS)}")


if __name__ == "__main__":
    main()
