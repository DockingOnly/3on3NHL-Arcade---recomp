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
//
// A way back when a restart setting breaks the game:
// - Rollback. Changing one writes video.pending beside the config, holding
//   the values the game is running with. The next start marks it as being
//   tried; reaching the main menu removes it. A start that finds it already
//   being tried means the last one never got to the menu (crash, hang, black
//   screen), so those values go back into the config, and the main menu's
//   ticker says so.
// - Holding Shift, or LB + RB on a controller, while the game starts puts all
//   of these settings back to their defaults (nhl3on3_boot_key.cpp).
// Leaving the settings screen with a restart setting changed shows "restart
// the game to apply" (tools/ui_mod.py asks through kRestartQueryScriptId).

#include "nhl3on3_video_settings.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <optional>
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

// DYNASTY_WAIVER_POPUPS, never listed: the settings screen reads it when the
// player leaves, as 1 while a restart setting has changed.
constexpr uint32_t kRestartQueryScriptId = 75;
uint32_t g_restart_query_adapter = 0xFFFFFFFF;

std::filesystem::path g_pending_path;
// The main menu's ticker text this session, after a rollback or a reset.
std::string g_notice;
std::atomic<bool> g_menu_reached{false};

// Localized texts this file watches for or replaces (VideoTextOverride).
constexpr std::string_view kMenuItem = "PLAY NOW";
constexpr std::string_view kMenuTicker =
    "SELECT AN ONLINE FEATURE IN ORDER TO SIGN IN TO THE EA SERVERS";
// The start of the settings screen's storage-device message
// (DeviceHasChangedOrNoLongerAvailable), which tools/ui_mod.py shows when a
// restart setting changed; no storage device ever changes here.
constexpr std::string_view kRestartPopupText = "WARNING!  YOUR SELECTED STORAGE DEVICE";

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
  const uint32_t query = adapter_id(kRestartQueryScriptId);
  bool shared = false;
  for (uint32_t s = 0; s <= kMaxScriptId; ++s) {
    const bool unused = s >= kFirstUnusedScriptId && s <= kLastUnusedScriptId;
    shared = shared || (s != kRestartQueryScriptId && !unused && adapter_id(s) == query);
  }
  for (const auto& row : g_rows) {
    shared = shared || row.adapter_id == query;
  }
  g_restart_query_adapter = shared ? kNoRow : query;
  if (shared) {
    REXLOG_WARN("[visual] no restart prompt: adapter setting {} is shared", query);
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

// Caller holds g_mutex.
bool RestartPending() {
  for (const auto& row : g_rows) {
    if (row.pending >= 0) {
      return true;
    }
  }
  return false;
}

std::string Unquote(std::string value) {
  if (value.size() >= 2 && value.front() == '"' && value.back() == '"') {
    value = value.substr(1, value.size() - 2);
  }
  return value;
}

struct PendingFile {
  bool trying = false;
  std::vector<std::pair<std::string, std::string>> values;  // key, TOML value
};

std::optional<PendingFile> ReadPending() {
  if (g_pending_path.empty()) {
    return std::nullopt;
  }
  std::ifstream in(g_pending_path);
  if (!in) {
    return std::nullopt;
  }
  PendingFile file;
  for (std::string line; std::getline(in, line);) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    const size_t eq = line.find(" = ");
    if (line.empty() || line[0] == '#' || eq == std::string::npos) {
      continue;
    }
    const std::string key = line.substr(0, eq), value = line.substr(eq + 3);
    if (key == "state") {
      file.trying = Unquote(value) == "trying";
    } else {
      file.values.emplace_back(key, value);
    }
  }
  return file;
}

void WritePending(const PendingFile& file) {
  std::ofstream out(g_pending_path, std::ios::trunc);
  out << "# Written by nhl3on3 when a video setting that applies on restart changed.\n"
         "# The values below are the ones the game ran with before. If the game does\n"
         "# not reach its main menu with the new ones, it puts these back.\n"
      << "state = \"" << (file.trying ? "trying" : "saved") << "\"\n";
  for (const auto& [key, value] : file.values) {
    out << key << " = " << value << "\n";
  }
}

void RemovePending() {
  std::error_code ec;
  std::filesystem::remove(g_pending_path, ec);
}

// Caller holds g_mutex. Records the running values of the restart rows
// changed this session; no file once none differs from what the game runs.
void UpdatePending() {
  if (g_pending_path.empty()) {
    return;
  }
  PendingFile file;
  for (const auto& row : g_rows) {
    if (row.pending >= 0) {
      const std::string running = rex::cvar::GetFlagByName(row.cvar);
      file.values.emplace_back(row.cvar,
                               row.quoted ? fmt::format("\"{}\"", running) : running);
    }
  }
  if (file.values.empty()) {
    RemovePending();
  } else {
    WritePending(file);
  }
}

}  // namespace

namespace nhl3on3 {

bool GetVideoSetting(const uint8_t* base, uint32_t id, int32_t& value) {
  std::lock_guard lock(g_mutex);
  MapOnce(base);
  if (id == g_restart_query_adapter && id != kNoRow) {
    value = RestartPending() ? 1 : 0;
    return true;
  }
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
    MapOnce(base);
    if (id == g_restart_query_adapter && id != kNoRow) {
      return true;
    }
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
        // Back to what the game runs with: nothing left to restart for.
        row->pending = choice == rex::cvar::GetFlagByName(row->cvar) ? -1 : value;
        UpdatePending();
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

void StartVideoSettings(const std::filesystem::path& config_path, bool reset) {
  std::lock_guard lock(g_mutex);
  g_pending_path = config_path.parent_path() / "video.pending";
  if (reset) {
    for (const auto& row : g_rows) {
      RemoveConfigValue(row.cvar);
      rex::cvar::ResetToDefault(row.cvar);
    }
    RemovePending();
    g_notice = "VIDEO SETTINGS ARE BACK TO THEIR DEFAULTS";
    REXLOG_WARN("[visual] boot key held: video settings reset to their defaults");
    return;
  }
  auto file = ReadPending();
  if (!file) {
    return;
  }
  if (!file->trying) {
    file->trying = true;
    WritePending(*file);
    REXLOG_INFO("[visual] starting with changed video settings; they stay once the main "
                "menu shows");
    return;
  }
  // The last start with these settings never reached the main menu.
  for (const auto& [key, value] : file->values) {
    SaveConfigValue(key, value);
    rex::cvar::SetFlagByName(key, Unquote(value));
    REXLOG_WARN("[visual] the last start never reached the main menu; {} back to {}", key,
                value);
  }
  rex::cvar::ClearPendingRestartFlags();
  RemovePending();
  g_notice = "VIDEO SETTINGS PUT BACK: THE GAME DID NOT START WITH THE LAST ONES";
}

std::optional<std::string> VideoTextOverride(std::string_view text) {
  const size_t end = text.find_last_not_of(' ');
  text = text.substr(0, end == std::string_view::npos ? 0 : end + 1);
  std::lock_guard lock(g_mutex);
  if (text == kMenuItem && !g_menu_reached.exchange(true)) {
    if (const auto file = ReadPending(); file && file->trying) {
      RemovePending();
      REXLOG_INFO("[visual] main menu reached; the changed video settings stay");
    }
  }
  if (text == kMenuTicker && !g_notice.empty()) {
    return g_notice;
  }
  if (text.starts_with(kRestartPopupText)) {
    std::string names;
    for (const auto& row : g_rows) {
      if (row.pending >= 0) {
        std::string_view title = row.title;
        title = title.substr(0, title.find(" ("));
        names += fmt::format("{}{}", names.empty() ? "" : " AND ", title);
      }
    }
    return fmt::format("RESTART THE GAME TO APPLY THE NEW {}.", names);
  }
  return std::nullopt;
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
