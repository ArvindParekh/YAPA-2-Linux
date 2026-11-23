# YAPA 2 — the redesign

This is the redesign on the `redesign` branch: what it is, why it looks the way it does,
and where things live. `./preview.sh` runs it next to the installed app, on a copy of
your settings and history.

## What changed

| Before | After |
|---|---|
| Three themes; the classic big-digit timer by default | **Studio**: one card. A status line, the time, one button. Classic and Motivational are still in Appearance. |
| Separate windows for Your day, Statistics and Settings | **One hub window**: a sidebar with *Your time* (Today, Week, Insights) and *Settings*, a breadcrumb, a live mini timer at the foot. |
| Ask YAPA: a chat panel | **The command bar**: one field that runs any action, and asks YAPA anything else. |
| Each window with its own palette | **One design system** (`YAPA.Avalonia/Design/Theme.axaml`) that every surface draws from. |

## Principles

1. **Monochrome, except for meaning.** Everything is a near-black zinc scale. Colour shows
   up in one place only: the timer's state. Focus is ember, rest is mint, held is amber,
   and off the record is violet. When something is coloured, it is telling you something.
2. **The timer is a glance.** At rest the card shows the state, the time and the one next
   step. Skip, stop and the window's buttons wait for the pointer. A hairline along the
   bottom is the progress, in the state's colour.
3. **Keyboard first, mouse never punished.** Every action is reachable from the command
   bar (`/` on the timer, `Ctrl K` in the hub). Navigation chords work as in Linear (`G T`,
   `G W`, `G I`, `G S`). Keycaps are shown wherever a key works.
4. **Hairlines, not boxes.** Surfaces step up in lightness by a few percent. Separation
   comes from 6–10% white lines and spacing, not borders and shadows. Shadows are for
   things that float: the timer, the command bar, menus.
5. **One window per job.** The timer floats; everything you *read* lives in the hub;
   everything you *do* is one keystroke away in the command bar.

## Tokens

Defined once in `Design/Theme.axaml`; use `{StaticResource …}`.

| Group | Tokens |
|---|---|
| Surfaces (darkest first) | `Canvas` #09090B · `Sunken` #0C0C0F · `Surface` #111114 · `Raised` #18181C · `Overlay` #1E1E23 |
| Lines | `Line` 6% white · `LineStrong` 10% · `LineFocus` 25% |
| Fills | `FillSubtle` 3% · `FillHover` 6% · `FillActive` 9% |
| Text | `Text1` #EDEDEF · `Text2` #A1A1AA · `Text3` #71717A · `Text4` #4C4C55 |
| State | `Focus` #FF6A3D · `Rest` #34D3A0 · `Hold` #F5B544 · `Quiet` #9B8AFB, each with a `…Soft` 14% tint |
| Shadows | `ShadowCard` (a lift) · `ShadowFloat` (a floating panel) |

Radii: 6 controls · 8–10 rows · 14 cards · 16–18 floating panels. Spacing on a 4 px grid.

**Type.** Geist for words and Geist Mono for keys, counts and code (both are bundled).
The timer's digits use Geist with tabular figures (`+tnum`), not Geist Mono: the
monospace colon leaves a gap in the middle of the time. Classes: `display`, `h1`, `h2`,
`body`, `small`, `eyebrow`, `mono`.

**Components** are opt-in classes, so older surfaces keep their look until they're
ported: `Button.primary` (white on black, the one thing to press), `.secondary`, `.quiet`,
`.square`; `Border.kbd`, `.panel`, `.well`, `.hairline`, `.pill`. Menus everywhere are
Fluent's own controls, retoned through Fluent's resource keys (`MenuFlyout…`), so the
behaviour stays stock.

## Surfaces

**Studio timer** (`Windows/StudioWindow`). It is a 296×100 card in a transparent window,
with a margin that holds the drawn shadow. Mutter draws no shadow on ARGB windows, and an
X11 input shape lets clicks in the margin through. The layout is drawn at the default
size and scaled with a `Viewbox`. The status line reads: dot, state, place in the cycle
(`2/4`), then the user's note or the goal's forecast (`goal by 18:40`), then today's
count. On hover, the count gives way to ⋯, minimise and quit. When an interval ends, the
card's edge glows in the state's colour (held still if flashing is turned off).

**Hub** (`Hub/HubWindow`). It is the old Settings window, grown: a 248 px sidebar and a
content area with its own title bar (no system decorations; drag and double-click on the
bar, resize grips at the edges). Your day and Insights are now `UserControl`s
(`Journal/JournalView`, `Statistics/InsightsView`) hosted by DataTemplates. The mini
timer at the foot of the sidebar shows the state, the time and a scaled progress hairline.
Clicking it brings the timer back.

**Command bar** (`Assistant/AssistantWindow`, ranking in `Assistant/CommandPalette.cs`,
the actions in `Assistant/PaletteActions.cs`). The field is on top and the orb is its
mark. Under it is a list that depends on what you've typed:

- *Nothing typed:* the timer's next step, a few questions worth asking, then places.
  Timer first because Enter on it is cheap and undoable; Enter on a question spends a
  request.
- *Something typed:* actions whose title or keywords start with each typed word, best
  first. Ask YAPA is always on offer. A word or two that names an action ("pause",
  "week") puts the action first. A sentence, a question, or nothing to run puts asking
  first. Enter does the obvious thing either way.
- *In a conversation:* Enter replies. `/` brings the actions back without leaving the
  conversation.

Esc clears what's typed, then closes. The foot shows what the orb is doing and the keys
that work right now. It opens beside the timer, or over the hub when called from there.

**Your day and the week** (`Journal/JournalView`). Built for a glance. The day opens on
a card with a headline of a few words, one or two plain sentences, three big numbers
(focused, pomodoros, time at the computer that wasn't timed), the timeline and the top
few projects. Under it, everything done that day is one line each: time, title, project
and length. Lines are grouped by part of the day (Morning, Afternoon, Evening), and time
outside a pomodoro is a quieter grey line. A line opens to its one detail and what was
recorded. The week is the same card over one line per day. The write-ups follow the same
rules (`JournalPrompt.System`): everyday words, titles of at most 6 words, no tool or site
names, and project names reused from earlier days. `JournalPrompt.Style` is part of every
fingerprint, so a change of style rewrites each day once, when it's next opened.

## Decisions worth knowing

- **Existing profiles move to Studio once.** The classic timer used to be the default, so
  older profiles store it whether or not anyone chose it. `AvaloniaThemeSettings` switches
  `YAPA 1.0` to Studio a single time (`StudioIntroduced`); a theme picked after that
  sticks.
- **Transparent windows render text in greyscale** (`TextOptions.TextRenderingMode=Antialias`).
  Subpixel antialiasing on an ARGB surface fringes against whatever is behind the window.
- **The hub handles its chords on the way down** (a tunnel handler). Your day has
  single-letter keys of its own: `W` toggles day and week, so `G W` from the week has to
  be caught before the page sees the `W`.
- **Progress bars scale rather than size.** The hub's mini-timer hairline is full width
  with `scaleX(progress)`, so it never depends on layout having run first.
- **The heatmap ramps the other way on dark.** More work is brighter, so busy days stand
  out instead of sinking into the background.
- **Names line up.** Statistics is now Insights everywhere (menu, tray, hub, keys), and
  Your day's calendar key moved from G to C, because G now starts a chord.

## Not done yet

- The Classic and Motivational timers keep their own looks. They're still there for
  anyone who prefers them, but they don't draw from the tokens.
- Settings pages use the retuned `SettingsStyles.axaml` palette, not the new components
  throughout.
- No light theme. The tokens are laid out so one could be added as a second dictionary.
