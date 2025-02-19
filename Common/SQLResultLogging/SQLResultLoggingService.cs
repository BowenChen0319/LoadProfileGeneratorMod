﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Data.SQLite;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Automation;
using Automation.ResultFiles;
using Common.SQLResultLogging.InputLoggers;
using JetBrains.Annotations;
using Newtonsoft.Json;

namespace Common.SQLResultLogging {
    /*public interface ITypeDescriber {
        [JetBrains.Annotations.NotNull]
        HouseholdKey HouseholdKey { get; }

        [UsedImplicitly]
        int ID { get; set; }

        [JetBrains.Annotations.NotNull]
        string GetTypeDescription();
    }*/

    [SuppressMessage("ReSharper", "RedundantNameQualifier")]
    public class SqlResultLoggingService {
        [JetBrains.Annotations.NotNull] private readonly string _basePath;

        [JetBrains.Annotations.NotNull] private readonly Dictionary<HouseholdKey, List<string>> _createdTablesPerHousehold = new Dictionary<HouseholdKey, List<string>>();

        [JetBrains.Annotations.NotNull] private readonly Dictionary<HouseholdKey, FileEntry> _filenameByHouseholdKey =
            new Dictionary<HouseholdKey, FileEntry>();

        private bool _isFileNameDictLoaded;
        //static readonly List<SqlResultLoggingService> loggingServices = new List<SqlResultLoggingService>();
        public bool DoesTableExist(HouseholdKey key, [JetBrains.Annotations.NotNull] string tableName)
        {
            if (!File.Exists(FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename)) {
                throw new LPGException(
                    "Missing file: " + FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename);
            }

            string constr = "Data Source=" + FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename +
                            ";Version=3";
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection(constr)) {
                //;Synchronous=OFF;Journal Mode=WAL;
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE'" +
                                                             tableName + "'")) {
                    cmd.Connection = conn;
                    using (var dr = cmd.ExecuteReader()) {
                        while (dr.Read()) {
                            var s = dr.GetString(0);
                            if (string.Equals(s, tableName, StringComparison.OrdinalIgnoreCase)) {
                                return true;
                            }
                        }
                    }
                    return false;
                }
            }
        }

        public SqlResultLoggingService([JetBrains.Annotations.NotNull] string basePath)
        {
            //loggingServices.Add(this);

            _basePath = basePath;

            if (_basePath.Contains(".sqlite")) {
                throw new LPGException("need to put in the path, not a filename");
            }

            //initialize main file
            GetFilenameForHouseholdKey(Constants.GeneralHouseholdKey);
        }

        [JetBrains.Annotations.NotNull]
        public Dictionary<HouseholdKey, FileEntry> FilenameByHouseholdKey => _filenameByHouseholdKey;

        [ItemNotNull]
        [JetBrains.Annotations.NotNull]
        public List<DatabaseEntry> LoadDatabases()
        {
            List<DatabaseEntry> td = new List<DatabaseEntry>();
            const string sql = "SELECT * FROM DatabaseList";

            string constr = "Data Source=" + FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename + ";Version=3";
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection(constr)) {
                //;Synchronous=OFF;Journal Mode=WAL;
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand()) {
                    cmd.Connection = conn;
                    cmd.CommandText = sql;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read()) {
                        string keyStr = reader["HouseholdKey"].ToString() ?? "";
                        HouseholdKey key = new HouseholdKey(keyStr);
                        string filename = reader["Filename"].ToString() ?? "";
                        DatabaseEntry fe = new DatabaseEntry(filename, key);
                        td.Add(fe);
                    }
                }

                conn.Close();
            }

            return td;
        }

        [ItemNotNull]
        [JetBrains.Annotations.NotNull]
        public List<ResultTableDefinition> LoadTables([JetBrains.Annotations.NotNull] HouseholdKey dbKey)
        {
            List<ResultTableDefinition> td = new List<ResultTableDefinition>();
            if (!FilenameByHouseholdKey.ContainsKey(dbKey)) {
                return td;
            }
            const string sql = "SELECT * FROM TableDescription";

            string constr = "Data Source=" + FilenameByHouseholdKey[dbKey].Filename + ";Version=3";
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection(constr)) {
                //;Synchronous=OFF;Journal Mode=WAL;
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand()) {
                    cmd.Connection = conn;
                    cmd.CommandText = sql;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read()) {
                        string tableName = reader["TableName"].ToString() ?? "no table name";
                        string description = reader["Description"].ToString() ?? "no description";
                        int resultTableid = (int)(long)reader["ResultTableID"];
                        CalcOption enablingOption = (CalcOption)(long)reader["EnablingOption"];
                        ResultTableDefinition fe = new ResultTableDefinition(tableName, (ResultTableID)resultTableid, description, enablingOption);
                        td.Add(fe);
                    }
                }

                conn.Close();
            }

            return td;
        }

        //[JetBrains.Annotations.NotNull]
        //private string MainFilename { get; set; }

        public void MakeTableForListOfFields([JetBrains.Annotations.NotNull] [ItemNotNull]
                                             List<FieldDefinition> fields,
                                             [JetBrains.Annotations.NotNull] HouseholdKey householdKey,
                                             [JetBrains.Annotations.NotNull] string tableName)
        {
            if (fields.Count == 0) {
                throw new LPGException("No fields defined for database");
            }

            string dstFileName = GetFilenameForHouseholdKey(householdKey);
            string sql = "CREATE TABLE " + tableName + "(";
            foreach (var field in fields) {
                sql += field.Name + " " + field.Type + ",";
            }

            sql = sql.Substring(0, sql.Length - 1) + ");";
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection("Data Source=" + dstFileName + ";Version=3;")
            ) {
                conn.Open();
                var command = conn.CreateCommand();
                command.CommandText = sql;
                var result = command.ExecuteNonQuery();
                if (result != 0) {
                    throw new LPGException("Creating the table " + tableName + " failed.");
                }

                conn.Close();
            }
        }
        [ItemNotNull]
        [JetBrains.Annotations.NotNull]
        public IEnumerable<T> ReadFromJsonAsEnumerable<T>([JetBrains.Annotations.NotNull] ResultTableDefinition rtd, [JetBrains.Annotations.NotNull] HouseholdKey key)
        {
            if (!_isFileNameDictLoaded)
            {
                LoadFileNameDict();
            }

            string sql = "SELECT json FROM " + rtd.TableName;
            if (!FilenameByHouseholdKey.ContainsKey(key))
            {
                throw new LPGException("Missing sql file for household key " + key);
            }

            string constr = "Data Source=" + FilenameByHouseholdKey[key].Filename + ";Version=3";
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection(constr))
            {
                //;Synchronous=OFF;Journal Mode=WAL;
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand())
                {
                    cmd.Connection = conn;
                    /*
                    List<string> tables = new List<string>();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                    using (var reader2 = cmd.ExecuteReader()) {
                        while (reader2.Read())
                        {
                            tables.Add(reader2[0].ToString());
                        }
                    }*/

                    cmd.CommandText = sql;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read()) {
                        string s = reader[0].ToString() ?? "";
                        T re = JsonConvert.DeserializeObject<T>(s);
                        if (re is null) {
                            throw new LPGException("object was null");
                        }

                        yield return re;
                    }
                    conn.Close();
                }
            }
        }


        [ItemNotNull]
        [JetBrains.Annotations.NotNull]
        public List<T> ReadFromJson<T>([JetBrains.Annotations.NotNull] ResultTableDefinition rtd, [JetBrains.Annotations.NotNull] HouseholdKey key,
                                       ExpectedResultCount expectedResult)
        {
            if (!_isFileNameDictLoaded) {
                LoadFileNameDict();
            }

            string sql = "SELECT json FROM " + rtd.TableName;
            if (!FilenameByHouseholdKey.ContainsKey(key)) {
                throw new LPGException("Missing sql file for household key "+ key);
            }

            string constr = "Data Source=" + FilenameByHouseholdKey[key].Filename + ";Version=3";
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection(constr)) {
                //;Synchronous=OFF;Journal Mode=WAL;
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand()) {
                    cmd.Connection = conn;
                    /*
                    List<string> tables = new List<string>();
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                    using (var reader2 = cmd.ExecuteReader()) {
                        while (reader2.Read())
                        {
                            tables.Add(reader2[0].ToString());
                        }
                    }*/
                    List<T> resultsObjects = new List<T>();
                    cmd.CommandText = sql;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read()) {
                        string s = reader[0].ToString() ?? "";
                        T re = JsonConvert.DeserializeObject<T>(s);
                        resultsObjects.Add(re);
                    }

                    switch (expectedResult) {
                        case ExpectedResultCount.One:
                            if (resultsObjects.Count != 1) {
                                throw new DataIntegrityException("Not exactly one result");
                            }

                            break;
                        case ExpectedResultCount.Many:
                            if (resultsObjects.Count < 2) {
                                throw new DataIntegrityException("Not many results");
                            }

                            break;
                        case ExpectedResultCount.OneOrMore:
                            if (resultsObjects.Count < 1) {
                                throw new DataIntegrityException("Not one or more results");
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(expectedResult), expectedResult, null);
                    }
                    conn.Close();
                    return resultsObjects;
                }
            }
        }

        public void SaveDictionaryToDatabaseNewConnection([JetBrains.Annotations.NotNull] Dictionary<string, object> values,
                                                          [JetBrains.Annotations.NotNull] string tableName,
                                                          [JetBrains.Annotations.NotNull] HouseholdKey householdKey)
        {
            List<Dictionary<string, object>> valuesList = new List<Dictionary<string, object>> {
                values
            };
            SaveDictionaryToDatabaseNewConnection(valuesList, tableName, householdKey);
        }

        public void SaveDictionaryToDatabaseNewConnection([ItemNotNull] [JetBrains.Annotations.NotNull]
                                                          List<Dictionary<string, object>> values,
                                                          [JetBrains.Annotations.NotNull] string tableName,
                                                          [JetBrains.Annotations.NotNull] HouseholdKey householdKey)
        {
            string sql = "Insert into " + tableName + "(";
            string fields = "";
            string parameters = "";
            foreach (KeyValuePair<string, object> pair in values[0]) {
                fields += pair.Key + ",";
                parameters += "@" + pair.Key + ",";
            }

            fields = fields.Substring(0, fields.Length - 1);
            parameters = parameters.Substring(0, parameters.Length - 1);
            sql += fields + ") VALUES (" + parameters + ")";
            string dstFileName = GetFilenameForHouseholdKey(householdKey);
            using (System.Data.SQLite.SQLiteConnection conn =
                new System.Data.SQLite.SQLiteConnection("Data Source=" + dstFileName + ";Version=3;Synchronous=OFF;Journal Mode=WAL;")) {
                conn.Open();
                using (var transaction = conn.BeginTransaction()) {
                    var command = conn.CreateCommand();
                    command.CommandText = sql;
                    foreach (Dictionary<string, object> row in values) {
                        if (row.Count != values[0].Count) {
                            throw new LPGException("Incorrect number of columns");
                        }

                        command.Parameters.Clear();
                        foreach (KeyValuePair<string, object> pair in row) {
                            string parameter = "@" + pair.Key;
                            command.Parameters.AddWithValue(parameter, pair.Value);
                        }

                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }

                conn.Close();
            }
        }

        public void SaveResultEntry([JetBrains.Annotations.NotNull] SaveableEntry entry)
        {
            entry.IntegrityCheck();
            string dstFileName = GetFilenameForHouseholdKey(entry.HouseholdKey);
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection("Data Source=" + dstFileName + ";Version=3")) {
                //;Synchronous=OFF;Journal Mode=WAL;"
                conn.Open();
                if (!IsTableCreated(entry)) {
                    MakeTableForListOfFields(entry.Fields, conn, entry.ResultTableDefinition.TableName);
                    Dictionary<string, object> fields = new Dictionary<string, object> {
                        {"TableName", entry.ResultTableDefinition.TableName},
                        {"Description", entry.ResultTableDefinition.Description},
                        {"ResultTableID", entry.ResultTableDefinition.ResultTableID},
                        {"EnablingOption", entry.ResultTableDefinition.EnablingOption}
                    };
                    List<Dictionary<string, object>> rows = new List<Dictionary<string, object>> {
                        fields
                    };
                    SaveDictionaryToDatabase(rows, "TableDescription", conn);
                    if (!_createdTablesPerHousehold.ContainsKey(entry.HouseholdKey)) {
                        _createdTablesPerHousehold.Add(entry.HouseholdKey, new List<string>());
                    }

                    _createdTablesPerHousehold[entry.HouseholdKey].Add(entry.ResultTableDefinition.TableName);
                }

                SaveDictionaryToDatabase(entry.RowEntries, entry.ResultTableDefinition.TableName, conn);
                conn.Close();
            }
        }
        /*
        public void SaveToDatabase<T>([JetBrains.Annotations.NotNull] [ItemNotNull] List<T> items) where T : ITypeDescriber
        {
            Dictionary<HouseholdKey, List<T>> itemsByKey = new Dictionary<HouseholdKey, List<T>>();
            foreach (T item in items) {
                HouseholdKey key = item.HouseholdKey;
                if (!itemsByKey.ContainsKey(key)) {
                    itemsByKey.Add(key, new List<T>());
                }

                itemsByKey[key].Add(item);
            }

            foreach (KeyValuePair<HouseholdKey, List<T>> pair in itemsByKey) {
                var filteredItems = items.Where(x => x.HouseholdKey == pair.Key).ToList();
                SaveableEntry se = new SaveableEntry(pair.Key, typeof(T).Name, filteredItems[0].GetTypeDescription());
                var properties = typeof(T).GetProperties();
                var fprops = properties.Where(x => !IgnoreThisField(x.Name)).ToList();
                foreach (var prop in fprops) {
                    se.AddField(prop.Name, prop.PropertyType);
                }

                foreach (T item in filteredItems) {
                    RowBuilder rb = new RowBuilder();
                    foreach (var prop in fprops) {
                        rb.Add(prop.Name, prop.GetValue(item));
                    }

                    se.AddRow(rb.ToDictionary());
                }

                SaveResultEntry(se);
            }
        }*/

        [JetBrains.Annotations.NotNull]
        private string GetFilenameForHouseholdKey([JetBrains.Annotations.NotNull] HouseholdKey key)
        {
            if (FilenameByHouseholdKey.ContainsKey(key)) {
                return FilenameByHouseholdKey[key].Filename;
            }

            bool isMainDatabase = key == Constants.GeneralHouseholdKey;

            string newName = Path.Combine(_basePath, "Results." + key + ".sqlite");
            FilenameByHouseholdKey.Add(key, new FileEntry(newName));
            FileInfo fi = new FileInfo(newName);
            FilenameByHouseholdKey[key].DescriptionTableWritten = true;
            if (fi.Exists && fi.Length > 1000) {
                return newName;
            }

            if (fi.FullName.Length > 260) {
                throw new LPGException("Filename length > 260. This is a Windows limitation: " + fi.FullName);
            }

            if (fi.Directory?.Exists != true) {
                throw new LPGException("Directory does not exist.");
            }
            string connectionString = MakeconnectionString(fi.FullName);
            using (SQLiteConnection dbcon = new SQLiteConnection(connectionString)) {
                dbcon.Open();
                {
                    FieldDefinition fd1 = new FieldDefinition("TableName", "Text");
                    FieldDefinition fd2 = new FieldDefinition("Description", "Text");
                    FieldDefinition fd3 = new FieldDefinition("ResultTableID", "Integer");
                    FieldDefinition fd4 = new FieldDefinition("EnablingOption", "Integer");
                    List<FieldDefinition> fields = new List<FieldDefinition> {
                        fd1,
                        fd2,
                        fd3,
                        fd4
                    };
                    MakeTableForListOfFields(fields, dbcon, Constants.TableDescriptionTableName);
                }

                if (isMainDatabase) {
                    //MainFilename = newName;
                    {
                        FieldDefinition fd1 = new FieldDefinition("Filename", "Text");
                        //FieldDefinition fd3 = new FieldDefinition("ID", "INTEGER");
                        FieldDefinition fd2 = new FieldDefinition("HouseholdKey", "Text");
                        List<FieldDefinition> fields = new List<FieldDefinition> {
                            fd1,
                            fd2
                        };
                        //fields.Add(fd3);
                        MakeTableForListOfFields(fields, dbcon, nameof(DatabaseList));
                    }
                }

                dbcon.Close();
            }
            ResultFileEntry rfe = new ResultFileEntry("Database", fi, false, ResultFileID.SqliteResultFiles, key.Key,null
                , CalcOption.BasicOverview);
            ResultFileEntryLogger rfel = new ResultFileEntryLogger(this);
                rfel.Run(key,rfe);

            /* DatabaseList dbl = new DatabaseList(key.Key, null, newName)
             {
                 HouseholdKey = key.Key,
                 Filename = newName
             };*/
            var row = RowBuilder.Start("HouseholdKey", key.Key).Add("Filename", newName).ToDictionary();
            SaveDictionaryToDatabaseNewConnection(row, "DatabaseList", Constants.GeneralHouseholdKey);
            return newName;
        }

        /*
        private bool IgnoreThisField([JetBrains.Annotations.NotNull] string fieldname)
        {
            if (fieldname == "HouseholdKey") {
                return true;
            }

            return false;
        }*/

        /// <summary>
        /// Returns whether a matching table for the entry exists
        /// </summary>
        /// <param name="entry">The entry for which the table is intended</param>
        /// <returns>True if a matching table exists, else false</returns>
        private bool IsTableCreated([JetBrains.Annotations.NotNull] SaveableEntry entry)
        {
            if (!_createdTablesPerHousehold.ContainsKey(entry.HouseholdKey)) {
                return false;
            }

            var tables = _createdTablesPerHousehold[entry.HouseholdKey];
            if (!tables.Contains(entry.ResultTableDefinition.TableName)) {
                return false;
            }

            return true;
        }

        private void LoadFileNameDict()
        {
            const string sql = "SELECT * FROM DatabaseList";
            if (!File.Exists(FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename)) {
                throw new LPGException("Missing file: " + FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename);
            }
            string constr = "Data Source=" + FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename + ";Version=3";
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection(constr)) {
                //;Synchronous=OFF;Journal Mode=WAL;
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand()) {
                    cmd.Connection = conn;

                    cmd.CommandText = sql;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read()) {
                        string keyStr = reader["HouseholdKey"].ToString() ?? "";
                        HouseholdKey key = new HouseholdKey(keyStr);
                        string filename = reader["Filename"].ToString() ?? "";
                        FileEntry fe = new FileEntry(filename) {
                            DescriptionTableWritten = true
                        };
                        if (!FilenameByHouseholdKey.ContainsKey(key)) {
                            FilenameByHouseholdKey.Add(key, fe);
                        }
                    }
                }

                conn.Close();
            }

            _isFileNameDictLoaded = true;
        }

        /// <summary>
        /// Deletes an entry from a database table
        /// </summary>
        /// <param name="entry">A dictionary containing field values of the entry to delete.</param>
        /// <param name="tableName">The name of the table to delete from</param>
        /// <param name="householdKey">The HouseholdKey matching the entry</param>
        public void DeleteEntry(Dictionary<string, object> entry, [JetBrains.Annotations.NotNull] string tableName, HouseholdKey householdKey)
        {
            DeleteEntries(new List<Dictionary<string, object>> { entry }, tableName, householdKey);
        }

        /// <summary>
        /// Deletes a list of entries from a database table
        /// </summary>
        /// <param name="entries">A list of dictionaries, one for each entry to delete. Each dictionary contains field values of the entry to delete.</param>
        /// <param name="tableName">The name of the table to delete entries from</param>
        /// <param name="householdKey">The HouseholdKey matching the entries</param>
        public void DeleteEntries([JetBrains.Annotations.NotNull][ItemNotNull] List<Dictionary<string, object>> entries,
                                   [JetBrains.Annotations.NotNull] string tableName, HouseholdKey householdKey)
        {
            if (entries.Count == 0)
            {
                // nothing to do
                return;
            }

            // open the SQLite database connection
            string dstFileName = GetFilenameForHouseholdKey(householdKey);
            using SQLiteConnection conn = new SQLiteConnection("Data Source=" + dstFileName + ";Version=3");
            conn.Open();

            // prepare the sql command without the specific conditions
            string sqlBase = "DELETE FROM " + tableName + " WHERE ";
            using (var transaction = conn.BeginTransaction())
            {
                using (var command = conn.CreateCommand())
                {
                    foreach (Dictionary<string, object> row in entries)
                    {
                        // get an enumerable of "field=@field" strings and concatenate them with AND in between
                        var conditions = row.Select(pair => pair.Key + " = @" + pair.Key);
                        string conditionString = string.Join(" AND ", conditions);
                        // combine base and conditions to full command
                        command.CommandText = sqlBase + conditionString;
                        // add all parameter values
                        command.Parameters.Clear();
                        foreach (KeyValuePair<string, object> pair in row)
                        {
                            string parameter = "@" + pair.Key;
                            command.Parameters.AddWithValue(parameter, pair.Value);
                        }
                        command.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
            conn.Close();
        }

        [JetBrains.Annotations.NotNull]
        private static string MakeconnectionString([JetBrains.Annotations.NotNull] string filename) =>
            "Data Source=" + filename + ";Version=3;Synchronous=OFF;Journal Mode=WAL;";

        private static void MakeTableForListOfFields([JetBrains.Annotations.NotNull] [ItemNotNull]
                                                     List<FieldDefinition> fields,
                                                     [JetBrains.Annotations.NotNull] System.Data.SQLite.SQLiteConnection conn,
                                                     [JetBrains.Annotations.NotNull] string tableName)
        {
            if (fields.Count == 0) {
                throw new LPGException("No fields defined for database");
            }

            string sql = "CREATE TABLE " + tableName + "(";
            foreach (var field in fields) {
                sql += field.Name + " " + field.Type + ",";
            }

            sql = sql.Substring(0, sql.Length - 1) + ");";
            int result;
            using (var command = conn.CreateCommand()) {
                command.CommandText = sql;
                result = command.ExecuteNonQuery();
            }

            if (result != 0) {
                throw new LPGException("Creating the table " + tableName + " failed.");
            }
        }

        //[JetBrains.Annotations.NotNull]
        //public string ReturnMainSqlPath() => _filenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename;

        private static void SaveDictionaryToDatabase([JetBrains.Annotations.NotNull] [ItemNotNull] List<Dictionary<string, object>> values,
                                                     [JetBrains.Annotations.NotNull] string tableName,
                                                     [JetBrains.Annotations.NotNull] System.Data.SQLite.SQLiteConnection conn)
        {
            if (values.Count == 0) {
                return;
            }

            //figure out sql
            var firstrow = values[0];
            string sql = "Insert into " + tableName + "(";
            string fields = "";
            string parameters = "";
            foreach (KeyValuePair<string, object> pair in firstrow) {
                fields += pair.Key + ",";
                parameters += "@" + pair.Key + ",";
            }

            fields = fields.Substring(0, fields.Length - 1);
            parameters = parameters.Substring(0, parameters.Length - 1);
            sql += fields + ") VALUES (" + parameters + ")";
            //execute the sql
            using (var transaction = conn.BeginTransaction()) {
                using (var command = conn.CreateCommand()) {
                    command.CommandText = sql;
                    foreach (Dictionary<string, object> row in values) {
                        if (row.Count != firstrow.Count) {
                            throw new LPGException("Incorrect number of columns");
                        }

                        command.Parameters.Clear();
                        foreach (KeyValuePair<string, object> pair in row) {
                            string parameter = "@" + pair.Key;
                            command.Parameters.AddWithValue(parameter, pair.Value);
                        }

                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }
        }

        public class DatabaseEntry {
            public DatabaseEntry([JetBrains.Annotations.NotNull] string filename, [JetBrains.Annotations.NotNull] HouseholdKey key)
            {
                Filename = filename;
                Key = key;
            }

            [JetBrains.Annotations.NotNull]
            public string Filename { get; }

            [JetBrains.Annotations.NotNull]
            public HouseholdKey Key { get; }

            [JetBrains.Annotations.NotNull]
            public override string ToString() => Filename;
        }

        public class DatabaseList {
            public DatabaseList([JetBrains.Annotations.NotNull] string householdKey, [CanBeNull] long? id, [JetBrains.Annotations.NotNull] string filename)
            {
                HouseholdKey = householdKey;
                ID = id;
                Filename = filename;
            }

            [JetBrains.Annotations.NotNull]
            public string Filename { get; set; }

            [JetBrains.Annotations.NotNull]
            public string HouseholdKey { get; set; }

            [UsedImplicitly]
            [CanBeNull]
            public long? ID { get; set; }
        }

        public class FieldDefinition {
            public FieldDefinition([JetBrains.Annotations.NotNull] string name, [JetBrains.Annotations.NotNull] string type)
            {
                Name = name;
                Type = type;
            }

            [JetBrains.Annotations.NotNull]
            public string Name { get; }

            [JetBrains.Annotations.NotNull]
            public string Type { get; }
        }

        public class FileEntry {
            public FileEntry([JetBrains.Annotations.NotNull] string filename) => Filename = filename;

            public bool DescriptionTableWritten { get; set; }

            [JetBrains.Annotations.NotNull]
            public string Filename { get; }

            [JetBrains.Annotations.NotNull]
            public override string ToString() => Filename;
        }

        public bool CheckifTableExits(string tableName)
        {
            string sql = "SELECT name FROM sqlite_master WHERE type='table' AND name='" + tableName+ "';";

            string constr = "Data Source=" + FilenameByHouseholdKey[Constants.GeneralHouseholdKey].Filename + ";Version=3";
            int lines = 0;
            using (System.Data.SQLite.SQLiteConnection conn = new System.Data.SQLite.SQLiteConnection(constr))
            {
                //;Synchronous=OFF;Journal Mode=WAL;
                conn.Open();
                using (SQLiteCommand cmd = new SQLiteCommand())
                {
                    cmd.Connection = conn;

                    cmd.CommandText = sql;
                    var reader = cmd.ExecuteReader();
                    while (reader.Read()) {
                        lines++;
                    }
                }
                conn.Close();
            }
            if (lines > 0) {
                return true;
            }

            return false;
        }
    }

    public enum ExpectedResultCount {
        One,
        OneOrMore,
        Many
    }

    public enum SqliteDataType {
        Text,
        Integer,
        Double,
        Bit,
        DateTime,
        JsonField
    }

    public class SaveableEntry {
        public SaveableEntry([JetBrains.Annotations.NotNull] HouseholdKey householdKey, [JetBrains.Annotations.NotNull] ResultTableDefinition resultTableDefinition)
        {
            HouseholdKey = householdKey;
            ResultTableDefinition = resultTableDefinition;
        }

        [JetBrains.Annotations.NotNull]
        [ItemNotNull]
        public List<SqlResultLoggingService.FieldDefinition> Fields { get; } = new List<SqlResultLoggingService.FieldDefinition>();

        [JetBrains.Annotations.NotNull]
        public HouseholdKey HouseholdKey { get; }

        [JetBrains.Annotations.NotNull]
        public ResultTableDefinition ResultTableDefinition { get; }

        [JetBrains.Annotations.NotNull]
        [ItemNotNull]
        public List<Dictionary<string, object>> RowEntries { get; } = new List<Dictionary<string, object>>();

        public void AddField([JetBrains.Annotations.NotNull] string name, SqliteDataType datatype)
        {
            Fields.Add(new SqlResultLoggingService.FieldDefinition(name, datatype.ToString()));
        }

        public void AddField([JetBrains.Annotations.NotNull] string name, [JetBrains.Annotations.NotNull] Type datatype)
        {
            string sqlDataType;
            switch (datatype.Name) {
                case "String":
                    sqlDataType = "TEXT";
                    break;
                case "Int32":
                    sqlDataType = "INTEGER";
                    break;
                case "Boolean":
                    sqlDataType = "BIT";
                    break;
                case "DateTime":
                    sqlDataType = "DateTime";
                    break;
                default:
                    throw new LPGException("Unknown data type:" + datatype.Name);
            }

            Fields.Add(new SqlResultLoggingService.FieldDefinition(name, sqlDataType));
        }

        public void AddRow([JetBrains.Annotations.NotNull] Dictionary<string, object> row)
        {
            RowEntries.Add(row);
        }

        public void IntegrityCheck()
        {
            if (RowEntries.Count > 0 && Fields.Count != RowEntries[0].Count) {
                throw new LPGException("Inconsistent number of columns");
            }
        }
    }

    public class RowBuilder {
        [JetBrains.Annotations.NotNull]
        public Dictionary<string, object> Row { get; } = new Dictionary<string, object>();

        [JetBrains.Annotations.NotNull]
        public RowBuilder Add([JetBrains.Annotations.NotNull] string name, [CanBeNull] object content)
        {
            Row.Add(name, content);
            return this;
        }

        [JetBrains.Annotations.NotNull]
        public static RowBuilder Start([JetBrains.Annotations.NotNull] string name, [CanBeNull] object content)
        {
            RowBuilder rb = new RowBuilder();
            return rb.Add(name, content);
        }

        [JetBrains.Annotations.NotNull]
        public Dictionary<string, object> ToDictionary() => Row;
    }
}