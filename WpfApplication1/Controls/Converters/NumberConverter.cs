//-----------------------------------------------------------------------

// <copyright>
//
// Copyright (c) TU Chemnitz, Prof. Technische Thermodynamik
// Written by Noah Pflugradt.
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//  Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the distribution.
//  All advertising materials mentioning features or use of this software must display the following acknowledgement:
//  “This product includes software developed by the TU Chemnitz, Prof. Technische Thermodynamik and its contributors.”
//  Neither the name of the University nor the names of its contributors may be used to endorse or promote products
//  derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE UNIVERSITY 'AS IS' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING,
// BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE UNIVERSITY OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, S
// PECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; L
// OSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
// STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
// ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

// </copyright>

//-----------------------------------------------------------------------

using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Automation;
using Common;
using JetBrains.Annotations;

namespace LoadProfileGenerator.Controls.Converters {

    public class BrushConverter : IValueConverter
    {
        #region IValueConverter Members

        public object Convert(object value, [CanBeNull] Type targetType, object parameter,
                              [CanBeNull] CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        public object ConvertBack(object value, [CanBeNull] Type targetType, object parameter,
                                  [CanBeNull] CultureInfo culture)
        {
            if (targetType !=typeof(Brush)) {
                throw new NotImplementedException();
            }

            if (value == null) {
                return null;
            }
            ColorRGB rgb = (ColorRGB) value;
            Color c = Color.FromRgb(rgb.R,rgb.G,rgb.B);
            return new SolidColorBrush(c);
        }

        #endregion
    }
    public class NumberConverter : IValueConverter {
        #region IValueConverter Members

        [NotNull]
        public object Convert(object value, [CanBeNull] Type targetType, object parameter,
                              [CanBeNull] CultureInfo culture) {
            string result;
            if (value is decimal d1) {
                result = d1.ToString("0.00", CultureInfo.CurrentCulture);
            }
            else if (value is double d2) {
                result = d2.ToString("0.00", CultureInfo.CurrentCulture);
            }
            else {
                result = value as string;
            }
            if (result == null) {
                result = string.Empty;
            }
            return result;
        }

        [NotNull]
        public object ConvertBack(object value, [CanBeNull] Type targetType, object parameter,
            [CanBeNull] CultureInfo culture) {
            if (value == null) {
                return 0;
            }
            var myValue = value.ToString();
            if (targetType == typeof(decimal)) {
                var success = decimal.TryParse(myValue, out decimal d);
                if (!success) {
                    Logger.Error("Could not convert " + myValue + " to decimal.");
                }
                return d;
            }
            if (targetType == typeof(double)) {
                var success = double.TryParse(myValue, out double d);
                if (!success) {
                    Logger.Error("Could not convert " + myValue + " to double.");
                }
                return d;
            }
            throw new LPGNotImplementedException();
        }

        #endregion
    }
}