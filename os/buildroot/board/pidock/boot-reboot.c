#include <linux/reboot.h>
#include <sys/syscall.h>
#include <unistd.h>
#include <string.h>
#include <stdio.h>
int main(int argc, char **argv)
{
    if (argc != 2 || (strcmp(argv[1], "1") && strcmp(argv[1], "normal"))) return 2;
    sync();
    if (!strcmp(argv[1], "normal")) syscall(SYS_reboot, LINUX_REBOOT_MAGIC1, LINUX_REBOOT_MAGIC2, LINUX_REBOOT_CMD_RESTART, 0);
    else syscall(SYS_reboot, LINUX_REBOOT_MAGIC1, LINUX_REBOOT_MAGIC2, LINUX_REBOOT_CMD_RESTART2, "1");
    perror("PiDock reboot");
    return 1;
}
