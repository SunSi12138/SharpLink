#define _GNU_SOURCE
#include <errno.h>
#include <inttypes.h>
#include <pthread.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/resource.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

static atomic_uint_fast64_t sink;
static atomic_int go;
static atomic_long worker_tid;
static uint64_t mono(void) {
    struct timespec t;
    if (clock_gettime(CLOCK_MONOTONIC, &t)) { perror("clock_gettime"); exit(2); }
    return (uint64_t)t.tv_sec * UINT64_C(1000000000) + t.tv_nsec;
}
static uint64_t cpu_user(void) {
    struct rusage r;
    if (getrusage(RUSAGE_SELF, &r)) { perror("getrusage"); exit(2); }
    return (uint64_t)r.ru_utime.tv_sec * UINT64_C(1000000000) + r.ru_utime.tv_usec * UINT64_C(1000);
}
#define BUSY(name) __attribute__((noinline)) static void name(uint64_t ns) { \
    uint64_t until = mono() + ns, x = 17; \
    do { for (int i = 0; i < 20000; i++) x = x * UINT64_C(1664525) + UINT64_C(1013904223); } while (mono() < until); \
    atomic_store(&sink, x); }
BUSY(PerfBusyBefore)
BUSY(PerfBusyInside)
BUSY(PerfWorkerInside)
BUSY(PerfBusyAfter)
__attribute__((noinline)) static void PerfSleepInside(void) {
    struct timespec t = {1, 0};
    while (nanosleep(&t, &t) && errno == EINTR) { }
}
static void* worker(void* unused) {
    (void)unused;
    atomic_store(&worker_tid, syscall(SYS_gettid));
    while (!atomic_load(&go)) { struct timespec t = {0, 1000000}; nanosleep(&t, NULL); }
    PerfWorkerInside(UINT64_C(800000000));
    return NULL;
}
int main(int argc, char** argv) {
    if (argc != 2) { fprintf(stderr, "calibration OUTPUT_JSON\n"); return 2; }
    pthread_t thread;
    if (pthread_create(&thread, NULL, worker, NULL)) return 3;
    PerfBusyBefore(UINT64_C(600000000));
    uint64_t cpu0 = cpu_user(), begin = mono();
    atomic_store(&go, 1);
    PerfBusyInside(UINT64_C(800000000));
    if (pthread_join(thread, NULL)) return 4;
    uint64_t sleep_begin = mono();
    PerfSleepInside();
    uint64_t end = mono(), cpu1 = cpu_user();
    PerfBusyAfter(UINT64_C(600000000));
    FILE* f = fopen(argv[1], "w");
    if (!f) { perror("fopen"); return 5; }
    fprintf(f, "{\"schemaVersion\":1,\"clock\":\"CLOCK_MONOTONIC\",\"clockId\":1,\"pid\":%ld,\"workerTid\":%ld,\"beginNs\":%" PRIu64 ",\"sleepBeginNs\":%" PRIu64 ",\"endNs\":%" PRIu64 ",\"userCpuNs\":%" PRIu64 "}\n", (long)getpid(), atomic_load(&worker_tid), begin, sleep_begin, end, cpu1-cpu0);
    if (fclose(f)) return 6;
    return atomic_load(&sink) == 0 ? 7 : 0;
}
