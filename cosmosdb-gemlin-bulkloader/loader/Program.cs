﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
// ------------------------------------------------------------

// Chris Joakim, Microsoft, May 2021

using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Newtonsoft.Json;

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using CosmosGemlinBulkLoader.Csv;
using CosmosGemlinBulkLoader.Element;
using Microsoft.Azure.Cosmos;

namespace CosmosGemlinBulkLoader
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Newtonsoft.Json;

    public class Program
    {
        // Class variables:
        private static Config config = null;
        private static GraphBulkExecutor graphBulkExecutor = null;
        private static HeaderRow headerRow = null;
        private static CsvRowParser parser = null;
        private static BlobServiceClient blobServiceClient = null;
        private static BlobContainerClient containerClient = null;
        private static BlobClient blobClient = null;
        private static string infile = null;
        private static string source = null;
        private static string fileType = null;
        private static bool doLoad = false;
        private static bool verbose = false;
        private static long rowCount = 0;
        private static long batchSize = Config.DEFAULT_BATCH_SIZE;
        private static long batchCount = 0;

        private static void DisplayCliOptions(string msg)
        {
            Console.WriteLine("");
            Console.WriteLine("Command-Line Options:");
            if (msg != null) {
                Console.WriteLine($"ERROR: {msg}");
            }
            Console.WriteLine("$ dotnet run preprocess --file-type vertex --csv-infile /data/vertex.csv");
            Console.WriteLine("$ dotnet run preprocess --file-type vertex --csv-infile Data/amtrak-station-vertices.csv");
            Console.WriteLine("");
        }

        static async Task Main(string[] args)
        {
            try
            {
                long startTime = EpochMsTime();
                Console.WriteLine($"start timestamp: {CurrentTimestamp()}");
                config = InitializeConfig(args);
                if (!config.IsValid())
                {
                    throw new ConfigurationException("Program configuration is invalid.");
                }
                graphBulkExecutor = new GraphBulkExecutor(config);
                await graphBulkExecutor.InitializeThrottle();
                if (!graphBulkExecutor.throttle.IsValid())
                {
                    throw new ConfigurationException("Throttling configuration is invalid.");
                }

                if (config.GetCliKeywordArg("--test", "") == "throttle")
                {
                    Console.WriteLine("TESTING: let's test the Throttle!");
                    int baseCount = 100;
                    Throttle t = null;
                    
                    for (int level = 1; level <= 10; level++)
                    {
                        List<Task> tasks = new List<Task>();
                        t = new Throttle(config, 10000, level);
                        t.Display();
                        for (int i = 0; i < baseCount; i++)
                        {
                            t.AddTask(tasks);
                        }
                        List<Task> shuffled = t.AddShuffleThrottlingTasks(tasks);
                        Console.WriteLine($"TESTING: {shuffled.Count} tasks for level {level} with base count {baseCount}");
                    }
                    System.Environment.Exit(0);
                }
                
                parser = new CsvRowParser(config);
                List<IGremlinElement> elements = new List<IGremlinElement>();
                
                var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    NewLine = config.GetNewLine(),
                    Delimiter = config.GetCsvFieldSeparator().ToString()
                };
                using (var csv = new CsvReader(GetSourceStreamReader(), csvConfig))
                {
                    // Header Row Processing and Validation
                    csv.Read();
                    csv.ReadHeader();
                    rowCount++;
                    string json = JsonConvert.SerializeObject(csv.HeaderRecord, Formatting.Indented);
                    Console.WriteLine("CsvReader header row fields: {0}", json);
                    ParseHeaderRow(csv.HeaderRecord);
                    if (!headerRow.IsValid())
                    {
                        throw new InvalidOperationException("CSV header row format is invalid.");
                    }

                    // Data Row Processing Loop
                    while (csv.Read())
                    {
                        var dict = csv.GetRecord<dynamic>() as IDictionary<string, object>;  // System.Dynamic.ExpandoObject;
                        // See https://joshclose.github.io/CsvHelper/examples/reading/reading-by-hand/
                        // See https://docs.microsoft.com/en-us/dotnet/api/system.dynamic.expandoobject?view=net-5.0
                        rowCount++;
                        if (verbose || (rowCount < 5)) { 
                            json = JsonConvert.SerializeObject(dict, Formatting.Indented);
                            Console.WriteLine("  row dict: {0}", json);
                        }
                        IGremlinElement element = parser.ParseRow(dict);
                        if (element == null)
                        {
                            json = JsonConvert.SerializeObject(dict, Formatting.Indented);
                            Console.WriteLine("ERROR: Unable to parse row number: {0} {1}", rowCount, json);
                        }
                        else
                        {
                            if (verbose || (rowCount < 5))
                            {
                                Console.WriteLine(JsonConvert.SerializeObject(element, Formatting.Indented));
                            }
                            if (doLoad)
                            {
                                elements.Add(element);

                                // Process the Bulk Loads in configurable batches so as to handle huge input files
                                if (elements.Count == batchSize)
                                {
                                    await LoadDatabase(elements);
                                    elements = new List<IGremlinElement>();  // reset the List for the next batch
                                }
                            }
                        }
                    }
                }

                if (doLoad)
                {
                    if (elements.Count > 0)
                    {
                        await LoadDatabase(elements);  // load the last batch
                    }
                }
                
                long elapsedTime = EpochMsTime() - startTime;

                Console.WriteLine($"finish timestamp: {CurrentTimestamp()}");

                string message = $"Main completed in: {elapsedTime} ms, rowCount: {rowCount}";

                Console.WriteLine(message);

                await WriteDoneBlobAsync(message);
            }
            catch (Exception e)
            {
                string message = $"ERROR: Exception in Main() - {e.Message}";

                await WriteErrorBlobAsync(message, e);

                Console.WriteLine(message);
                Console.WriteLine(e.StackTrace);
            }
            finally
            {
                if (graphBulkExecutor != null)
                {
                    Console.WriteLine("Disposing GraphBulkExecutor...");
                    graphBulkExecutor.Dispose();
                }
            }
            await Task.Delay(0);
        }

        static Config InitializeConfig(string[] args)
        {
            config = new Config(args);
            if (config.IsValid())
            {
                fileType = config.GetFileType();
                doLoad   = config.DoLoad();
                verbose  = config.IsVerbose();
                batchSize = config.GetBatchSize(); 
                config.Display();
            }
            return config;
        }

        /**
         * Return a StreamReader sourced from either a local file, or an Azure Storage Blob,
         * per command-line inputs.  If Blob, then also connect to Azure Storage.
         */
        static StreamReader GetSourceStreamReader()
        {
            if (config.IsBlobInput())
            {
                string connStr = config.GetStorageConnString();
                if (config.IsVerbose())
                {
                    Console.WriteLine($"connection string: {connStr}");
                }
                blobServiceClient = new BlobServiceClient(config.GetStorageConnString());
                containerClient = blobServiceClient.GetBlobContainerClient(config.GetStorageContainerName());
                blobClient = containerClient.GetBlobClient(config.GetStorageBlobName());
                source = $"blob: {config.GetStorageContainerName()} {config.GetStorageBlobName()}";
                return new StreamReader(blobClient.OpenRead());
            }
            else
            {
                infile = config.GetCsvInfile();
                source = $"file: {infile}";
                return new StreamReader(infile);
            }
        }

        static async Task WriteDoneBlobAsync(string contents)
        {
            string doneBlobPath = config.GetStorageDoneBlobName();

            if (!string.IsNullOrWhiteSpace(doneBlobPath))
            {
                await WriteFinalBlobAsync(doneBlobPath, contents); 
            }
        }

        static async Task WriteErrorBlobAsync(string message, Exception ex)
        {
            string errorBlobPath = config.GetStorageErrorBlobName();

            if (!string.IsNullOrWhiteSpace(errorBlobPath))
            {
                string contents = $"{message}{Environment.NewLine}{ex}";
                await WriteFinalBlobAsync(errorBlobPath, contents);
            }
        }

        static async Task WriteFinalBlobAsync(string path, string contents)
        {
            var blob = new BlobServiceClient(config.GetStorageConnString())
                        .GetBlobContainerClient(config.GetStorageContainerName())
                        .GetBlobClient(path);

            using MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(contents));

            await blob.UploadAsync(ms);
        }

        static void ParseHeaderRow(string[] headerFields)
        {
            headerRow = new HeaderRow(
                source,
                headerFields,
                config.GetFileType(),
                config.GetCsvFieldSeparator(),
                config.GetDatatypeSeparator());

            if (headerRow.IsValid())
            {
                parser.SetHeaderRow(headerRow);
                headerRow.Display();
            }
            else
            {
                Console.WriteLine("ERROR: headerRow is invalid, program will exit...");
            }
        }

        static async Task LoadDatabase(List<IGremlinElement> elements)
        {
            batchCount++;
            long startTime = EpochMsTime();
            Console.WriteLine("Start of batch load {0}, with {1} elements, at {2}", 
                batchCount, elements.Count, CurrentTimestamp());
            
            await graphBulkExecutor.BulkImportAsync(elements, true);
            
            Console.WriteLine("Batch load {0} completed in {1}ms", batchCount, EpochMsTime() - startTime);
        }

        private static long EpochMsTime()
        {
            return new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        }

        private static string CurrentTimestamp()
        {
            return DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
        }
    }
}
