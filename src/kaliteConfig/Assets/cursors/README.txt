Bundled cursor packs.

Each subfolder here is one pack. Drop .cur and .ani files into a subfolder and
it shows up in Personalize > Cursor as a selectable pack.

File names are matched to pointer roles by name, for example:
  arrow.cur  help.cur  appstarting.ani  wait.ani  ibeam.cur  hand.cur
  sizeall.cur  sizens.cur  sizewe.cur  unavail.cur  working.ani

User-imported packs are stored in %LOCALAPPDATA%\kaliteConfig\personalize\
cursors\cursor-packs\<PackName> and appear in the same list.

Generated packs ship animated busy states: wait.ani and appstarting.ani
(8-frame pulse, built from the pack's own art). Regenerate with:
  powershell -NoProfile -ExecutionPolicy Bypass -File src/kaliteConfig/obj/gen-cursor-styles.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File src/kaliteConfig/obj/gen-cursor-ani.ps1
When a role has both files, the app prefers the .ani.
