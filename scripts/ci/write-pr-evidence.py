#!/usr/bin/env python3
"""Write pr-evidence.json (docs/schemas/pr-evidence.schema.json, ADR-0002).

Inputs come from the environment so no workflow expression is spliced into code:
  EVIDENCE_SOURCE      pull_request | verify-main
  PR_NUMBER, BASE_SHA  pull_request only
  HEAD_SHA             PR head commit, or the main commit for verify-main
  MERGE_REF_SHA        commit actually checked out and tested
  JOB_RESULTS          JSON object of job id -> result, from toJSON(needs)
  IMAGE_DIGEST, TRIVY_RESULT, TRIVY_REPORT_SHA256, TRIVY_HIGH, TRIVY_CRITICAL
  GITHUB_RUN_ID, GITHUB_RUN_ATTEMPT

The tree hash is computed here from the checked-out commit, never taken as input.
Exits non-zero if any required job did not succeed, after writing the file.
"""
import json
import os
import subprocess
import sys


def env(name, required=True):
    value = os.environ.get(name, "")
    if required and not value:
        sys.exit(f"write-pr-evidence: {name} is required")
    return value


def main(output):
    source = env("EVIDENCE_SOURCE")
    needs = json.loads(env("JOB_RESULTS"))
    jobs = {job: data.get("result", "unknown") for job, data in sorted(needs.items())}

    merge_tree = subprocess.run(
        ["git", "rev-parse", "HEAD^{tree}"], capture_output=True, text=True, check=True
    ).stdout.strip()
    checked_out = subprocess.run(
        ["git", "rev-parse", "HEAD"], capture_output=True, text=True, check=True
    ).stdout.strip()
    merge_ref = env("MERGE_REF_SHA")
    if checked_out != merge_ref:
        sys.exit(f"write-pr-evidence: checked out {checked_out}, expected {merge_ref}")

    evidence = {
        "schema_version": 1,
        "source": source,
        "head_sha": env("HEAD_SHA"),
        "merge_ref_sha": merge_ref,
        "merge_tree": merge_tree,
        "run_id": int(env("GITHUB_RUN_ID")),
        "run_attempt": int(env("GITHUB_RUN_ATTEMPT")),
        "jobs": jobs,
        "image": {
            "digest": env("IMAGE_DIGEST"),
            "trivy_result": env("TRIVY_RESULT"),
            "trivy_report_sha256": env("TRIVY_REPORT_SHA256"),
            "high": int(env("TRIVY_HIGH")),
            "critical": int(env("TRIVY_CRITICAL")),
        },
    }
    if source == "pull_request":
        evidence["pr_number"] = int(env("PR_NUMBER"))
        evidence["base_sha"] = env("BASE_SHA")

    with open(output, "w", encoding="utf-8") as handle:
        json.dump(evidence, handle, indent=2, sort_keys=True)
        handle.write("\n")
    print(json.dumps(evidence, indent=2, sort_keys=True))

    failed = {job: result for job, result in jobs.items() if result != "success"}
    if failed:
        print(f"::error::Required jobs did not succeed: {failed}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "pr-evidence.json"))
