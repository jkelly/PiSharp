"""Offline contract tests: no SDK builds, provider calls, package restores or network."""
import ctypes
import json
import os
import platform
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from owned_process import Cancelled, run_owned
from run_gap_suites import execute, parse_evidence, resolve_sdk_host


def cleanup_fixture(temporary):
    # Windows scanners can briefly hold a closed log after all owned processes exit.
    # The independent process/job assertions still must pass; do not hide a persistent lease.
    target = Path(temporary.name).resolve()
    if target.parent != Path(tempfile.gettempdir()).resolve():
        raise AssertionError("fixture cleanup escaped its directly-created temporary directory")
    deadline = time.monotonic() + 2
    while True:
        try:
            temporary.cleanup()
            return
        except PermissionError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(.05)


class ProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        cleanup_fixture(self.temp)

    def run_child(self, code, timeout=5, on_start=None):
        return run_owned([sys.executable, "-c", code], self.root, self.root / "out", self.root / "err", timeout, on_start)

    def test_exit_and_separate_streams(self):
        result = self.run_child("import sys; print('actual output'); print('actual error', file=sys.stderr); sys.exit(7)")
        self.assertEqual(result["exitCode"], 7)
        self.assertFalse(result["timedOut"])
        self.assertIn("actual output", (self.root / "out").read_text())
        self.assertIn("actual error", (self.root / "err").read_text())

    def test_selected_host_is_resolved_and_propagated_to_owned_child(self):
        host = resolve_sdk_host(sys.executable)
        self.assertTrue(Path(host).is_absolute())
        result = run_owned([sys.executable, "-c", "import os; print(os.environ['PISHARP_TEST_DOTNET'])"],
                           self.root, self.root / "out", self.root / "err", 5,
                           env=dict(os.environ) | {"PISHARP_TEST_DOTNET": host})
        self.assertEqual(result["exitCode"], 0)
        self.assertEqual((self.root / "out").read_text().strip(), host)
        with self.assertRaises(FileNotFoundError):
            resolve_sdk_host(str(self.root / "missing-dotnet"))

    def test_hang_is_bounded(self):
        # Establish fixture readiness separately from the short hang deadline;
        # a busy host may need more than 300ms merely to start Python.
        def ready(_):
            until = time.monotonic() + 2
            while "before hang" not in (self.root / "out").read_text():
                if time.monotonic() >= until:
                    self.fail("hang fixture did not become ready")
                time.sleep(.01)
        result = self.run_child("import time; print('before hang', flush=True); time.sleep(100)", .3, ready)
        self.assertTrue(result["timedOut"])
        self.assertLess(result["durationSeconds"], 5)
        self.assertIn("before hang", (self.root / "out").read_text())

    def test_timeout_must_be_finite(self):
        for timeout in (0, -1, float("inf"), float("nan"), True):
            with self.assertRaises(ValueError):
                self.run_child("pass", timeout)

    def assert_dead(self, pid):
        if os.name == "nt":
            from ctypes import wintypes as w
            api = ctypes.WinDLL("kernel32", use_last_error=True)
            api.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
            api.OpenProcess.restype = w.HANDLE
            api.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
            api.CloseHandle.argtypes = [w.HANDLE]
            handle = api.OpenProcess(0x100000, False, pid)
            if handle:
                try:
                    self.assertEqual(api.WaitForSingleObject(handle, 2000), 0)
                finally:
                    api.CloseHandle(handle)
        else:
            # An orphan can briefly remain a zombie pending init's reap; it cannot execute.
            for _ in range(100):
                try:
                    os.kill(pid, 0)
                    proc = Path(f"/proc/{pid}/stat")
                    if proc.exists() and proc.read_text().split()[2] == "Z":
                        return
                except ProcessLookupError:
                    return
                time.sleep(.02)
            self.fail(f"descendant {pid} still alive")

    def test_timeout_kills_grandchild_without_touching_unrelated_process(self):
        sentinel = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(100)"], creationflags=0x08000000 if os.name == "nt" else 0)
        try:
            code = "import subprocess,sys,time; p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(100)']); print(p.pid,flush=True); time.sleep(100)"
            result = self.run_child(code, .5)
            self.assertTrue(result["timedOut"])
            self.assert_dead(int((self.root / "out").read_text().strip()))
            self.assertIsNone(sentinel.poll())
        finally:
            sentinel.kill()
            sentinel.wait(timeout=5)

    def test_parent_exit_still_cleans_grandchild(self):
        result = self.run_child("import subprocess,sys; p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(100)']); print(p.pid,flush=True)")
        self.assertEqual(result["exitCode"], 0)
        self.assert_dead(int((self.root / "out").read_text().strip()))

    def test_cancellation_callback_still_cleans_tree(self):
        pids = []
        def cancel(start):
            pids.append(start["pid"])
            raise Cancelled("offline cancellation")
        with self.assertRaises(Cancelled) as caught:
            self.run_child("import time; time.sleep(100)", on_start=cancel)
        self.assert_dead(pids[0])
        self.assertEqual(caught.exception.process_result["cleanup"], "owned-tree-terminated-and-parent-joined")
        self.assertIn("terminationExitCode", caught.exception.process_result)


class EvidenceTests(unittest.TestCase):
    def test_json_complete_counts_and_unknown_status(self):
        policy = {"format": "json", "minimumCases": 1, "requireDeclaredCount": True}
        report = {"cases": 1, "failures": 0, "results": [{"Id": "actual", "status": "PASS_AUTHORED_NATIVE_ONLY"}]}
        self.assertFalse(parse_evidence(json.dumps(report), policy, "Windows")["errors"])
        for change in ({"cases": 2}, {"results": []}, {"skipped": 1}, {"results": [{"Id": "actual", "status": "UNKNOWN"}]}):
            self.assertTrue(parse_evidence(json.dumps(report | change), policy, "Windows")["errors"])

    def test_missing_empty_malformed_and_partial_json(self):
        for text in ("", "{}", '{"results":[]}', "not JSON"):
            self.assertTrue(parse_evidence(text, {"format": "json"}, "Windows")["errors"])
        result = parse_evidence('{"results":[{"Id":"first","status":"PASS"},{', {"format": "json"}, "Windows")
        self.assertTrue(result["errors"])
        self.assertEqual([c["id"] for c in result["cases"]], ["first"])

    def test_tap_plan_and_skips(self):
        policy = {"format": "tap"}
        self.assertFalse(parse_evidence("1..2\nok 1 first\nok 2 second", policy, "Linux")["errors"])
        for text in ("ok 1 first", "1..2\nok 1 first", "1..1\nok 1 first # SKIP reason", "1..1\nnot ok 1 failure", "1..1\nok 1 first # TODO later"):
            self.assertTrue(parse_evidence(text, policy, "Linux")["errors"])

    def test_lines_require_completion_and_expected_ids(self):
        policy = {"format": "lines", "completionPattern": r"2/2 passed", "expectedCaseIds": {"all": ["first", "second"]}}
        self.assertFalse(parse_evidence("PASS first\nPASS second\n2/2 passed", policy, "Windows")["errors"])
        for text in ("PASS first\n2/2 passed", "PASS first\nPASS second", "PASS first\nSKIP second\n2/2 passed"):
            self.assertTrue(parse_evidence(text, policy, "Windows")["errors"])

    def test_only_explicit_platform_skip_is_allowed_and_not_credited(self):
        policy = {"format": "lines", "completionPattern": "PASS: 1 done", "allowedSkips": {"Windows": [{"line": "SKIP Unix case", "reason": "Unix only", "suppressesPassId": "case"}]}}
        text = "SKIP Unix case\nPASS case\nPASS: 1 done"
        result = parse_evidence(text, policy, "Windows")
        self.assertFalse(result["errors"])
        self.assertTrue(result["hasAllowedSkips"])
        self.assertEqual([c["status"] for c in result["cases"]], ["SKIP"])
        self.assertEqual(result["counts"], {"cases": 1, "passed": 0, "skipped": 1, "failed": 0})
        self.assertEqual(result["consoleSkipObservations"], [{"raw": "SKIP Unix case", "caseId": "case", "reason": "Unix only"}])
        self.assertTrue(parse_evidence(text, policy, "Linux")["errors"])

    def test_nested_skip_marker_rejects_orphan_duplicate_unknown_and_bad_summary(self):
        policy = {"format": "lines", "completionPattern": r"PASS: [0-9]+ done", "expectedCaseIds": {"all": ["case"]},
                  "allowedSkips": {"Windows": [{"line": "SKIP Unix case", "reason": "Unix only", "suppressesPassId": "case"}]}}
        for raw in ("SKIP Unix case\nPASS: 0 done",
                    "SKIP Unix case\nSKIP Unix case\nPASS case\nPASS: 1 done",
                    "SKIP another unsupported thing\nPASS case\nPASS: 1 done",
                    "SKIP Unix case\nPASS case\nPASS: 2 done"):
            self.assertTrue(parse_evidence(raw, policy, "Windows")["errors"], raw)

    def test_summary_is_not_invented_cases(self):
        policy = {"format": "summary", "minimumCases": 0, "completionPattern": r"PASS: 131 checks"}
        evidence = parse_evidence("PASS: 131 checks", policy, "Windows")
        self.assertFalse(evidence["errors"])
        self.assertEqual(evidence["cases"], [])
        self.assertFalse(evidence["individualCasesAvailable"])

    def test_captured_json_pass_retains_status_and_unknown_pass_status_fails(self):
        policy = {"format": "json", "requireDeclaredCount": True}
        report = {"cases": 1, "failures": 0, "results": [{"Id": "captured", "status": "PASS_UPSTREAM_CAPTURE"}]}
        evidence = parse_evidence(json.dumps(report), policy, "Linux")
        self.assertFalse(evidence["errors"])
        self.assertEqual(evidence["cases"][0]["status"], "PASS_UPSTREAM_CAPTURE")
        self.assertEqual(evidence["counts"], {"cases": 1, "passed": 1, "skipped": 0, "failed": 0})
        report["results"][0]["status"] = "PASS_INVENTED"
        self.assertTrue(parse_evidence(json.dumps(report), policy, "Linux")["errors"])

    def test_adapter_requires_every_actual_platform_case_and_final_trace(self):
        policy = json.loads(Path(__file__).with_name("gap-suites.json").read_text())["policies"]["PiSharp.Env.Adapter.Tests"]
        summary = "PASS remote adapters: paths, chunked transfers, lines, listing, errors, exec, cancellation and session fencing"
        for host, count in (("Windows", 54), ("Linux", 60), ("Darwin", 60)):
            ids = policy["expectedCaseIds"][host]
            raw = summary + "\n" + "\n".join("PASS " + case for case in ids)
            evidence = parse_evidence(raw, policy, host)
            self.assertFalse(evidence["errors"], evidence["errors"])
            self.assertEqual(evidence["counts"]["passed"], count)
            self.assertTrue(evidence["individualCasesAvailable"])
            for incomplete in (summary, raw.rsplit("\n", 1)[0], raw + "\nSKIP unavailable", raw + "\nPASS invented-case"):
                self.assertTrue(parse_evidence(incomplete, policy, host)["errors"])

    def test_registered_daemon_and_legacy_platform_skips(self):
        policies = json.loads(Path(__file__).with_name("gap-suites.json").read_text())["policies"]
        for name, host, summary in (("PiSharp.Env.Daemon.Tests", "Windows", "PASS: 10 daemon integration tests"),
                                    ("PiSharp.Chord.Legacy.Cluster.Tests", "Linux", "0 failures; legacy cluster/manager tests; authored native evidence, source " + "a" * 40)):
            policy = policies[name]
            exception = policy["allowedSkips"][host][0]
            raw = exception["line"] + "\n" + "\n".join("PASS " + case for case in policy["expectedCaseIds"][host]) + "\n" + summary
            result = parse_evidence(raw, policy, host)
            self.assertFalse(result["errors"], result["errors"])
            self.assertTrue(result["hasAllowedSkips"])
            case = next(c for c in result["cases"] if c["id"] == exception["suppressesPassId"])
            self.assertEqual(case["status"], "SKIP")
            selected = 10 if name == "PiSharp.Env.Daemon.Tests" else 25
            self.assertEqual(result["counts"], {"cases": selected, "passed": selected - 1, "skipped": 1, "failed": 0})
            self.assertEqual(len(result["consoleSkipObservations"]), 1)
            self.assertEqual(result["consoleSkipObservations"][0]["raw"], exception["line"])
            self.assertEqual({c["id"] for c in result["cases"]}, set(policy["expectedCaseIds"][host]))

    def test_models_json_platform_skip_requires_exact_identity_reason_and_count(self):
        policy = json.loads(Path(__file__).with_name("gap-suites.json").read_text())["policies"]["PiSharp.Models.Tests"]
        report = {"cases": 2, "failures": 0, "skipped": 1, "results": [
            {"Id": "config-value.windows-default-shell-fallback", "status": "SKIP", "reason": "requires-windows-cmd-exe"},
            {"Id": "actual-linux-case", "status": "PASS_AUTHORED_NATIVE_ONLY"}]}
        result = parse_evidence(json.dumps(report), policy, "Linux")
        self.assertFalse(result["errors"])
        self.assertTrue(result["hasAllowedSkips"])
        self.assertTrue(parse_evidence(json.dumps(report), policy, "Windows")["errors"])
        self.assertTrue(parse_evidence(json.dumps(report | {"skipped": 0}), policy, "Linux")["errors"])
        report["results"][0]["reason"] = "different reason"
        self.assertTrue(parse_evidence(json.dumps(report), policy, "Linux")["errors"])

    def test_production_installer_requires_exactly_one_passing_case_without_skips(self):
        policy = json.loads(Path(__file__).with_name("gap-suites.json").read_text())["policies"]["PiSharp.ExtensionParity.Tests"]
        case = {"Id": "runtime.pi-packages-installed-with-verified-provenance", "status": "PASS_AUTHORED_NATIVE_ONLY"}
        report = {"cases": 1, "failures": 0, "skipped": 0, "results": [case]}
        self.assertFalse(parse_evidence(json.dumps(report), policy, "Linux")["errors"])
        for changed in ({"cases": 0, "results": []},
                        {"skipped": 1, "results": [case | {"status": "SKIP_NO_PRODUCTION_RUNTIME"}]},
                        {"results": [case | {"Id": "gap.runtime-installed-pi150-real-abi-and-attribution"}]},
                        {"cases": 2, "results": [case, case | {"Id": "unfiltered-extra"}]}):
            self.assertTrue(parse_evidence(json.dumps(report | changed), policy, "Linux")["errors"])


class LedgerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        # Git operations remain local to this temporary repository.
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)
        subprocess.run(["git", "-C", str(self.root), "-c", "user.name=Offline", "-c", "user.email=offline@example.invalid", "commit", "--allow-empty", "-qm", "fixture"], check=True)
        self.output = self.root / "output"
        self.manifest = self.root / "suites.json"
        self.fake = self.root / "fake.py"
        self.fake.write_text("import sys\nif sys.argv[1]=='run': print('PASS first\\n1/1 passed')\n")
        self.plan = {"schemaVersion": 2, "groups": {"core": ["One", "Two"]}, "buildTimeoutSeconds": 2, "testTimeoutSeconds": 2,
                     "policies": {n: {"format": "lines", "completionPattern": "1/1 passed", "expectedCaseIds": {"all": ["first"]}} for n in ("One", "Two")}}
        for name in ("One", "Two"):
            project = self.root / "tests" / name / f"{name}.csproj"
            project.parent.mkdir(parents=True)
            project.write_text("offline fixture")

    def tearDown(self):
        cleanup_fixture(self.temp)

    def run_plan(self):
        self.manifest.write_text(json.dumps(self.plan))
        exit_code = execute(self.root, self.output, self.manifest, "core", [sys.executable, str(self.fake)])
        return exit_code, json.loads((self.output / "manifest.json").read_text())

    def test_pass_retains_sha_stage_hashes_and_fresh_runs(self):
        code, ledger = self.run_plan()
        self.assertEqual(code, 0)
        self.assertRegex(ledger["checkoutSha"], r"^[a-f0-9]{40}$")
        for suite in ledger["suites"]:
            self.assertEqual(suite["result"], "passed")
            for stage in suite["stages"]:
                self.assertEqual(stage["checkoutSha"], ledger["checkoutSha"])
                self.assertRegex(stage["stdoutSha256"], r"^[a-f0-9]{64}$")
        _, second = self.run_plan()
        self.assertNotEqual(ledger["runDirectory"], second["runDirectory"])
        self.assertTrue(Path(ledger["runDirectory"], "manifest.json").is_file())

    def test_sdk_host_reaches_every_build_test_and_test_descendant(self):
        self.manifest.write_text(json.dumps(self.plan))
        self.fake.write_text("import os,subprocess,sys\n"
                             "assert os.environ['PISHARP_TEST_DOTNET']==sys.executable\n"
                             "subprocess.run([sys.executable,'-c',\"import os,sys; assert os.environ['PISHARP_TEST_DOTNET']==sys.executable\"],check=True)\n"
                             "if sys.argv[1]=='run': print('PASS first\\n1/1 passed')\n")
        actual = run_owned
        stages = []
        def launch(command, *args, **kwargs):
            if command[0] == sys.executable:
                stages.append(command[1])
                self.assertEqual(kwargs["env"]["PISHARP_TEST_DOTNET"], sys.executable)
                command = [sys.executable, str(self.fake), *command[1:]]
            return actual(command, *args, **kwargs)
        with patch("run_gap_suites.resolve_sdk_host", return_value=sys.executable), patch("run_gap_suites.run_owned", side_effect=launch):
            code = execute(self.root, self.output, self.manifest, "core", sys.executable)
        self.assertEqual(code, 0)
        self.assertEqual(stages, ["build", "run", "build", "run"])
        self.assertEqual(json.loads((self.output / "manifest.json").read_text())["dotnetHost"], sys.executable)

    def test_missing_project_retains_whole_plan(self):
        (self.root / "tests/One/One.csproj").unlink()
        code, ledger = self.run_plan()
        self.assertEqual(code, 1)
        self.assertEqual(ledger["suites"][0]["result"], "failed")
        self.assertEqual(ledger["suites"][1]["result"], "passed")

    def test_failed_test_exit_preserved_with_partial_evidence(self):
        self.fake.write_text("import sys\nif sys.argv[1]=='run': print('PASS first'); sys.exit(9)\n")
        code, ledger = self.run_plan()
        self.assertEqual(code, 1)
        suite = ledger["suites"][0]
        self.assertEqual(suite["stages"][1]["exitCode"], 9)
        self.assertEqual(suite["evidence"]["cases"][0]["id"], "first")
        self.assertTrue(suite["evidence"]["errors"])

    def test_zero_exit_missing_and_skipped_reports_are_failures(self):
        for text in ("", "SKIP first\\n1/1 passed"):
            self.fake.write_text(f"import sys\nif sys.argv[1]=='run': print('{text}')\n")
            code, ledger = self.run_plan()
            self.assertEqual(code, 1)
            self.assertTrue(ledger["suites"][0]["evidence"]["errors"])

    def test_approved_nested_skip_ledger_counts_only_registered_cases(self):
        self.fake.write_text("import sys\nif sys.argv[1]=='run': print('SKIP Unix body\\nPASS first\\n1/1 passed')\n")
        for policy in self.plan["policies"].values():
            policy["allowedSkips"] = {platform.system(): [{"line": "SKIP Unix body", "reason": "Unix only", "suppressesPassId": "first"}]}
        code, ledger = self.run_plan()
        self.assertEqual(code, 0)
        self.assertEqual(ledger["result"], "passed-with-policy-skips")
        for suite in ledger["suites"]:
            self.assertEqual(suite["evidence"]["counts"], {"cases": 1, "passed": 0, "skipped": 1, "failed": 0})
            self.assertEqual(len(suite["evidence"]["consoleSkipObservations"]), 1)

    def test_cancellation_stops_remaining_suites_and_retains_plan(self):
        actual = run_owned
        def cancel_test(command, *args, **kwargs):
            if "run" in command:
                raise Cancelled("offline stop")
            return actual(command, *args, **kwargs)
        with patch("run_gap_suites.run_owned", side_effect=cancel_test):
            code, ledger = self.run_plan()
        self.assertEqual(code, 130)
        self.assertEqual([s["result"] for s in ledger["suites"]], ["cancelled", "not-run"])
        self.assertIsNotNone(ledger["finishedUtc"])

    def test_setup_failure_has_final_ledger(self):
        self.manifest.write_text("{")
        self.assertEqual(execute(self.root, self.output, self.manifest, "core"), 1)
        ledger = json.loads((self.output / "manifest.json").read_text())
        self.assertEqual(ledger["result"], "failed")
        self.assertIsNotNone(ledger["finishedUtc"])

    def test_failed_build_never_runs_test_and_keeps_exit(self):
        self.fake.write_text("import sys; print('build failed',file=sys.stderr); sys.exit(23)\n")
        code, ledger = self.run_plan()
        self.assertEqual(code, 1)
        for suite in ledger["suites"]:
            self.assertEqual(len(suite["stages"]), 1)
            self.assertEqual(suite["stages"][0]["exitCode"], 23)
            self.assertIsNone(suite["evidence"])

    def test_timeout_retains_partial_case_and_termination(self):
        self.fake.write_text("import sys,time\nif sys.argv[1]=='run': print('PASS first',flush=True); time.sleep(100)\n")
        self.plan["testTimeoutSeconds"] = 1
        code, ledger = self.run_plan()
        self.assertEqual(code, 1)
        stage = ledger["suites"][0]["stages"][1]
        self.assertTrue(stage["timedOut"])
        self.assertIn("terminationExitCode", stage)
        self.assertEqual(ledger["suites"][0]["evidence"]["cases"][0]["id"], "first")

    def test_plan_and_running_stage_exist_before_child_runs(self):
        actual = run_owned
        observed = []
        def check(command, *args, **kwargs):
            ledger = json.loads((self.output / "manifest.json").read_text())
            self.assertEqual(len(ledger["suites"]), 2)
            if "run" in command:
                stage = ledger["suites"][0 if len(observed) == 0 else 1]["stages"][-1]
                self.assertEqual(stage["result"], "running")
                observed.append(True)
            return actual(command, *args, **kwargs)
        with patch("run_gap_suites.run_owned", side_effect=check):
            code, _ = self.run_plan()
        self.assertEqual(code, 0)
        self.assertEqual(len(observed), 2)

    def test_installer_preflight_rejects_broad_filter_and_reused_cache(self):
        production = json.loads(Path(__file__).with_name("gap-suites.json").read_text())["policies"]["PiSharp.ExtensionParity.Tests"]
        self.plan["groups"]["core"] = ["One"]
        self.plan["policies"]["One"] = production
        variables = dict(production["requiredEnvironment"]) | {"PISHARP_PI_RUNTIME_DIR": str(self.root / "fresh-runtime")}
        for changed in ({"EXTPARITY_FILTER": "runtime."}, {"PISHARP_TEST_INSTALL_PI_RUNTIME": "0"}, {"PISHARP_PI_RUNTIME_DIR": str(self.root)}):
            with patch.dict(os.environ, variables | changed):
                code, ledger = self.run_plan()
            self.assertEqual(code, 1)
            self.assertEqual(ledger["suites"][0]["stages"], [])
            self.assertEqual(ledger["suites"][0]["result"], "failed")

    def test_exact_installer_contract_preserves_separate_evidence_identity(self):
        production = json.loads(Path(__file__).with_name("gap-suites.json").read_text())["policies"]["PiSharp.ExtensionParity.Tests"]
        self.plan["groups"]["core"] = ["One"]
        self.plan["policies"]["One"] = production
        # Fake report verifies the gate contract only; it performs no production install.
        report = {"cases": 1, "failures": 0, "skipped": 0, "results": [{"Id": production["expectedCaseIds"]["all"][0], "status": "PASS_AUTHORED_NATIVE_ONLY"}]}
        self.fake.write_text("import sys\nif sys.argv[1]=='run': print(" + repr(json.dumps(report)) + ")\n")
        variables = dict(production["requiredEnvironment"]) | {"PISHARP_PI_RUNTIME_DIR": str(self.root / "fresh-runtime")}
        with patch.dict(os.environ, variables):
            code, ledger = self.run_plan()
        self.assertEqual(code, 0)
        self.assertEqual(ledger["suites"][0]["policy"]["evidenceKind"], "production-runtime-install-and-registry-integrity-verification")
        self.assertEqual(ledger["suites"][0]["productionRuntimeDirectory"], variables["PISHARP_PI_RUNTIME_DIR"])


if __name__ == "__main__":
    unittest.main()
