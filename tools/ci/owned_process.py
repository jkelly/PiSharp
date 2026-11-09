"""Bounded process trees: suspended hidden Windows child/job; Unix session/group."""
import ctypes
import math
import os
import signal
import subprocess
import time
from pathlib import Path


class Cancelled(BaseException):
    pass


def install_cancel_handlers():
    def cancel(signum, frame):
        raise Cancelled(f"received signal {signum}")
    for name in ("SIGINT", "SIGTERM", "SIGBREAK"):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), cancel)


class WindowsJob:
    def __init__(self):
        from ctypes import wintypes as w
        class Basic(ctypes.Structure):
            _fields_ = [("processTime", ctypes.c_int64), ("jobTime", ctypes.c_int64),
                        ("flags", w.DWORD), ("min", ctypes.c_size_t), ("max", ctypes.c_size_t),
                        ("active", w.DWORD), ("affinity", ctypes.c_size_t), ("priority", w.DWORD), ("scheduling", w.DWORD)]
        class IO(ctypes.Structure):
            _fields_ = [(x, ctypes.c_uint64) for x in ("readOps", "writeOps", "otherOps", "readBytes", "writeBytes", "otherBytes")]
        class Extended(ctypes.Structure):
            _fields_ = [("basic", Basic), ("io", IO), ("processMemory", ctypes.c_size_t),
                        ("jobMemory", ctypes.c_size_t), ("peakProcessMemory", ctypes.c_size_t), ("peakJobMemory", ctypes.c_size_t)]
        class Accounting(ctypes.Structure):
            _fields_ = [(x, ctypes.c_int64) for x in ("user", "kernel", "periodUser", "periodKernel")] + [(x, w.DWORD) for x in ("faults", "total", "active", "terminated")]
        self.accounting = Accounting
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.CreateJobObjectW.argtypes = [ctypes.c_void_p, w.LPCWSTR]
        self.api.CreateJobObjectW.restype = w.HANDLE
        self.api.SetInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD]
        self.api.AssignProcessToJobObject.argtypes = [w.HANDLE, w.HANDLE]
        self.api.QueryInformationJobObject.argtypes = [w.HANDLE, ctypes.c_int, ctypes.c_void_p, w.DWORD, ctypes.c_void_p]
        self.api.TerminateJobObject.argtypes = [w.HANDLE, w.UINT]
        self.api.CloseHandle.argtypes = [w.HANDLE]
        self.handle = self.api.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        info = Extended()
        info.basic.flags = 0x2000  # KILL_ON_JOB_CLOSE
        if not self.api.SetInformationJobObject(self.handle, 9, ctypes.byref(info), ctypes.sizeof(info)):
            self.close()
            raise ctypes.WinError(ctypes.get_last_error())

    def attach_and_resume(self, process):
        from ctypes import wintypes as w
        if not self.api.AssignProcessToJobObject(self.handle, int(process._handle)):
            raise ctypes.WinError(ctypes.get_last_error())
        resume = ctypes.WinDLL("ntdll").NtResumeProcess
        resume.argtypes = [w.HANDLE]
        resume.restype = ctypes.c_long
        status = resume(int(process._handle))
        if status != 0:
            raise OSError(f"NtResumeProcess failed: {status:#x}")

    def terminate_and_join(self):
        if not self.api.TerminateJobObject(self.handle, 125):
            raise ctypes.WinError(ctypes.get_last_error())
        until = time.monotonic() + 10
        info = self.accounting()
        while True:
            if not self.api.QueryInformationJobObject(self.handle, 1, ctypes.byref(info), ctypes.sizeof(info), None):
                raise ctypes.WinError(ctypes.get_last_error())
            if not info.active:
                break
            if time.monotonic() >= until:
                raise TimeoutError("owned Windows job did not become empty")
            time.sleep(.02)

    def close(self):
        if self.handle:
            self.api.CloseHandle(self.handle)
            self.handle = None


def run_owned(command, cwd, stdout_path, stderr_path, timeout, on_start=None, *, env=None):
    if isinstance(timeout, bool) or not math.isfinite(timeout) or timeout <= 0:
        raise ValueError("timeout must be positive and finite")
    started = time.monotonic()
    process = job = None
    result = {"command": list(command), "timeoutSeconds": timeout, "exitCode": None,
              "timedOut": False, "pid": None, "cleanup": "not-started"}
    try:
        if os.name == "nt":
            job = WindowsJob()
        with Path(stdout_path).open("wb") as out, Path(stderr_path).open("wb") as err:
            old_mode = job.api.SetErrorMode(0x8003) if job else None
            try:
                process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.DEVNULL, stdout=out, stderr=err,
                                           shell=False, close_fds=True, env=env,
                                           creationflags=0x08000004 if job else 0,
                                           start_new_session=not job)
            finally:
                if job:
                    job.api.SetErrorMode(old_mode)
            result["pid"] = process.pid
            if job:
                job.attach_and_resume(process)
            if on_start:
                on_start(dict(result))
            try:
                result["exitCode"] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                result["timedOut"] = True
    except BaseException as error:
        # The same dictionary is completed by finally before the caller observes it.
        # Preserve cleanup/termination evidence even when cancellation must propagate.
        error.process_result = result
        raise
    finally:
        # Includes signal/KeyboardInterrupt and ledger callback failure. No process-name kills.
        try:
            if process:
                try:
                    if job:
                        job.terminate_and_join()
                    else:
                        try:
                            os.killpg(process.pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                finally:
                    if process.poll() is None:
                        process.kill()  # also handles failure to assign a suspended child
                    process.wait(timeout=10)
                    result["terminationExitCode"] = process.returncode
                result["cleanup"] = "owned-tree-terminated-and-parent-joined"
        finally:
            if job:
                job.close()
            result["durationSeconds"] = round(time.monotonic() - started, 3)
    return result
