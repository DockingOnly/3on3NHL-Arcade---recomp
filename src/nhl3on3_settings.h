#pragma once

#include <filesystem>
#include <string_view>

namespace nhl3on3 {

// The config file game_settings is written back to when an option changes.
void SetConfigPath(const std::filesystem::path& path);

// Writes `key = toml_value` into that file (toml_value is TOML: true, 3,
// "fsr"), replacing the key's line or adding one; the rest is left alone.
void SaveConfigValue(std::string_view key, std::string_view toml_value);

// In an online game every console runs the same simulation, so options
// (CPU difficulty, rules, ...) must agree; nothing sends one console's to the
// others. While true, the options game_settings changed are back at the
// game's defaults, the same on every console; false restores the saved ones.
// Takes effect the next time the game uses its settings.
void UseDefaultGameSettings(bool use_defaults);

}  // namespace nhl3on3
