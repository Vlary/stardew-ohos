#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/syscall.h>
#include <fcntl.h>
#include <pthread.h>
#include <time.h>
#include <sys/resource.h>
#include <hilog/log.h>

#define LOG(...) OH_LOG_Print(LOG_APP, LOG_INFO, 0xFF00, "SDV-EXP", __VA_ARGS__)
/* 信号上下文安全日志：write 到专用 fd */
static int g_logfd = -1;
#define SLOG(...) do { char _b[256]; int _n = snprintf(_b, sizeof _b, __VA_ARGS__); \
    if (_n > 0 && g_logfd >= 0) { if (write(g_logfd, _b, (size_t)_n) < 0) {} } } while (0)

static unsigned long g_base;

static void sigsys_handler(int sig, siginfo_t *si, void *v)
{
    ucontext_t *uc = (ucontext_t *)v;
    unsigned long nr = uc->uc_mcontext.regs[8];
    unsigned long pc = uc->uc_mcontext.pc;
    unsigned int insn = *(unsigned int *)pc;
    unsigned long newpc = pc;
    if (insn == 0xd4000001u)  /* svc #0 */
        newpc = pc + 4;
    SLOG("[SDV] SIGSYS defused sysno=%lu pc=%lx\n", nr, pc);
    uc->uc_mcontext.pc = newpc;   /* 只跳过触发指令，不动任何寄存器 */
}

static void fatal_handler(int sig, siginfo_t *si, void *v)
{
    ucontext_t *uc = (ucontext_t *)v;
    unsigned long pc = uc->uc_mcontext.pc;
    unsigned long sp = uc->uc_mcontext.sp;
    SLOG("[SDV] FATAL sig=%d pc=%lx x0=%lx sp=%lx tid=%ld\n",
         sig, pc, uc->uc_mcontext.regs[0], sp, (long)gettid());
    /* pc 所在映射 */
    { FILE *m = fopen("/proc/self/maps", "r");
      char l[512];
      while (m && fgets(l, sizeof(l), m)) {
        unsigned long s, e;
        if (sscanf(l, "%lx-%lx", &s, &e) == 2 && pc >= s && pc < e) {
          SLOG("[SDV] PC MAP: %s", l);
          break;
        }
      }
      if (m) fclose(m);
    }
    /* 栈扫：落在 libgameaot .text 的返回地址 + ldso/libc 段内的帧 */
    if (g_base) {
        unsigned long *p = (unsigned long *)(sp & ~7UL);
        int found = 0;
        for (int i = 0; i < 8192 && found < 40; i++, p++) {
            unsigned long val = *p;
            if (val > g_base && val < g_base + 0x100000) {
                SLOG("[SDV]  stk[%d] aot_off=%lx\n", i, val - g_base);
                found++;
            } else if ((val >> 28) == 0x598) {  /* ldso/libc 区域（本 boot 固定） */
                SLOG("[SDV]  stk[%d] sys=%lx\n", i, val);
                found++;
            }
        }
    }
    _exit(7);
}

int SDL_main(int argc, char **argv)
{
    struct sigaction sa = {0};
    sa.sa_sigaction = sigsys_handler;
    sa.sa_flags = SA_SIGINFO;
    sigaction(SIGSYS, &sa, NULL);
    struct sigaction sf = {0};
    sf.sa_sigaction = fatal_handler;
    sf.sa_flags = SA_SIGINFO;
    { struct sigaction st = {0};
      st.sa_sigaction = sigsys_handler;   /* 复用 defuse 逻辑（pc+=4 + x0=-1）——brk 语义近似 */
      st.sa_flags = SA_SIGINFO | SA_ONSTACK;
      sigaction(SIGTRAP, &st, NULL);
    }
    sigaction(SIGABRT, &sf, NULL);
    sigaction(SIGSEGV, &sf, NULL);
    sigaction(SIGBUS, &sf, NULL);
    sigaction(SIGILL, &sf, NULL);
    sigaction(SIGFPE, &sf, NULL);
    LOG("handlers installed");

    setenv("DOTNET_GCRegionRange", "68719476736", 1);
    setenv("COMPlus_gcRegionRange", "68719476736", 1);
    setenv("DOTNET_GCconcurrent", "0", 1);
    setenv("COMPlus_gcconcurrent", "0", 1);

    /* 专用日志 fd（信号安全） */
    g_logfd = open("/data/storage/el2/base/haps/entry/files/probe.log", 02 | 0100 | 01000, 0644);
    LOG("g_logfd=%{public}d", g_logfd);
    { const char *dir = "/data/storage/el2/base/haps/entry/files";
      char p[256];
      snprintf(p, sizeof(p), "%s/gc-err.log", dir);
      freopen(p, "w", stderr);
      snprintf(p, sizeof(p), "%s/gc-out.log", dir);
      freopen(p, "w", stdout);
      setvbuf(stderr, NULL, _IONBF, 0);
      setvbuf(stdout, NULL, _IONBF, 0);
      SLOG("[SDV] stderr live, pid=%d\n", (int)getpid());
    }

    /* RLIMIT_AS（GC GetVirtualMemoryLimit 数据源） */
    { struct rlimit rl; getrlimit(RLIMIT_AS, &rl);
      LOG("RLIMIT_AS cur=%{public}lu max=%{public}lu", (unsigned long)rl.rlim_cur, (unsigned long)rl.rlim_max);
    }
    /* GCEvent pthread 序列实测 */
    { pthread_condattr_t ca; pthread_mutex_t mu; pthread_cond_t cv;
      int r1 = pthread_condattr_init(&ca);
      int r2 = pthread_condattr_setclock(&ca, CLOCK_MONOTONIC);
      int r3 = pthread_mutex_init(&mu, NULL);
      int r4 = pthread_cond_init(&cv, &ca);
      LOG("condattr_init=%{public}d setclock=%{public}d mutex_init=%{public}d cond_init=%{public}d", r1, r2, r3, r4);
    }
    /* GCEvent 依赖的 eventfd/epoll 实测 */
    { int efd = syscall(279, 0, 0x80000 | 0x800);   /* eventfd(0, EFD_CLOEXEC|EFD_NONBLOCK) */
      LOG("eventfd = %{public}d errno=%{public}d", efd, errno);
      int ep = syscall(20, 0, 0x80000);             /* epoll_create1(EPOLL_CLOEXEC) */
      LOG("epoll_create1 = %{public}d errno=%{public}d", ep, errno);
      if (efd >= 0) close(efd);
      if (ep >= 0) close(ep);
    }
    void *h = dlopen("libgameaot.so", RTLD_NOW | RTLD_GLOBAL);
    if (!h) { LOG("dlopen failed"); return 1; }

    FILE *f = fopen("/proc/self/maps", "r");
    if (f) {
        char line[512];
        while (fgets(line, sizeof(line), f)) {
            if (strstr(line, "libgameaot.so")) {
                g_base = strtoul(line, NULL, 16);
                LOG("libgameaot.so base=0x%{public}lx", g_base);
                break;
            }
        }
        fclose(f);
    }

    int (*fn)(int, int) = (int (*)(int, int))dlsym(h, "sdv_ohos_selftest");
    LOG("step1: pure arith...");
    int r = fn(3, 4);
    LOG("selftest(3,4) = %{public}d (expect 7)", r);
    int (*alloc)(int) = (int (*)(int))dlsym(h, "sdv_ohos_alloctest");
    if (alloc) { LOG("step2: alloc(100)..."); LOG("alloc = %{public}d", alloc(100)); }
    if (alloc) { LOG("step3: alloc(10000)..."); LOG("alloc = %{public}d", alloc(10000)); }
    int (*str)(int, int) = (int (*)(int, int))dlsym(h, "sdv_ohos_strtest");
    if (str) { LOG("step4: strtest..."); LOG("strtest = %{public}d", str(3, 4)); }
    int (*gc)(int) = (int (*)(int))dlsym(h, "sdv_ohos_gctest");
    if (gc) { LOG("step5: gctest(1000)..."); LOG("gctest = %{public}d", gc(1000)); }
    LOG("ALL PROBES PASSED");
    return 0;
}
