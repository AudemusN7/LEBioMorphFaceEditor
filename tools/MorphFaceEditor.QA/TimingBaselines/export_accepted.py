"""Export the reviewed .39 timing audit, never the QA run currently being judged.

Usage: python export_accepted.py path/to/original/baseline-candidates.json path/to/corrected-reviewed-audit.json
The accepted report hash/seed are pinned below; this is not an automatic updater.
"""
import hashlib
import json
import math
import sys
from pathlib import Path

SOURCE_SHA256 = "5DEFE6C9B17BB75B921D563968294CD749AA1E403ACFF97085C7492EB6608980"
ORIGINAL_AUDIT_SHA256 = "250AF83D76F4A77FAF22974612DBFF4FEA8D5CC269350853F8BA361EC58B2C8C"
PLAYER_FIXTURE_CASES = {f"load/le{game}-{sex}-player" for game in (1, 2, 3) for sex in ("hmf", "hmm")}


def seeded_player_descendant(candidate):
    # The original audit mistook the sole stock-face selector for proof that the
    # randomised fixture's descendants were fixed. Ryan approved these 18
    # classifications on 24 October; their accepted observations stay frozen.
    if candidate["phase"] != "Test workflow":
        return False
    if candidate["caseId"] in PLAYER_FIXTURE_CASES and candidate["kind"] == "Load":
        return candidate["purpose"] in ("Load · <face> · load-occurrence-1",
                                         "Preview renderer initialisation · loaded.png")
    return candidate["caseId"] == "pcc-audit" and candidate["kind"] == "FileTransfer" and any(
        candidate["purpose"] == f"PCC read/import audit · {case} · seeded-player.pcc"
        for case in PLAYER_FIXTURE_CASES)


audit_path = Path(sys.argv[1])
corrected_audit_path = Path(sys.argv[2])
assert audit_path.resolve() != corrected_audit_path.resolve(), "Preserve the original reviewed audit."
assert hashlib.sha256(audit_path.read_bytes()).hexdigest().upper() == ORIGINAL_AUDIT_SHA256
audit = json.loads(audit_path.read_text(encoding="utf-8-sig"))
metadata = audit["metadata"]
assert metadata["build"] == "0.10.0.39" and metadata["seed"] == 713890369
assert metadata["sourceSha256"] == SOURCE_SHA256
assert metadata["complete"] and metadata["failed"] == 0 and metadata["blocked"] == 0

keys = set()
entries = []
corrected_keys = []
for candidate in audit["candidates"]:
    statistic = candidate["reportingStatistic"]
    key = " | ".join((candidate["caseId"], candidate["phase"], candidate["purpose"], candidate["kind"], statistic))
    assert key not in keys, "Archetype/context projection is not unique: " + key
    keys.add(key)
    if candidate["excluded"]:
        continue
    assert candidate["kind"] in ("Load", "Randomisation", "FileTransfer")
    if seeded_player_descendant(candidate):
        assert candidate["selectedHeadroomPercent"] == 25 and candidate["inputVariability"] == "fixed-source-input"
        candidate["selectedHeadroomPercent"] = 50.0
        candidate["inputVariability"] = "seeded-derived-fixture"
        candidate["inputVariabilityEvidence"] = (
            "Prepared Player fixture is randomised before its workflow; its load, preview and saved-package audit are seed-dependent.")
        corrected_keys.append(key)
    margin = candidate["selectedHeadroomPercent"]
    assert margin in (25, 50)
    envelope = candidate["baselineEnvelopeMs"]
    floor = 300 if candidate["kind"] == "FileTransfer" else 100
    value = max(envelope * (1 + margin / 100), envelope + 100, floor)
    assert math.ceil(value / 100) * 100 >= candidate["distribution"]["maximumMs"] * 1.15
    if key in corrected_keys:
        candidate["proposedLimitMs"] = math.ceil(value / 100) * 100
        candidate["actualHeadroomOverGroupMaxPercent"] = (candidate["proposedLimitMs"] / candidate["distribution"]["maximumMs"] - 1) * 100
    entries.append(dict(key=key, envelopeMs=envelope, marginPercent=int(margin)))
assert len(keys) == 3292 and len(entries) == 2945 and len(corrected_keys) == 18

correction = dict(reviewedDate="2026-10-24", originalReviewedAuditSha256=ORIGINAL_AUDIT_SHA256,
                  entryCount=18, fromMarginPercent=25, toMarginPercent=50,
                  note="Ryan approved correcting seeded Player load/preview/audit classifications. All .39 observations and envelopes remain unchanged.")
metadata["playerFixtureClassificationCorrection"] = correction
corrected_audit_bytes = (json.dumps(audit, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
corrected_audit_path.write_bytes(corrected_audit_bytes)

document = dict(
    schemaVersion=1,
    source=dict(build=metadata["build"], seed=metadata["seed"], sha256=SOURCE_SHA256,
                report=metadata["sourceReport"].replace("\\", "/")),
    provenance=dict(reviewedAuditSha256=hashlib.sha256(corrected_audit_bytes).hexdigest().upper(),
                    playerFixtureClassificationCorrection=correction,
                    note="Frozen from Ryan's accepted .39 full run. Approved 25/50 percent margins, upward100ms caps, fixed2x/variable3x severe thresholds. Never rebase from a judged run."),
    policy=dict(fixedMarginPercent=25, variableMarginPercent=50, absoluteHeadroomMs=100,
                loadRandomisationFloorMs=100, fileTransferFloorMs=300, roundingMs=100,
                fixedFailureMultiplier=2, variableFailureMultiplier=3),
    entries=sorted(entries, key=lambda entry: entry["key"]))
destination = Path(__file__).with_name("accepted-0.10.0.39.json")
destination.write_text(json.dumps(document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Exported {len(entries)} unique calibrated keys to {destination}")
