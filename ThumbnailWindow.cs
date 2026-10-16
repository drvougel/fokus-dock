using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FokusDock;

/// <summary>
/// Fenster mit DWM-Thumbnails. Jedes Thumbnail liegt über einem WPF-Element (Bereich);
/// Position und Größe werden bei jedem Layoutdurchlauf nachgezogen. Mit <see cref="ClipElement"/>
/// wird die Vorschau auf den sichtbaren Bereich (z. B. eines ScrollViewers) begrenzt.
/// </summary>
public abstract class ThumbnailWindow : Window
{
    private readonly List<ThumbBinding> _bindings = new();

    protected ThumbnailWindow()
    {
        LayoutUpdated += (_, _) => UpdateThumbnails();
    }

    /// <summary>Begrenzt alle Vorschauen auf dieses Element. Null = keine Begrenzung.</summary>
    internal FrameworkElement ClipElement { get; set; }

    internal void PlaceWindow(NativeRect rect)
    {
        double scale = Monitors.Scale;
        Left = rect.Left / scale;
        Top = rect.Top / scale;
        Width = (rect.Right - rect.Left) / scale;
        Height = (rect.Bottom - rect.Top) / scale;
    }

    /// <summary>Zeigt die Vorschau von source über area. Das Fenster muss bereits sichtbar sein.</summary>
    internal void Bind(FrameworkElement area, IntPtr source)
    {
        var binding = new ThumbBinding { Area = area };
        IntPtr self = new WindowInteropHelper(this).Handle;
        if (self != IntPtr.Zero && Native.DwmRegisterThumbnail(self, source, out IntPtr thumb) == 0)
        {
            binding.Thumb = thumb;
            Native.DwmQueryThumbnailSourceSize(thumb, out binding.Source);
        }
        _bindings.Add(binding);

        // Ein Layoutdurchlauf kann vor dem Binden gelaufen sein; dann positioniert LayoutUpdated nichts mehr
        UpdateThumbnails();
    }

    internal void ReleaseThumbnails()
    {
        foreach (ThumbBinding binding in _bindings)
        {
            if (binding.Thumb != IntPtr.Zero)
            {
                Native.DwmUnregisterThumbnail(binding.Thumb);
            }
        }
        _bindings.Clear();
    }

    private void UpdateThumbnails()
    {
        if (!IsVisible || _bindings.Count == 0 || Content is not Visual root)
        {
            return;
        }

        var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
        if (dpi == null)
        {
            return;
        }
        double scale = dpi.Value.M11;

        Rect? clip = null;
        if (ClipElement != null)
        {
            try
            {
                clip = ToDevice(ClipElement, root, scale);
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }

        foreach (ThumbBinding binding in _bindings)
        {
            if (binding.Thumb == IntPtr.Zero || binding.Source.Width <= 0 || binding.Source.Height <= 0)
            {
                continue;
            }

            // Noch nicht im Visual-Baum (z. B. beim Binden vor dem Einhängen): später positioniert LayoutUpdated
            Rect area;
            try
            {
                area = ToDevice(binding.Area, root, scale);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            Rect dest = FitInto(binding.Source, area);
            Rect visible = clip.HasValue ? Rect.Intersect(dest, clip.Value) : dest;
            bool show = dest.Width > 0 && dest.Height > 0 && visible.Width > 0 && visible.Height > 0;

            var props = new ThumbnailProperties
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_RECTSOURCE | Native.DWM_TNP_OPACITY
                          | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                opacity = 255,
                fVisible = show,
                fSourceClientAreaOnly = false,
                rcSource = new NativeRect { Left = 0, Top = 0, Right = binding.Source.Width, Bottom = binding.Source.Height },
            };
            if (show)
            {
                props.rcDestination = ToNative(visible);
                props.rcSource = SourceRect(binding.Source, dest, visible);
            }

            Native.DwmUpdateThumbnailProperties(binding.Thumb, ref props);
        }
    }

    /// <summary>Position eines Elements in Gerätepixeln, relativ zum Fensterinhalt.</summary>
    private static Rect ToDevice(FrameworkElement element, Visual root, double scale)
    {
        Rect r = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);
    }

    /// <summary>Quelle proportional in den Bereich einpassen, zentriert (Letterbox).</summary>
    private static Rect FitInto(NativeSize source, Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return Rect.Empty;
        }

        double factor = Math.Min(area.Width / source.Width, area.Height / source.Height);
        double w = source.Width * factor;
        double h = source.Height * factor;
        return new Rect(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    /// <summary>Ausschnitt der Quelle, der dem sichtbaren Teil der eingepassten Vorschau entspricht.</summary>
    private static NativeRect SourceRect(NativeSize source, Rect dest, Rect visible)
    {
        double fx = source.Width / dest.Width;
        double fy = source.Height / dest.Height;

        return new NativeRect
        {
            Left = Math.Clamp((int)Math.Round((visible.Left - dest.Left) * fx), 0, source.Width),
            Top = Math.Clamp((int)Math.Round((visible.Top - dest.Top) * fy), 0, source.Height),
            Right = Math.Clamp((int)Math.Round((visible.Right - dest.Left) * fx), 0, source.Width),
            Bottom = Math.Clamp((int)Math.Round((visible.Bottom - dest.Top) * fy), 0, source.Height),
        };
    }

    private static NativeRect ToNative(Rect r) => new()
    {
        Left = (int)Math.Round(r.Left),
        Top = (int)Math.Round(r.Top),
        Right = (int)Math.Round(r.Right),
        Bottom = (int)Math.Round(r.Bottom),
    };

    private sealed class ThumbBinding
    {
        public FrameworkElement Area;
        public IntPtr Thumb;
        public NativeSize Source;
    }
}
