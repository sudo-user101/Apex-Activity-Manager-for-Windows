# Changelog

## 1.4.1 (2026-10-28)

- **Smoother.** The process list only makes the rows you can see and
  updates them in place; while you scroll, it holds still and catches up a
  moment after. Services, Startup apps and Users build rows as they scroll
  into view; graphs reuse their shapes. Measured on the build's Windows
  machine: a Processes update went from 22.5 ms to 2.8 ms (worst 34.7 →
  4.9 ms), under one screen frame.
- **Analyze shows results straight away:** the top level at once, each
  folder filled in as soon as it's measured, biggest first.
- **Network:** apps you open in the connection list stay open while it
  refreshes.

## 1.4.0 (2026-10-28)

- **Lumen AI** in the Assistant: CyberNet Labs' own cloud AI, as on the Mac.
  Until it opens, the Assistant says it's coming soon and shows the two
  ways in (Monthly Plan, API Access). Once open: choose one, paste the Lumen
  key from your CyberNet Labs account; Apex checks it, then keeps it in
  Windows' Credential Manager. Answers stream in; the Assistant says that
  your question and the PC's readings go to Lumen. Sign Out removes the key.

## 1.3.0 (2026-10-28)

- **Memory compression:** the Memory page shows whether it's on and what it
  holds (also in the composition bar), the page file in use, and memory read
  back from disk each second. A button explains it and turns it on or off
  with Windows' own setting (administrator; takes effect after a restart).
- **Analyze** any drive, not only your files: one walk per drive, folders
  read in parallel, kept so moving between folders is instant; **Largest
  files** anywhere on it; OneDrive's **online-only** files counted from their
  attributes and marked (nothing downloads); linked folders not counted
  twice; sizes update after Move to Recycle Bin. When Windows' own shared
  system files make a drive add up to more than its used space, Analyze says
  so and shows the real figure.
- **Compress in place** (a folder in Analyze): screenshots and scans (PNG,
  BMP, TIFF) become high-quality JPEGs keeping their names; smaller copies
  first, then Done puts them in place and moves the originals to the Recycle
  Bin. Photos with see-through parts, online-only files and JPEGs are left
  as they are.

## 1.2.0 (2026-10-26)

- **Assistant:** Apex Agent answers common questions with this PC's
  readings and carries out commands after you press its button (end apps,
  close everything except…, stop a process, Game mode with a frame-rate
  target), always on the exact processes it named. **Local AI** chats with a
  model on this PC (LM Studio or Ollama); nothing leaves the PC.
- **Gaming:** live FPS, 1% low and frame time for every app that draws.
  **Game mode** quiets other apps (untick what should keep running), uses the
  fastest power plan, gives the game high priority, watches the frame rate
  against your target and says what holds it back. Everything is restored
  when you stop or close the game.
- **Free Up Space:** Clean (temporary files, thumbnails, crash dumps and
  error reports, browser and shader caches; Windows temp, Windows Update
  downloads and Delivery Optimization with administrator rights), the Recycle
  Bin, and Analyze (biggest folders first, Show in Explorer, Move to Recycle
  Bin).
- **Network:** connections by app (live, with Pause), ports in use with Free
  Port, public IP (hidden until clicked), speed test, and Network Doctor.
- Frame rates use Intel's open-source PresentMon (MIT); see
  THIRD-PARTY-NOTICES.txt.

## 1.1.0 (2026-10-26)

- **Tray panel:** Apex's icon by the clock opens Monitor (CPU, memory,
  GPU, disk and network with mini graphs), Energy (battery and **Stay
  Awake**) and **Clipboard history** (last 50 texts, files and pictures;
  search; click to copy again; password managers' copies skipped; memory
  only, switch off any time; Win+V link). Closing the window keeps Apex
  running by the clock (a switch in Settings); Quit from the icon's menu.
- **Free Up Memory** on the Memory page: apps by memory; Efficiency mode,
  Trim memory, Close and remember, Reopen, Restore all.
- **Efficiency mode** now also lowers the app's memory priority, minimizes
  its windows, covers processes the app starts later, switches off when
  you bring the app to the front, and restores the windows it minimized.
  Nothing stays slowed down after Apex quits.
- **Safer End task:** Apex remembers exactly which process you chose and
  ends it through a handle checked against its start time, so a process
  that took the same ID is never ended. Anything not ended is explained.
- **Source available:** the source code is now in this repository, under
  the same licence as Apex for Mac (PolyForm Strict 1.0.0): free for
  personal and other noncommercial use; read and build it, but no
  modifying, redistributing or commercial use without a licence.

## 1.0.0 (2026-10-25): first Windows release

- **Processes:** Apps, Background processes and Windows processes, each
  program with its processes underneath; CPU, memory, disk and GPU per app
  with Task Manager-style heat shading; search by name, publisher or PID;
  **End task**; **Efficiency mode** (keeps an app running at the lowest
  priority on power-saving cores) with **Restore all**.
- **Performance:** CPU (with speed, cores, processes, threads, handles, up
  time), Memory (composition and pressure: Normal, Elevated, Critical), every
  disk (active time, transfer rate, response time), every network connection
  (Wi-Fi, Ethernet, VPN) and the GPU (3D, Copy, Video Decode / Encode,
  dedicated memory), each with a live graph.
- **Startup apps:** everything Windows starts when you sign in, on / off
  like Windows' own Startup settings.
- **Users** signed in, with what their apps use.
- **Services:** start, stop and restart, with search and a filter.
- Laid out like Task Manager, with macOS touches: a translucent sidebar,
  rounded cards and pills, large bold titles, soft graph fills and gentle
  fades. Light and dark mode.
