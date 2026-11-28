#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <unordered_map>

namespace nhl3on3 {

// Rows this port adds to Help & Options > Visual (tools/ui_mod.py,
// VISUAL_EXTRA_ROWS) borrow NHL 09 setting ids the arcade never uses. The
// settings adapter's getter and setter hand those ids here instead of to the
// game. `id` is the adapter's id (not the script's eSetting).
// Each returns true if `id` is one of these rows.
bool GetVideoSetting(const uint8_t* base, uint32_t id, int32_t& value);
bool SetVideoSetting(const uint8_t* base, uint32_t id, int32_t value);

// Looks up the rows' adapter ids (once the game image is loaded).
void MapVideoRows(const uint8_t* base);

// The borrowed rows' localized titles and the text shown instead, for the
// front end's text overrides (nhl3on3_ui.cpp).
void AddVideoRowTitles(std::unordered_map<std::string, std::string>& renames);

// Runs work on the UI thread (window changes); set by the app once it exists.
void SetUiThreadCaller(std::function<void(std::function<void()>)> caller);

}  // namespace nhl3on3
