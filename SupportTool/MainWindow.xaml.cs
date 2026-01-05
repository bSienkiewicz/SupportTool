using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using SupportTool.NumberRange;
using SupportTool.Features.Alerts;
using SupportTool.Features.Services;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SupportTool;
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Type> _pageMapping = new Dictionary<string, Type>
        {
            { "Alerting", typeof(Alerting) },
            { "NRAlertAuditMPM", typeof(AlertAudit) },
            { "NRAlertAuditDM", typeof(AlertAuditDM) },
            { "NRThresholdManager", typeof(ThresholdManager) },
            { "FLRange_RoyalMail", typeof(RoyalMail) }
        };
    
    private DispatcherTimer? _gitBranchTimer;
    private string? _lastBranchName;
    private List<string> _allBranches = new List<string>();
    private string? _repositoryPath;

    public MainWindow()
    {
        this.InitializeComponent();
        TrySetMicaBackdrop(false);
        ExtendsContentIntoTitleBar = true;
        ContentFrame.Navigated += OnNavigated;
        SetTitleBar(this.AppTitleBar);
        this.ContentFrame.Navigate(typeof(Alerting));
        this.Closed += MainWindow_Closed;
        _ = LoadGitBranchAsync();
        _ = LoadAllBranchesAsync();
        StartGitBranchPolling();
        
        // Initially disable search box until we check for repository
        BranchSearchBox.IsEnabled = false;
    }

    private async Task LoadGitBranchAsync()
    {
        try
        {
            // Try to get branch from configured repository path first (alerts repository)
            var settingsService = new Features.Alerts.Services.SettingsService();
            string? repositoryPath = settingsService.GetSetting("NRAlertsDir");
            
            string? branchName = null;
            
            if (!string.IsNullOrEmpty(repositoryPath) && Directory.Exists(repositoryPath))
            {
                branchName = await GitService.GetCurrentBranchAsync(repositoryPath);
            }
            
            // Fallback to current directory if no repository path is configured
            if (string.IsNullOrEmpty(branchName))
            {
                branchName = await GitService.GetCurrentBranchAsync();
            }
            
            // Only update if branch name changed
            if (branchName != _lastBranchName)
            {
                _lastBranchName = branchName;
                
                if (!string.IsNullOrEmpty(branchName))
                {
                    AppTitleBarText.Text = branchName;
                }
                else
                {
                    AppTitleBarText.Text = "No git branch";
                }
            }
        }
        catch
        {
            AppTitleBarText.Text = "No git branch";
        }
    }

    private void StartGitBranchPolling()
    {
        _gitBranchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        
        _gitBranchTimer.Tick += (sender, e) =>
        {
            _ = LoadGitBranchAsync();
        };
        
        _gitBranchTimer.Start();

        // Reload branches every 30 seconds
        var branchListTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        
        branchListTimer.Tick += (sender, e) =>
        {
            _ = LoadAllBranchesAsync();
        };
        
        branchListTimer.Start();
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.SourcePageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        var targetTag = _pageMapping.FirstOrDefault(x => x.Value == e.SourcePageType).Key;
        if (targetTag != null)
        {
            var selectedItem = NavView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == targetTag);

            if (selectedItem != null)
            {
                NavView.SelectedItem = selectedItem;
            }
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            // Navigate to the settings page
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }
        if (args.SelectedItem is NavigationViewItem selectedItem && selectedItem.Tag != null)
        {
            // Navigate to the corresponding page when a NavigationViewItem is selected
            if (_pageMapping.TryGetValue(selectedItem.Tag.ToString(), out var pageType))
            {
                ContentFrame.Navigate(pageType);
            }
        }
    }

    bool TrySetMicaBackdrop(bool useMicaAlt)
    {
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            Microsoft.UI.Xaml.Media.MicaBackdrop micaBackdrop = new()
            {
                Kind = useMicaAlt ? Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt : Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base
            };
            this.SystemBackdrop = micaBackdrop;

            return true;
        }

        return false;
    }

    private void NavigationView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        AppTitleBar.Margin = new Thickness()
        {
            Left = sender.CompactPaneLength * (sender.DisplayMode == NavigationViewDisplayMode.Minimal ? 2 : 1),
            Top = AppTitleBar.Margin.Top,
            Right = AppTitleBar.Margin.Right,
            Bottom = AppTitleBar.Margin.Bottom
        };
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _gitBranchTimer?.Stop();
    }

    private async Task LoadAllBranchesAsync()
    {
        try
        {
            var settingsService = new Features.Alerts.Services.SettingsService();
            _repositoryPath = settingsService.GetSetting("NRAlertsDir");
            
            // Check if repository path is set and exists
            bool hasRepositoryPath = !string.IsNullOrEmpty(_repositoryPath) && Directory.Exists(_repositoryPath);
            
            if (!hasRepositoryPath)
            {
                _repositoryPath = null;
                BranchSearchBox.IsEnabled = false;
                BranchSearchBox.PlaceholderText = "No repository path configured";
                _allBranches = new List<string>();
                return;
            }

            string? testBranch = await GitService.GetCurrentBranchAsync(_repositoryPath);
            bool hasRepository = !string.IsNullOrEmpty(testBranch) || GitService.FindGitRoot(_repositoryPath) != null;

            if (!hasRepository)
            {
                BranchSearchBox.IsEnabled = false;
                BranchSearchBox.PlaceholderText = "No git repository detected";
                _allBranches = new List<string>();
                return;
            }

            // Try to load branches - if successful, enable the search box
            _allBranches = await GitService.GetAllBranchesAsync(_repositoryPath);
            
            BranchSearchBox.IsEnabled = true;
            BranchSearchBox.PlaceholderText = "Search a branch name to checkout";
        }
        catch
        {
            _allBranches = new List<string>();
            BranchSearchBox.IsEnabled = false;
            BranchSearchBox.PlaceholderText = "Error loading repository";
        }
    }

    private async void BranchSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Refresh branch list when user focuses on search box
        await LoadAllBranchesAsync();
    }

    private void BranchSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = sender.Text.Trim();
            if (string.IsNullOrEmpty(query))
            {
                sender.ItemsSource = null;
                return;
            }

            var suggestions = new List<string>();
            
            // Filter existing branches
            var matchingBranches = _allBranches
                .Where(b => b.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList();
            
            suggestions.AddRange(matchingBranches);

            // If no exact match, add option to create new branch
            bool exactMatch = _allBranches.Any(b => b.Equals(query, StringComparison.OrdinalIgnoreCase));
            if (!exactMatch && !string.IsNullOrEmpty(query))
            {
                suggestions.Add($"Create branch: {query}");
            }

            sender.ItemsSource = suggestions;
        }
    }

    private void BranchSearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        sender.Text = args.SelectedItem.ToString()?.Replace("Create branch: ", "") ?? string.Empty;
    }

    private async void BranchSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var branchName = args.QueryText.Trim();
        if (string.IsNullOrEmpty(branchName))
            return;

        // Remove "Create branch: " prefix if present
        branchName = branchName.Replace("Create branch: ", "").Trim();

        // Check if branch exists
        bool branchExists = _allBranches.Any(b => b.Equals(branchName, StringComparison.OrdinalIgnoreCase));

        if (branchExists)
        {
            // Checkout existing branch
            await CheckoutBranchAsync(branchName);
        }
        else
        {
            // Show dialog to create new branch
            await ShowCreateBranchDialogAsync(branchName);
        }

        sender.Text = string.Empty;
    }

    private async Task CheckoutBranchAsync(string branchName)
    {
        try
        {
            bool success = await GitService.CheckoutBranchAsync(branchName, _repositoryPath);
            if (success)
            {
                // Reload branches and current branch
                await LoadAllBranchesAsync();
                await LoadGitBranchAsync();
            }
            else
            {
                // Show error
                var dialog = new ContentDialog
                {
                    Title = "Error",
                    Content = $"Failed to checkout branch '{branchName}'",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Error checking out branch: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private async Task ShowCreateBranchDialogAsync(string branchName)
    {
        try
        {
            var createDialog = new Features.Dialogs.CreateBranchDialog(branchName, _allBranches)
            {
                XamlRoot = this.Content.XamlRoot
            };

            var result = await createDialog.ShowAsync();
            
            if (result == ContentDialogResult.Primary && createDialog.ShouldCreate)
            {
                string baseBranch = createDialog.SelectedBaseBranch;
                if (string.IsNullOrEmpty(baseBranch))
                {
                    // Fallback: try to determine default branch from available branches
                    if (_allBranches.Any(b => b.Equals("main", StringComparison.OrdinalIgnoreCase)))
                    {
                        baseBranch = "main";
                    }
                    else if (_allBranches.Any(b => b.Equals("master", StringComparison.OrdinalIgnoreCase)))
                    {
                        baseBranch = "master";
                    }
                    else if (_allBranches.Count > 0)
                    {
                        baseBranch = _allBranches.First();
                    }
                    else
                    {
                        // Last resort: show error
                        var errorDialog = new ContentDialog
                        {
                            Title = "Error",
                            Content = "No base branch available. Please ensure you have at least one branch in the repository.",
                            CloseButtonText = "OK",
                            XamlRoot = this.Content.XamlRoot
                        };
                        await errorDialog.ShowAsync();
                        return;
                    }
                }

                bool success = await GitService.CreateAndCheckoutBranchAsync(branchName, baseBranch, _repositoryPath);
                if (success)
                {
                    // Reload branches and current branch
                    await LoadAllBranchesAsync();
                    await LoadGitBranchAsync();
                }
                else
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Error",
                        Content = $"Failed to create branch '{branchName}' from '{baseBranch}'",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Error creating branch: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}
