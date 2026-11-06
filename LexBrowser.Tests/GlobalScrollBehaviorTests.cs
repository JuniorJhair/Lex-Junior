using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LexBrowser.UI;
using Xunit;

namespace LexBrowser.Tests;

[Collection("SettingsCollection")]
public class GlobalScrollBehaviorTests
{
    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null) ExceptionDispatchInfo.Capture(ex).Throw();
    }

    [Fact]
    public void IsInsideOpenComboBoxOrPopup_NullOrOrdinaryElement_ReturnsFalse()
    {
        RunOnSta(() =>
        {
            Assert.False(GlobalScrollBehavior.IsInsideOpenComboBoxOrPopup(null));

            var border = new Border();
            var textBlock = new TextBlock();
            border.Child = textBlock;

            Assert.False(GlobalScrollBehavior.IsInsideOpenComboBoxOrPopup(border));
            Assert.False(GlobalScrollBehavior.IsInsideOpenComboBoxOrPopup(textBlock));
        });
    }

    [Fact]
    public void IsInsideOpenComboBoxOrPopup_ClosedComboBox_ReturnsFalse()
    {
        RunOnSta(() =>
        {
            var cb = new ComboBox();
            cb.Items.Add("Elemento 1");
            cb.IsDropDownOpen = false;

            Assert.False(GlobalScrollBehavior.IsInsideOpenComboBoxOrPopup(cb));
        });
    }

    [Fact]
    public void IsInsideOpenComboBoxOrPopup_OpenComboBox_ReturnsTrue()
    {
        RunOnSta(() =>
        {
            GlobalScrollBehavior.Initialize();

            var cb = new ComboBox();
            cb.Items.Add("Dominio 1");
            var win = new Window { Content = cb, Width = 300, Height = 300 };
            win.Show();

            try
            {
                cb.IsDropDownOpen = true;
                Assert.True(cb.IsDropDownOpen);
                Assert.True(GlobalScrollBehavior.IsInsideOpenComboBoxOrPopup(cb));
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void IsInsideOpenComboBoxOrPopup_ChildInsideOpenPopup_ReturnsTrue()
    {
        RunOnSta(() =>
        {
            var popup = new Popup { IsOpen = true };
            var grid = new Grid();
            var text = new TextBlock();
            grid.Children.Add(text);
            popup.Child = grid;

            Assert.True(GlobalScrollBehavior.IsInsideOpenComboBoxOrPopup(popup));
            Assert.True(GlobalScrollBehavior.IsInsideOpenComboBoxOrPopup(text));
        });
    }

    [Fact]
    public void IsInsideComboBoxOrPopup_DetectsHierarchyProperly()
    {
        RunOnSta(() =>
        {
            var popup = new Popup { IsOpen = false };
            var border = new Border();
            popup.Child = border;

            Assert.True(GlobalScrollBehavior.IsInsideComboBoxOrPopup(popup));
            Assert.True(GlobalScrollBehavior.IsInsideComboBoxOrPopup(border));
        });
    }

    [Fact]
    public void RegisterOpenComboBox_And_CloseAllOpenComboBoxes_WorksReliably()
    {
        RunOnSta(() =>
        {
            GlobalScrollBehavior.Initialize();

            var cb = new ComboBox();
            cb.Items.Add("item");
            var win = new Window { Content = cb, Width = 300, Height = 300 };
            win.Show();

            try
            {
                cb.IsDropDownOpen = true;
                Assert.True(cb.IsDropDownOpen);

                GlobalScrollBehavior.RegisterOpenComboBox(cb);
                Assert.True(GlobalScrollBehavior.HasAnyOpenComboBox());

                GlobalScrollBehavior.CloseAllOpenComboBoxes();
                Assert.False(cb.IsDropDownOpen);
                Assert.False(GlobalScrollBehavior.HasAnyOpenComboBox());
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void IsElementVisibleInViewport_DetectsVisibilityCorrectly()
    {
        RunOnSta(() =>
        {
            var sv = new ScrollViewer { Width = 300, Height = 300 };
            var panel = new StackPanel { Height = 1000 };
            var cb = new ComboBox { Width = 100, Height = 30 };
            panel.Children.Add(cb);
            sv.Content = panel;

            var win = new Window { Content = sv, Width = 400, Height = 400 };
            win.Show();
            win.UpdateLayout();

            try
            {
                Assert.True(GlobalScrollBehavior.IsElementVisibleInViewport(cb, sv));
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void TryScrollOpenComboBox_WithNullOrClosed_ReturnsFalse()
    {
        RunOnSta(() =>
        {
            Assert.False(GlobalScrollBehavior.TryScrollOpenComboBox(null, -120));

            var cb = new ComboBox();
            cb.IsDropDownOpen = false;
            Assert.False(GlobalScrollBehavior.TryScrollOpenComboBox(cb, -120));
        });
    }
}
