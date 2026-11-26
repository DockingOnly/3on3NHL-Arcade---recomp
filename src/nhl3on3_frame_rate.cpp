// Frame rate: the game's present interval and live frame statistics.
//
// The game's D3D swap ends in sub_822D4C00(value), which queues the frame for the vblank
// handler (sub_822D4AE8) to flip. `value` packs the front buffer address (bits 12-31), the
// present interval in vblanks (bits 8-11) and a late-frame threshold in percent (bits 0-7).
// The flip waits for `last flip + interval` vblanks, so interval 2 at 60 Hz is 30 fps and
// interval 0 flips as soon as the frame is ready.
//
// The game asks for interval 1 (60 fps) everywhere except single loading frames, and its
// simulation advances one fixed step per frame: uncapped, the faceoff countdown ran ~4x
// fast and the scoreboard text stopped drawing. So present_interval is a testing aid, not a
// way past 60 fps.

#include "nhl3on3_frame_rate.h"

#include <atomic>
#include <chrono>
#include <cstdint>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

REXCVAR_DEFINE_INT32(present_interval, -1, "NHL3on3/Video",
                     "Testing only: vblanks per frame (1 = 60 fps, 0 = no limit, -1 = the "
                     "game's own). Game speed follows the frame rate, so 1 is the only "
                     "correct override");

REXCVAR_DEFINE_BOOL(fps_log, false, "NHL3on3/Video",
                    "Log the frame rate and the game's present interval every 5 seconds");

namespace nhl3on3 {
namespace {

using Clock = std::chrono::steady_clock;

std::atomic<uint32_t> g_game_interval{0xFFFFFFFF};
std::atomic<uint64_t> g_frame_count{0};
std::atomic<double> g_fps{0};
std::atomic<double> g_frame_time_ms{0};

// Called once per frame from the swap; only that thread writes the statistics.
void CountFrame(uint32_t game_interval, uint32_t used_interval) {
  static Clock::time_point window_start = Clock::now();
  static uint32_t window_frames = 0;
  static Clock::time_point log_start = window_start;
  static uint32_t log_frames = 0;

  g_frame_count.fetch_add(1, std::memory_order_relaxed);
  ++window_frames;
  ++log_frames;
  const auto now = Clock::now();
  const double window = std::chrono::duration<double>(now - window_start).count();
  if (window >= 0.5) {
    g_fps.store(window_frames / window, std::memory_order_relaxed);
    g_frame_time_ms.store(window * 1000.0 / window_frames, std::memory_order_relaxed);
    window_start = now;
    window_frames = 0;
  }
  if (REXCVAR_GET(fps_log)) {
    const double seconds = std::chrono::duration<double>(now - log_start).count();
    if (seconds >= 5.0) {
      REXLOG_INFO("[video] {:.1f} fps (game interval {}, using {})", log_frames / seconds,
                  game_interval, used_interval);
      log_start = now;
      log_frames = 0;
    }
  }
}

}  // namespace

rex::ui::FrameStats GuestFrameStats() {
  rex::ui::FrameStats stats;
  stats.frame_count = g_frame_count.load(std::memory_order_relaxed);
  stats.fps = g_fps.load(std::memory_order_relaxed);
  stats.frame_time_ms = g_frame_time_ms.load(std::memory_order_relaxed);
  return stats;
}

}  // namespace nhl3on3

extern "C" REX_FUNC(__imp__sub_822D4C00);
extern "C" REX_FUNC(sub_822D4C00) {
  const uint32_t value = ctx.r3.u32;
  const uint32_t interval = (value >> 8) & 0xF;
  if (nhl3on3::g_game_interval.exchange(interval) != interval && REXCVAR_GET(fps_log)) {
    REXLOG_INFO("[video] game present interval {}", interval);
  }
  const int32_t wanted = REXCVAR_GET(present_interval);
  if (wanted >= 0 && wanted <= 15) {
    ctx.r3.u64 = (value & ~0xF00u) | (uint32_t(wanted) << 8);
  }
  nhl3on3::CountFrame(interval, (ctx.r3.u32 >> 8) & 0xF);
  __imp__sub_822D4C00(ctx, base);
}
