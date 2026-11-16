/* Signed build time is a plausibility floor, never authenticated time evidence. */
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>

#ifdef PIDOCK_CLOCK_WORKER
int main(int argc, char **argv)
{
    if (argc != 2 || !argv[1][0]) return 2;
    for (const char *p = argv[1]; *p; ++p) if (*p < '0' || *p > '9') return 2;
    errno = 0;
    char *end;
    long long epoch = strtoll(argv[1], &end, 10);
    if (errno || *end || epoch < 946684800LL || epoch > 4102444800LL) return 2;
    struct timespec now;
    if (clock_gettime(CLOCK_REALTIME, &now)) { perror("boot clock read"); return 1; }
    if (now.tv_sec >= epoch) { puts("Clock already meets signed build-time floor."); return 0; }
    struct timespec floor = { .tv_sec = (time_t)epoch, .tv_nsec = 0 };
    if (clock_settime(CLOCK_REALTIME, &floor)) { perror("boot clock adjustment"); return 1; }
    puts("Applied signed build-time floor; authenticated time is still required.");
    return 0;
}

#else
/* A monotonic deadline supervises the setter without relying on a BusyBox applet.
 * Clock failure is nonfatal to verified boot; authentication still requires NTS. */
int main(int argc, char **argv)
{
    if (argc != 2) return 2;
    pid_t child = fork();
    if (child < 0) { perror("boot clock fork"); return 1; }
    if (!child) {
        execl("/usr/lib/pidock/boot-clock-set", "boot-clock-set", argv[1], (char *)0);
        perror("boot clock exec");
        _exit(127);
    }
    struct timespec start, now, pause = { .tv_sec = 0, .tv_nsec = 100000000 };
    if (clock_gettime(CLOCK_MONOTONIC, &start)) { kill(child, SIGKILL); return 1; }
    for (;;) {
        int status;
        pid_t result = waitpid(child, &status, WNOHANG);
        if (result == child) return WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
        if (result < 0 && errno != EINTR) { kill(child, SIGKILL); return 1; }
        if (clock_gettime(CLOCK_MONOTONIC, &now)) { kill(child, SIGKILL); return 1; }
        if (now.tv_sec - start.tv_sec > 5 ||
            (now.tv_sec - start.tv_sec == 5 && now.tv_nsec >= start.tv_nsec)) {
            kill(child, SIGKILL);
            /* Do not let an uninterruptible worker make PID 1 wait indefinitely. */
            (void)waitpid(child, &status, WNOHANG);
            fputs("Boot clock adjustment exceeded five seconds.\n", stderr);
            return 124;
        }
        nanosleep(&pause, NULL);
    }
}
#endif
