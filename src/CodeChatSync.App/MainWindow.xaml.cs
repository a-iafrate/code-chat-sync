using CodeChatSync.App.Services;
using CodeChatSync.Core;
using CodeChatSync.Git;
using CodeChatSync.Providers.Claude;
using CodeChatSync.Providers.VisualStudio;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace CodeChatSync.App;

public sealed partial class MainWindow : Window
{
	private const string VisualStudioProviderId = "visualstudio";
	private const string ClaudeProviderId = "claudecode";

	private readonly SyncHost _syncHost;
	private string? _loadedFolder;
	private string? _loadedOrigin;
	private string? _configuredFolder;
	private Uri? _repositoryWebUrl;
	private bool _loadingSettings;
	private bool _manualSyncRunning;
	private DateTime? _lastOutcomeTime;
	private readonly List<(string ProviderId, ProjectIdentity Project, CheckBox Restore, CheckBox All, List<(string Id, CheckBox Selected)> Sessions)> _sessionGroups = [];

	public MainWindow(SyncHost syncHost)
	{
		_syncHost = syncHost ?? throw new ArgumentNullException(nameof(syncHost));

		InitializeComponent();

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		if (File.Exists(AppIcons.AppIconPath))
		{
			AppWindow.SetIcon(AppIcons.AppIconPath);
		}

		_syncHost.SyncCompleted += OnSyncCompleted;
		_syncHost.ProviderRunningChanged += OnProviderRunningChanged;
		Closed += OnClosed;
		WindowRoot.Loaded += OnWindowRootLoaded;

		NavView.SelectedItem = SyncNavItem;
		if (_syncHost.LastOutcome is { } lastOutcome)
		{
			ShowOutcome(lastOutcome);
		}

		UpdateStatus();
		_ = LoadSettingsAsync();
	}

	private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
	{
		var page = (args.SelectedItem as NavigationViewItem)?.Tag as string;
		SyncPage.Visibility = page == "sync" ? Visibility.Visible : Visibility.Collapsed;
		ProjectsPage.Visibility = page == "projects" ? Visibility.Visible : Visibility.Collapsed;
		SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
	}

	private async Task<bool> LoadSettingsAsync()
	{
		SaveSettingsButton.IsEnabled = false;
		SaveSelectionButton.IsEnabled = false;
		SelectionHintText.Text = string.Empty;
		ProjectsPanel.Children.Clear();
		AvailableProjectsPanel.Children.Clear();
		AvailableProjectsSection.Visibility = Visibility.Collapsed;
		_loadingSettings = true;
		SessionSelectionPanel.Children.Clear();
		_sessionGroups.Clear();
		LocalConfig? config = null;
		var remoteConnected = false;
		var anyProjectSynced = false;
		try
		{
			config = LocalConfig.Load();
			ThemeComboBox.SelectedIndex = config.ThemePreference switch
			{
				ThemePreference.System => 0,
				ThemePreference.Light => 1,
				ThemePreference.Dark => 2,
				_ => throw new InvalidDataException($"Unknown window theme: {config.ThemePreference}")
			};
			ApplyTheme(config.ThemePreference);
			AutomaticSyncToggle.IsOn = config.AutomaticSyncOnProviderClose;
			SkipVisualStudioCheckToggle.IsOn = config.IsRunningCheckSkipped(VisualStudioProviderId);
			SkipVisualStudioCheckInfoBar.IsOpen = SkipVisualStudioCheckToggle.IsOn;
			SkipClaudeCheckToggle.IsOn = config.IsRunningCheckSkipped(ClaudeProviderId);
			SkipClaudeCheckInfoBar.IsOpen = SkipClaudeCheckToggle.IsOn;
			_loadedFolder = config.SyncRootPath;
			_configuredFolder = config.SyncRootPath;
			SyncFolderTextBox.Text = config.SyncRootPath ?? string.Empty;
			FolderSummaryText.Text = config.SyncRootPath ?? "Not configured";
			OpenFolderButton.Visibility = config.SyncRootPath is { Length: > 0 } configuredFolder && Directory.Exists(configuredFolder)
				? Visibility.Visible
				: Visibility.Collapsed;
			RemoteTextBox.Text = string.Empty;
			_loadedOrigin = null;
			RepositorySummaryText.Text = "No remote set";
			_repositoryWebUrl = null;
			OpenRepositoryButton.Visibility = Visibility.Collapsed;
			ProjectsCountText.Text = config.Projects.Count > 0 ? config.Projects.Count.ToString() : string.Empty;
			if (config.SyncRootPath is not { Length: > 0 } folder)
			{
				ShowSettingsMessage("Choose a folder for your private sync repository.", InfoBarSeverity.Informational);
				ShowEmptyProjects();
				return true;
			}

			if (!Directory.Exists(folder))
			{
				ShowSettingsMessage("The configured sync folder is missing. Choose an existing folder or initialize it.", InfoBarSeverity.Warning);
				ShowEmptyProjects();
				return true;
			}

			var repository = new SyncRepository(folder);
			var status = await Task.Run(() => repository.GetStatus());
			if (status.IsGitRepository)
			{
				var origin = await Task.Run(repository.GetOriginRemoteUrl) ?? string.Empty;
				RemoteTextBox.Text = origin;
				if (origin.Length > 0)
				{
					_loadedOrigin = origin;
					remoteConnected = true;
					RepositorySummaryText.Text = ShortenRemote(origin);
					_repositoryWebUrl = GitRemoteWebUrl.TryCreate(origin);
					OpenRepositoryButton.Visibility = _repositoryWebUrl is null ? Visibility.Collapsed : Visibility.Visible;
				}
			}
			else
			{
				ShowSettingsMessage("This folder is not a Git repository. Turn on Initialize to add an origin remote.", InfoBarSeverity.Warning);
			}

			anyProjectSynced = await LoadSessionsAsync(config, folder);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or GitCommandException or NotSupportedException or InvalidDataException)
		{
			ShowSettingsMessage($"Could not load settings: {exception.Message}", InfoBarSeverity.Error);
			return false;
		}
		finally
		{
			_loadingSettings = false;
			SaveSettingsButton.IsEnabled = true;
			if (config is not null)
			{
				UpdateGettingStarted(config, remoteConnected, anyProjectSynced);
			}

			UpdateStatus();
		}
	}

	private async void OnOpenRepositoryClick(object sender, RoutedEventArgs e)
	{
		if (_repositoryWebUrl is not null && !await Windows.System.Launcher.LaunchUriAsync(_repositoryWebUrl))
		{
			ShowAutomaticSyncMessage($"Could not open {_repositoryWebUrl} in the browser.", InfoBarSeverity.Warning);
		}
	}

	private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
	{
		if (_configuredFolder is not { Length: > 0 } folder)
		{
			return;
		}

		if (!Directory.Exists(folder) || !await Windows.System.Launcher.LaunchFolderPathAsync(folder))
		{
			ShowAutomaticSyncMessage($"Could not open the folder {folder}.", InfoBarSeverity.Warning);
		}
	}

	private static string ShortenRemote(string remote)
	{
		var trimmed = remote.Trim();
		if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
		{
			return uri.Host + uri.AbsolutePath;
		}

		return trimmed;
	}

	private void ShowEmptyProjects()
	{
		ProjectsPanel.Children.Add(CreateHintCard("Set up the sync repository in Settings, then add a project below."));
		SessionSelectionPanel.Children.Add(CreateHintCard("Add a project above to choose its chats."));
	}

	private Border CreateHintCard(string text) => new()
	{
		Style = (Style)WindowRoot.Resources["CardStyle"],
		MinHeight = 0,
		Padding = new Thickness(16),
		Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)WindowRoot.Resources["CaptionStyle"], FontSize = 14 }
	};

	private void OnSyncFolderChanged(object sender, TextChangedEventArgs e)
	{
		if (!_loadingSettings && _loadedFolder is not null
			&& !string.Equals(SyncFolderTextBox.Text, _loadedFolder, StringComparison.OrdinalIgnoreCase))
		{
			RemoteTextBox.Text = string.Empty;
		}
	}

	private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
	{
		try
		{
			if (await PickFolderAsync() is { } path)
			{
				SyncFolderTextBox.Text = path;
			}
		}
		catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
			or UnauthorizedAccessException or InvalidOperationException)
		{
			ShowSettingsMessage($"Could not choose a folder: {exception.Message}", InfoBarSeverity.Error);
		}
	}

	private async void OnBrowseProjectClick(object sender, RoutedEventArgs e)
	{
		try
		{
			if (await PickFolderAsync() is { } path)
			{
				ProjectFolderTextBox.Text = path;
			}
		}
		catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
			or UnauthorizedAccessException or InvalidOperationException)
		{
			ShowProjectMessage($"Could not choose a project folder: {exception.Message}", InfoBarSeverity.Error);
		}
	}

	private async Task<string?> PickFolderAsync()
	{
		var picker = new FolderPicker();
		picker.FileTypeFilter.Add("*");
		WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
		return (await picker.PickSingleFolderAsync())?.Path;
	}

	private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
	{
		var folder = SyncFolderTextBox.Text;
		if (string.IsNullOrWhiteSpace(folder))
		{
			ShowSettingsMessage("Choose a sync folder before saving.", InfoBarSeverity.Error);
			return;
		}

		SaveSettingsButton.IsEnabled = false;
		SettingsProgress.IsActive = true;
		try
		{
			var saved = await _syncHost.SaveSettingsAsync(folder, RemoteTextBox.Text, InitializeToggle.IsOn);
			if (!saved)
			{
				ShowSettingsMessage("A sync is in progress. Try saving again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			InitializeToggle.IsOn = false;
			if (await LoadSettingsAsync())
			{
				ShowSettingsMessage("Settings saved. Sync will use this folder on the next run.", InfoBarSeverity.Success);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or GitCommandException or NotSupportedException)
		{
			ShowSettingsMessage($"Could not save settings: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			SaveSettingsButton.IsEnabled = true;
			SettingsProgress.IsActive = false;
		}
	}

	private async void OnAutomaticSyncToggled(object sender, RoutedEventArgs e)
	{
		if (_loadingSettings)
		{
			return;
		}

		var enabled = AutomaticSyncToggle.IsOn;
		AutomaticSyncToggle.IsEnabled = false;
		try
		{
			if (!await _syncHost.SaveAutomaticSyncAsync(enabled))
			{
				RevertAutomaticSyncToggle(!enabled);
				ShowAutomaticSyncMessage("A sync is in progress. Try again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			AutomaticSyncInfoBar.IsOpen = false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			RevertAutomaticSyncToggle(!enabled);
			ShowAutomaticSyncMessage($"Could not save automatic sync preference: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			AutomaticSyncToggle.IsEnabled = true;
			UpdateStatus();
		}
	}

	private void RevertAutomaticSyncToggle(bool value)
	{
		_loadingSettings = true;
		AutomaticSyncToggle.IsOn = value;
		_loadingSettings = false;
	}

	private async void OnSkipRunningCheckToggled(object sender, RoutedEventArgs e)
	{
		if (_loadingSettings || sender is not ToggleSwitch { Tag: string providerId } toggle)
		{
			return;
		}

		var isVisualStudio = providerId == VisualStudioProviderId;
		var toolName = isVisualStudio ? "Visual Studio" : "Claude Code";
		var infoBar = isVisualStudio ? SkipVisualStudioCheckInfoBar : SkipClaudeCheckInfoBar;
		var skip = toggle.IsOn;
		toggle.IsEnabled = false;
		try
		{
			if (skip)
			{
				var risk = isVisualStudio
					? "CodeChatSync will read and overwrite Copilot chats under .vs even while Visual Studio is open. "
						+ "Visual Studio can hold those files, archive them half-written, or overwrite a restored chat when it saves or closes. "
					: "CodeChatSync will read and overwrite Claude Code transcripts even while Claude Code is open. "
						+ "A session writing at the same time can leave an incomplete chat in the archive, or lose the changes made to a restored chat. ";
				var confirm = new ContentDialog
				{
					XamlRoot = Content.XamlRoot,
					RequestedTheme = WindowRoot.ActualTheme,
					Title = $"Sync {toolName} while it is running?",
					Content = risk + "Local files are still backed up before being replaced.",
					PrimaryButtonText = "Turn off the check",
					CloseButtonText = "Cancel",
					DefaultButton = ContentDialogButton.Close
				};
				if (await confirm.ShowAsync() != ContentDialogResult.Primary)
				{
					RevertToggle(toggle, false);
					return;
				}
			}

			if (!await _syncHost.SaveSkipRunningCheckAsync(providerId, skip))
			{
				RevertToggle(toggle, !skip);
				ShowSettingsMessage("A sync is in progress. Try again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			infoBar.IsOpen = skip;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or InvalidDataException or NotSupportedException)
		{
			RevertToggle(toggle, !skip);
			ShowSettingsMessage($"Could not save the {toolName} setting: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			toggle.IsEnabled = true;
		}
	}

	private void RevertToggle(ToggleSwitch toggle, bool value)
	{
		_loadingSettings = true;
		toggle.IsOn = value;
		_loadingSettings = false;
	}

	private void ShowAutomaticSyncMessage(string message, InfoBarSeverity severity)
	{
		AutomaticSyncInfoBar.Message = message;
		AutomaticSyncInfoBar.Severity = severity;
		AutomaticSyncInfoBar.IsOpen = true;
	}

	private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loadingSettings || ThemeComboBox.SelectedIndex is < 0 or > 2)
		{
			return;
		}

		var preference = ThemeComboBox.SelectedIndex switch
		{
			0 => ThemePreference.System,
			1 => ThemePreference.Light,
			2 => ThemePreference.Dark,
			_ => throw new InvalidOperationException("Unknown window theme selection.")
		};

		ThemeComboBox.IsEnabled = false;
		try
		{
			if (!await _syncHost.SaveThemePreferenceAsync(preference))
			{
				ShowThemeMessage("A sync is in progress. Try again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			ApplyTheme(preference);
			ThemeInfoBar.IsOpen = false;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			ShowThemeMessage($"Could not save window theme: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			ThemeComboBox.IsEnabled = true;
		}
	}

	private void ApplyTheme(ThemePreference preference)
	{
		WindowRoot.RequestedTheme = preference switch
		{
			ThemePreference.System => ElementTheme.Default,
			ThemePreference.Light => ElementTheme.Light,
			ThemePreference.Dark => ElementTheme.Dark,
			_ => throw new InvalidDataException($"Unknown window theme: {preference}")
		};
		AppWindow.TitleBar.PreferredTheme = preference switch
		{
			ThemePreference.Light => TitleBarTheme.Light,
			ThemePreference.Dark => TitleBarTheme.Dark,
			_ => TitleBarTheme.UseDefaultAppMode
		};
	}

	private void ShowThemeMessage(string message, InfoBarSeverity severity)
	{
		ThemeInfoBar.Message = message;
		ThemeInfoBar.Severity = severity;
		ThemeInfoBar.IsOpen = true;
	}

	private async void OnRefreshProjectsClick(object sender, RoutedEventArgs e)
	{
		RefreshProjectsButton.IsEnabled = false;
		try
		{
			await LoadSettingsAsync();
		}
		finally
		{
			RefreshProjectsButton.IsEnabled = true;
		}
	}

	private void OnAddProviderChanged(object sender, SelectionChangedEventArgs e)
	{
		var claude = AddProviderRadioButtons.SelectedIndex == 1;
		VisualStudioAddPanel.Visibility = claude ? Visibility.Collapsed : Visibility.Visible;
		ClaudeAddPanel.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
	}

	private async void OnAddProjectClick(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(ProjectFolderTextBox.Text))
		{
			ShowProjectMessage("Choose a project folder before adding it.", InfoBarSeverity.Error);
			return;
		}

		AddProjectButton.IsEnabled = false;
		try
		{
			var added = await _syncHost.AddProjectAsync(ProjectFolderTextBox.Text,
				ProjectNameTextBox.Text, ProjectRemoteTextBox.Text);
			if (!added)
			{
				ShowProjectMessage("A sync is in progress. Try adding the project again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			ProjectFolderTextBox.Text = string.Empty;
			ProjectNameTextBox.Text = string.Empty;
			ProjectRemoteTextBox.Text = string.Empty;
			if (await LoadSettingsAsync())
			{
				AddProjectExpander.IsExpanded = false;
				ShowProjectMessage("Project registered on this PC. Sync now to archive its chats.", InfoBarSeverity.Success);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
		{
			ShowProjectMessage($"Could not add project: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			AddProjectButton.IsEnabled = true;
		}
	}

	/// <summary>A registerable Claude Code project: one Git repository, however many folders it was run from.</summary>
	private sealed record ClaudeRepositoryCandidate(string ProjectPath, bool Eligible, string Description);

	/// <summary>
	/// Turns the working directories Claude Code recorded into the projects that can
	/// actually be registered.
	/// </summary>
	/// <remarks>
	/// Claude Code stores a session under the directory it was started in, which is often
	/// a subfolder — running it from a repository's <c>src</c> is the common case. Each
	/// candidate is therefore resolved to its repository root and the ones sharing a root
	/// are offered as a single project, since that root is what gets registered and what
	/// the provider collects transcripts beneath.
	/// </remarks>
	private static ClaudeRepositoryCandidate[] GroupClaudeCandidatesByRepository(
		IReadOnlyList<ClaudeProjectCandidate> candidates)
	{
		var usable = candidates
			.Where(candidate => candidate.LocalPathExists
				&& !candidate.HasStorageCollision
				&& candidate.StorageFolderName is not null
				&& candidate.Sessions.Any(session => session.IsInStorageFolder))
			.Select(candidate => (Candidate: candidate, Root: GitRemoteReader.FindRepositoryRoot(candidate.LocalPath)))
			.ToArray();

		var grouped = usable
			.Where(entry => entry.Root is not null)
			.GroupBy(entry => entry.Root!, StringComparer.OrdinalIgnoreCase)
			.Select(group =>
			{
				var remote = GitRemoteReader.FindPrimaryRemoteUrl(group.Key);
				var transcripts = group.Sum(entry =>
					entry.Candidate.Sessions.Count(session => session.IsInStorageFolder));
				var folders = group.Count();

				if (string.IsNullOrWhiteSpace(remote) || !ProjectIdentity.TryFromRemote(remote, out _))
				{
					return new ClaudeRepositoryCandidate(
						group.Key,
						false,
						"Unavailable: this Git repository has no usable remote, and a project is identified by its remote.");
				}

				var from = folders == 1 ? string.Empty : $" from {folders} folders";
				return new ClaudeRepositoryCandidate(
					group.Key, true, $"Git remote: {remote} · {transcripts} transcripts{from}");
			});

		// Working directories outside any repository cannot be registered: say so rather
		// than hiding them, since the user chose where to run Claude Code.
		var orphans = usable
			.Where(entry => entry.Root is null)
			.Select(entry => new ClaudeRepositoryCandidate(
				entry.Candidate.LocalPath,
				false,
				"Unavailable: this folder is not inside a Git repository."));

		var unusable = candidates
			.Where(candidate => !candidate.LocalPathExists
				|| candidate.HasStorageCollision
				|| candidate.StorageFolderName is null
				|| !candidate.Sessions.Any(session => session.IsInStorageFolder))
			.Select(candidate => new ClaudeRepositoryCandidate(
				candidate.LocalPath,
				false,
				candidate.LocalPathExists
					? "Unavailable: Claude Code's storage folder for this path is shared with another folder."
					: "Unavailable: this folder no longer exists on this PC."));

		return
		[
			.. grouped.Concat(orphans).Concat(unusable)
				.OrderByDescending(candidate => candidate.Eligible)
				.ThenBy(candidate => candidate.ProjectPath, StringComparer.OrdinalIgnoreCase)
		];
	}

	private async void OnDiscoverClaudeClick(object sender, RoutedEventArgs e)
	{
		DiscoverClaudeButton.IsEnabled = false;
		ClaudeCandidatePanel.Children.Clear();
		try
		{
			var processGuard = _syncHost.CreateDiscoveryProcessGuard();
			var candidates = await Task.Run(() => GroupClaudeCandidatesByRepository(
				ClaudeProjectDiscovery.DiscoverCandidates(processGuard)));

			if (candidates.Length == 0)
			{
				ClaudeCandidatePanel.Children.Add(CreateHintCard("No Claude Code sessions with recorded project folders were found."));
			}

			foreach (var candidate in candidates)
			{
				var row = new Grid { ColumnSpacing = 16 };
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				row.Children.Add(CreateProviderBadge(ClaudeProviderId, 32));
				var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
				text.Children.Add(new TextBlock
				{
					Text = candidate.ProjectPath, TextTrimming = TextTrimming.CharacterEllipsis,
					FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13
				});
				text.Children.Add(new TextBlock
				{
					Style = (Style)WindowRoot.Resources["CaptionStyle"],
					Text = candidate.Description
				});
				Grid.SetColumn(text, 1);
				row.Children.Add(text);
				if (candidate.Eligible)
				{
					var add = new Button { Content = "Add", VerticalAlignment = VerticalAlignment.Center };
					add.Click += async (_, _) => await AddClaudeProjectAsync(candidate.ProjectPath, add);
					Grid.SetColumn(add, 2);
					row.Children.Add(add);
				}
				else
				{
					row.Opacity = 0.6;
				}

				ClaudeCandidatePanel.Children.Add(new Border { Style = (Style)WindowRoot.Resources["CardStyle"], Child = row });
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
		{
			ShowProjectMessage($"Could not discover Claude Code projects: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			DiscoverClaudeButton.IsEnabled = true;
		}
	}

	private async Task AddClaudeProjectAsync(string path, Button button)
	{
		button.IsEnabled = false;
		try
		{
			if (!await _syncHost.AddClaudeProjectAsync(path))
			{
				ShowProjectMessage("A sync is in progress. Try adding the Claude Code project again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			ClaudeCandidatePanel.Children.Clear();
			if (await LoadSettingsAsync())
			{
				AddProjectExpander.IsExpanded = false;
				ShowProjectMessage(
					$"Claude Code enabled for this Git project on this PC. Its transcripts restore only onto a PC "
					+ $"where the project sits at the same path, '{path}', until transcript paths can be rewritten.",
					InfoBarSeverity.Success);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
		{
			ShowProjectMessage($"Could not add Claude Code project: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			button.IsEnabled = true;
		}
	}

	private async Task RemoveProjectAsync(ProjectIdentity identity, Button button)
	{
		var confirm = new ContentDialog
		{
			XamlRoot = Content.XamlRoot,
			RequestedTheme = WindowRoot.ActualTheme,
			Title = "Remove project from this PC?",
			Content = $"{identity.NormalizedRemote} will no longer sync on this PC. Archived chats in the private repository and local chat files will not be deleted.",
			PrimaryButtonText = "Remove",
			CloseButtonText = "Cancel",
			DefaultButton = ContentDialogButton.Close
		};
		if (await confirm.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}

		button.IsEnabled = false;
		try
		{
			if (!await _syncHost.RemoveProjectAsync(identity))
			{
				ShowProjectMessage("A sync is in progress. Try removing the project again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			if (await LoadSettingsAsync())
			{
				ShowProjectMessage("Project removed from this PC. Archived chats remain in the private repository.", InfoBarSeverity.Success);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
		{
			ShowProjectMessage($"Could not remove project: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			button.IsEnabled = true;
		}
	}

	private async Task SyncProjectAsync(ProjectIdentity identity, Button button)
	{
		button.IsEnabled = false;
		button.Content = "Syncing…";
		try
		{
			var outcome = await _syncHost.SyncProjectAsync(identity);
			ShowOutcome(outcome);
			if (outcome.NeedsAttention || outcome.Status == SyncOutcomeStatus.AlreadyRunning)
			{
				ShowProjectMessage(outcome.Summary, InfoBarSeverity.Warning);
			}
			else if (await LoadSettingsAsync())
			{
				ShowProjectMessage(outcome.Summary, InfoBarSeverity.Success);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
		{
			ShowProjectMessage($"Could not sync project: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			button.Content = "Sync this project";
			button.IsEnabled = true;
		}
	}

	private void ShowProjectMessage(string message, InfoBarSeverity severity)
	{
		ProjectInfoBar.Message = message;
		ProjectInfoBar.Severity = severity;
		ProjectInfoBar.IsOpen = true;
	}

	private static string GetProviderName(string providerId) =>
		providerId == ClaudeProviderId ? "Claude Code" : "Visual Studio (Copilot)";

	private static Border CreateProviderBadge(string providerId, double size)
	{
		var claude = providerId == ClaudeProviderId;
		return new Border
		{
			Width = size,
			Height = size,
			CornerRadius = new CornerRadius(4),
			VerticalAlignment = VerticalAlignment.Center,
			Background = new SolidColorBrush(claude
				? Windows.UI.Color.FromArgb(0xFF, 0xC1, 0x5F, 0x3C)
				: Windows.UI.Color.FromArgb(0xFF, 0x68, 0x21, 0x7A)),
			Child = new TextBlock
			{
				Text = claude ? "CC" : "VS",
				Foreground = new SolidColorBrush(Colors.White),
				FontSize = size >= 32 ? 11 : 10,
				FontWeight = FontWeights.Bold,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			}
		};
	}

	/// <summary>Builds the project and restore rows; returns whether any project has synced on this PC.</summary>
	private async Task<bool> LoadSessionsAsync(LocalConfig config, string syncRoot)
	{
		var (groups, available) = await Task.Run(() =>
		{
			var shared = SharedConfig.Load(syncRoot);
			var results = new List<(string ProviderId, ProjectIdentity Project, string Name, string LocalPath, DateTime? LastRunUtc, LocalRestoreSelection? Selection, IReadOnlyList<(string Id, string? Name, DateTimeOffset? UpdatedAt)> Sessions)>();
			foreach (var entry in config.Projects)
			{
				if (!ProjectIdentity.TryFromRemote(entry.Remote, out var identity) || identity is null)
				{
					throw new InvalidDataException($"Invalid registered project remote: {entry.Remote}");
				}

				var name = shared.Find(identity)?.Name ?? identity.Slug;
				if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
					|| name.Contains('/') || name.Contains('\\'))
				{
					throw new InvalidDataException($"Invalid shared project folder name: {name}");
				}

				foreach (var providerId in LocalConfig.GetEnabledProviderIds(entry))
				{
					var projectRoot = Path.Combine(syncRoot, providerId, name);
					IReadOnlyList<(string Id, string? Name, DateTimeOffset? UpdatedAt)> sessions = providerId switch
					{
						VisualStudioProviderId => CopilotChatDiscovery.DiscoverSessions(projectRoot)
							.Select(session => (Id: Path.GetFileName(session.SessionDirectory), session.Name, session.UpdatedAt))
							.ToArray(),
						ClaudeProviderId => ClaudeArchivedChatDiscovery.Discover(projectRoot)
							.Select(session => (session.Id, session.Title, (DateTimeOffset?)new DateTimeOffset(session.UpdatedAt)))
							.ToArray(),
						_ => throw new InvalidDataException($"Unsupported registered provider: {providerId}")
					};
					var statePath = new SyncWorkspace(config, shared).GetStatePath(providerId, new ProjectInfo
					{
						Identity = identity, LocalPath = entry.LocalPath, DisplayName = name
					});
					DateTime? lastRun = File.Exists(statePath) ? File.GetLastWriteTimeUtc(statePath) : null;
					results.Add((providerId, identity, name, entry.LocalPath, lastRun,
						config.FindRestoreSelection(providerId, identity), sessions));
				}
			}

			return (Results: results, Available: AvailableProjects.FindUnregistered(config, shared));
		});

		RenderAvailableProjects(available);

		if (groups.Count == 0)
		{
			ProjectsPanel.Children.Add(CreateHintCard("No projects registered yet. Expand Add a project below to get started."));
			SessionSelectionPanel.Children.Add(CreateHintCard("Add a project above to choose its chats."));
			return false;
		}

		foreach (var group in groups)
		{
			ProjectsPanel.Children.Add(CreateProjectRow(group.ProviderId, group.Project, group.Name,
				group.LocalPath, group.LastRunUtc, group.Sessions.Count));
			SessionSelectionPanel.Children.Add(CreateRestoreRow(group.ProviderId, group.Project, group.Name,
				group.Selection, group.Sessions));
		}

		return groups.Any(group => group.LastRunUtc is not null);
	}

	/// <summary>Shows the projects already known to the sync repository but not registered here.</summary>
	private void RenderAvailableProjects(IReadOnlyList<UnregisteredProject> available)
	{
		AvailableProjectsPanel.Children.Clear();
		AvailableProjectsSection.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		foreach (var project in available)
		{
			AvailableProjectsPanel.Children.Add(CreateAvailableProjectRow(project));
		}
	}

	private Border CreateAvailableProjectRow(UnregisteredProject project)
	{
		var row = new Grid { ColumnSpacing = 16 };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		row.Children.Add(new FontIcon { Glyph = "", Style = (Style)WindowRoot.Resources["CardIconStyle"] });

		var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		text.Children.Add(new TextBlock { Text = project.Name, TextTrimming = TextTrimming.CharacterEllipsis });
		text.Children.Add(new TextBlock
		{
			Style = (Style)WindowRoot.Resources["CaptionStyle"],
			Text = project.Remote,
			FontFamily = new FontFamily("Cascadia Mono, Consolas"),
			FontSize = 13,
			TextTrimming = TextTrimming.CharacterEllipsis
		});
		Grid.SetColumn(text, 1);
		row.Children.Add(text);

		var add = new Button { Content = "Browse and add…", VerticalAlignment = VerticalAlignment.Center };
		add.Click += async (_, _) => await AddAvailableProjectAsync(project, add);
		Grid.SetColumn(add, 2);
		row.Children.Add(add);

		return new Border { Style = (Style)WindowRoot.Resources["CardStyle"], Child = row };
	}

	/// <summary>
	/// Registers a project already known to the sync repository: only the local folder is
	/// missing, so the remote and sync folder name are reused as-is instead of being
	/// re-derived from a Git remote on this PC.
	/// </summary>
	private async Task AddAvailableProjectAsync(UnregisteredProject project, Button button)
	{
		button.IsEnabled = false;
		try
		{
			var path = await PickFolderAsync();
			if (path is null)
			{
				return;
			}

			var added = await _syncHost.AddProjectAsync(path, project.Name, project.Remote);
			if (!added)
			{
				ShowProjectMessage("A sync is in progress. Try adding the project again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			if (await LoadSettingsAsync())
			{
				ShowProjectMessage("Project registered on this PC. Sync now to archive its chats.", InfoBarSeverity.Success);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException
			or System.Runtime.InteropServices.COMException)
		{
			ShowProjectMessage($"Could not add project: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			button.IsEnabled = true;
		}
	}

	private Expander CreateProjectRow(string providerId, ProjectIdentity project, string name,
		string localPath, DateTime? lastRunUtc, int chatCount)
	{
		var providerName = GetProviderName(providerId);
		var header = new Grid { ColumnSpacing = 16, MinHeight = 44 };
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		header.Children.Add(CreateProviderBadge(providerId, 32));
		var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
		title.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
		title.Children.Add(new TextBlock
		{
			Style = (Style)WindowRoot.Resources["CaptionStyle"],
			Text = $"{providerName} · {chatCount} archived chats"
		});
		Grid.SetColumn(title, 1);
		header.Children.Add(title);

		var details = new Grid { ColumnSpacing = 24, RowSpacing = 6 };
		details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		AddDetail(details, 0, "Provider", providerName, monospace: false);
		AddDetail(details, 1, "Git remote", project.NormalizedRemote, monospace: true);
		AddDetail(details, 2, "Local folder", localPath, monospace: true);
		AddDetail(details, 3, "Last local sync run",
			lastRunUtc is { } time ? time.ToLocalTime().ToString("g") : "Never", monospace: false);

		var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		var sync = new Button { Content = "Sync this project" };
		sync.Click += async (_, _) => await SyncProjectAsync(project, sync);
		actions.Children.Add(sync);
		var remove = new Button { Content = "Remove from this PC" };
		remove.Click += async (_, _) => await RemoveProjectAsync(project, remove);
		actions.Children.Add(remove);

		var content = new StackPanel { Spacing = 16, Padding = new Thickness(48, 0, 0, 0) };
		content.Children.Add(details);
		content.Children.Add(actions);

		return new Expander
		{
			Header = header,
			Content = content,
			IsExpanded = false,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
	}

	private void AddDetail(Grid grid, int row, string label, string value, bool monospace)
	{
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		var labelText = new TextBlock { Text = label, Style = (Style)WindowRoot.Resources["CaptionStyle"], FontSize = 14, LineHeight = 20 };
		var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
		if (monospace)
		{
			valueText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
			valueText.FontSize = 13;
		}

		Grid.SetRow(labelText, row);
		Grid.SetRow(valueText, row);
		Grid.SetColumn(valueText, 1);
		grid.Children.Add(labelText);
		grid.Children.Add(valueText);
	}

	private Expander CreateRestoreRow(string providerId, ProjectIdentity project, string name,
		LocalRestoreSelection? selection, IReadOnlyList<(string Id, string? Name, DateTimeOffset? UpdatedAt)> sessions)
	{
		var providerName = GetProviderName(providerId);
		var captionStyle = (Style)WindowRoot.Resources["CaptionStyle"];

		var restore = new CheckBox
		{
			Content = "Restore on this PC",
			MinWidth = 0,
			IsChecked = selection is null || selection.SessionIds.Count > 0,
			VerticalAlignment = VerticalAlignment.Center
		};
		ToolTipService.SetToolTip(restore, $"Controls {providerName} restores on this PC; archived chats remain in Git.");

		var summary = new TextBlock { Style = captionStyle, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
		var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
		label.Children.Add(CreateProviderBadge(providerId, 24));
		var labelText = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
		labelText.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
		labelText.Children.Add(summary);
		label.Children.Add(labelText);

		var header = new Grid { ColumnSpacing = 16, MinHeight = 44 };
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		header.Children.Add(restore);
		var divider = new Border { Style = (Style)WindowRoot.Resources["VerticalDividerStyle"] };
		Grid.SetColumn(divider, 1);
		header.Children.Add(divider);
		Grid.SetColumn(label, 2);
		header.Children.Add(label);

		var all = new CheckBox { Content = "Restore all chats, including future ones", IsChecked = selection is null };
		var selectionStatus = new TextBlock { Style = captionStyle, Margin = new Thickness(28, 0, 0, 8) };
		var search = new TextBox { PlaceholderText = "Search by title or session ID", Header = "Find chats", MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 320 };

		var list = new StackPanel();
		var items = new List<(string Id, CheckBox Selected)>();
		var searchable = new List<(string Id, string Title, FrameworkElement Row)>();
		foreach (var session in sessions)
		{
			var title = FormatChatTitle(session.Name, session.Id);
			var text = new StackPanel();
			text.Children.Add(new TextBlock
			{
				Text = title, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
				Opacity = title == "Untitled chat" ? 0.7 : 1
			});
			var shortId = session.Id.Length > 8 ? session.Id[..8] : session.Id;
			text.Children.Add(new TextBlock
			{
				Style = captionStyle,
				Text = session.UpdatedAt is { } updated
					? $"Updated {updated.ToLocalTime():g} · ID {shortId}"
					: $"ID {shortId}"
			});
			var selected = new CheckBox
			{
				Content = text,
				MinWidth = 0,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				IsChecked = selection?.SessionIds.Contains(session.Id, StringComparer.OrdinalIgnoreCase) ?? true
			};
			ToolTipService.SetToolTip(selected, $"Session ID: {session.Id}");
			var row = new Border { Style = (Style)WindowRoot.Resources["ChatRowStyle"], Child = selected };
			list.Children.Add(row);
			items.Add((session.Id, selected));
			searchable.Add((session.Id, session.Name ?? title, row));
		}

		var noMatches = new TextBlock { Text = "No chats match this search.", Style = captionStyle, FontSize = 14, Margin = new Thickness(12, 16, 12, 16), Visibility = Visibility.Collapsed };
		list.Children.Add(noMatches);
		var listBorder = new Border
		{
			Style = (Style)WindowRoot.Resources["ChatListStyle"],
			Child = new ScrollViewer { MaxHeight = 360, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
		};

		var content = new StackPanel { Spacing = 8, Padding = new Thickness(48, 0, 0, 0) };
		content.Children.Add(all);
		content.Children.Add(selectionStatus);
		if (items.Count == 0)
		{
			content.Children.Add(new TextBlock { Text = "No synced chats for this project yet.", Style = captionStyle, FontSize = 14 });
		}
		else
		{
			content.Children.Add(search);
			content.Children.Add(listBorder);
		}

		var expander = new Expander
		{
			Header = header,
			Content = content,
			IsExpanded = false,
			HorizontalAlignment = HorizontalAlignment.Stretch,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};

		// While "restore all" is on, every chat shows as selected; the individual picks are kept for when it is turned off.
		var individual = items.ToDictionary(item => item.Id, item => item.Selected.IsChecked == true);
		var applyingAll = false;

		void ApplyRestoreAll()
		{
			applyingAll = true;
			var restoreAll = all.IsChecked == true;
			foreach (var (id, box) in items)
			{
				box.IsChecked = restoreAll || individual[id];
				box.IsEnabled = !restoreAll;
			}

			applyingAll = false;
		}

		void UpdateSelectionSummary()
		{
			var enabled = restore.IsChecked == true;
			var restoreAll = all.IsChecked == true;
			var selectedCount = items.Count(item => item.Selected.IsChecked == true);
			content.IsHitTestVisible = enabled;
			content.Opacity = enabled ? 1 : 0.5;
			label.Opacity = enabled ? 1 : 0.5;
			summary.Text = !enabled
				? $" · {providerName} · {items.Count} chats · not restored"
				: restoreAll
					? $" · {providerName} · {items.Count} chats · restore all"
					: $" · {providerName} · {selectedCount}/{items.Count} chats selected";
			selectionStatus.Text = !enabled
				? $"No {providerName} chats from this project will be restored on this PC. Archived and existing local chats remain unchanged."
				: restoreAll
					? $"All {items.Count} archived chats selected. Future chats will be restored automatically."
					: $"{selectedCount} of {items.Count} archived chats selected. Future chats will not be restored automatically.";
		}

		foreach (var (id, box) in items)
		{
			box.Checked += (_, _) => OnSessionToggled(id, true);
			box.Unchecked += (_, _) => OnSessionToggled(id, false);
		}

		void OnSessionToggled(string id, bool value)
		{
			if (applyingAll)
			{
				return;
			}

			individual[id] = value;
			UpdateSelectionSummary();
			MarkSelectionDirty();
		}

		restore.Checked += (_, _) =>
		{
			if (all.IsChecked != true && individual.Values.All(value => !value))
			{
				all.IsChecked = true;
			}

			UpdateSelectionSummary();
			MarkSelectionDirty();
		};
		restore.Unchecked += (_, _) =>
		{
			expander.IsExpanded = false;
			UpdateSelectionSummary();
			MarkSelectionDirty();
		};
		all.Checked += (_, _) => { ApplyRestoreAll(); UpdateSelectionSummary(); MarkSelectionDirty(); };
		all.Unchecked += (_, _) => { ApplyRestoreAll(); UpdateSelectionSummary(); MarkSelectionDirty(); };
		search.TextChanged += (_, _) =>
		{
			var query = search.Text.Trim();
			var matches = 0;
			foreach (var (id, title, row) in searchable)
			{
				var visible = title.Contains(query, StringComparison.OrdinalIgnoreCase)
					|| id.Contains(query, StringComparison.OrdinalIgnoreCase);
				row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
				if (visible)
				{
					matches++;
				}
			}

			noMatches.Visibility = items.Count > 0 && matches == 0 ? Visibility.Visible : Visibility.Collapsed;
		};

		ApplyRestoreAll();
		UpdateSelectionSummary();
		_sessionGroups.Add((providerId, project, restore, all, items));
		return expander;
	}

	private void MarkSelectionDirty()
	{
		SaveSelectionButton.IsEnabled = true;
		SelectionHintText.Text = "Unsaved changes";
	}

	private static string FormatChatTitle(string? name, string id)
	{
		if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), id, StringComparison.OrdinalIgnoreCase))
		{
			return "Untitled chat";
		}

		var title = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		if (title.StartsWith("The following code changes are from one or more source files in a diff format.", StringComparison.OrdinalIgnoreCase))
		{
			return "Untitled chat";
		}

		return title.Length > 90 ? title[..87] + "..." : title;
	}

	private async void OnSaveSelectionClick(object sender, RoutedEventArgs e)
	{
		var selections = _sessionGroups.Select(group =>
			(group.ProviderId, group.Project, SessionIds: group.Restore.IsChecked != true
				? (IReadOnlyList<string>)[]
				: group.All.IsChecked == true
					? null
					: group.Sessions.Where(session => session.Selected.IsChecked == true)
						.Select(session => session.Id).ToArray())).ToArray();

		SaveSelectionButton.IsEnabled = false;
		var saved = false;
		try
		{
			if (!await _syncHost.SaveRestoreSelectionsAsync(selections))
			{
				ShowSelectionMessage("A sync is in progress. Try again when it finishes.", InfoBarSeverity.Warning);
				return;
			}

			saved = true;
			SelectionInfoBar.IsOpen = false;
			SelectionHintText.Text = "Chat selection saved on this PC. Unselected chats remain in the Git repository; existing local chats are not deleted.";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
			or ArgumentException or InvalidDataException or NotSupportedException)
		{
			ShowSelectionMessage($"Could not save chat selection: {exception.Message}", InfoBarSeverity.Error);
		}
		finally
		{
			SaveSelectionButton.IsEnabled = !saved;
		}
	}

	private void ShowSelectionMessage(string message, InfoBarSeverity severity)
	{
		SelectionInfoBar.Message = message;
		SelectionInfoBar.Severity = severity;
		SelectionInfoBar.IsOpen = true;
	}

	private void ShowSettingsMessage(string message, InfoBarSeverity severity)
	{
		SettingsInfoBar.Message = message;
		SettingsInfoBar.Severity = severity;
		SettingsInfoBar.IsOpen = true;
	}

	private async void OnSyncNowClick(object sender, RoutedEventArgs e) => await RunSyncNowAsync();

	private async Task RunSyncNowAsync()
	{
		_manualSyncRunning = true;
		UpdateStatus();

		try
		{
			ShowOutcome(await _syncHost.SyncNowAsync());
		}
		finally
		{
			_manualSyncRunning = false;
			UpdateStatus();
		}
	}

	private void OnSyncCompleted(object? sender, SyncOutcome outcome) =>
		DispatcherQueue.TryEnqueue(() => ShowOutcome(outcome));

	private void OnProviderRunningChanged(object? sender, bool isRunning) =>
		DispatcherQueue.TryEnqueue(UpdateStatus);

	private void ShowOutcome(SyncOutcome outcome)
	{
		_lastOutcomeTime = DateTime.Now;
		LastRunText.Text = $"{_lastOutcomeTime:g} · {outcome.Status}: {outcome.Summary}";
		MarkFirstSyncCompleted(outcome);
		UpdateStatus();
	}

	private void UpdateStatus()
	{
		var syncing = _manualSyncRunning || _syncHost.IsSyncing;
		var outcome = _syncHost.LastOutcome;
		var repository = RepositorySummaryText.Text;
		string title;
		string description;
		FrameworkElement icon;

		if (syncing)
		{
			(title, description, icon) = ("Syncing chats…", $"Archiving chats and pushing them to {repository}.", StatusSyncingIcon);
		}
		else if (_configuredFolder is not { Length: > 0 })
		{
			StatusCautionGlyph.Glyph = "\uE7BA";
			(title, description, icon) = ("Set up your sync repository", "Choose a private sync folder in Settings to start syncing.", StatusCautionIcon);
		}
		else if (outcome is not null && outcome.Status != SyncOutcomeStatus.AlreadyRunning && outcome.NeedsAttention)
		{
			(title, description, icon) = (outcome.HasConflicts ? "Sync needs attention" : "Sync did not complete", outcome.Summary, StatusErrorIcon);
		}
		else if (_syncHost.IsProviderRunning && AutomaticSyncToggle.IsOn)
		{
			StatusCautionGlyph.Glyph = "\uE823";
			(title, description, icon) = ("Waiting for chat tools to close",
				"Visual Studio or Claude Code is open. Chats sync automatically once it closes; Sync now archives them right away.", StatusCautionIcon);
		}
		else if (outcome is { Status: SyncOutcomeStatus.Completed } && _lastOutcomeTime is { } time)
		{
			(title, description, icon) = ("Up to date", $"Last synced {time:g} · {outcome.Summary}", StatusSuccessIcon);
		}
		else if (!AutomaticSyncToggle.IsOn)
		{
			StatusNeutralGlyph.Glyph = "\uE769";
			(title, description, icon) = ("Automatic sync is off", "Use Sync now, or sync individual projects from Projects.", StatusNeutralIcon);
		}
		else
		{
			StatusNeutralGlyph.Glyph = "\uE895";
			(title, description, icon) = ("Ready", "Chats sync automatically after Visual Studio or Claude Code closes.", StatusNeutralIcon);
		}

		StatusTitleText.Text = title;
		StatusDescriptionText.Text = description;
		foreach (var candidate in new FrameworkElement[] { StatusNeutralIcon, StatusSyncingIcon, StatusSuccessIcon, StatusCautionIcon, StatusErrorIcon })
		{
			candidate.Visibility = candidate == icon ? Visibility.Visible : Visibility.Collapsed;
		}

		SyncProgressBar.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
		SyncNowButton.IsEnabled = !syncing;
		SyncNowButton.Content = syncing ? "Syncing…" : "Sync now";
	}

	/// <summary>
	/// The app lives in the tray, so closing this window only detaches it from the
	/// sync host rather than stopping the watch.
	/// </summary>
	private void OnClosed(object sender, WindowEventArgs args)
	{
		_syncHost.SyncCompleted -= OnSyncCompleted;
		_syncHost.ProviderRunningChanged -= OnProviderRunningChanged;
		Closed -= OnClosed;
	}
}
