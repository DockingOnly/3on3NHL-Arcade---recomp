// Frame rate: the game's present interval and live frame statistics.

#pragma once

#include <rex/ui/overlay/debug_overlay.h>

namespace nhl3on3 {

// Frames the game handed to D3D, averaged over the last half second; shown by the F3
// debug overlay.
rex::ui::FrameStats GuestFrameStats();

}  // namespace nhl3on3
