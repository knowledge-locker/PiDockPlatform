#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

/* Fixed protocol reader: no arguments, shell, network, diagnostic output or alternate paths. */
int main(int argc, char **argv) {
    (void)argv;
    if (argc != 1) return 3;
    int fd = open("/run/pidock/health/health.json", O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
    if (fd < 0) return errno == ENOENT ? 2 : 3;
    struct stat info;
    if (fstat(fd, &info) != 0 || !S_ISREG(info.st_mode) || info.st_size < 1 || info.st_size > 1024) { close(fd); return 3; }
    char bytes[1025]; size_t length = 0;
    while (length < sizeof bytes) {
        ssize_t count = read(fd, bytes + length, sizeof bytes - length);
        if (count < 0) { if (errno == EINTR) continue; close(fd); return 3; }
        if (count == 0) break;
        length += (size_t)count;
    }
    close(fd);
    if (length < 1 || length > 1024) return 3;
    size_t sent = 0;
    while (sent < length) {
        ssize_t count = write(STDOUT_FILENO, bytes + sent, length - sent);
        if (count < 0) { if (errno == EINTR) continue; return 3; }
        if (count == 0) return 3;
        sent += (size_t)count;
    }
    return 0;
}
