// The boot key: Shift, or LB + RB on any controller, held while the game
// starts puts the video settings back to their defaults
// (nhl3on3_video_settings.cpp). Read once, before the window opens, so it
// works whichever window has focus.

#include "nhl3on3_video_settings.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <xinput.h>
#endif

namespace nhl3on3 {

bool BootKeyHeld() {
#ifdef _WIN32
  if (GetAsyncKeyState(VK_SHIFT) & 0x8000) {
    return true;
  }
  // XInput is loaded here rather than linked, so a missing DLL costs only the
  // controller half of the check.
  HMODULE xinput = LoadLibraryW(L"xinput1_4.dll");
  if (!xinput) {
    xinput = LoadLibraryW(L"xinput9_1_0.dll");
  }
  if (!xinput) {
    return false;
  }
  using GetState = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
  const auto get_state = reinterpret_cast<GetState>(GetProcAddress(xinput, "XInputGetState"));
  bool held = false;
  constexpr WORD kBoth = XINPUT_GAMEPAD_LEFT_SHOULDER | XINPUT_GAMEPAD_RIGHT_SHOULDER;
  for (DWORD pad = 0; get_state && pad < XUSER_MAX_COUNT && !held; ++pad) {
    XINPUT_STATE state{};
    held = get_state(pad, &state) == ERROR_SUCCESS && (state.Gamepad.wButtons & kBoth) == kBoth;
  }
  FreeLibrary(xinput);
  return held;
#else
  return false;
#endif
}

}  // namespace nhl3on3
