/* PiSharp Unix child-process boundary. No managed code runs between fork and exec.
 * The parent allocates/validates argv, envp and executable before calling here.
 * MIT, matching the surrounding PiSharp project. */
#define _GNU_SOURCE
#define _DARWIN_C_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/resource.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>
#if defined(__APPLE__)
#include <util.h>
#include <libproc.h>
#else
#include <pty.h>
#include <dirent.h>
#include <limits.h>
#include <sys/syscall.h>
#endif

#define API __attribute__((visibility("default")))

static int cloexec(int fd) { return fcntl(fd, F_SETFD, FD_CLOEXEC); }
static int make_pipe(int fds[2]) {
#if defined(__linux__)
    return pipe2(fds, O_CLOEXEC);
#else
    if (pipe(fds) < 0) return -1;
    if (cloexec(fds[0]) == 0 && cloexec(fds[1]) == 0) return 0;
    int saved = errno; close(fds[0]); close(fds[1]); errno = saved; return -1;
#endif
}
static void close_pair(int fds[2]) { if (fds[0] >= 0) close(fds[0]); if (fds[1] >= 0) close(fds[1]); }
static int nonblock(int fd) { int flags = fcntl(fd, F_GETFL); return flags < 0 ? -1 : fcntl(fd, F_SETFL, flags | O_NONBLOCK); }

/* All code in this function after fork is native and uses only async-signal-safe
 * syscalls. Failure is reported through a CLOEXEC pipe before returning in parent. */
API int pisharp_spawn(const char *executable, char *const argv[], char *const envp[],
                     const char *cwd, int terminal, uint16_t cols, uint16_t rows,
                     int *pid_out, int *input_out, int *output_out, int *error_out) {
    int in[2] = {-1,-1}, out[2] = {-1,-1}, err[2] = {-1,-1}, report[2] = {-1,-1};
    int master = -1, slave = -1, failure = 0;
    long maxfd = sysconf(_SC_OPEN_MAX);
    if (maxfd < 0) maxfd = 65536;
    if (terminal) {
        struct winsize size = { .ws_row = rows, .ws_col = cols };
        if (openpty(&master, &slave, NULL, NULL, &size) < 0) goto failed;
        if (cloexec(master) < 0 || cloexec(slave) < 0) goto failed;
    } else if (make_pipe(in) < 0 || make_pipe(out) < 0 || make_pipe(err) < 0) goto failed;
    if (make_pipe(report) < 0) goto failed;
    pid_t pid = fork();
    if (pid < 0) goto failed;
    if (pid == 0) {
        close(report[0]);
        if (setsid() < 0) goto child_failed;
        if (terminal && ioctl(slave, TIOCSCTTY, 0) < 0) goto child_failed;
        if (dup2(terminal ? slave : in[0], STDIN_FILENO) < 0 ||
            dup2(terminal ? slave : out[1], STDOUT_FILENO) < 0 ||
            dup2(terminal ? slave : err[1], STDERR_FILENO) < 0) goto child_failed;
        for (int fd = 3; fd < maxfd; fd++) if (fd != report[1]) close(fd);
        struct sigaction action;
        memset(&action, 0, sizeof(action)); action.sa_handler = SIG_DFL; sigemptyset(&action.sa_mask);
        for (int signal_number = 1; signal_number < NSIG; signal_number++)
            if (signal_number != SIGKILL && signal_number != SIGSTOP) sigaction(signal_number, &action, NULL);
        sigset_t mask; sigemptyset(&mask); sigprocmask(SIG_SETMASK, &mask, NULL);
        if (chdir(cwd) < 0) goto child_failed;
        execve(executable, argv, envp);
child_failed:
        failure = errno;
        while (write(report[1], &failure, sizeof(failure)) < 0 && errno == EINTR) { }
        _exit(127);
    }
    close(report[1]); report[1] = -1;
    if (terminal) { close(slave); slave = -1; }
    else { close(in[0]); in[0] = -1; close(out[1]); out[1] = -1; close(err[1]); err[1] = -1; }
    ssize_t received;
    do { received = read(report[0], &failure, sizeof(failure)); } while (received < 0 && errno == EINTR);
    int read_error = errno;
    close(report[0]); report[0] = -1;
    if (received != 0) {
        if (received < 0) failure = read_error;
        kill(-pid, SIGKILL); kill(pid, SIGKILL);
        while (waitpid(pid, NULL, 0) < 0 && errno == EINTR) { }
        errno = failure; goto failed;
    }
    if (terminal) {
        in[1] = dup(master); out[0] = master; master = -1;
        if (in[1] < 0 || cloexec(in[1]) < 0) goto started_failed;
    }
    if (nonblock(in[1]) < 0 || nonblock(out[0]) < 0 || (!terminal && nonblock(err[0]) < 0)) goto started_failed;
    *pid_out = pid; *input_out = in[1]; *output_out = out[0]; *error_out = terminal ? -1 : err[0];
    return 0;
started_failed:
    failure = errno; kill(-pid, SIGKILL); kill(pid, SIGKILL);
    while (waitpid(pid, NULL, 0) < 0 && errno == EINTR) { }
    errno = failure;
failed:
    failure = errno;
    close_pair(in); close_pair(out); close_pair(err); close_pair(report);
    if (master >= 0) close(master);
    if (slave >= 0) close(slave);
    return failure;
}

/* Keep the leader unreaped so its pid/group id cannot be recycled before cleanup. */
API int pisharp_wait_exit(int pid, int *exit_code) {
    siginfo_t info; memset(&info, 0, sizeof(info));
    int result;
    do { result = waitid(P_PID, (id_t)pid, &info, WEXITED | WNOWAIT); } while (result < 0 && errno == EINTR);
    if (result < 0) return errno;
    *exit_code = info.si_code == CLD_EXITED ? info.si_status : 128 + info.si_status;
    return 0;
}
API int pisharp_reap(int pid) { int result; do { result = waitpid(pid, NULL, 0); } while (result < 0 && errno == EINTR); return result < 0 ? errno : 0; }
/* Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/env/daemon/src/sys/unix.rs.
 * A job-control shell can put each background job into its own group. The
 * unreaped leader reserves the session id until we've signalled its members. */
static int kill_session_member(pid_t member, pid_t session) {
    if (member <= 0 || member == session || getsid(member) != session) return 0;
#if defined(__linux__) && defined(SYS_pidfd_open) && defined(SYS_pidfd_send_signal)
    int fd = (int)syscall(SYS_pidfd_open, member, 0);
    if (fd >= 0) {
        int signalled = getsid(member) == session && syscall(SYS_pidfd_send_signal, fd, SIGKILL, NULL, 0) == 0;
        close(fd);
        return signalled;
    }
#endif
    return getsid(member) == session && kill(member, SIGKILL) == 0;
}

static int kill_session_members(pid_t session) {
    int signalled = 0;
#if defined(__APPLE__)
    int count = proc_listallpids(NULL, 0);
    if (count <= 0 || count > INT32_MAX / (int)sizeof(pid_t) - 64) return 0;
    int bytes = (count + 64) * (int)sizeof(pid_t);
    pid_t *members = malloc((size_t)bytes);
    if (!members) return 0;
    count = proc_listallpids(members, bytes);
    for (int i = 0; i < count; ++i) signalled |= kill_session_member(members[i], session);
    free(members);
#else
    DIR *processes = opendir("/proc");
    if (!processes) return 0;
    struct dirent *entry;
    while ((entry = readdir(processes))) {
        char *end;
        long pid = strtol(entry->d_name, &end, 10);
        if (*end == '\0' && pid > 0 && pid <= INT_MAX) signalled |= kill_session_member((pid_t)pid, session);
    }
    closedir(processes);
#endif
    return signalled;
}

API int pisharp_kill_group(int pid) {
    int error = 0;
    if (kill(-pid, SIGKILL) < 0 && kill(pid, SIGKILL) < 0 && errno != ESRCH) error = errno;
    /* A member may fork during enumeration. Bound passes when zombies remain visible. */
    for (int attempt = 0; attempt < 8; ++attempt) if (!kill_session_members(pid)) break;
    return error;
}
API int pisharp_poll(int fd, int writing) { struct pollfd item = { .fd = fd, .events = writing ? POLLOUT : POLLIN }; int result = poll(&item, 1, 100); return result < 0 ? -errno : result; }
API int pisharp_read(int fd, void *buffer, int length) { ssize_t result = read(fd, buffer, (size_t)length); return result < 0 ? -errno : (int)result; }
API int pisharp_write(int fd, const void *buffer, int length) { ssize_t result = write(fd, buffer, (size_t)length); return result < 0 ? -errno : (int)result; }
API void pisharp_close(int fd) { close(fd); }
API int pisharp_resize(int fd, uint16_t cols, uint16_t rows) { struct winsize size = { .ws_row = rows, .ws_col = cols }; return ioctl(fd, TIOCSWINSZ, &size) < 0 ? errno : 0; }
API int pisharp_foreground(int fd, int *pid) { pid_t value = tcgetpgrp(fd); if (value < 0) return errno; *pid = value; return 0; }
API int pisharp_process_name(int pid, char *buffer, int length) {
#if defined(__APPLE__)
    return proc_name(pid, buffer, (uint32_t)length);
#else
    (void)pid; (void)buffer; (void)length; return 0;
#endif
}
