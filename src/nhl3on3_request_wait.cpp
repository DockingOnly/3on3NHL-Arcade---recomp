// Fixes the occasional freeze on the loading screen before a match.
//
// sub_82B39B58(manager) makes room in a manager's list of requests in flight: under the manager's
// critical section (manager + 8) it removes and releases the requests that have completed, and
// while the list is still full (count >= manager + 76) it checks again - without leaving the
// critical section. The worker threads that complete those requests take the same critical
// section when they run out of pooled buffers (sub_82B5AD90), so if that happens while the list
// is full, neither side can continue and the game stops on the loading screen.
//
// This is the same function, but it leaves the critical section between checks, so the workers
// can get in. The list is only read or changed under the critical section, and each check starts
// over from its current contents, so nothing the original relies on changes.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>
#include <rex/thread.h>

extern "C" REX_FUNC(__imp__RtlEnterCriticalSection);
extern "C" REX_FUNC(__imp__RtlLeaveCriticalSection);
extern "C" REX_FUNC(sub_82B01BB0);  // Whether a request has completed.
extern "C" REX_FUNC(sub_82B12710);  // Releases a completed request...
extern "C" REX_FUNC(sub_82B39948);  // ...and returns it to its pool.

namespace {

inline uint8_t* GuestHost(uint8_t* base, uint32_t address) {
  return base + address + (address >= 0xE0000000u ? 0x1000u : 0u);
}
inline uint32_t Load32(uint8_t* base, uint32_t address) {
  return __builtin_bswap32(*reinterpret_cast<volatile uint32_t*>(GuestHost(base, address)));
}
inline void Store32(uint8_t* base, uint32_t address, uint32_t value) {
  *reinterpret_cast<volatile uint32_t*>(GuestHost(base, address)) = __builtin_bswap32(value);
}

}  // namespace

extern "C" REX_FUNC(sub_82B39B58) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t critical_section = manager + 8;
  // Requests in flight: [begin, end) array of pointers at manager + 80, room for manager + 76.
  const uint32_t begin_ptr = manager + 80;
  const uint32_t end_ptr = manager + 84;
  const uint32_t capacity_ptr = manager + 76;
  for (;;) {
    ctx.r3.u64 = critical_section;
    __imp__RtlEnterCriticalSection(ctx, base);
    uint32_t kept = 0;
    uint32_t offset = 0;
    while (kept < uint32_t(int32_t(Load32(base, end_ptr) - Load32(base, begin_ptr)) >> 2)) {
      uint32_t slot = Load32(base, begin_ptr) + offset;
      ctx.r3.u64 = Load32(base, slot);
      sub_82B01BB0(ctx, base);
      if (!(ctx.r3.u32 & 0xFF)) {
        ++kept;
        offset += 4;
        continue;
      }
      // Completed: move the last request into its slot and release it.
      uint32_t end = Load32(base, end_ptr);
      uint32_t request = Load32(base, slot);
      Store32(base, slot, Load32(base, end - 4));
      Store32(base, end_ptr, end - 4);
      ctx.r3.u64 = request;
      sub_82B12710(ctx, base);
      ctx.r3.u64 = request;
      sub_82B39948(ctx, base);
    }
    uint32_t count = uint32_t(int32_t(Load32(base, end_ptr) - Load32(base, begin_ptr)) >> 2);
    bool has_room = count < Load32(base, capacity_ptr);
    ctx.r3.u64 = critical_section;
    __imp__RtlLeaveCriticalSection(ctx, base);
    if (has_room) {
      return;
    }
    static std::atomic<bool> logged{false};
    if (!logged.exchange(true, std::memory_order_relaxed)) {
      REXLOG_INFO("[loader] request list {:08X} full, waiting without holding its lock", manager);
    }
    rex::thread::MaybeYield();
  }
}
