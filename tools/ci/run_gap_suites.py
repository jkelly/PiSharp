"""CI console-suite driver. Results are observations, never manufactured test cases."""
import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

from owned_process import Cancelled, install_cancel_handlers, run_owned

PASS_STATUSES = ("PASS", "PASS_AUTHORED_NATIVE_ONLY", "PASS_AUTHORED_NATIVE", "PASS_UPSTREAM_CAPTURE")


def resolve_sdk_host(executable):
    """Use the selected SDK host for apphost-launched tests and their children."""
    found = shutil.which(executable)
    if not found:
        raise FileNotFoundError(f"Configured dotnet host not found: {executable}")
    host = Path(found).resolve(strict=True)
    if not host.is_file():
        raise ValueError("Configured dotnet host is not a regular file")
    return str(host)


def now():
    return datetime.now(timezone.utc).isoformat()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def write_ledger(path, value):
    temporary = path.with_suffix(".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def parse_evidence(text, policy, host):
    """Parse declared formats. A PASS summary is never expanded into invented cases."""
    cases, summaries, errors = [], [], []
    allowed = policy.get("allowedSkips", {}).get(host, [])
    console_skips = []
    mode = policy["format"]
    if mode == "json":
        try:
            report = json.loads(text)
            entries = report["results"]
            if not isinstance(entries, list):
                raise ValueError("results is not an array")
            for entry in entries:
                case_id = entry.get("Id", entry.get("id", entry.get("Name", entry.get("name"))))
                status = entry.get("status")
                if not isinstance(case_id, str) or not case_id or not isinstance(status, str):
                    raise ValueError("case missing identity/status")
                cases.append({"id": case_id, "status": status, "raw": entry})
            if "cases" in report and report["cases"] != len(cases):
                errors.append("declared case count does not match results")
            if policy.get("requireDeclaredCount") and "cases" not in report:
                errors.append("missing declared case count")
            actual_skips = sum(c["status"] in ("SKIP", "SKIPPED") for c in cases)
            if any(report[key] != actual_skips for key in ("skipped", "skips", "skipCount") if key in report):
                errors.append("inconsistent JSON skip count")
            if report.get("failures") != sum(c["status"] == "FAIL" for c in cases):
                errors.append("missing or inconsistent failures count")
        except (ValueError, KeyError, TypeError, AttributeError) as error:
            errors.append(f"invalid/incomplete JSON report: {error}")
            if not cases:
                match = re.search(r'"results"\s*:\s*\[', text)
                if match:
                    remainder = text[match.end():].lstrip()
                    decoder = json.JSONDecoder()
                    while remainder:
                        try:
                            entry, end = decoder.raw_decode(remainder)
                        except ValueError:
                            break
                        if isinstance(entry, dict) and isinstance(entry.get("status"), str):
                            cases.append({"id": entry.get("Id", entry.get("id", entry.get("Name", entry.get("name")))), "status": entry["status"], "raw": entry})
                        remainder = remainder[end:].lstrip().removeprefix(",").lstrip()
    elif mode == "tap":
        plan = None
        for line in text.splitlines():
            if match := re.fullmatch(r"1\.\.(\d+)(?:\s*#.*)?", line):
                if plan is not None:
                    errors.append("duplicate TAP plan")
                plan = int(match[1])
            elif match := re.match(r"^(not ok|ok)\s+(\d+)(?:\s+-)?\s*(.*)$", line):
                status = "SKIP" if re.search(r"#\s*(SKIP|TODO)\b", match[3], re.I) else "PASS" if match[1] == "ok" else "FAIL"
                cases.append({"id": match[2], "status": status, "raw": line})
            elif line.startswith("Bail out!"):
                errors.append(line)
        if plan is None or plan != len(cases) or [c["id"] for c in cases] != [str(i) for i in range(1, len(cases) + 1)]:
            errors.append("missing/incomplete TAP plan")
    elif mode in ("lines", "summary"):
        for line in text.splitlines():
            if re.fullmatch(policy["completionPattern"], line):
                summaries.append(line)
                continue
            if exception := next((a for a in allowed if a.get("line") == line), None):
                console_skips.append({"raw": line, "caseId": exception.get("suppressesPassId"), "reason": exception["reason"]})
                continue
            if match := re.match(r"^(PASS_UPSTREAM_CAPTURE|PASS|FAIL|SKIP|SKIPPED)(?: |: )(.*)$", line):
                cases.append({"id": match[2], "status": "PASS" if match[1] == "PASS_UPSTREAM_CAPTURE" else match[1], "raw": line})
        if len(summaries) != 1:
            errors.append("missing or duplicate completion summary")
        if mode == "summary" and cases:
            errors.append("unexpected case output in summary-only suite")
        for summary in summaries:
            fraction = re.match(r"(\d+)/(\d+) ", summary)
            if fraction and (int(fraction[1]) != int(fraction[2]) or int(fraction[2]) != len(cases)):
                errors.append("summary total does not match completed cases")
            count = re.match(r"(?:Passed |PASS: |PASS_UPSTREAM_CAPTURE: |PASS )(\d+)(?: |;)", summary)
            if mode == "lines" and count and int(count[1]) != sum(c["status"] == "PASS" for c in cases):
                errors.append("summary passed count does not match completed cases")
    else:
        raise ValueError(f"unknown evidence format: {mode}")

    expected_map = policy.get("expectedCaseIds", {})
    expected = expected_map.get(host, expected_map.get("all"))
    if expected_map and expected is None:
        errors.append(f"no required-case policy for platform {host}")
    if expected is not None and set(expected) != {c["id"] for c in cases}:
        errors.append("required case identities missing or unexpected")
    if len(cases) < policy.get("minimumCases", 1):
        errors.append("empty/incomplete required case results")
    if len({c["id"] for c in cases}) != len(cases):
        errors.append("duplicate case identities")
    observed_skips = []
    for case in cases:
        if case["status"] in ("SKIP", "SKIPPED"):
            exception = next((a for a in allowed if
                              ("id" in a and case["id"] == a["id"] and isinstance(case.get("raw"), dict) and case["raw"].get("reason") == a["reason"])), None)
            if exception:
                case["reason"] = exception["reason"]
                observed_skips.append(case)
            else:
                errors.append(f"unexpected skip: {case['id']}")
        elif case["status"] not in PASS_STATUSES:
            errors.append(f"nonpassing/unknown status: {case['id']}: {case['status']}")
    # Nested console markers are observations about a registered case, never cases.
    # Keep its emitted PASS line as raw evidence while recording the body's real skip.
    if len({observation["raw"] for observation in console_skips}) != len(console_skips):
        errors.append("duplicate approved console skip marker")
    for observation in console_skips:
        matching = [case for case in cases if case["id"] == observation["caseId"]]
        if len(matching) != 1 or matching[0]["status"] != "PASS":
            errors.append(f"console skip marker lacks its registered PASS case: {observation['caseId']}")
        else:
            matching[0]["status"] = "SKIP"
            matching[0]["reason"] = observation["reason"]
            observed_skips.append(matching[0])
    for line in text.splitlines():
        if re.match(r"^(?:SKIP|SKIPPED)(?:\s|:|$)", line, re.I) and not any(a.get("line") == line for a in allowed):
            errors.append(f"unexpected skip output: {line}")
    passed = sum(case["status"] in PASS_STATUSES for case in cases)
    skipped = sum(case["status"] in ("SKIP", "SKIPPED") for case in cases)
    return {"format": mode, "cases": cases, "summaries": summaries, "errors": errors,
            "consoleSkipObservations": console_skips,
            "counts": {"cases": len(cases), "passed": passed, "skipped": skipped, "failed": len(cases) - passed - skipped},
            "hasAllowedSkips": bool(observed_skips), "individualCasesAvailable": mode != "summary"}


def execute(root, output, manifest_path, group, executable="dotnet", build_seconds=None, test_seconds=None):
    output.mkdir(parents=True, exist_ok=True)
    run_dir = output / uuid.uuid4().hex
    run_dir.mkdir()
    ledger = {"schemaVersion": 2, "group": group, "checkoutSha": None, "platform": platform.system(),
              "startedUtc": now(), "finishedUtc": None, "result": "running", "errors": [], "suites": [],
              "runDirectory": str(run_dir), "manifestSha256": digest(manifest_path)}
    def save():
        write_ledger(run_dir / "manifest.json", ledger)
        write_ledger(output / "manifest.json", ledger)
    save()
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        if manifest.get("schemaVersion") != 2:
            raise ValueError("unsupported suite manifest schemaVersion")
        names = manifest["groups"][group]
        if not isinstance(names, list) or not names or len(set(names)) != len(names):
            raise ValueError("empty or duplicate required suite plan")
        for name in names:
            if not re.fullmatch(r"[A-Za-z0-9_.-]+", name):
                raise ValueError("unsafe suite identity")
            ledger["suites"].append({"suite": name, "result": "planned", "stages": [], "evidence": None})
        save()
        child_environment = dict(os.environ)
        if isinstance(executable, str):
            executable = resolve_sdk_host(executable)
            child_environment["PISHARP_TEST_DOTNET"] = executable
            ledger["dotnetHost"] = executable
        checkout = {"name": "checkout", "checkoutSha": None, "startedUtc": now(), "result": "running",
                    "stdout": str(run_dir / "checkout.stdout.log"), "stderr": str(run_dir / "checkout.stderr.log")}
        ledger["checkoutStage"] = checkout
        save()
        try:
            checkout.update(run_owned(["git", "-C", str(root), "rev-parse", "HEAD"], root,
                                      checkout["stdout"], checkout["stderr"], 20))
            checkout["result"] = "passed" if checkout["exitCode"] == 0 and not checkout["timedOut"] else "failed"
        finally:
            checkout["finishedUtc"] = now()
            for stream in ("stdout", "stderr"):
                checkout[stream + "Sha256"] = digest(Path(checkout[stream]))
            save()
        sha = Path(checkout["stdout"]).read_text().strip()
        if checkout["result"] != "passed" or not re.fullmatch(r"[a-f0-9]{40,64}", sha):
            raise ValueError("cannot establish current checkout SHA")
        ledger["checkoutSha"] = checkout["checkoutSha"] = sha
        save()
        for suite in ledger["suites"]:
            name = suite["suite"]
            suite.update(startedUtc=now(), result="running")
            save()
            try:
                policy = manifest["policies"][name]
                suite["policy"] = policy
                for key, required in policy.get("requiredEnvironment", {}).items():
                    if os.environ.get(key) != required:
                        raise ValueError(f"Required suite environment missing/mismatched: {key}")
                if fresh_key := policy.get("freshDirectoryEnvironment"):
                    configured = os.environ.get(fresh_key)
                    if not configured or not Path(configured).is_absolute() or Path(configured).exists():
                        raise ValueError(f"{fresh_key} must name a fresh absolute production runtime directory")
                    suite["productionRuntimeDirectory"] = configured
                project = root / "tests" / name / f"{name}.csproj"
                if not project.is_file():
                    raise FileNotFoundError(f"Required test project missing: {project}")
                prefix = [executable] if isinstance(executable, str) else list(executable)
                commands = [("build", prefix + ["build", str(project), "--configuration", "Release", "--disable-build-servers", "-m:1", "-p:RestoreLockedMode=false", "-p:UseSharedCompilation=false", "-p:RestoreSources=https://api.nuget.org/v3/index.json"], build_seconds or manifest["buildTimeoutSeconds"]),
                            ("tests", prefix + ["run", "--project", str(project), "--configuration", "Release", "--no-build", "--no-restore"], test_seconds or policy.get("timeoutSeconds", manifest["testTimeoutSeconds"]))]
                for kind, command, timeout in commands:
                    stage = {"name": kind, "result": "running", "startedUtc": now(), "checkoutSha": sha,
                             "stdout": str(run_dir / f"{name}.{kind}.stdout.log"), "stderr": str(run_dir / f"{name}.{kind}.stderr.log")}
                    suite["stages"].append(stage)
                    save()
                    try:
                        stage.update(run_owned(command, root, stage["stdout"], stage["stderr"], timeout,
                                               lambda running: (stage.update(running), save()), env=child_environment))
                        stage["result"] = "passed" if stage["exitCode"] == 0 and not stage["timedOut"] else "failed"
                    except (Cancelled, KeyboardInterrupt) as error:
                        stage.update(getattr(error, "process_result", {}))
                        stage["result"] = "cancelled"
                        raise
                    except Exception as error:
                        stage.update(getattr(error, "process_result", {}))
                        stage["result"] = "failed"
                        stage["error"] = str(error)
                    finally:
                        stage["finishedUtc"] = now()
                        for stream in ("stdout", "stderr"):
                            stage[stream + "Sha256"] = digest(Path(stage[stream]))
                        if kind == "tests":
                            out = Path(stage["stdout"])
                            text = out.read_text(encoding="utf-8", errors="replace") if out.exists() else ""
                            suite["evidence"] = parse_evidence(text, policy, platform.system())
                            err = Path(stage["stderr"])
                            if err.exists() and re.search(r"^(?:SKIP|SKIPPED)(?:\s|:|$)", err.read_text(encoding="utf-8", errors="replace"), re.M | re.I):
                                suite["evidence"]["errors"].append("explicit skip on stderr")
                        save()
                    if stage["result"] != "passed":
                        break
                evidence = suite["evidence"]
                suite["result"] = "passed" if len(suite["stages"]) == 2 and all(s["result"] == "passed" for s in suite["stages"]) and evidence and not evidence["errors"] else "failed"
                if suite["result"] == "passed" and evidence["hasAllowedSkips"]:
                    suite["result"] = "passed-with-policy-skips"
            except (Cancelled, KeyboardInterrupt):
                suite["result"] = "cancelled"
                raise
            except Exception as error:
                suite["result"] = "failed"
                suite["error"] = str(error)
            finally:
                suite["finishedUtc"] = now()
                save()
            print(f"{name}: {suite['result']}", flush=True)
        results = [s["result"] for s in ledger["suites"]]
        ledger["result"] = "failed" if any(s not in ("passed", "passed-with-policy-skips") for s in results) else "passed-with-policy-skips" if "passed-with-policy-skips" in results else "passed"
    except (Cancelled, KeyboardInterrupt) as error:
        ledger["result"] = "cancelled"
        ledger["errors"].append(str(error))
    except Exception as error:
        ledger["result"] = "failed"
        ledger["errors"].append(str(error))
    finally:
        for suite in ledger["suites"]:
            if suite["result"] in ("planned", "running"):
                suite["result"] = "not-run"
                suite["reason"] = "run interrupted before completion"
        ledger["finishedUtc"] = now()
        save()
    return 0 if ledger["result"] in ("passed", "passed-with-policy-skips") else 130 if ledger["result"] == "cancelled" else 1


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--group", required=True, choices=["core", "chord", "env", "runtime-installer"])
    parser.add_argument("--output-directory", default="artifacts/gap-ci")
    parser.add_argument("--dotnet", default=os.environ.get("PISHARP_TEST_DOTNET", "dotnet"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    install_cancel_handlers()
    os.environ["MSBUILDDISABLENODEREUSE"] = "1"
    return execute(root, root / args.output_directory, Path(__file__).with_name("gap-suites.json"), args.group, args.dotnet)


if __name__ == "__main__":
    sys.exit(main())
