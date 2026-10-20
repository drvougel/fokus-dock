using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace FokusDock;

/// <summary>
/// Windows-Benachrichtigungen für den Launcher (über die Pipe). Gelesen wird nur auf Nachfrage: jede Anfrage liefert den
/// Zwischenspeicher und stößt, wenn er älter als gut eine Sekunde ist, im Hintergrund eine neue Abfrage an.
/// Läuft der Launcher nicht, wird nichts gelesen.
/// </summary>
internal static class NotificationFeed
{
    private const int MaxItems = 40;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(1200);

    private static readonly UserNotificationListener Listener = UserNotificationListener.Current;
    private static List<NotificationItem> _items = new();
    private static DateTime _loaded = DateTime.MinValue;
    private static bool _busy;
    private static bool _accessAsked;
    private static bool _denied;

    /// <summary>Antwortzeile für „notifs“: {"denied":false,"items":[{id,app,title,body,time}]}</summary>
    public static string Json()
    {
        if (!_busy && DateTime.UtcNow - _loaded > MaxAge)
        {
            _ = RefreshAsync();
        }

        var items = _items.Select(i => new
        {
            id = i.Id,
            app = i.App,
            title = i.Title,
            body = i.Body,
            time = i.Time.ToString("s"),
        }).ToList();
        return JsonSerializer.Serialize(new { denied = _denied, items });
    }

    public static void Dismiss(uint id)
    {
        try
        {
            Listener.RemoveNotification(id);
        }
        catch (Exception)
        {
            // Schon weg oder nicht entfernbar: aus dem Zwischenspeicher nehmen wir sie trotzdem
        }
        _items = _items.Where(i => i.Id != id).ToList();
    }

    public static void ClearAll()
    {
        try
        {
            Listener.ClearNotifications();
        }
        catch (Exception)
        {
            // Nicht löschbar: die nächste Abfrage zeigt, was noch da ist
        }
        _items = new List<NotificationItem>();
    }

    private static async Task RefreshAsync()
    {
        _busy = true;
        try
        {
            if (!_accessAsked)
            {
                _accessAsked = true;
                UserNotificationListenerAccessStatus access = await Listener.RequestAccessAsync();
                _denied = access == UserNotificationListenerAccessStatus.Denied;
            }
            if (_denied)
            {
                _items = new List<NotificationItem>();
                return;
            }

            IReadOnlyList<UserNotification> toasts = await Listener.GetNotificationsAsync(NotificationKinds.Toast);
            _items = toasts.Select(NotificationPanel.Convert).Where(i => i != null)
                .OrderByDescending(i => i.Time).Take(MaxItems).ToList();
        }
        catch (Exception ex)
        {
            App.WriteLog("Benachrichtigungen (Launcher): " + ex.Message);
        }
        finally
        {
            _loaded = DateTime.UtcNow;
            _busy = false;
        }
    }
}
