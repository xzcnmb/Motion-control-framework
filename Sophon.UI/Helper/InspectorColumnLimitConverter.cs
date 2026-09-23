using System;
using System.Globalization;
using System.Windows.Data;

namespace Sophon.UI.Helper
{
    /// <summary>
    /// 右侧节点属性面板列的宽限值转换。
    /// 收起（true）时把列的可拖宽度压成 32px 细条，画布即可收回原本被面板占掉的宽度；
    /// 展开（false）时还原为可拖拽区间 [220, 400]（MinWidth 走默认分支，MaxWidth 传 ConverterParameter="Max"）。
    /// 之所以只控 MinWidth/MaxWidth 而不绑定 ColumnDefinition.Width，是因为 GridSplitter 是直接给 Width 写本地值、
    /// 会把绑定冲掉；只约束宽限即能既让面板收起、又保留分割条的正常拖拽。
    /// </summary>
    public class InspectorColumnLimitConverter : IValueConverter
    {
        public const double CollapsedWidth = 32;
        public const double ExpandedMinWidth = 220;
        public const double ExpandedMaxWidth = 400;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isCollapsed && isCollapsed)
            {
                return CollapsedWidth;
            }

            return parameter is string p && string.Equals(p, "Max", StringComparison.OrdinalIgnoreCase)
                ? ExpandedMaxWidth
                : ExpandedMinWidth;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
