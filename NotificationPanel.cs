using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace FokusDock;

/// <summary>
/// Ein Eintrag im Benachrichtigungsfeld: eine Windows-Benachrichtigung in einfacher Form.
/// </summary>
internal sealed record NotificationItem(uint Id, string App, string Title, string Body, DateTime Time);

/// <summary>
/// Benachrichtigungsfeld: zeigt die Windows-Benachrichtigungen (Toasts) schlicht als Karten in einem Platz,
/// neueste oben. Ein Klick auf eine Karte räumt sie auch aus dem Info-Center weg, „Alle löschen“ leert alles.
/// Die Daten liest der UserNotificationListener; erlaubt der Nutzer den Zugriff nicht, steht ein Hinweis im Feld.
/// Das Fenster ist rahmenlos, nimmt keinen Fokus und liegt über den anderen Fenstern.
/// </summary>
internal sealed class NotificationPanel : OverlayWindow
{
    private const int MaxItems = 40;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _count = new();
    private readonly Button _clear = new();
    private readonly StackPanel _cards = new();
    private readonly TextBlock _empty = new();
    private bool _accessAsked;
    private bool _denied;
    private bool _busy;
    private string _signature = "";

    public NotificationPanel()
    {
        ClickThrough = false;
        // Vor dem ersten Platzieren winzig und außerhalb des Bildschirms, damit nichts aufblitzt
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;

        var title = new TextBlock
        {
            Text = "Benachrichtigungen",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Text ?? Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _count.Foreground = Theme.Muted ?? Brushes.Gray;
        _count.Margin = new Thickness(8, 0, 0, 0);
        _count.VerticalAlignment = VerticalAlignment.Center;
        _clear.Content = "Alle löschen";
        _clear.Padding = new Thickness(10, 3, 10, 3);
        _clear.Visibility = Visibility.Collapsed;
        _clear.Click += (_, _) => ClearAll();

        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 8), LastChildFill = false };
        DockPanel.SetDock(_clear, Dock.Right);
        header.Children.Add(_clear);
        header.Children.Add(title);
        header.Children.Add(_count);

        _empty.Text = "Keine Benachrichtigungen";
        _empty.Foreground = Theme.Muted ?? Brushes.Gray;
        _empty.FontSize = 14;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(0, 40, 0, 0);
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.TextAlignment = TextAlignment.Center;

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { _cards, _empty } },
            Padding = new Thickness(10, 0, 10, 10),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(scroll, 1);
        grid.Children.Add(header);
        grid.Children.Add(scroll);

        Content = new Border
        {
            Background = Theme.Surface ?? Brushes.Black,
            BorderBrush = Theme.Line ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid,
        };

        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
                await RefreshAsync();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    /// <summary>Fragt die Berechtigung einmal an und liest die Benachrichtigungen. Fehler landen im Feld, nicht beim Nutzer.</summary>
    private async Task RefreshAsync()
    {
        if (_busy || !IsVisible)
        {
            return;
        }

        _busy = true;
        try
        {
            if (!_accessAsked)
            {
                _accessAsked = true;
                UserNotificationListenerAccessStatus access = await _listener.RequestAccessAsync();
                _denied = access == UserNotificationListenerAccessStatus.Denied;
            }
            if (_denied)
            {
                Show(new List<NotificationItem>(), "Fokus-Dock darf Benachrichtigungen nicht lesen.\nErlaube es unter Einstellungen › Datenschutz und Sicherheit › Benachrichtigungen.");
                return;
            }

            IReadOnlyList<UserNotification> toasts = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            List<NotificationItem> items = toasts.Select(Convert).Where(i => i != null)
                .OrderByDescending(i => i.Time).Take(MaxItems).ToList();
            Show(items, null);
        }
        catch (Exception ex)
        {
            Show(new List<NotificationItem>(), "Benachrichtigungen konnten nicht gelesen werden.\n" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    internal static NotificationItem Convert(UserNotification n)
    {
        try
        {
            NotificationBinding binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            List<string> texts = binding?.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList()
                                 ?? new List<string>();
            string app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            string title = texts.Count > 0 ? texts[0] : app;
            string body = string.Join("\n", texts.Skip(1));
            return new NotificationItem(n.Id, app, title, body, n.CreationTime.LocalDateTime);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Baut die Karten nur neu, wenn sich der Inhalt geändert hat (die Zeitangaben ändern sich minütlich mit).</summary>
    private void Show(List<NotificationItem> items, string message)
    {
        string signature = message + "|" + string.Join(",", items.Select(i => i.Id)) + "|" + DateTime.Now.ToString("HH:mm");
        if (signature == _signature)
        {
            return;
        }
        _signature = signature;

        _cards.Children.Clear();
        foreach (NotificationItem item in items)
        {
            _cards.Children.Add(MakeCard(item));
        }

        _count.Text = items.Count > 0 ? items.Count.ToString() : "";
        _clear.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = message ?? "Keine Benachrichtigungen";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement MakeCard(NotificationItem item)
    {
        string name = string.IsNullOrEmpty(item.App) ? "?" : item.App;

        var badge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(ColorOf(name)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = name.Substring(0, 1).ToUpperInvariant(),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var meta = new DockPanel { LastChildFill = true };
        var time = new TextBlock { Text = Relative(item.Time), Foreground = Theme.Muted ?? Brushes.Gray, FontSize = 12 };
        DockPanel.SetDock(time, Dock.Right);
        meta.Children.Add(time);
        meta.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Theme.Muted ?? Brushes.Gray,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        texts.Children.Add(meta);
        texts.Children.Add(new TextBlock
        {
            Text = item.Title,
            Foreground = Theme.Text ?? Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40,
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (item.Body.Length > 0)
        {
            texts.Children.Add(new TextBlock
            {
                Text = item.Body,
                Foreground = Theme.Muted ?? Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 54,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(texts);

        var card = new Border
        {
            Background = Theme.Card ?? Brushes.DimGray,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row,
            Cursor = Cursors.Hand,
            ToolTip = "Klicken zum Wegräumen",
        };
        card.MouseEnter += (_, _) => card.Background = Theme.Hover ?? Brushes.Gray;
        card.MouseLeave += (_, _) => card.Background = Theme.Card ?? Brushes.DimGray;
        card.MouseLeftButtonUp += (_, _) => Dismiss(item.Id, card);
        return card;
    }

    private void Dismiss(uint id, UIElement card)
    {
        try
        {
            _listener.RemoveNotification(id);
        }
        catch (Exception)
        {
            // Schon weg oder nicht entfernbar: die Karte verschwindet trotzdem aus dem Feld
        }

        _cards.Children.Remove(card);
        int left = _cards.Children.Count;
        _count.Text = left > 0 ? left.ToString() : "";
        _clear.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = left == 0 ? Visibility.Visible : Visibility.Collapsed;
        _signature = "";   // der nächste Abgleich baut sauber neu auf
    }

    private void ClearAll()
    {
        try
        {
            _listener.ClearNotifications();
        }
        catch (Exception)
        {
            // Nicht löschbar: der nächste Abgleich zeigt, was noch da ist
        }

        _signature = "";
        _ = RefreshAsync();
    }

    /// <summary>Feste Farbe pro App-Name, damit dieselbe App immer gleich aussieht.</summary>
    private static Color ColorOf(string name)
    {
        Color[] palette =
        {
            Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0xF5, 0x9E, 0x0B),
            Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x06, 0xB6, 0xD4),
            Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x64, 0x74, 0x8B),
        };
        int hash = 17;
        foreach (char c in name)
        {
            hash = unchecked(hash * 31 + c);
        }
        return palette[(hash & 0x7FFFFFFF) % palette.Length];
    }

    private static string Relative(DateTime time)
    {
        TimeSpan age = DateTime.Now - time;
        if (age.TotalMinutes < 1)
        {
            return "jetzt";
        }
        if (age.TotalMinutes < 60)
        {
            return $"vor {(int)age.TotalMinutes} Min.";
        }
        if (time.Date == DateTime.Today)
        {
            return time.ToString("HH:mm");
        }
        return time.Date == DateTime.Today.AddDays(-1) ? "gestern " + time.ToString("HH:mm") : time.ToString("dd.MM. HH:mm");
    }
}
