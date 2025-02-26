﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Automation;
using Automation.ResultFiles;
using Common;

namespace ChartCreator2.OxyCharts {
    internal class AffordanceTimeUse : ChartBaseFileStep
    {
        public AffordanceTimeUse([JetBrains.Annotations.NotNull] ChartCreationParameters parameters,
                                 [JetBrains.Annotations.NotNull] FileFactoryAndTracker fft,
                                 [JetBrains.Annotations.NotNull] ICalculationProfiler calculationProfiler) : base(parameters, fft,
            calculationProfiler, new List<ResultFileID>() { ResultFileID.AffordanceTimeUse
            },
            "Affordance Time Use", FileProcessingResult.ShouldCreateFiles
        )
        {
        }

        protected override FileProcessingResult MakeOnePlot(ResultFileEntry srcResultFileEntry)
        {
            string plotName = "Affordance Time Use " + srcResultFileEntry.HouseholdNumberString;
            Profiler.StartPart(Utili.GetCurrentMethodAndClass());
            var allConsumptions =
                new Dictionary<string, List<Tuple<string, double>>>();
            var lastName = string.Empty;
            var taggingSets = new Dictionary<string, List<ChartTaggingSet>>();
            if (srcResultFileEntry.FullFileName == null)
            {
                throw new LPGException("Srcfile was null");
            }
            using (var sr = new StreamReader(srcResultFileEntry.FullFileName)) {
                while (!sr.EndOfStream) {
                    var s = sr.ReadLine();
                    if (s == null) {
                        throw new LPGException("Readline failed");
                    }
                    if (s.StartsWith("----", StringComparison.Ordinal)) {
                        s = sr.ReadLine();
                        if (s == null) {
                            throw new LPGException("Readline failed");
                        }
                        var arr = s.Split(Parameters.CSVCharacterArr, StringSplitOptions.None);
                        lastName = arr[0];
                        allConsumptions.Add(lastName, new List<Tuple<string, double>>());
                        taggingSets.Add(lastName, new List<ChartTaggingSet>());
                        for (var i = 2; i < arr.Length; i++) {
                            if (!string.IsNullOrWhiteSpace(arr[i])) {
                                taggingSets[lastName].Add(new ChartTaggingSet(arr[i]));
                            }
                        }
                    }
                    else {
                        var cols = s.Split(Parameters.CSVCharacterArr, StringSplitOptions.None);
                        var d = Convert.ToDouble(cols[1], CultureInfo.CurrentCulture);
                        allConsumptions[lastName].Add(new Tuple<string, double>(cols[0], d));
                        for (var i = 2; i < cols.Length; i++) {
                            taggingSets[lastName][i - 2].AffordanceToCategories.Add(cols[0], cols[i]);
                        }
                    }
                }
            }
            IntervallBarMaker ivm = new IntervallBarMaker();
            foreach (var pair in allConsumptions) {
                var cts = taggingSets[pair.Key];

                foreach (var set in cts) {
                    ivm.MakeIntervalBars(srcResultFileEntry, plotName, Parameters.BaseDirectory, pair.Value, set, "." + pair.Key + "." + set.Name,false,this,CalcOption.ActivationFrequencies );
                }
            }
            Profiler.StopPart(Utili.GetCurrentMethodAndClass());
            return FileProcessingResult.ShouldCreateFiles;
        }
    }
}