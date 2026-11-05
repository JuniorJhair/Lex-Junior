using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LexBrowser.UI;

/// <summary>
/// Comportamiento global de aislamiento de scroll para ComboBox y ScrollViewer.
/// Registrado una sola vez al inicio de la aplicación en App.xaml.cs.
/// </summary>
public static class GlobalScrollBehavior
{
    private static bool _isInitialized = false;
    private static readonly List<WeakReference<ComboBox>> _openComboBoxes = new();
    private static readonly object _lock = new();
    private static bool _suppressScrollClose = false;

    public static void Initialize()
    {
        if (_isInitialized) return;
        _isInitialized = true;

        // 1. Manejador defensivo en burbujeo para ComboBox
        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            UIElement.MouseWheelEvent,
            new MouseWheelEventHandler(OnComboBoxMouseWheel),
            handledEventsToo: true);

        // 2. Registro inmediato de eventos al interactuar con cualquier ComboBox
        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnComboBoxPreviewMouseDown),
            handledEventsToo: true);

        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnComboBoxLoaded));

        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnComboBoxUnloaded));

        // 3. Si la barra de scroll lateral de la ventana principal se arrastra con el ratón,
        //    cerrar de inmediato los ComboBoxes para que no queden flotando.
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollViewerScrollChanged),
            handledEventsToo: true);
    }

    public static bool HasAnyOpenComboBox()
    {
        lock (_lock)
        {
            for (int i = _openComboBoxes.Count - 1; i >= 0; i--)
            {
                if (_openComboBoxes[i].TryGetTarget(out var cb))
                {
                    if (cb.IsDropDownOpen) return true;
                }
                else
                {
                    _openComboBoxes.RemoveAt(i);
                }
            }
        }

        // Búsqueda en ventanas activas como respaldo infalible
        try
        {
            var app = Application.Current;
            if (app != null)
            {
                foreach (Window win in app.Windows)
                {
                    if (win.IsVisible && HasOpenComboBoxInTree(win))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool HasOpenComboBoxInTree(DependencyObject parent)
    {
        if (parent == null) return false;
        if (parent is ComboBox cb && cb.IsDropDownOpen) return true;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (HasOpenComboBoxInTree(child)) return true;
        }
        return false;
    }

    public static void RegisterOpenComboBox(ComboBox cb)
    {
        lock (_lock)
        {
            _openComboBoxes.RemoveAll(w => !w.TryGetTarget(out var target) || ReferenceEquals(target, cb));
            _openComboBoxes.Add(new WeakReference<ComboBox>(cb));
        }
    }

    /// <summary>
    /// Desplaza suavemente la lista de opciones del ComboBox que se encuentre actualmente abierto.
    /// </summary>
    public static void ScrollActiveComboBox(int delta)
    {
        ComboBox? activeCb = null;

        lock (_lock)
        {
            for (int i = _openComboBoxes.Count - 1; i >= 0; i--)
            {
                if (_openComboBoxes[i].TryGetTarget(out var cb) && cb.IsDropDownOpen)
                {
                    activeCb = cb;
                    break;
                }
            }
        }

        if (activeCb == null)
        {
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    foreach (Window win in app.Windows)
                    {
                        if (win.IsVisible)
                        {
                            activeCb = FindFirstOpenComboBox(win);
                            if (activeCb != null) break;
                        }
                    }
                }
            }
            catch { }
        }

        if (activeCb == null) return;

        Popup? popup = activeCb.Template?.FindName("Popup", activeCb) as Popup ?? FindVisualChild<Popup>(activeCb);
        if (popup != null && popup.Child != null)
        {
            var sv = FindVisualChild<ScrollViewer>(popup.Child);
            if (sv != null && sv.ScrollableHeight > 0)
            {
                double deltaUnits = delta / 120.0;
                double scrollPixels = deltaUnits * 28.0;
                double newOffset = Math.Clamp(sv.VerticalOffset - scrollPixels, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);
            }
        }
    }

    private static ComboBox? FindFirstOpenComboBox(DependencyObject parent)
    {
        if (parent == null) return null;
        if (parent is ComboBox cb && cb.IsDropDownOpen) return cb;

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var found = FindFirstOpenComboBox(child);
            if (found != null) return found;
        }
        return null;
    }

    public static bool IsInsideComboBoxOrPopup(DependencyObject? obj)
    {
        if (obj == null) return false;
        DependencyObject? current = obj;
        int depth = 0;

        while (current != null && depth < 50)
        {
            depth++;
            if (current is ComboBox) return true;
            if (current is Popup) return true;
            if (current is ComboBoxItem) return true;

            if (current is FrameworkElement fe)
            {
                if (fe.TemplatedParent is ComboBox || fe.TemplatedParent is Popup)
                    return true;
                if (!string.IsNullOrEmpty(fe.Name) && fe.Name.StartsWith("DropDown", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            string typeName = current.GetType().Name;
            if (typeName.Contains("PopupRoot") || typeName.Contains("Popup") || typeName.Contains("ComboBox"))
                return true;

            DependencyObject? parent = null;
            try
            {
                if (current is Visual or System.Windows.Media.Media3D.Visual3D)
                {
                    parent = VisualTreeHelper.GetParent(current);
                }
            }
            catch { }

            current = parent ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    public static bool IsEventFromOpenComboBoxOrPopup(DependencyObject? source)
    {
        if (source == null) return false;
        return IsInsideComboBoxOrPopup(source);
    }

    /// <summary>
    /// Cierra todos los ComboBoxes que se encuentren abiertos en la aplicación.
    /// </summary>
    public static void CloseAllOpenComboBoxes()
    {
        List<ComboBox> toClose = new();
        lock (_lock)
        {
            for (int i = _openComboBoxes.Count - 1; i >= 0; i--)
            {
                if (_openComboBoxes[i].TryGetTarget(out var cb))
                {
                    if (cb.IsDropDownOpen) toClose.Add(cb);
                }
                else
                {
                    _openComboBoxes.RemoveAt(i);
                }
            }
        }

        foreach (var cb in toClose)
        {
            cb.IsDropDownOpen = false;
        }

        try
        {
            var app = Application.Current;
            if (app != null)
            {
                foreach (Window win in app.Windows)
                {
                    if (win.IsVisible)
                    {
                        CloseOpenComboBoxesInVisualTree(win);
                    }
                }
            }
        }
        catch { }
    }

    private static void CloseOpenComboBoxesInVisualTree(DependencyObject parent)
    {
        if (parent == null) return;

        if (parent is ComboBox cb && cb.IsDropDownOpen)
        {
            cb.IsDropDownOpen = false;
        }

        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            CloseOpenComboBoxesInVisualTree(child);
        }
    }

    private static void OnComboBoxMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ComboBox cb && cb.IsDropDownOpen)
        {
            e.Handled = true;
        }
    }

    private static void OnComboBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBox cb)
        {
            cb.DropDownOpened -= OnDropDownOpened;
            cb.DropDownOpened += OnDropDownOpened;
            cb.DropDownClosed -= OnDropDownClosed;
            cb.DropDownClosed += OnDropDownClosed;
        }
    }

    private static void OnComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb)
        {
            cb.DropDownOpened -= OnDropDownOpened;
            cb.DropDownOpened += OnDropDownOpened;
            cb.DropDownClosed -= OnDropDownClosed;
            cb.DropDownClosed += OnDropDownClosed;
        }
    }

    private static void OnComboBoxUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb)
        {
            cb.DropDownOpened -= OnDropDownOpened;
            cb.DropDownClosed -= OnDropDownClosed;

            lock (_lock)
            {
                _openComboBoxes.RemoveAll(w => !w.TryGetTarget(out var target) || ReferenceEquals(target, cb));
            }
        }
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox cb)
        {
            _suppressScrollClose = true;
            var disp = cb.Dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            disp.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                _suppressScrollClose = false;
            }));

            RegisterOpenComboBox(cb);
        }
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox cb)
        {
            lock (_lock)
            {
                _openComboBoxes.RemoveAll(w => !w.TryGetTarget(out var target) || ReferenceEquals(target, cb));
            }
        }
    }

    private static void OnScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
        if (_suppressScrollClose) return;
        if (sender is not DependencyObject d) return;
        if (IsInsideComboBoxOrPopup(d)) return;

        CloseAllOpenComboBoxes();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }
}
