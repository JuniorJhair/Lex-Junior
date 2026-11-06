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
            UIElement.MouseEnterEvent,
            new MouseEventHandler(OnComboBoxMouseEnter),
            handledEventsToo: true);

        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            UIElement.GotFocusEvent,
            new RoutedEventHandler(OnComboBoxGotFocus),
            handledEventsToo: true);

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
        ComboBox? activeCb = GetFirstOpenComboBox();
        if (activeCb == null) return;

        var sv = GetDropDownScrollViewer(activeCb);
        if (sv != null && sv.ScrollableHeight > 0)
        {
            double deltaUnits = delta / 120.0;
            double scrollPixels = deltaUnits * 28.0;
            double newOffset = Math.Clamp(sv.VerticalOffset - scrollPixels, 0, sv.ScrollableHeight);
            sv.ScrollToVerticalOffset(newOffset);
        }
    }

    /// <summary>
    /// Si el elemento apuntado pertenece a un ComboBox abierto con scroll interno, desplaza sus opciones y devuelve true.
    /// Si el ComboBox no tiene scroll (pocas opciones) o el puntero está fuera, devuelve false para permitir desplazar el padre.
    /// </summary>
    public static bool TryScrollOpenComboBox(DependencyObject? hitSource, int delta)
    {
        if (hitSource == null) return false;
        if (!IsInsideOpenComboBoxOrPopup(hitSource)) return false;

        ScrollViewer? targetSv = null;
        DependencyObject? curr = hitSource;
        int depth = 0;

        while (curr != null && depth < 30)
        {
            depth++;
            if (curr is ScrollViewer sv && !string.IsNullOrEmpty(sv.Name) && sv.Name.Contains("DropDown", StringComparison.OrdinalIgnoreCase))
            {
                targetSv = sv;
                break;
            }
            if (curr is ComboBox cb && cb.IsDropDownOpen)
            {
                targetSv = GetDropDownScrollViewer(cb);
                break;
            }
            if (curr is Popup pop && pop.IsOpen)
            {
                if (pop.PlacementTarget is ComboBox pcb && pcb.IsDropDownOpen)
                    targetSv = GetDropDownScrollViewer(pcb);
                else if (pop.Child != null)
                    targetSv = FindVisualChild<ScrollViewer>(pop.Child);
                break;
            }

            DependencyObject? parent = null;
            try
            {
                if (curr is Visual or System.Windows.Media.Media3D.Visual3D)
                    parent = VisualTreeHelper.GetParent(curr);
            }
            catch { }

            if (parent == null)
            {
                if (curr is FrameworkElement fe && fe.Parent != null)
                    parent = fe.Parent;
                else
                    parent = LogicalTreeHelper.GetParent(curr);

                if (parent == null && curr is FrameworkElement fe2 && fe2.TemplatedParent != null)
                    parent = fe2.TemplatedParent;

                if (parent == null && curr is Popup p && p.PlacementTarget != null)
                    parent = p.PlacementTarget;
            }
            curr = parent;
        }

        if (targetSv == null)
        {
            ComboBox? activeCb = GetFirstOpenComboBox();
            if (activeCb != null)
            {
                targetSv = GetDropDownScrollViewer(activeCb);
            }
        }

        if (targetSv != null)
        {
            if (targetSv.ScrollableHeight > 0)
            {
                double deltaUnits = delta / 120.0;
                double scrollPixels = deltaUnits * 28.0;
                double newOffset = Math.Clamp(targetSv.VerticalOffset - scrollPixels, 0, targetSv.ScrollableHeight);
                targetSv.ScrollToVerticalOffset(newOffset);
                return true;
            }
            return false;
        }

        return false;
    }

    public static ScrollViewer? GetDropDownScrollViewer(ComboBox cb)
    {
        if (cb == null) return null;
        Popup? popup = cb.Template?.FindName("Popup", cb) as Popup ?? FindVisualChild<Popup>(cb);
        if (popup != null && popup.Child != null)
        {
            return FindVisualChild<ScrollViewer>(popup.Child);
        }
        return null;
    }

    public static ComboBox? GetFirstOpenComboBox()
    {
        lock (_lock)
        {
            for (int i = _openComboBoxes.Count - 1; i >= 0; i--)
            {
                if (_openComboBoxes[i].TryGetTarget(out var cb) && cb.IsDropDownOpen)
                {
                    return cb;
                }
            }
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
                        var found = FindFirstOpenComboBox(win);
                        if (found != null) return found;
                    }
                }
            }
        }
        catch { }

        return null;
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

    /// <summary>
    /// Determina si un elemento visual de origen proviene de un ComboBox ABIERTO o de su lista desplegable (Popup).
    /// Si el ComboBox está cerrado, devuelve false para que la rueda pueda desplazar el ScrollViewer de la página normalmente.
    /// </summary>
    public static bool IsInsideOpenComboBoxOrPopup(DependencyObject? obj)
    {
        if (obj == null) return false;
        DependencyObject? current = obj;
        int depth = 0;

        while (current != null && depth < 60)
        {
            depth++;

            if (current is ComboBox cb)
            {
                return cb.IsDropDownOpen;
            }

            if (current is Popup pop)
            {
                return pop.IsOpen;
            }

            if (current is ComboBoxItem cbi)
            {
                var parentCombo = ItemsControl.ItemsControlFromItemContainer(cbi) as ComboBox;
                return parentCombo == null || parentCombo.IsDropDownOpen;
            }

            if (current is FrameworkElement fe)
            {
                if (fe.TemplatedParent is ComboBox parentCb)
                    return parentCb.IsDropDownOpen;
                if (fe.TemplatedParent is Popup parentPop)
                    return parentPop.IsOpen;
                if (!string.IsNullOrEmpty(fe.Name) && fe.Name.StartsWith("DropDown", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (fe.Parent is ComboBox pCb)
                    return pCb.IsDropDownOpen;
                if (fe.Parent is Popup pPop)
                    return pPop.IsOpen;
            }

            string typeName = current.GetType().Name;
            if (typeName.Contains("PopupRoot"))
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

            if (parent == null)
            {
                if (current is FrameworkElement fe2 && fe2.Parent != null)
                    parent = fe2.Parent;
                else
                    parent = LogicalTreeHelper.GetParent(current);

                if (parent == null && current is Popup popup && popup.PlacementTarget != null)
                    parent = popup.PlacementTarget;
            }

            current = parent;
        }

        return false;
    }

    public static bool IsInsideComboBoxOrPopup(DependencyObject? obj)
    {
        if (obj == null) return false;
        DependencyObject? current = obj;
        int depth = 0;

        while (current != null && depth < 60)
        {
            depth++;
            if (current is ComboBox or Popup or ComboBoxItem) return true;

            if (current is FrameworkElement fe)
            {
                if (fe.TemplatedParent is ComboBox or Popup)
                    return true;
                if (!string.IsNullOrEmpty(fe.Name) && fe.Name.StartsWith("DropDown", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (fe.Parent is ComboBox or Popup)
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

            if (parent == null)
            {
                if (current is FrameworkElement fe2 && fe2.Parent != null)
                    parent = fe2.Parent;
                else
                    parent = LogicalTreeHelper.GetParent(current);

                if (parent == null && current is Popup pop && pop.PlacementTarget != null)
                    parent = pop.PlacementTarget;
            }

            current = parent;
        }
        return false;
    }

    public static bool IsEventFromOpenComboBoxOrPopup(DependencyObject? source)
    {
        return IsInsideOpenComboBoxOrPopup(source);
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
            var sv = GetDropDownScrollViewer(cb);
            if (sv != null && sv.ScrollableHeight > 0)
            {
                double deltaUnits = e.Delta / 120.0;
                double scrollPixels = deltaUnits * 28.0;
                double newOffset = Math.Clamp(sv.VerticalOffset - scrollPixels, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }
    }

    private static void HookComboBoxEvents(ComboBox cb)
    {
        cb.DropDownOpened -= OnDropDownOpened;
        cb.DropDownOpened += OnDropDownOpened;
        cb.DropDownClosed -= OnDropDownClosed;
        cb.DropDownClosed += OnDropDownClosed;
    }

    private static void OnComboBoxMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ComboBox cb) HookComboBoxEvents(cb);
    }

    private static void OnComboBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb) HookComboBoxEvents(cb);
    }

    private static void OnComboBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBox cb) HookComboBoxEvents(cb);
    }

    private static void OnComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb) HookComboBoxEvents(cb);
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
            HandleDropDownOpened(cb);
        }
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox cb)
        {
            HandleDropDownClosed(cb);
        }
    }

    private static void HandleDropDownOpened(ComboBox cb)
    {
        _suppressScrollClose = true;
        var disp = cb.Dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        disp.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _suppressScrollClose = false;
        }));

        RegisterOpenComboBox(cb);
    }

    private static void HandleDropDownClosed(ComboBox cb)
    {
        lock (_lock)
        {
            _openComboBoxes.RemoveAll(w => !w.TryGetTarget(out var target) || ReferenceEquals(target, cb));
        }
    }

    private static void OnScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Filtrar estrictamente por VerticalChange != 0 para no cerrar desplegables por simples cambios de layout o scroll horizontal
        if (e.VerticalChange == 0) return;
        if (_suppressScrollClose) return;
        if (sender is not ScrollViewer parentSv) return;
        if (IsInsideComboBoxOrPopup(parentSv)) return;

        CloseOutOfViewComboBoxes(parentSv);
    }

    /// <summary>
    /// Cierra únicamente los ComboBoxes que hayan salido del área visual (viewport) del ScrollViewer.
    /// Si el ComboBox todavía se puede ver en la ventana, sincroniza la posición del desplegable (Reposition)
    /// para que permanezca perfectamente pegado al control mientras se desplaza.
    /// </summary>
    public static void CloseOutOfViewComboBoxes(ScrollViewer parentSv)
    {
        List<ComboBox> toClose = new();

        lock (_lock)
        {
            for (int i = _openComboBoxes.Count - 1; i >= 0; i--)
            {
                if (_openComboBoxes[i].TryGetTarget(out var cb) && cb.IsDropDownOpen)
                {
                    if (IsDescendantOf(cb, parentSv))
                    {
                        if (!IsElementVisibleInViewport(cb, parentSv))
                        {
                            toClose.Add(cb);
                        }
                        else
                        {
                            RepositionComboBoxPopup(cb);
                        }
                    }
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
    }

    private static readonly System.Reflection.MethodInfo? _popupRepositionMethod =
        typeof(Popup).GetMethod("Reposition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    public static void RepositionComboBoxPopup(ComboBox cb)
    {
        try
        {
            Popup? popup = cb.Template?.FindName("Popup", cb) as Popup
                        ?? cb.Template?.FindName("PART_Popup", cb) as Popup
                        ?? FindVisualChild<Popup>(cb);

            if (popup != null && popup.IsOpen)
            {
                if (_popupRepositionMethod != null)
                {
                    _popupRepositionMethod.Invoke(popup, null);
                }
                else
                {
                    popup.HorizontalOffset += 0.0001;
                    popup.HorizontalOffset -= 0.0001;
                }
            }
        }
        catch { }
    }

    public static bool IsElementVisibleInViewport(FrameworkElement element, ScrollViewer parentSv)
    {
        try
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;

            GeneralTransform transform = element.TransformToAncestor(parentSv);
            Rect elementBounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            Rect viewportBounds = new Rect(0, 0, parentSv.ActualWidth, parentSv.ActualHeight);

            return elementBounds.IntersectsWith(viewportBounds);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject potentialAncestor)
    {
        DependencyObject? curr = node;
        int depth = 0;
        while (curr != null && depth < 50)
        {
            depth++;
            if (ReferenceEquals(curr, potentialAncestor)) return true;

            DependencyObject? parent = null;
            try
            {
                if (curr is Visual or System.Windows.Media.Media3D.Visual3D)
                    parent = VisualTreeHelper.GetParent(curr);
            }
            catch { }

            if (parent == null)
            {
                if (curr is FrameworkElement fe && fe.Parent != null)
                    parent = fe.Parent;
                else
                    parent = LogicalTreeHelper.GetParent(curr);
            }
            curr = parent;
        }
        return false;
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
