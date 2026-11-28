#pragma once

#include <cstdint>
#include <filesystem>
#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>

namespace nhl3on3 {

// Rows this port adds to Help & Options > Visual (tools/ui_mod.py,
// VISUAL_EXTRA_ROWS) borrow NHL 09 setting ids the arcade never uses. The
// settings adapter's getter and setter hand those ids here instead of to the
// game. `id` is the adapter's id (not the script's eSetting).
// Each returns true if `id` is one of these rows.
bool GetVideoSetting(const uint8_t* base, uint32_t id, int32_t& value);
bool SetVideoSetting(const uint8_t* base, uint32_t id, int32_t value);

// At startup, once the config is loaded and before the window and renderer
// exist: with `reset` (the boot key, BootKeyHeld) puts every row's setting
// back to its default; otherwise rolls back restart settings the last start
// never reached the main menu with. config_path is nhl3on3.toml; the rollback
// file (video.pending) sits beside it.
void StartVideoSettings(const std::filesystem::path& config_path, bool reset);

// Shift, or LB + RB on any controller, held while the game starts (Windows).
bool BootKeyHeld();

// Front-end text this file replaces, given each localized string
// (nhl3on3_ui.cpp): the main menu's ticker after a rollback or reset, and the
// "restart to apply" popup. Also notes the main menu showing.
std::optional<std::string> VideoTextOverride(std::string_view text);

// Looks up the rows' adapter ids (once the game image is loaded).
void MapVideoRows(const uint8_t* base);

// The borrowed rows' localized titles and the text shown instead, for the
// front end's text overrides (nhl3on3_ui.cpp).
void AddVideoRowTitles(std::unordered_map<std::string, std::string>& renames);

// Runs work on the UI thread (window changes); set by the app once it exists.
void SetUiThreadCaller(std::function<void(std::function<void()>)> caller);

}  // namespace nhl3on3
