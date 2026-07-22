using System;
using System.Windows;
using System.Windows.Controls;

namespace CodexPortableManager
{
    public sealed class ResponsiveButtonPanel : Panel
    {
        public double MinimumItemWidth { get; set; } = 120;
        public double Spacing { get; set; } = 10;
        public double RowSpacing { get; set; } = 10;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (InternalChildren.Count == 0) return new Size(0, 0);
            double width = double.IsInfinity(availableSize.Width)
                ? MinimumItemWidth * InternalChildren.Count
                : availableSize.Width;
            int columns = CalculateColumns(width);
            double itemWidth = Math.Max(MinimumItemWidth, (width - ((columns - 1) * Spacing)) / columns);
            double rowHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(itemWidth, availableSize.Height));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            int rows = (int)Math.Ceiling((double)InternalChildren.Count / columns);
            return new Size(width, (rows * rowHeight) + (Math.Max(0, rows - 1) * RowSpacing));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (InternalChildren.Count == 0) return finalSize;
            int columns = CalculateColumns(finalSize.Width);
            double itemWidth = (finalSize.Width - ((columns - 1) * Spacing)) / columns;
            double rowHeight = 0;
            foreach (UIElement child in InternalChildren)
            {
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            for (int index = 0; index < InternalChildren.Count; index++)
            {
                int row = index / columns;
                int column = index % columns;
                InternalChildren[index].Arrange(new Rect(
                    column * (itemWidth + Spacing),
                    row * (rowHeight + RowSpacing),
                    itemWidth,
                    rowHeight));
            }
            int rows = (int)Math.Ceiling((double)InternalChildren.Count / columns);
            return new Size(finalSize.Width, (rows * rowHeight) + (Math.Max(0, rows - 1) * RowSpacing));
        }

        private int CalculateColumns(double width)
        {
            return Math.Max(
                1,
                Math.Min(
                    InternalChildren.Count,
                    (int)Math.Floor((width + Spacing) / (MinimumItemWidth + Spacing))));
        }
    }
}
