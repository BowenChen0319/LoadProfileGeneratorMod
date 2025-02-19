﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Automation;
using Common;
using JetBrains.Annotations;
using OxyPlot;
using OxyPlot.Legends;
using OxyPlot.Series;
using BarSeries = OxyPlot.Series.BarSeries;
using CategoryAxis = OxyPlot.Axes.CategoryAxis;
using LinearAxis = OxyPlot.Axes.LinearAxis;
using LineSeries = OxyPlot.Series.LineSeries;

namespace ChartCreator2.SettlementMergePlots {
    [SuppressMessage("ReSharper", "RedundantNameQualifier")]
    public class MergedAffordanceEnergy {
        private const int Fontsize = 20;

        private static void MakeGeneralBarChart([ItemNotNull] [JetBrains.Annotations.NotNull] List<AffordanceEntry> entries, [JetBrains.Annotations.NotNull] string dstDir) {
            var householdNames = entries.Select(x => x.HouseholdName.Trim()).Distinct().ToList();
            // make absolute values
            var plotModel1 = MakePlotmodel(householdNames,
                ChartLocalizer.Get().GetTranslation("Electricity") + " in kWh");
            var affNames = entries.Select(x => x.AffordanceName).Distinct().ToList();
            affNames.Sort((x, y) => string.Compare(x, y, StringComparison.Ordinal));
            OxyPalette p;
            if (affNames.Count < 2) {
                p = OxyPalettes.Hue64;
            }
            else {
                p = OxyPalettes.HueDistinct(affNames.Count);
            }
            for (var i = 0; i < affNames.Count; i++) {
                var tag = affNames[i];
                var columnSeries2 = new BarSeries
                {
                    IsStacked = true,
                    StackGroup = "1",
                    StrokeThickness = 1,
                    StrokeColor = OxyColors.White,
                    Title = ChartLocalizer.Get().GetTranslation(tag),
                    LabelPlacement = LabelPlacement.Middle,
                    FillColor = p.Colors[i]
                };
                foreach (var householdName in householdNames) {
                    var te =
                        entries.FirstOrDefault(x => x.AffordanceName == tag && x.HouseholdName == householdName);
                    if (te != null) {
                        columnSeries2.Items.Add(new BarItem(te.Value));
                    }
                    else {
                        columnSeries2.Items.Add(new BarItem(0));
                    }
                }
                plotModel1.Series.Add(columnSeries2);
            }

            var fileName = Path.Combine(dstDir, "MergedAffordanceEnergyUse.pdf");
            OxyPDFCreator.Run(plotModel1, fileName);
            var hhSums = new Dictionary<string, double>();
            var households = entries.Select(x => x.HouseholdName).Distinct().ToList();
            foreach (var household in households) {
                var sum = entries.Where(x => x.HouseholdName == household).Select(x => x.Value).Sum();
                hhSums.Add(household, sum);
            }
            foreach (var affordanceName in affNames) {
                var plotModel2 = MakePlotmodel(householdNames, "Anteil am Gesamtverbrauch in Prozent");
                var legend = new Legend();
                plotModel2.Legends.Add(legend);
                legend.LegendFontSize = Fontsize;
                var columnSeries2 = new BarSeries
                {
                    IsStacked = true,
                    StackGroup = "1",
                    StrokeThickness = 1,
                    Title = ChartLocalizer.Get().GetTranslation(affordanceName),
                    LabelPlacement = LabelPlacement.Middle,
                    FillColor = OxyColors.LightBlue
                };
                var averageValue =
                    entries.Where(x => x.AffordanceName == affordanceName)
                        .Select(x => x.Value / hhSums[x.HouseholdName] * 100)
                        .Average();
                var ls = new LineSeries
                {
                    Color = OxyColors.Red,
                    Title = "Durchschnitt"
                };
                for (var i = 0; i < householdNames.Count; i++) {
                    var householdName = householdNames[i];
                    ls.Points.Add(new DataPoint(i, averageValue));
                    var te =
                        entries.FirstOrDefault(
                            x => x.AffordanceName == affordanceName && x.HouseholdName == householdName);

                    if (te != null) {
                        columnSeries2.Items.Add(new BarItem(te.Value / hhSums[householdName] * 100));
                    }
                    else {
                        columnSeries2.Items.Add(new BarItem(0));
                    }
                }
                plotModel2.Series.Add(columnSeries2);
                plotModel2.Series.Add(ls);
                var cleanTag = AutomationUtili.CleanFileName(affordanceName);
                var relfileName = Path.Combine(dstDir, "MergedAffordanceTaggingEnergyUse." + cleanTag + ".pdf");
                OxyPDFCreator.Run(plotModel2, relfileName);
            }
        }

        [JetBrains.Annotations.NotNull]
        private static PlotModel MakePlotmodel([ItemNotNull] [JetBrains.Annotations.NotNull] List<string> householdNames, [JetBrains.Annotations.NotNull] string yaxislabel)
        {
            var plotModel1 = new PlotModel {
                DefaultFontSize = Fontsize,
            };
            var l = new Legend();
            plotModel1.Legends.Add(l);

            l.LegendFontSize = 10;
            l.LegendBorderThickness = 0;
            l.LegendOrientation = LegendOrientation.Horizontal;
            l.LegendPlacement = LegendPlacement.Outside;
            l.LegendPosition = LegendPosition.BottomCenter;
            l.LegendSymbolMargin = 20;

            var categoryAxis1 = new CategoryAxis
            {
                MajorStep = 1,
                Minimum = -0.5,
                Angle = 45,
                MaximumPadding = 0.03,
                AxisTickToLabelDistance = 60
            };
            for (var i = 0; i < householdNames.Count; i++) {
                if (i % 5 == 0) {
                    var householdName = householdNames[i];
                    categoryAxis1.ActualLabels.Add(householdName.Substring(0, 5));
                }
                else {
                    categoryAxis1.ActualLabels.Add(string.Empty);
                }
            }
            categoryAxis1.GapWidth = 0;
            plotModel1.Axes.Add(categoryAxis1);
            var linearAxis1 = new LinearAxis
            {
                AbsoluteMinimum = 0,
                MaximumPadding = 0.06,
                MinimumPadding = 0,
                Title = yaxislabel
            };
            plotModel1.Axes.Add(linearAxis1);
            return plotModel1;
        }

        public void Run([JetBrains.Annotations.NotNull] Dictionary<string, List<AffordanceEntry>> entries, [JetBrains.Annotations.NotNull] string dstDir) {
            var allEntries = new List<AffordanceEntry>();
            foreach (var pair in entries) {
                allEntries.AddRange(pair.Value);
            }
            MakeGeneralBarChart(allEntries, dstDir);
        }

        public class AffordanceEntry {
            public AffordanceEntry([JetBrains.Annotations.NotNull] string affName, double value, [JetBrains.Annotations.NotNull] string householdName) {
                AffordanceName = affName;
                Value = value;
                HouseholdName = householdName;
            }

            [JetBrains.Annotations.NotNull]
            public string AffordanceName { get; }

            [JetBrains.Annotations.NotNull]
            public string HouseholdName { get; }
            public double Value { get; }
        }
    }
}