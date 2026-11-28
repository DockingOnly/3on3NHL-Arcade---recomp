// nhl3on3 - ReXGlue Recompiled Project
//
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

#include <filesystem>
#include <functional>
#include <optional>

#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/logging.h>
#include <rex/rex_app.h>
#include <rex/ui/keybinds.h>
#include <rex/ui/ui_event.h>

#include "kernel_stubs.h"
#include "nhl3on3_frame_rate.h"
#include "nhl3on3_settings.h"
#include "nhl3on3_ui_patch.h"

class Nhl3on3App : public rex::ReXApp {
 public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp> Create(
      rex::ui::WindowedAppContext& ctx) {
    return std::unique_ptr<Nhl3on3App>(new Nhl3on3App(ctx, "nhl3on3",
        PPCImageConfig));
  }

  // Use the Xenos GPU plugin unless --gpu_plugin says otherwise.
  void OnPreSetup(rex::RuntimeConfig& config) override {
    if (config.gpu_plugin.empty()) {
      config.gpu_plugin = "xenos";
    }
  }

  // Find the game files when --game_data_root is not given: a "game" folder next to the
  // executable (release layout), else "game/extracted" in any parent folder (repo layout).
  // In-game options are saved to the config file (nhl3on3.toml next to the executable unless
  // given), see nhl3on3_settings.cpp.
  void OnConfigurePaths(rex::PathConfig& paths) override {
    nhl3on3::SetConfigPath(paths.config_path);
    if (!paths.game_data_root.empty()) {
      return;
    }
    const auto exe_dir = rex::filesystem::GetExecutableFolder();
    if (std::filesystem::exists(exe_dir / "game" / "default.xex")) {
      paths.game_data_root = exe_dir / "game";
      return;
    }
    for (auto dir = exe_dir; dir.has_parent_path() && dir != dir.parent_path();
         dir = dir.parent_path()) {
      if (std::filesystem::exists(dir / "game" / "extracted" / "default.xex")) {
        paths.game_data_root = dir / "game" / "extracted";
        return;
      }
    }
  }

  // Patch the front-end screens in data0.big before the runtime opens the game files (logging
  // is up by now; see nhl3on3_ui_patch.cpp).
  std::optional<rex::PathConfig> OnFinalizePaths(
      const rex::PathConfig& defaults, std::function<void(rex::PathConfig)>) override {
    nhl3on3::ApplyUiPatches(defaults.game_data_root);
    return defaults;
  }

  // The game's Achievements menu item opens the achievements overlay, the same as its hotkey
  // (bind_achievements, F7 by default).
  void OnPostSetup() override {
    SetGuestFrameStats(nhl3on3::GuestFrameStats);
    nhl3on3::SetAchievementsOpener([this] {
      app_context().CallInUIThreadDeferred([] {
        rex::ui::KeyEvent press(nullptr,
                                rex::ui::ParseVirtualKey(rex::cvar::GetFlagByName("bind_achievements")),
                                0, false, false, false, false, false);
        rex::ui::ProcessKeyEvent(press);
      });
    });
  }

  // Other hooks available for customization:
  // void OnPostInitLogging() override {}
  // void OnLoadXexImage(std::string& xex_image) override {}
  // void OnPostLoadXexImage() override {}
  // void OnCreateDialogs(rex::ui::ImGuiDrawer* drawer) override {}
  // std::unique_ptr<rex::ui::ImGuiDialog> CreateAchievementsOverlay() override;
  // std::unique_ptr<rex::ui::AchievementNotificationDialog>
  // CreateAchievementNotificationDialog() override;
  // void OnShutdown() override {}
};
