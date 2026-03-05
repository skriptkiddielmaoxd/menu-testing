using System;
using System.Windows.Input;
using Avalonia.Threading;
using Frikadellen.UI.Models;
using Frikadellen.UI.Services;

namespace Frikadellen.UI.ViewModels;

/// <summary>
/// Root view-model that drives the entire window lifecycle:
///   Splash → (optional) Login → Shell (sidebar + pages)
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService     _settings = new();
    private readonly RustProcessLauncher _launcher = new();
    private readonly BackendClient       _client;
    private readonly BackendSocket       _socket;

    // ── Phase tracking ──
    public enum Phase { Splash, Login, Shell }

    private Phase _currentPhase = Phase.Splash;
    private ViewModelBase _currentView = null!;

    // ── Shell state ──
    private bool _isSidebarExpanded = true;
    private string _activeNav = "Dashboard";
    private string _statusText = "Stopped";

    // ── Polling ──
    private DispatcherTimer? _pollTimer;

    // Child view-models (lazy)
    private DashboardViewModel?  _dashboard;
    private EventsViewModel?     _events;
    private ConfigViewModel?     _config;
    private NotifierViewModel?   _notifier;
    private ConsoleViewModel?    _console;
    private AnalyticsViewModel?  _analytics;
    private BazaarViewModel?     _bazaar;

    public MainWindowViewModel()
    {
        // Initialise backend services using the persisted port
        var saved = _settings.Load();
        var port  = saved.WebGuiPort > 0 ? saved.WebGuiPort : 8080;
        _client = new BackendClient($"http://localhost:{port}");
        _socket = new BackendSocket($"ws://localhost:{port}/ws");

        // Wire socket events (lambdas capture fields, so they always reach the
        // current child-VM without requiring an explicit re-subscribe)
        _socket.FlipReceived           += flip => _dashboard?.TrackFlip(flip);
        _socket.EventReceived          += evt  => _events?.AddEvent(evt);
        _socket.StateChanged           += HandleStateChange;
        _socket.ConnectionStateChanged += HandleSocketConnection;

        // Start with splash
        var splash = new SplashViewModel();
        splash.Completed += OnSplashCompleted;
        CurrentView = splash;

        NavigateCommand      = new RelayCommand(o => Navigate(o?.ToString()));
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarExpanded = !IsSidebarExpanded);
        ToggleThemeCommand   = new RelayCommand(() => App.ToggleTheme());

        // Propagate launcher running-state to the status chip
        _launcher.RunningChanged += running =>
        {
            StatusText = running ? "Running" : "Stopped";
            OnPropertyChanged(nameof(StatusChipColor));
        };
    }

    // ── Properties ──

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => SetField(ref _currentView, value);
    }

    public Phase CurrentPhase
    {
        get => _currentPhase;
        private set
        {
            if (SetField(ref _currentPhase, value))
            {
                OnPropertyChanged(nameof(IsSplash));
                OnPropertyChanged(nameof(IsLogin));
                OnPropertyChanged(nameof(IsShell));
            }
        }
    }

    public bool IsSplash => _currentPhase == Phase.Splash;
    public bool IsLogin  => _currentPhase == Phase.Login;
    public bool IsShell  => _currentPhase == Phase.Shell;

    public bool IsSidebarExpanded
    {
        get => _isSidebarExpanded;
        set
        {
            if (SetField(ref _isSidebarExpanded, value))
            {
                OnPropertyChanged(nameof(SidebarWidth));
                OnPropertyChanged(nameof(SidebarCollapseIcon));
            }
        }
    }

    public double SidebarWidth => _isSidebarExpanded ? 220 : 60;
    public string SidebarCollapseIcon => _isSidebarExpanded ? "◀" : "▶";

    public string ActiveNav
    {
        get => _activeNav;
        set => SetField(ref _activeNav, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string StatusChipColor => StatusText switch
    {
        "Running"                              => "#4ADE80",
        var s when s.StartsWith("Starting") => "#FBBF24",
        _                                      => "#FB7185",
    };

    public ICommand NavigateCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public Services.ToastService ToastService { get; } = Services.ToastService.Instance;

    // ── Lifecycle ──

    private void OnSplashCompleted()
    {
        var saved = _settings.Load();
        if (!saved.FirstRunComplete)
        {
            var login = new LoginViewModel(_settings, saved);
            login.Completed += OnLoginCompleted;
            CurrentPhase = Phase.Login;
            CurrentView  = login;
        }
        else
        {
            TransitionToShell();
        }
    }

    private void OnLoginCompleted() => TransitionToShell();

    private void TransitionToShell()
    {
        CurrentPhase = Phase.Shell;

        if (_dashboard is null)
        {
            _dashboard = new DashboardViewModel();
            // Wire the toggle button to real backend control
            _dashboard.ToggleRequested += isRunning =>
            {
                if (isRunning) StartScript();
                else           StopScript();
            };
        }

        CurrentView = _dashboard;
        ActiveNav   = "Dashboard";
    }

    // ── Navigation ──

    public void Navigate(string? target)
    {
        ActiveNav = target ?? "Dashboard";
        CurrentView = target switch
        {
            "Events"    => _events    ??= new EventsViewModel(),
            "Config"    => _config    ??= new ConfigViewModel(_settings),
            "Notifier"  => _notifier  ??= new NotifierViewModel(_settings),
            "Console"   => _console   ??= new ConsoleViewModel(_launcher),
            "Analytics" => _analytics ??= new AnalyticsViewModel(),
            "Bazaar"    => _bazaar    ??= new BazaarViewModel(),
            _           => _dashboard ??= new DashboardViewModel(),
        };
    }

    // ── Script control ──

    /// <summary>
    /// Connects the WebSocket, POSTs /api/start, and begins REST polling.
    /// </summary>
    public async void StartScript()
    {
        StatusText = "Starting…";
        OnPropertyChanged(nameof(StatusChipColor));

        _socket.Connect();
        await _client.StartBotAsync();
        StartPolling();
    }

    /// <summary>
    /// POSTs /api/stop, disconnects the WebSocket, and stops polling.
    /// </summary>
    public async void StopScript()
    {
        StatusText = "Stopped";
        OnPropertyChanged(nameof(StatusChipColor));

        StopPolling();
        await _client.StopBotAsync();
        _socket.Disconnect();
    }

    // ── REST polling ──

    private void StartPolling()
    {
        if (_pollTimer is not null) return;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        var stats = await _client.GetStatsAsync();
        if (stats is not null && _dashboard is not null)
            _dashboard.ApplyStats(stats);
    }

    // ── Socket event handlers ──

    private void HandleStateChange(WsStateChange sc)
    {
        StatusText = sc.State == "Running" ? "Running" : "Stopped";
        OnPropertyChanged(nameof(StatusChipColor));

        if (_dashboard is null) return;
        var purse     = sc.Purse.HasValue ? Fmt.Coins(sc.Purse.Value) : _dashboard.Purse;
        var queue     = sc.QueueDepth ?? _dashboard.QueueDepth;
        var botStatus = sc.State == "Running" ? "Online" : "Offline";
        _dashboard.UpdateFromStatus(sc.State, purse, queue, botStatus);
    }

    private void HandleSocketConnection(bool connected)
    {
        if (!connected) return;
        // Real backend is live — stop mock data generators
        _dashboard?.DisableMockMode();
        _events?.DisableMockMode();
    }

    // ── Cleanup ──

    public void Dispose()
    {
        StopPolling();
        _socket.Dispose();
        _client.Dispose();
        _launcher.Dispose();
    }
}

