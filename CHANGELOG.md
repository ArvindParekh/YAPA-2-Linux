# Changelog

## 2.1.0 (unreleased)

A ground-up revival of the Linux port.

### Fixed
- **No sound on start, pause, resume or stop.**
  - The cue and the background music shared one player, and music stopped it on every phase change.
  - Sound tools were looked up through a `PATH` that apps launched from GNOME don't have.
  - `ffplay` was given malformed arguments.
  - Pause and stop had no sound at all.
  - The stock tick peaked at −27 dBFS, so it was barely audible.
- **Clicks not registering near the Ubuntu dock.** On X11, GNOME Shell takes every click
  inside its input region, and that includes the dock's whole invisible container
  (90% of the screen width), not just the icons. The timer now keeps out of
  that region and moves itself if the dock appears under it.
- **"Today" and the dashboard counted the wrong days.** Local midnight was compared
  with UTC timestamps, and the week view was cumulative.
- Resuming a paused break turned it into a work interval.
- Changing "pomodoros before long break" required a restart.
- Sleep or suspend counted as work time.
- Settings *Cancel* didn't revert anything; settings now apply instantly, with per-page reset.
- A crash while saving settings could reset all of them; writes are now atomic.
- Digits jittered and clipped. They now use tabular figures, and the digit-spacing workaround is gone.
- Your day slowed as history grew: today's windows were found by scanning every one ever recorded
  (200 ms after a year, on every refresh). It's now a bounded index lookup (6 ms, flat). Browser
  histories (tens of megabytes) are no longer copied again when they haven't changed, and the copies
  are made in memory, never on disk.

### Added
- **Ask YAPA**, an assistant that runs the whole app. Double-click the timer, press <kbd>/</kbd>, or use
  the menu, tray or `--ask`, and ask or tell it anything. It sees and changes the timer, history,
  statistics, every setting and the profiles, live. It plans the day on the timer's own rules, answers
  history questions with SQL over a local-time copy of the history, and undoes any request in one click.
  It runs on the user's Claude sign-in through Claude Code, limited to YAPA's own tools.
- The assistant can also schedule actions and reminders for later (kept across restarts), move the timer,
  choose music and sound files, export history to CSV, reset a settings section, read the app log,
  remember notes across conversations and look back through past ones.
- Every word on the timer can be changed, a note line of your own can show live values ("{left} to go"),
  and the Motivational theme can show your own quote; from Settings or by asking YAPA.
- Looks: six built-in styles (Classic, Glass, Minimal, Bold, Neon, Paper) and your own, saved by name or
  designed by YAPA on request; new style options for the digits (font, weight), the progress line colours,
  and a backdrop plate.
- Reminders and actions the assistant scheduled announce themselves in a bubble on the timer, with
  a chime (a new "Reminder or notice" cue, the original YAPA ding); the desktop's own notification
  while the timer is hidden, or always if chosen in Settings → General.
- The assistant no longer stops at "OAuth session expired". Claude Code's sign-in lapses on the
  account's schedule; a request that fails on it is retried on the other sign-in, and when none
  works the question waits for a one-click **Sign in** in the panel, which gives YAPA a token of
  its own for a year (renewal offered two weeks ahead). The panel also says so as it opens,
  before you've typed anything. YAPA no longer borrows the sign-in of a Claude Code session
  it was started from.
- **Your day**, a journal of what you did in every pomodoro, with nothing to type as you go. It
  rebuilds each pomodoro from what was going on during it: your Claude Code conversations (their
  titles and what you typed), your git commits, browser history and, from now on, the window in
  front. Claude then writes a line for each pomodoro, a title and summary for each session (the
  pomodoros between long breaks) and a few lines for the day. It works for past days too, from the
  first open. A ribbon shows the day by project; every line opens to its evidence; a double-click
  renames a pomodoro and the day is rewritten around it; Copy gives the day as text for a standup.
  From the timer's menu, <kbd>Ctrl</kbd>+<kbd>D</kbd>, the tray or `--day`, and Ask YAPA answers
  "what did I do in my second session?" from it. Settings → Journal switches each source off, keeps
  an ignore list, and forgets what was recorded.
- Your day covers the whole day, not just pomodoros: breaks show inside their session and the time
  between sessions as "Off the timer", each written up like a pomodoro, with a thin grey line on the
  ribbon. The window in front is recorded whenever you're at the computer, including while a video
  plays without input (anything holding GNOME's idle inhibitor), never on a locked screen or the bare
  desktop. While Tor Browser runs (any app in Settings → Journal → "Stops everything while open"),
  nothing is recorded and nothing any source found in that time is used. Skipped apps (Telegram, the
  password managers and private windows by default) leave nothing at all, not even their time.
- **Forget** and **off the record**, so nothing on your own computer has to be done around YAPA.
  Every entry in Your day has Forget, and Forget in its header takes any stretch of time ("the last
  15 minutes", 21:40 to 22:15, a whole day). The windows recorded in it are deleted, and so is every
  write-up that could mention it (the day's and sessions' lines are rewritten without it). The names
  of pomodoros wholly in it go too, and so does what YAPA's transcript index copied from it.
  Optionally, Ask YAPA's conversations since then are deleted, and the one on screen is let go. Your
  browser's and Claude Code's own history aren't YAPA's to change: that time is simply never read from
  them again. A forgotten pomodoro still counts and shows as "Off the record". Going off the record
  (the timer's menu, the tray, Your day, Settings or Ask YAPA: for 30 minutes, an hour, three hours or
  until you turn it back on) records nothing and keeps everything from that time out; a crossed-out
  eye on the timer says so, and it survives a restart. Ask YAPA can do both ("forget what I did after
  11 pm", "stop watching me for an hour").
- Adult sites are left out by default: about 8,300 of them, drawn from The Block List Project's list
  (Unlicense) and kept to well-known sites whose names say what they are, plus hand-checked brands that
  don't. Mainstream sites that list also carries (news, shops, hosting, dating apps) are excluded on purpose. While an
  adult page is in front the time is off the record, and adult pages never reach the history or Claude.
- Ask YAPA's panel, reworked. A long request no longer spills out of the card or vanishes past
  three lines: the prompt grows to six lines, then scrolls, and the conversation gives way to it.
  Shift+Enter starts a new line, and Up and Down walk back through earlier requests. Your words
  sit in a bubble, so the turns read as turns. Lists hang their bullets. A failed request has Try
  again (in place of the failed one), every answer has Copy, and Ctrl+N starts over. Scrolled up
  to read, new text no longer pulls you down: a fade and an arrow say there's more. Server
  failures read plainly ("Claude ran into a problem on its side").
- **Your week.** Your day has a Day | Week switch (or W): the week's days with their focus and
  headlines, focus by project across the week, and the week written up in a few lines from the
  days' own write-ups (one short request; days not yet written up are written first). A day row
  opens that day; the arrows and Today step by week; Copy gives the week as text for a review or
  a status update. Ask YAPA reads it too ("how did last week go?"). Forgetting time drops the
  week's write-up with the day's.
- Statistics, redrawn in the app's look, and connected: click a day in the year's grid, a bar in
  the last two weeks, or the best day, and it opens in Your day. The cards lead with the number.
- An idle window no longer keeps the renderer busy: hidden "waiting" and "writing" dots stopped
  animating out of sight (Your day open went from 18% of a core to 1%, on software rendering),
  and Ask YAPA's orb rests after a minute alone, waking with the next keystroke or answer.
- Your day: a pomodoro that's paused says so (it read "under way"), and keeps the start it
  really had instead of drifting later with every paused minute. A glance at a window (under
  half a minute) no longer makes an empty "Nothing on record" stretch. A failed write-up says
  why in a line, never a stack trace. Statistics asks for a dark title bar like the rest, and
  Settings → About lists the keys for Your day and Ask YAPA.
- Your day has a calendar: click the date (or press <kbd>G</kbd>) to jump to any day; days with
  pomodoros and days at the computer are marked.
- A timer event log (starts, pauses, stops, breaks) in `Yapa.db`, so breaks and abandoned pomodoros
  can be asked about, not only completed ones.
- A redesigned Settings window: dark grouped cards, a sidebar with icons, steppers, a live preview of the
  timer, a to-scale strip of one cycle, and an Assistant page showing what it remembers and has scheduled.
- Geist (bundled, OFL) as the interface font; the timer keeps Inter.
- Timer operations behind it: lengthen or shorten the interval under way, backdate one started late,
  and jump to any place in the cycle without recording anything.
- Ambience while you work: soft, steady or pouring rain and brown noise (synthesized live,
  never looping), a ticking clock, or your own recording on loop. It fades with the
  session, optionally continues through breaks, and has a quick switch in the menu, tray and CLI.
- Long break on demand: <kbd>L</kbd>, the ⋯ menu, the tray, `--long-break`, or the notification button.
  It starts the cycle over afterwards, and a running short break turns into the long one without losing the time already rested.
- The timer stays visible through Show Desktop; the tray restores a minimized timer in one click.
- Right-click opens settings again, as in the original; every other action is on a ⋯ button.
- A distinct, soft sound for every event, with a per-event choice of built-in sounds or your own files.
- Desktop notifications with *Start break* / *Skip break* / *Start pomodoro* buttons.
- A statistics window: streaks, daily goal, year heatmap, two-week chart.
- A command line with replies: `--toggle`, `--status --json`, `--skip`, `--quit`, …
- Options for show on all workspaces, hide from Alt+Tab, launch at login,
  start hidden, and pause while the screen is locked.
- Live theme switching, a context menu, keyboard shortcuts, and a tray menu that follows the timer state.
- An interrupted pomodoro is restored paused on the next launch, with no dialog.
- A per-user install with no sudo, an uninstaller, and a desktop entry with quick actions.
- A log file at `~/.cache/YAPA2/yapa2.log`.

### Changed
- Stopping a pomodoro abandons it and never records it as completed.
- The history database is accessed with plain SQLite instead of Entity Framework, which makes the install about 5 MB smaller.
- The Windows-only WPF code was removed from this branch; it lives on upstream.
