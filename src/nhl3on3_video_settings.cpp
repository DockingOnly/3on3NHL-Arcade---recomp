// Video settings in the game's own Help & Options > Visual screen.
//
// The rows are NHL 09 settings the arcade defines but never lists (Dynasty and
// Tournament options); tools/ui_mod.py (patch_game_settings) adds them to the
// Visual category and gives them their choices. The screen reads a row through
// the settings adapter's getter (sub_82580628) and changes it through its
// setter (sub_82580C90), both keyed by the adapter's id, which the game derives
// from the script's eSetting id through a table (kScriptToAdapter). Those ids
// are answered here from the runtime's settings and never reach the game.
//
// A change is saved to nhl3on3.toml at once. Rows that apply on restart keep
// the running value: the row shows the choice, the file holds it, and the
// game uses it next time (the runtime sizes its render targets and output
// effect at startup).

#include "nhl3on3_video_settings.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include <fmt/format.h>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "nhl3on3_settings.h"

namespace {

// int32 adapter id per script eSetting id (the lookup in sub_8258B668 and
// sub_82571C70).
constexpr uint32_t kScriptToAdapter = 0x82FC2658;
constexpr uint32_t kMaxScriptId = 127;

enum class Apply { kLive, kUiThread, kRestart };

struct Row {
  const char* setting;        // borrowed eSetting name, as in tools/ui_mod.py
  uint32_t script_id;         // its value (fe/ion/main.big)
  const char* borrowed_title; // the borrowed row's localized title
  const char* title;          // shown instead
  const char* cvar;
  Apply apply;
  bool quoted;                      // a string setting (TOML value in quotes)
  std::vector<const char*> choices; // cvar value of each choice, in the row's order
  uint32_t adapter_id = 0;
  int32_t pending = -1;             // restart rows: the choice saved for next time
};

std::mutex g_mutex;
// Order and choices match VISUAL_EXTRA_ROWS in tools/ui_mod.py.
std::array<Row, 6> g_rows{{
    {"DYNASTY_SALARYCAP", 69, "NHL SALARY CAP", "FULLSCREEN", "fullscreen", Apply::kUiThread,
     false, {"false", "true"}},
    {"DYNASTY_CPU_TRADES", 77, "CPU TRADES", "VSYNC", "vsync", Apply::kLive, false,
     {"false", "true"}},
    {"DYNASTY_SEASON_OVERTIME_LOSS", 70, "SEASON OVERTIME LOSS", "LETTERBOX", "present_letterbox",
     Apply::kLive, false, {"false", "true"}},
    {"DYNASTY_PLAYOFFS_LENGTH", 71, "PLAYOFF SERIES LENGTH", "TEXTURE FILTERING",
     "anisotropic_override", Apply::kLive, false, {"0", "3", "4", "5"}},
    {"DYNASTY_PLAYOFFS_TIE_BREAK", 72, "PLAYOFF TIE BREAK", "RESOLUTION (RESTART)",
     "resolution_scale", Apply::kRestart, false, {"1", "2", "3"}},
    {"DYNASTY_TRADE_POPUPS", 74, "TRADE OFFER NOTIFICATIONS", "UPSCALING (RESTART)",
     "present_effect", Apply::kRestart, true, {"bilinear", "fsr", "cas"}},
}};
std::atomic<bool> g_mapped{false};
std::function<void(std::function<void()>)> g_ui_thread;

uint32_t Load32(const uint8_t* base, uint32_t address) {
  const uint8_t* p = base + address;
  return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3];
}

// The script's Dynasty and Tournament settings (eSetting 66-87), none of
// which the arcade shows.
constexpr uint32_t kFirstUnusedScriptId = 66, kLastUnusedScriptId = 87;
constexpr uint32_t kNoRow = 0xFFFFFFFF;

// Caller holds g_mutex. A row takes its adapter id only if nothing else on
// the screen can reach it: no setting the arcade uses, and no other row, maps
// to the same adapter id. Otherwise the row would change that setting too, so
// it is left with the game.
void MapOnce(const uint8_t* base) {
  if (g_mapped.load(std::memory_order_relaxed)) {
    return;
  }
  const auto adapter_id = [&](uint32_t script_id) {
    return Load32(base, kScriptToAdapter + 4 * script_id);
  };
  for (auto& row : g_rows) {
    row.adapter_id = kNoRow;
    if (row.script_id > kMaxScriptId) {
      continue;
    }
    const uint32_t id = adapter_id(row.script_id);
    bool shared = false;
    for (uint32_t s = 0; s <= kMaxScriptId && !shared; ++s) {
      const bool unused = s >= kFirstUnusedScriptId && s <= kLastUnusedScriptId;
      shared = s != row.script_id && !unused && adapter_id(s) == id;
    }
    for (const auto& other : g_rows) {
      shared = shared || (&other != &row && other.script_id <= kMaxScriptId &&
                          adapter_id(other.script_id) == id);
    }
    if (shared) {
      REXLOG_WARN("[visual] {} left out: adapter setting {} is shared", row.title, id);
      continue;
    }
    row.adapter_id = id;
    REXLOG_INFO("[visual] {} ({}) is adapter setting {}", row.title, row.setting, id);
  }
  g_mapped.store(true, std::memory_order_relaxed);
}

Row* Find(const uint8_t* base, uint32_t id) {
  MapOnce(base);
  for (auto& row : g_rows) {
    if (row.adapter_id == id && id != kNoRow) {
      return &row;
    }
  }
  return nullptr;
}

// The choice showing the setting's current value. Values the menu does not
// offer show as the nearest choice: no override, 1x or 2x anisotropic
// filtering as off, the temporal FSR variants as FSR.
int32_t CurrentChoice(const Row& row) {
  if (row.pending >= 0) {
    return row.pending;
  }
  std::string value = rex::cvar::GetFlagByName(row.cvar);
  if (value == "fsr2" || value == "fsr3") {
    value = "fsr";
  }
  if (std::string_view(row.cvar) == "anisotropic_override" && value != "4" && value != "5") {
    value = value == "3" ? "3" : "0";
  }
  for (size_t i = 0; i < row.choices.size(); ++i) {
    if (value == row.choices[i]) {
      return int32_t(i);
    }
  }
  return 0;
}

}  // namespace

namespace nhl3on3 {

bool GetVideoSetting(const uint8_t* base, uint32_t id, int32_t& value) {
  std::lock_guard lock(g_mutex);
  const Row* row = Find(base, id);
  if (!row) {
    return false;
  }
  value = CurrentChoice(*row);
  return true;
}

bool SetVideoSetting(const uint8_t* base, uint32_t id, int32_t value) {
  std::function<void()> on_ui_thread;
  {
    std::lock_guard lock(g_mutex);
    Row* row = Find(base, id);
    if (!row) {
      return false;
    }
    if (value < 0 || size_t(value) >= row->choices.size() || value == CurrentChoice(*row)) {
      return true;
    }
    const std::string choice = row->choices[value];
    const std::string toml = row->quoted ? fmt::format("\"{}\"", choice) : choice;
    REXLOG_INFO("[visual] {} = {}{}", row->title, choice,
                row->apply == Apply::kRestart ? " (on restart)" : "");
    switch (row->apply) {
      case Apply::kRestart:
        row->pending = value;
        break;
      case Apply::kLive:
        rex::cvar::SetFlagByName(row->cvar, choice);
        break;
      case Apply::kUiThread:
        on_ui_thread = [cvar = std::string(row->cvar), choice] {
          rex::cvar::SetFlagByName(cvar, choice);
        };
        break;
    }
    SaveConfigValue(row->cvar, toml);
    if (on_ui_thread && g_ui_thread) {
      g_ui_thread(std::move(on_ui_thread));
      on_ui_thread = nullptr;
    }
  }
  if (on_ui_thread) {
    on_ui_thread();
  }
  return true;
}

void MapVideoRows(const uint8_t* base) {
  std::lock_guard lock(g_mutex);
  MapOnce(base);
}

void AddVideoRowTitles(std::unordered_map<std::string, std::string>& renames) {
  for (const auto& row : g_rows) {
    renames.emplace(row.borrowed_title, row.title);
  }
}

void SetUiThreadCaller(std::function<void(std::function<void()>)> caller) {
  std::lock_guard lock(g_mutex);
  g_ui_thread = std::move(caller);
}

}  // namespace nhl3on3

extern "C" void __imp__sub_82580628(PPCContext& __restrict ctx, uint8_t* base);

// Settings adapter getter(adapter, id) -> value.
extern "C" REX_FUNC(sub_82580628) {
  int32_t value = 0;
  if (nhl3on3::GetVideoSetting(base, ctx.r4.u32, value)) {
    ctx.r3.s64 = value;
    return;
  }
  __imp__sub_82580628(ctx, base);
}
