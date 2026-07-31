using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using RemoteDesktop.Shared;

namespace RemoteDesktop.Master;

/// <summary>
/// Browses the server's groups and the computers in them, so the operator can pick a
/// machine to connect to instead of typing an ID. Read-only view of the same data the
/// admin panel manages (fetched from <c>/directory</c> with the admin password).
/// On Connect it returns the chosen id via <see cref="SelectedId"/>.
/// </summary>
public partial class DirectoryWindow : Window
{
    // Must be public: WPF's GridView column bindings can't read properties off a
    // non-public type, which would leave every cell blank.
    public sealed record Row(string Id, string Display, string GroupName, bool Online, string StatusText);

    private readonly string _serverUrl;
    private readonly AppConfig _config;
    private readonly DirectoryClient _client = new();

    public string? SelectedId { get; private set; }

    public DirectoryWindow(string serverUrl, AppConfig config)
    {
        InitializeComponent();
        _serverUrl = serverUrl;
        _config = config;
        PwBox.Password = config.DirectoryPassword ?? "";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        var pw = PwBox.Password;
        try
        {
            var data = await _client.FetchAsync(_serverUrl, pw);
            _config.DirectoryPassword = pw;
            _config.Save("master");

            var groupName = data.Groups.ToDictionary(g => g.Id, g => g.Name);
            var rows = data.Clients
                .Select(c => new Row(
                    c.Id,
                    string.IsNullOrWhiteSpace(c.Name) ? "(unnamed)" : c.Name,
                    c.GroupId != null && groupName.TryGetValue(c.GroupId, out var gn) ? gn : "Ungrouped",
                    c.Online,
                    c.Online ? (c.Busy ? "● In session" : "● Online") : "○ Offline"))
                // online first, then by group, then by name
                .OrderByDescending(r => r.Online)
                .ThenBy(r => r.GroupName == "Ungrouped")
                .ThenBy(r => r.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Row.GroupName)));
            List.ItemsSource = view;
            StatusText.Text = $"{rows.Count(r => r.Online)} online / {rows.Count} total";
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "Wrong directory password.";
            List.ItemsSource = null;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Couldn't load: " + ex.Message;
            List.ItemsSource = null;
        }
    }

    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Choose();
    private void Connect_Click(object sender, RoutedEventArgs e) => Choose();

    private void Choose()
    {
        if (List.SelectedItem is not Row row) { StatusText.Text = "Select a computer first."; return; }
        if (!row.Online) { StatusText.Text = "That computer is offline."; return; }
        SelectedId = row.Id;
        DialogResult = true;
    }
}
