"""Export the reviewed .39 timing audit, never the QA run currently being judged.

Usage: python export_accepted.py path/to/reviewed/baseline-candidates.json
The accepted report hash/seed are pinned below; this is not an automatic updater.
"""
import hashlib
import json
import math
import sys
from pathlib import Path

SOURCE_SHA256 = "5DEFE6C9B17BB75B921D563968294CD749AA1E403ACFF97085C7492EB6608980"
audit_path = Path(sys.argv[1])
audit = json.loads(audit_path.read_text(encoding="utf-8-sig"))
metadata = audit["metadata"]
assert metadata["build"] == "0.10.0.39" and metadata["seed"] == 713890369
assert metadata["sourceSha256"] == SOURCE_SHA256
assert metadata["complete"] and metadata["failed"] == 0 and metadata["blocked"] == 0

keys = set()
entries = []
for candidate in audit["candidates"]:
    statistic = candidate["reportingStatistic"]
    key = " | ".join((candidate["caseId"], candidate["phase"], candidate["purpose"], candidate["kind"], statistic))
    assert key not in keys, "Archetype/context projection is not unique: " + key
    keys.add(key)
    if candidate["excluded"]:
        continue
    assert candidate["kind"] in ("Load", "Randomisation", "FileTransfer")
    margin = candidate["selectedHeadroomPercent"]
    assert margin in (25, 50)
    envelope = candidate["baselineEnvelopeMs"]
    floor = 300 if candidate["kind"] == "FileTransfer" else 100
    value = max(envelope * (1 + margin / 100), envelope + 100, floor)
    assert math.ceil(value / 100) * 100 >= candidate["distribution"]["maximumMs"] * 1.15
    entries.append(dict(key=key, envelopeMs=envelope, marginPercent=int(margin)))
assert len(keys) == 3292 and len(entries) == 2945

document = dict(
    schemaVersion=1,
    source=dict(build=metadata["build"], seed=metadata["seed"], sha256=SOURCE_SHA256,
                report=metadata["sourceReport"].replace("\\", "/")),
    provenance=dict(reviewedAuditSha256=hashlib.sha256(audit_path.read_bytes()).hexdigest().upper(),
                    note="Frozen from Ryan's accepted .39 full run. Approved 25/50 percent margins, upward100ms caps, fixed2x/variable3x severe thresholds. Never rebase from a judged run."),
    policy=dict(fixedMarginPercent=25, variableMarginPercent=50, absoluteHeadroomMs=100,
                loadRandomisationFloorMs=100, fileTransferFloorMs=300, roundingMs=100,
                fixedFailureMultiplier=2, variableFailureMultiplier=3),
    entries=sorted(entries, key=lambda entry: entry["key"]))
destination = Path(__file__).with_name("accepted-0.10.0.39.json")
destination.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Exported {len(entries)} unique calibrated keys to {destination}")
