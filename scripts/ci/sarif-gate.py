#!/usr/bin/env python3
"""Fail when a SARIF file holds security findings at error or warning level (ADR-0001).

Uploading results to code scanning only reports them. This makes them block.
A result counts when its rule is a security rule (tagged "security" or carrying a
security-severity) and its effective level is error or warning.

Usage: sarif-gate.py <file.sarif> [<file.sarif> ...]
"""
import json
import sys

BLOCKING_LEVELS = {"error", "warning"}


def rules_by_id(run):
    rules = {}
    driver = run.get("tool", {}).get("driver", {})
    extensions = run.get("tool", {}).get("extensions", [])
    for component in [driver, *extensions]:
        for rule in component.get("rules", []):
            rules[rule.get("id")] = rule
    return rules


def is_security(rule):
    props = rule.get("properties", {})
    return "security" in props.get("tags", []) or "security-severity" in props


def effective_level(result, rule):
    return result.get("level") or rule.get("defaultConfiguration", {}).get("level", "warning")


def main(paths):
    findings = []
    for path in paths:
        with open(path, encoding="utf-8") as handle:
            sarif = json.load(handle)
        for run in sarif.get("runs", []):
            rules = rules_by_id(run)
            for result in run.get("results", []):
                rule = rules.get(result.get("ruleId"), {})
                level = effective_level(result, rule)
                if not is_security(rule) or level not in BLOCKING_LEVELS:
                    continue
                location = result.get("locations", [{}])[0].get("physicalLocation", {})
                file = location.get("artifactLocation", {}).get("uri", "?")
                line = location.get("region", {}).get("startLine", "?")
                severity = rule.get("properties", {}).get("security-severity", "n/a")
                message = result.get("message", {}).get("text", "").splitlines()[0][:160]
                findings.append(f"{level:7} {result.get('ruleId')} (severity {severity}) {file}:{line} {message}")

    if not findings:
        print("sarif-gate: no blocking security findings")
        return 0

    print(f"sarif-gate: {len(findings)} blocking security finding(s):")
    for finding in findings:
        print(f"  {finding}")
        print(f"::error::{finding}")
    return 1


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)
    sys.exit(main(sys.argv[1:]))
