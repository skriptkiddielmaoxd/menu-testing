# Frikadellen UI – Overhauled Avalonia Desktop UI

A complete visual overhaul of the Frikadellen Avalonia desktop UI, built for easy merge into `frikadellen-fancy`. Backend services are fully wired up via `BackendClient` (HTTP REST), `BackendSocket` (WebSocket), and `RustProcessLauncher` (process management). When the Rust backend is not running, the UI falls back to mock data so development stays productive.

---

## Design

| Attribute | Detail |
|---|---|
| Theme | Deep midnight-navy dark (`#08060D` base) with fuchsia/cyan/orange accents |
| Corners | 12–24 px radius everywhere — zero sharp corners |
| Font | Inter (bundled via `Avalonia.Fonts.Inter`) |
| Animations | Splash progress, staggered card entrance, view fade+slide, button colour morph |

---

## Screens

### Splash Screen
- Animated progress bar with step messages (loading config, connecting to backend, ready)
- Transitions to Login on first run, otherwise directly to the Dashboard

### Account Setup (Login)
- Shown on first run (`config/ui-settings.json → FirstRunComplete: false`)
- Collect Minecraft username; skip option for later setup
- `Connect Account` fires the Microsoft device-code auth flow (stub — add real flow in `LoginViewModel.Proceed()`)

### Main Shell
- **Custom chrome**: no native title bar; draggable top bar with logo, live status chip, theme toggle, min/max/close
- **Horizontal navigation bar**: Dashboard, Events, Config, Notifier, Console
- **Page transitions**: fade + slide-up per view change

### Dashboard
- Live **stat cards** (Script state, Purse, Queue depth, Session profit)
- **Start / Stop** button wired to `BackendClient.StartBotAsync()` / `StopBotAsync()`
- Stats polled from `GET /api/stats` every 3 s while running
- Live flips streamed via WebSocket `flip_received` events
- Falls back to mock data while the backend is unreachable

### Events
- Real-time feed from WebSocket (`item_purchased`, `item_sold`, `bazaar_trade`, `error`)
- Type badge + timestamp per row; click to expand detail
- Falls back to mock events while the backend is unreachable

### Config
- Grouped settings: Flip Modes, Timing & Delays, Behaviour, Skip Filter, Network, Anti-Detection
- All fields bound to `UiSettings` and saved to `config/ui-settings.json`
- Save button with "Saved ✓" flash confirmation

### Notifier
- Discord bot token (show/hide toggle), Channel ID, Webhook URL
- Persisted to `config/ui-settings.json`

### Console
- Live stdout/stderr from the Rust binary via `RustProcessLauncher`
- Start / Stop / Clear controls; monospace font with colour-coded stderr

---

## Build & Run

**Requirements:** .NET 8 SDK

```bash
# Build
dotnet build Frikadellen.UI/Frikadellen.UI.sln

# Run (dev — shows mock data when backend is offline)
dotnet run --project Frikadellen.UI/Frikadellen.UI.csproj

# Publish self-contained Windows executable
dotnet publish Frikadellen.UI/Frikadellen.UI.csproj \
  -c Release -r win-x64 \
  -p:SelfContained=true \
  -p:PublishSingleFile=true
```

---

## Integration with frikadellen-fancy

The UI connects to `http://localhost:<WebGuiPort>` (default **8080**, configurable in Config → Network).

| Service | File | What it does |
|---|---|---|
| `BackendClient` | `Services/BackendClient.cs` | HTTP REST client — status, stats, flips, start/stop, config |
| `BackendSocket` | `Services/BackendSocket.cs` | WebSocket client — real-time flip/event/state stream, auto-reconnect |
| `RustProcessLauncher` | `Services/RustProcessLauncher.cs` | Spawns the Rust binary, streams stdout/stderr to the Console view |

### Backend REST API expected

| Method | Path | Used by |
|---|---|---|
| `GET` | `/api/status` | Status polling |
| `GET` | `/api/stats` | Dashboard stat cards (every 3 s) |
| `GET` | `/api/flips` | Recent flips table |
| `POST` | `/api/start` | Start button |
| `POST` | `/api/stop` | Stop button |
| `GET` | `/api/config` | Config screen load |
| `POST` | `/api/config` | Config screen save |

### Backend WebSocket events expected (`ws://localhost:<port>/ws`)

| `type` field | Payload fields | Handler |
|---|---|---|
| `flip_received` | `item`, `buy_price`, `sell_price`, `buy_speed_ms`, `finder`, `item_tag` | Dashboard flip table |
| `item_purchased` | `item`, `price` | Events feed |
| `item_sold` | `item`, `price`, `profit` | Events feed |
| `bazaar_trade` | `item`, `side`, `quantity`, `price_per_unit` | Events feed |
| `state_change` | `state`, `purse`, `queue_depth` | Status chip + dashboard stats |
| `error` | `message` | Events feed |

### Remaining stubs to complete

| File | Location | What to plug in |
|---|---|---|
| `LoginViewModel.cs` | `Proceed()` | Real Microsoft device-code auth flow |

---

## Project Structure

```
Frikadellen.UI/
├── Frikadellen.UI.sln / .csproj
├── Program.cs                        entry point
├── App.axaml / .cs                   global theme + startup
├── Models/Models.cs                  EventItem, FlipRecord, UiSettings, Fmt
├── Services/
│   ├── BackendClient.cs              HTTP REST client
│   ├── BackendSocket.cs              WebSocket client with auto-reconnect
│   ├── MockDataService.cs            random events/flips for offline demo
│   ├── RustProcessLauncher.cs        spawns + monitors the Rust binary
│   └── SettingsService.cs            JSON persistence → config/ui-settings.json
├── ViewModels/
│   ├── MainWindowViewModel.cs        root lifecycle + backend wiring
│   ├── SplashViewModel.cs            animated progress steps
│   ├── LoginViewModel.cs             first-run account setup
│   ├── DashboardViewModel.cs         stats, toggle, flip feed
│   ├── EventsViewModel.cs            live event collection
│   ├── ConfigViewModel.cs            all config.toml fields
│   ├── NotifierViewModel.cs          Discord bot + webhook
│   ├── ConsoleViewModel.cs           process console log
│   └── BoolToStringConverter.cs      IValueConverter helpers
└── Views/
    ├── MainWindow.axaml/.cs          custom chrome + phase routing
    ├── SplashView.axaml/.cs
    ├── LoginView.axaml/.cs
    ├── DashboardView.axaml/.cs
    ├── EventsView.axaml/.cs
    ├── ConfigView.axaml/.cs
    ├── NotifierView.axaml/.cs
    └── ConsoleView.axaml/.cs
```

