using System;
using System.Windows;
using System.Windows.Input;


namespace System.Windows.Input.StylusPlugIns
{
    public class StylusUtils
    {
        public static int thinPenSizeThreashold = 12;//判断是细笔头的触点大小临界值，大于该值认为是粗笔头
        public static Size GetStylusSize(StylusPoint stylusPoint)
        {
            double height = GetStylusPointProperty(stylusPoint, StylusPointProperties.Height);
            double width = GetStylusPointProperty(stylusPoint, StylusPointProperties.Width);
            return new Size(width, height);
        }

        /// <summary>
        /// 获取笔触面积
        /// </summary>
        /// <param name="stylusPoint"></param>
        /// <param name="stylusPointProperty"></param>
        /// <returns></returns>
        public static double GetStylusPointProperty(StylusPoint stylusPoint, StylusPointProperty stylusPointProperty)
        {
            double value = 0.0;
            if (stylusPoint.HasProperty(stylusPointProperty))
            {
                value = stylusPoint.GetPropertyValue(stylusPointProperty);

                StylusPointPropertyInfo propertyInfo = stylusPoint.Description.GetPropertyInfo(stylusPointProperty);

                if (Math.Abs(propertyInfo.Resolution) < 0.000001f) 
                {
                    value = 0.0;
                }
                else
                {
                    value /= propertyInfo.Resolution;
                }

                //属性值的单位
                if (propertyInfo.Unit == StylusPointPropertyUnit.Centimeters)
                {
                    value /= 2.54;
                }

                value *= 96.0;
            }

            return value;
        }
    }
}
