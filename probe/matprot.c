#define _GNU_SOURCE
#include <errno.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <signal.h>
#include <unistd.h>
#include <string.h>
#include <hilog/log.h>

#define LOG(...) OH_LOG_Print(LOG_APP, LOG_INFO, 0xFF00, "SDV-MAT", __VA_ARGS__)

static void sigsys_handler(int sig, siginfo_t *si, void *v)
{
    ucontext_t *uc = (ucontext_t *)v;
    OH_LOG_Print(LOG_APP, LOG_ERROR, 0xFF00, "SDV-MAT",
        "SIGSYS! sysno=%{public}lu pc=0x%{public}lx", uc->uc_mcontext.regs[8], uc->uc_mcontext.pc);
    _exit(9);
}

static volatile long g_sink;

#define TRY_MPROTECT(addr, len, prot, name) do { \
    g_sink = (long)mprotect(addr, len, prot); \
    LOG("mprotect %-28s -> ret=%{public}ld errno=%{public}d", name, g_sink, errno); \
} while (0)

int SDL_main(int argc, char **argv)
{
    struct sigaction sa = {0};
    sa.sa_sigaction = sigsys_handler;
    sa.sa_flags = SA_SIGINFO;
    sigaction(SIGSYS, &sa, NULL);
    LOG("=== mprotect/mmap permission matrix experiment begins ===");

    void *p = mmap(NULL, 0x10000, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    LOG("mmap RW = %{public}p", p);
    if (p == MAP_FAILED) return 1;

    TRY_MPROTECT(p, 0x10000, PROT_READ, "RW->R");
    TRY_MPROTECT(p, 0x10000, PROT_READ | PROT_WRITE, "R->RW");
    TRY_MPROTECT(p, 0x10000, PROT_READ | PROT_EXEC, "RW->RX");
    TRY_MPROTECT(p, 0x10000, PROT_READ | PROT_WRITE | PROT_EXEC, "RX->RWX");
    TRY_MPROTECT(p, 0x10000, PROT_READ | PROT_WRITE, "RWX->RW");
    TRY_MPROTECT(p, 0x10000, PROT_NONE, "RW->NONE");

    void *x = mmap(NULL, 0x10000, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    LOG("mmap RWX = %{public}p errno=%{public}d", x, errno);
    void *y = mmap(NULL, 0x10000, PROT_READ | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    LOG("mmap RX = %{public}p errno=%{public}d", y, errno);

    /* If RWX memory can be obtained, actually execute it (write NOP and jump) to verify */
    if (x != MAP_FAILED) {
        unsigned char *c = (unsigned char *)x;
        c[0] = 0x1f; c[1] = 0x20; c[2] = 0x03; c[3] = 0xd5;  /* nop */
        c[4] = 0xc0; c[5] = 0x03; c[6] = 0x5f; c[7] = 0xd6;  /* ret */
        typedef void (*fn)(void);
        __builtin___clear_cache((char*)c, (char*)c+8);
        ((fn)c)();
        LOG("RWX memory execution succeeded!");
    }
    void *big = mmap(NULL, 0x100000000UL, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    LOG("mmap 4G PROT_NONE = %{public}p errno=%{public}d", big, errno);
    if (big != MAP_FAILED) {
        g_sink = (long)mprotect(big, 0x10000, PROT_READ | PROT_WRITE);
        LOG("mprotect NONE->RW = %{public}ld errno=%{public}d", g_sink, errno);
    }
    /* membarrier: 223 */
    g_sink = (long)syscall(223, 0, 0, 0);
    LOG("membarrier QUERY = %{public}ld errno=%{public}d", g_sink, errno);
    g_sink = (long)syscall(223, 1<<16 | 1<<17, 0, 0);
    LOG("membarrier REG(GLOBAL_EXPEDITED) = %{public}ld errno=%{public}d", g_sink, errno);
    LOG("=== Experiment finished ===");
    return 0;
}
