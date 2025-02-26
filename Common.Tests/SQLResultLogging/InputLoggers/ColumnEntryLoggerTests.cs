﻿using System.Collections.Generic;
using System.IO;
using Automation;
using Automation.ResultFiles;
using Common.CalcDto;
using Common.JSON;
using Common.SQLResultLogging;
using Common.SQLResultLogging.InputLoggers;
using FluentAssertions;
using Newtonsoft.Json;

using Xunit;
using Xunit.Abstractions;


namespace Common.Tests.SQLResultLogging.InputLoggers {
    public class ColumnEntryLoggerTests : UnitTestBaseClass
    {
        [Fact]
        [Trait(UnitTestCategories.Category,UnitTestCategories.BasicTest)]
        public void RunColumnEntryLoggerTest()
        {
            using (WorkingDir wd = new WorkingDir(Utili.GetCurrentMethodAndClass()))
            {
                ColumnEntryLogger ael = new ColumnEntryLogger(wd.SqlResultLoggingService);
                HouseholdKey key = new HouseholdKey("hhkey");
                List<IDataSaverBase> savers = new List<IDataSaverBase>
            {
                ael
            };
                InputDataLogger idl = new InputDataLogger(savers.ToArray());
                CalcLoadTypeDto cltd = new CalcLoadTypeDto("ltname", "kw", "kwh", 1, false, "guid".ToStrGuid());
                CalcDeviceDto cdd = new CalcDeviceDto("device", "guid".ToStrGuid(), key, OefcDeviceType.Device, "devcatname", "", "guid".ToStrGuid(), "guid".ToStrGuid(), "loc", FlexibilityType.NoFlexibility, 0);
                ColumnEntry ce = new ColumnEntry("name", 1, "locname", "guid".ToStrGuid(), key, cltd, "oefckey", "devicecategory", cdd);
                List<ColumnEntry> aes = new List<ColumnEntry>
            {
                ce
            };
                idl.Save(aes);
                var res = ael.Read(key);
                var s1 = JsonConvert.SerializeObject(aes, Formatting.Indented);
                var s2 = JsonConvert.SerializeObject(res, Formatting.Indented);
                File.WriteAllText(wd.Combine("original.json"), s1);
                File.WriteAllText(wd.Combine("deserialized.json"), s2);
                s1.Should().Be(s2);
            }
            //wd.CleanUp();
        }

        public ColumnEntryLoggerTests([JetBrains.Annotations.NotNull] ITestOutputHelper testOutputHelper) : base(testOutputHelper)
        {
        }
    }
}