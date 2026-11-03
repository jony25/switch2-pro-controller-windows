# UI/UX redesign and feature specification (draft)

Status: **implemented, phase by phase.** The table below says what is done and where the code differs from this text. Open decisions are listed in section 12.

### Implementation status

| Phase (section 13) | Status | Notes |
|---|---|---|
| A. Visual system, header, grids | Done | Dark theme only. Icons use Segoe Fluent Icons. |
| B. Localization (es, en) | Done | English is the default. Logs stay in English. Settings → Interface language. |
| C. Profiles, settings v2, migration | Done | A profile holds the controller options plus the mapping (sections 3–6 below). See *Differences* for how saving works. |
| D. Calibration tab | Partly | Live visualizers, coordinates and the app latency widget are done. The inner deadzone ring on the visualizers is not. |
| E. Deadzones and curves | Done | Inner 0–30 %, outer 0–20 %, linear, exponential (1.0–3.0) and S-curve, with a graph and a live dot. The S-curve is not blended with linear. |
| F. Remap table | Done | Xbox target, keys (including combinations such as Alt+Tab), mouse buttons, warnings for Guide and shared keys. |
| G. Mouse targets and macros | Partly | Mouse targets are in F. Macros were dropped: key combinations cover the same need. |
| H. Motion output | Partly | DSU server for emulators is done and checked with Eden. DS4 virtual pad is not started. |

### Differences from this text

- **Saving.** Changes to the mapping and the profile controls are written to the active profile when you press **Save**, when you switch profile, and when you close the app. Nothing is written to the profile while you edit it, so a crash can lose edits made since the last save.
- **New profile.** **New** starts from the default values, not from the current ones.
- **Default profile.** It is called `Default` in the data. The first version called it `Predeterminado`; that name is migrated automatically.
- **Capture → F12.** The fixed option was removed. To press F12 with the Capture button, assign F12 to Capture in the Mapping tab.
- **Key capture.** A low-level keyboard hook is used while a key cell waits for input. The window does not receive system combinations such as Alt+Tab, so the hook is the only way to capture them. While it runs, the captured keys do not reach other programs.
- **Latency.** The widget shows the time the app takes to hand each report to the virtual pad. The controller-to-PC Bluetooth latency is not shown: the report timestamp has no documented unit and there is no reference clock to compare it with.

Scope: the WPF app in `src/Switch2ProManager`. It covers layout, visual system, localization, profiles, calibration, button mapping, settings and persistence. The protocol rules in `docs/PROTOCOL_NOTES.md` and the safety rules in section 10 apply to everything here.

Labels use these names: **Verified** (observed on our unit), **Community** (public documentation), **Design** (our decision, no hardware claim).

---

## 1. Principles

1. **Dark theme only (Design, decided by the user).** The app always uses the dark palette (`ThemeMode="Dark"`, `Resources/Theme.Dark.xaml`), so colors are the same on every PC. The light palette is kept in the spec for a possible future option, but is not used.
2. **Honest numbers (Design).** Every number shown is either measured by the app or labelled as an estimate.
3. **No empty space (Design).** Each tab uses a two-column grid of cards. Below 980 px wide it collapses to one column.
4. **Text plus color (Design).** Status is never shown by color alone: every badge has a text label.
5. **Controls match state (Design).** A control that cannot act right now is disabled, not hidden, and the tooltip says why.

## 2. Visual system

Geometry (same in both themes):

| Token | Value | Use |
|---|---|---|
| `Card.Radius` | 8 px | Cards |
| `Pill.Radius` | 16 px | Status badge, device chips |
| `Spacing.Unit` | 8 px | All margins and paddings are multiples of 8 (card padding 16, gap 16) |

Colors (each token has a value per theme):

| Token | Dark (`#1E1E1E` cards) | Light | Use |
|---|---|---|---|
| `Window.Background` | `#161616` | `#F3F3F3` | Window background (Design) |
| `Card.Background` | `#1E1E1E` | `#FFFFFF` | Cards |
| `Card.Border` | `#2B2B2B`, 1 px | `#E5E5E5`, 1 px | Card outline |
| `Text.Primary` | `#F3F3F3` | `#1A1A1A` | Body and titles (contrast above 4.5:1 on the card) |
| `Text.Secondary` | `#B3B3B3` | `#5C5C5C` | Hints and units (contrast above 4.5:1 on the card) |
| `Accent` | `#4CC2FF` | `#0067C0` | Pressed buttons, sliders, focus ring |
| `Accent.Text` | `#111111` | `#FFFFFF` | Text on an accent background |
| `Status.Connected` | `#2ECC71` | `#107C10` | Connected, virtual pad active |
| `Status.Busy` | `#F5A623` | `#9D5D00` | Scanning, connecting |
| `Status.Off` | `#E74C3C` | `#C42B1C` | Disconnected, virtual pad inactive |

Light values are darker where needed so text and status colors keep the contrast on white. The light values are a starting point and must be checked on screen (section 14).

Typography: `Segoe UI Variable Display` for the title (26 px, SemiBold), `Segoe UI Variable Text` for everything else (body 14 px, card titles 16 px SemiBold, hints 12 px). Icons: `Segoe Fluent Icons` (the gear is glyph `E713`).

Replace the current `CardFill` workaround with these tokens as named brushes, so the window does not depend on system colors.

## 3. Global header (fixed, above the tabs)

- Left: title **Switch 2 Pro Manager** and subtitle **Nintendo Switch 2 Pro Controller · Bluetooth LE**.
- Right: status badge and a gear button.
- Status badge text and color:

| State | Text (Spanish) | Color |
|---|---|---|
| Disconnected | ● Desconectado | Status.Off |
| Scanning | ● Buscando… | Status.Busy |
| Connecting | ● Conectando… | Status.Busy |
| Connected | ● Conectado | Status.Connected |

- ~~The gear button opens the **Ajustes** tab~~ **Removed:** the Ajustes tab is the only entry to settings, so the gear was redundant.

## 4. Tab 1: General

### Card 1: Connection

- **Device list.** Each row shows the name, the Bluetooth address, the signal in dBm (`Rssi`) and the mode hint (for example «Reconectando a este PC» or «Modo SYNC»). A device not seen for 10 s is removed (already implemented).
- **Buttons:** `Buscar`, `Conectar`, `Desconectar`. Enable rules are the same as the tray menu: Conectar only when a device can connect, Desconectar only when connected.
- **Battery.** Progress bar plus text. The controller reports 10 steps (0–9), so the text is `Batería: nivel 7/9 (≈78 %)`. Before the first report: `Batería: —`. Alerts stay at the existing thresholds (level 2 and level 1).

### Card 2: Quick settings and profiles

- **Active profile.** A ComboBox with the profile names, and buttons `Nuevo`, `Guardar` (overwrites the current profile with the current values) and `Eliminar`. Profile details are in section 9.
- **Checkboxes:**
  - `Emular mando de Xbox 360 (ViGEm)`
  - `Remapear disposición posicional (Nintendo B → Xbox A)`
  - `Botón Capturar → F12 (capturas de Steam)`
  - `Iniciar con Windows`: stored in the registry, global (not in profiles)
  - `Reconexión automática`: global (not in profiles)
- **Virtual pad badge.** Green `● Mando virtual activo` or red `● Mando virtual inactivo`. Reflects `XboxVirtualPad.IsRunning`, not the checkbox, so a ViGEm error shows as inactive.

## 5. Tab 2: Pruebas y calibración

### Card 1: Sticks and triggers (live)

- **Two circular visualizers**, one per stick. Each has:
  - the outer ring at 100 % of the calibrated travel (`MaxDelta`, currently 1750 raw counts, a community estimate);
  - the inner deadzone ring (from the mapping tab);
  - the dot position;
  - numeric values under each circle: `X: +0.02  Y: −0.01`, two decimals, explicit sign.
- **Triggers ZL and ZR.** Bars. These triggers are digital on this controller (Verified), so the bars show 0 % or 100 %. The label must say so: `ZL · digital`.

### Card 2: Buttons and latency

- **Button matrix.** All 18 physical inputs: A, B, X, Y, L, R, ZL, ZR, +, −, Home, Capturar, C, L3, R3, D-pad (↑ ↓ ← →), GL, GR. A pressed button lights up in `Accent`, with the text switching to a dark color for contrast.
- **Latency widget.** Label: **Latencia de la app: 4.2 ms** (median of the last 100 samples).
  - Definition (Design): time from the start of the BLE notification handler to the end of `XboxVirtualPad.Submit`, measured with `Stopwatch`.
  - This **does not** include the Bluetooth air time, Windows scheduling or the game. The tooltip must say so.
  - End-to-end latency needs external hardware, and is out of scope.

### Card 3: Vibration

- Toggle `Vibración en juegos`.
- Slider `Intensidad` 0–100 %, step 5 (already implemented as `RumbleStrength`).
- Button `Probar vibración` (existing test pulse).
- Hint: the built-in samples (`0x0A`) play at their fixed strength (Verified in code, see `HapticsService`).

## 6. Tab 3: Mapeo avanzado y zonas muertas

### Section 1: Deadzones and response curves (per stick)

- **Interactive graph.** X axis: input magnitude 0–1. Y axis: output 0–1. A diagonal reference line shows linear. The curve redraws when a slider moves. A live dot shows the current stick position on the curve.
- **Controls per stick:**
  - inner deadzone, 0–30 %, default 5 % (today a fixed `Deadzone = 0.05` in `InputReportParser`);
  - outer deadzone, 0–20 %, default 0 %;
  - curve: `Lineal` (default), `Exponencial`, `Curva S`. The exact formulas are defined in section 11 when the phase starts.
- **Reset** button per stick.
- These values are software transforms applied after parsing. They never write to the controller (no calibration writes, see section 10).

### Section 2: Button remap

- **Table.** One row per physical input (the 18 from section 5). Columns:
  - `Destino Xbox` (dropdown: A, B, X, Y, LB, RB, LT, RT, Back, Start, Guide, L3, R3, D-pad, or *Sin asignar*);
  - `Tecla` (a key-capture cell: press a key to assign it);
  - `Ratón` (dropdown: none, left, right, middle, X1, X2);
  - `Macro` (phase C).
- **GL and GR** are read by the parser but never sent to the virtual pad today (Verified in code). They need a keyboard or mouse target to be useful, so they are first in this table.
- **Validation:**
  - Warn when a row maps to `Guide`, and when the mapping would leave the controller with no way to open Guide (Home stays hard-wired as Guide unless the user confirms).
  - Warn when two inputs share a keyboard key, and allow it.
- Remaps are stored in the profile.

## 7. Tab 4: Avanzado / Registros and Ajustes

### Expanders (accordion, one open at a time, as now)

1. **Laboratorio de vibración HD (experimental).** Unchanged. Off by default. Never restored at startup (section 9).
2. **Investigación de audio Bluetooth (experimental).** Unchanged, with the warning text from `docs/AUDIO_RESEARCH.md`.
3. **Registro de eventos.** The live list, a `Abrir carpeta de registros` button (opens `%LOCALAPPDATA%\Switch2ProManager\logs`), and a `Borrar vista` button that clears the list only, never the file.

### Ajustes (Settings) section

- **Idioma:** `Español` (default) and `English`. Takes effect immediately (section 8).
- **Ventana y bandeja:**
  - `Minimizar oculta la ventana en la bandeja`
  - `Cerrar la ventana la mantiene en la bandeja (desactivado = salir)`
  - `Mostrar avisos de la bandeja (batería, ocultar)`
- **Alertas de batería:** shown as read-only text (`Avisos: nivel 2/9 y nivel 1/9`) in v1. Making them editable is a later decision.
- **Acerca de:** version, `docs/` links and the Nintendo trademark notice from the README.

## 8. Localization

- Default language Spanish. Second language English.
- All user-visible strings move to resource files, one per language, referenced by key. Keys are English and descriptive, for example `Status.Connected`, `Battery.Level`.
- Numbers use the current culture: `4,2 ms` in Spanish, `4.2 ms` in English.
- **Logs stay in English** (Design). They are pasted into bug reports, which are written in English. Log lines are not translated.
- Battery alerts and tray balloons use the resource strings, so they follow the language.
- Changing the language does not restart the app.

## 9. Profiles and persistence

### Profile content (decision recorded: "completo")

A profile stores: positional layout, Xbox emulation, Capture → F12, game rumble on/off, rumble intensity, motion reading, inner and outer deadzones per stick, curve per stick, button remap table.

A profile does **not** store: start with Windows, auto-reconnect, language, tray behaviour (global settings), and the test tools.

### Files

- `%LOCALAPPDATA%\Switch2ProManager\settings.json`: global settings and the list of profiles, with `Version`.
- Profiles live inside the same file, under `Profiles`, with a `Name`. This avoids a directory of files that can get out of sync.
- A default profile named `Predeterminado` always exists and cannot be deleted.

### Versioning

- `Version` 1 is the current file (already written by the app).
- `Version` 2 adds `Language`, `ActiveProfile`, `Profiles`.
- Loading a version 1 file migrates it to version 2: the current values become the `Predeterminado` profile. The version 1 file is kept as `settings.v1.json.bak` the first time it is migrated.

### When it is saved

- On exit, as now (Dispose).
- **Also on every profile change** (create, save, delete, switch). A crash must not lose a profile the user just saved. This extends the current "only on exit" rule, which is recorded as a known gap in the README.

### Restore on startup

- The active profile is applied at startup.
- Laboratory and probe state are not restored.
- The connection is never restored; auto-reconnect only acts on the controller's own advertisement.

## 10. Safety rules (carried over, not optional)

1. No new commands are sent to the controller. Everything in this spec is computed or applied in the PC. The only controller writes stay the documented ones (LED, feature select, vibration samples, HD rumble, pairing handshake behind its warning).
2. No calibration or flash reads or writes (commands `0x02`, `0x03`, `0x0D`).
3. The registration handshake (`0x15`) is never automatic. Auto-reconnect only connects to `ReconnectThisPc`.
4. The audio probe and the rumble lab are not restored at startup, so they cannot start by themselves.
5. Nothing is published, uploaded or sent (GitHub, issues) without the user's explicit confirmation.

## 11. Feature details

### 11.1 Response curves (Design, formulas to confirm)

Input `x` is the stick magnitude after the deadzones, in `[0, 1]`, and output `y` is in `[0, 1]`.

- Linear: `y = x`.
- Exponential: `y = x^p`, with `p` from 1.0 to 3.0 (default 2.0). Keeps small movements precise.
- S-curve: a smoothstep, `y = 3x² − 2x³`, blended with linear by a slider.

The direction (sign) and the angle are kept; the curve applies to the magnitude only.

### 11.2 Deadzones (Design)

- Inner (radial): the magnitude below `inner` maps to 0; above it, the remaining range is stretched to `[0, 1]`. This is what `RadialDeadzone` already does with a fixed value.
- Outer: the magnitude above `1 − outer` maps to 1, and the range below it is stretched to `[0, 1 − outer]`.
- Both can be set at once, and the inner value must stay below the outer threshold.

### 11.3 Latency widget (Design)

- Clock: `Stopwatch.GetTimestamp()`, converted to ms.
- Sample: taken in the BLE thread around `Submit`.
- Display: median of the last 100 samples, updated at 4 Hz, so the number is readable.

### 11.4 Battery text (Design)

- Level 0–9 maps to `level × 100 / 9`, rounded down, shown with `≈`.
- The alert thresholds remain at levels 2 and 1, which are the 22 % and 11 % steps.

## 12. Open decisions

These are not decided in this document. Each one changes the implementation:

1. ~~Dark theme~~ **Changed:** first "follow Windows", then "dark only" (section 1, principle 1).
2. **Profiles per game:** should a profile switch automatically when a game starts? (Not in this spec; would need a process watcher.)
3. ~~**Remap targets in v1**~~ **Decided:** mouse targets are in phase F. Macros were dropped (see the status table).
4. ~~**Latency name**~~ **Decided:** "Latencia de la app", with the caveat in the tooltip.
5. ~~**Logs language**~~ **Decided:** English only, for bug reports.

## 13. Phases and rough order

| Phase | Content | Main files |
|---|---|---|
| A | Visual system (light and dark palettes), header, two-column grids, gear button | `App.xaml`, `Views/MainWindow.xaml` |
| B | Localization (es, en) and language setting | new resource files, `Views/*.xaml`, `SettingsStore.cs` |
| C | Profiles, settings v2 and migration, save-on-change | `Services/SettingsStore.cs`, `ViewModels/MainViewModel.cs` |
| D | Calibration tab: visualizers, coordinates, latency widget | `Views/MainWindow.xaml`, `ViewModels/MainViewModel.cs`, `Services/HapticsService.cs` (timing hook) |
| E | Deadzones and curves: graph, sliders, transforms | `Protocol/InputReportParser.cs` (or a new transform service), ViewModel |
| F | Remap table: Xbox targets, GL/GR to keyboard | `Services/XboxVirtualPad.cs`, `Services/KeyboardInjector.cs` |
| G | Mouse targets and macros | new services, depends on decision 3 |
| H | Motion output (DS4 / DSU), tracked in the feature plan | new services |

Each phase must build, be published with `scripts\publish.bat`, and be tested on hardware before the next one starts.

## 14. Acceptance checks (per phase)

- Build passes with 0 errors, and `publish.bat` produces `dist\Switch2ProManager.exe`.
- Text contrast checked on both palettes (4.5:1 minimum for body text), with the system in light and in dark mode.
- Every control works with the keyboard alone (tab order, visible focus).
- Settings and profiles survive a restart, and a version 1 file migrates without loss.
- No controller write is added that is not on the documented list in section 10.
