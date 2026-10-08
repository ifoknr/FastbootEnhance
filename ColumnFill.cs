using System;
using System.Windows;
using System.Windows.Controls;

namespace FastbootEnhance
{
    /// <summary>
    /// Lets one GridView column take whatever width its list has left, so a list fills its
    /// card at any window size instead of leaving a gap or growing a horizontal scroll bar.
    /// The other columns keep the widths the XAML gives them (or the user drags them to).
    /// </summary>
    static class ColumnFill
    {
        // The vertical scroll bar (10), the row padding (4 + 4) and a little slack, so the
        // columns never add up to a hair more than the list and bring the scroll bar back.
        const double Reserve = 24;

        public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
            "Column", typeof(int), typeof(ColumnFill), new PropertyMetadata(-1, OnColumnChanged));

        public static readonly DependencyProperty MinWidthProperty = DependencyProperty.RegisterAttached(
            "MinWidth", typeof(double), typeof(ColumnFill), new PropertyMetadata(60.0));

        /// <summary>
        /// When the fill column would drop below MinWidth, hide it rather than scroll the list.
        /// For a column that is only a convenience, such as the hash on a narrow window.
        /// </summary>
        public static readonly DependencyProperty HideWhenNarrowProperty = DependencyProperty.RegisterAttached(
            "HideWhenNarrow", typeof(bool), typeof(ColumnFill), new PropertyMetadata(false));

        public static bool GetHideWhenNarrow(DependencyObject d) { return (bool)d.GetValue(HideWhenNarrowProperty); }
        public static void SetHideWhenNarrow(DependencyObject d, bool value) { d.SetValue(HideWhenNarrowProperty, value); }

        public static int GetColumn(DependencyObject d) { return (int)d.GetValue(ColumnProperty); }
        public static void SetColumn(DependencyObject d, int value) { d.SetValue(ColumnProperty, value); }
        public static double GetMinWidth(DependencyObject d) { return (double)d.GetValue(MinWidthProperty); }
        public static void SetMinWidth(DependencyObject d, double value) { d.SetValue(MinWidthProperty, value); }

        static void OnColumnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ListView list = d as ListView;
            if (list == null)
                return;

            list.SizeChanged -= Refit;
            list.Loaded -= Refit;
            if ((int)e.NewValue < 0)
                return;
            list.SizeChanged += Refit;
            list.Loaded += Refit;
        }

        static void Refit(object sender, EventArgs e)
        {
            ListView list = (ListView)sender;
            GridView view = list.View as GridView;
            int index = GetColumn(list);
            if (view == null || index < 0 || index >= view.Columns.Count || list.ActualWidth <= 0)
                return;

            double others = 0;
            for (int i = 0; i < view.Columns.Count; i++)
            {
                if (i == index)
                    continue;
                GridViewColumn column = view.Columns[i];
                others += double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
            }

            double available = list.ActualWidth
                - list.BorderThickness.Left - list.BorderThickness.Right
                - list.Padding.Left - list.Padding.Right
                - Reserve - others;
            double min = GetMinWidth(list);
            if (available < min)
                available = GetHideWhenNarrow(list) ? 0 : min;
            view.Columns[index].Width = Math.Floor(available);
        }
    }
}
