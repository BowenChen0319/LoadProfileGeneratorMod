﻿#region header

// // ProfileGenerator DatabaseIO changed: 2017 03 22 14:33

#endregion

using System;
using System.Collections.ObjectModel;
using System.Linq;
using Automation;
using Database.Database;
using Database.Tables.BasicHouseholds;
using JetBrains.Annotations;

namespace Database.Tables.Transportation {
    public class SiteLocation : DBBase {
        public const string TableName = "tblSiteLocations";

        [CanBeNull] private readonly Location _location;

        public SiteLocation([CanBeNull]int? pID, [CanBeNull] Location location, int siteID, [JetBrains.Annotations.NotNull] string connectionString,
            [JetBrains.Annotations.NotNull] string name, [NotNull] StrGuid guid) : base(name, TableName, connectionString, guid)
        {
            TypeDescription = "Site Location";
            ID = pID;
            _location = location;
            SiteID = siteID;
        }

        [UsedImplicitly]
        [JetBrains.Annotations.NotNull]
        public Location Location => _location ?? throw new InvalidOperationException();

        public override string Name {
            get {
                if (_location != null) {
                    return _location.Name;
                }
                return "(no name)";
            }
        }

        [UsedImplicitly]
        public int SiteID { get; }

        [JetBrains.Annotations.NotNull]
        private static SiteLocation AssignFields([JetBrains.Annotations.NotNull] DataReader dr, [JetBrains.Annotations.NotNull] string connectionString, bool ignoreMissingFields,
            [JetBrains.Annotations.NotNull] AllItemCollections aic)
        {
            var siteLocID = dr.GetIntFromLong("ID");
            var siteID = dr.GetIntFromLong("SiteID");
            var locationID = dr.GetIntFromLong("LocationID");
            var loc = aic.Locations.FirstOrDefault(x => x.ID == locationID);
            var name = "(no name)";
            if (loc != null) {
                name = loc.Name;
            }
            var guid = GetGuid(dr, ignoreMissingFields);
            var locdev = new SiteLocation(siteLocID, loc, siteID, connectionString, name, guid);
            return locdev;
        }

        protected override bool IsItemLoadedCorrectly(out string message)
        {
            if (_location == null) {
                message = "Location not found";
                return false;
            }
            message = "";
            return true;
        }

        public static void LoadFromDatabase([ItemNotNull] [JetBrains.Annotations.NotNull] ObservableCollection<SiteLocation> result, [JetBrains.Annotations.NotNull] string connectionString,
            [ItemNotNull] [JetBrains.Annotations.NotNull] ObservableCollection<Location> locations, bool ignoreMissingTables)
        {
            var aic = new AllItemCollections(locations: locations);
            LoadAllFromDatabase(result, connectionString, TableName, AssignFields, aic, ignoreMissingTables, false);
        }

        protected override void SetSqlParameters(Command cmd)
        {
            cmd.AddParameter("SiteID", SiteID);
            if(_location != null) {
                cmd.AddParameter("LocationID", _location.IntID);
            }
        }

        public override string ToString() => Name;
    }
}